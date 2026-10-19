using MojaveDesktopRadio.Fx;

namespace MojaveDesktopRadio.Dsp;

/// <summary>
/// The "old radio" chain. Processes interleaved stereo float samples in place.
/// Real-time safe: no allocations, no locks in <see cref="Process"/>. Settings and the tuning
/// trigger are handed over through volatile fields and applied at the start of a block.
///
/// Signal flow (non-Clean presets):
///   width → wow (modulated delay) → fading × dropout → + hiss/crackle
///   → HP(24 dB) → LP(24 dB, drifting) → speaker peak → compressor → saturation → trim
///   → tuning overlay → soft limiter
/// The Clean preset is a strict bypass (bit-identical output).
/// </summary>
public sealed class RadioFxProcessor
{
    private const int DelaySize = 4096; // > 2 * max wow depth at 192 kHz
    private const int CoefUpdateInterval = 32;

    private readonly float _sr;

    private RadioParams _p = RadioParams.ForPreset(RadioPreset.Clean);
    private volatile RadioParams? _pending;
    private int _tuneRequestMs;

    // filters: [stage * 2 + channel]
    private readonly Biquad[] _hp = new Biquad[4];
    private readonly Biquad[] _lp = new Biquad[4];
    private readonly Biquad[] _peak = new Biquad[2];
    private Biquad _tuneBand;

    // compressor
    private float _env, _atk, _rel, _thr, _ratioInv, _makeup, _trim;

    // wow
    private readonly float[] _delayL = new float[DelaySize];
    private readonly float[] _delayR = new float[DelaySize];
    private int _delayWrite;
    private double _wowPhase;

    // noise
    private FastRandom _rng = new(0x1234567u);
    private PinkNoise _pinkNoise;
    private float _hissGain, _crackle;

    // fading / dropouts / drift
    private WanderLfo _fadeLfo;
    private float _dropGain = 1f, _dropTarget = 1f, _dropSmooth;
    private int _dropoutCountdown, _dropoutRemaining;
    private int _driftCountdown, _driftRemaining, _driftTotal;
    private int _coefCounter;

    // tuning
    private int _tuneRemaining, _tuneTotal;
    private double _whistlePhase;

    public RadioFxProcessor(int sampleRate)
    {
        _sr = sampleRate;
        _dropSmooth = 1f - MathF.Exp(-1f / (0.006f * _sr));
        ApplyParams(_p);
    }

    public int SampleRate => (int)_sr;
    public bool IsBypassed => _p.Bypass && _tuneRemaining <= 0;

    public void Update(FxSettings settings) => _pending = RadioParams.Resolve(settings.Sanitized());

    /// <summary>Starts the tuning sweep (ignored for the Clean preset).</summary>
    public void TriggerTuning(int durationMs = 480) => Volatile.Write(ref _tuneRequestMs, Math.Clamp(durationMs, 100, 2000));

    public void Process(Span<float> interleavedStereo)
    {
        var pending = _pending;
        if (pending is not null)
        {
            _pending = null;
            ApplyParams(pending);
        }

        var tuneMs = Interlocked.Exchange(ref _tuneRequestMs, 0);
        if (tuneMs > 0 && !_p.Bypass)
        {
            _tuneTotal = _tuneRemaining = (int)(tuneMs * _sr / 1000f);
        }

        if (_p.Bypass)
            return; // Clean: no change at all

        var p = _p;
        var frames = interleavedStereo.Length / 2;
        var wowDepth = p.WowDepthMs * _sr / 1000f;
        var wowInc = 2 * Math.PI * p.WowRateHz / _sr;

        for (var i = 0; i < frames; i++)
        {
            var l = interleavedStereo[2 * i];
            var r = interleavedStereo[2 * i + 1];

            // --- stereo width (mid/side)
            var mid = (l + r) * 0.5f;
            var side = (l - r) * 0.5f * p.Width;
            l = mid + side;
            r = mid - side;

            // --- wow: modulated fractional delay
            if (wowDepth > 0f)
            {
                _delayL[_delayWrite] = l;
                _delayR[_delayWrite] = r;
                _wowPhase += wowInc;
                if (_wowPhase > 2 * Math.PI) _wowPhase -= 2 * Math.PI;
                var d = wowDepth * (1f + (float)Math.Sin(_wowPhase)) + 2f;
                var readPos = _delayWrite - d;
                if (readPos < 0) readPos += DelaySize;
                var whole = (int)readPos;
                var frac = readPos - whole;
                var i0 = whole & (DelaySize - 1); // float rounding can yield exactly DelaySize
                var i1 = (i0 + 1) & (DelaySize - 1);
                l = _delayL[i0] + (_delayL[i1] - _delayL[i0]) * frac;
                r = _delayR[i0] + (_delayR[i1] - _delayR[i0]) * frac;
                _delayWrite = (_delayWrite + 1) & (DelaySize - 1);
            }

            // --- weak signal: fading + dropouts (signal only, noise rises in comparison)
            var sigGain = 1f;
            if (p.FadeDepth > 0f)
            {
                var lfo = _fadeLfo.Next(ref _rng, _sr, p.FadeMinHz, p.FadeMaxHz);
                sigGain = 1f - p.FadeDepth * (0.5f + 0.5f * lfo);
            }

            if (p.HasDropouts)
                sigGain *= StepDropout(p);

            l *= sigGain;
            r *= sigGain;

            // --- static: band-limited later by the radio filters
            var noise = 0f;
            if (_hissGain > 0f)
            {
                var weak = p.FadeDepth > 0f ? 1f + 1.5f * (1f - sigGain) : 1f;
                noise = _pinkNoise.Next(ref _rng) * _hissGain * weak;
            }

            if (p.CrackleRate > 0f)
            {
                if (_rng.NextUnit() < p.CrackleRate / _sr)
                    _crackle = p.CrackleLevel * _rng.Range(0.3f, 1f) * (_rng.NextUnit() < 0.5f ? -1f : 1f);
                noise += _crackle;
                _crackle *= 0.82f;
            }

            l += noise;
            r += noise;

            // --- drift: low-pass slides down and back
            if (p.HasDrift)
                StepDrift(p);

            // --- band limiting
            l = _hp[2].Process(_hp[0].Process(l));
            r = _hp[3].Process(_hp[1].Process(r));
            l = _lp[2].Process(_lp[0].Process(l));
            r = _lp[3].Process(_lp[1].Process(r));
            l = _peak[0].Process(l);
            r = _peak[1].Process(r);

            // --- compressor (stereo linked, peak envelope)
            var level = Math.Max(Math.Abs(l), Math.Abs(r));
            _env += (level > _env ? _atk : _rel) * (level - _env);
            var levelDb = 20f * MathF.Log10(_env + 1e-9f);
            var over = levelDb - _thr;
            var gr = over > 0f ? over * (1f - _ratioInv) : 0f;
            var compGain = MathF.Pow(10f, (-gr) / 20f) * _makeup;
            l *= compGain;
            r *= compGain;

            // --- saturation (slightly asymmetric tanh)
            if (p.SatMix > 0f && p.Drive > 1.01f)
            {
                l = Saturate(l, p);
                r = Saturate(r, p);
            }

            l *= _trim;
            r *= _trim;

            // --- tuning overlay
            if (_tuneRemaining > 0)
                ApplyTuning(ref l, ref r);

            interleavedStereo[2 * i] = SoftLimit(l);
            interleavedStereo[2 * i + 1] = SoftLimit(r);
        }
    }

    private void ApplyParams(RadioParams p)
    {
        _p = p;
        if (p.Bypass) return;

        for (var ch = 0; ch < 2; ch++)
        {
            _hp[ch].SetHighPass(_sr, p.HighPassHz, p.FilterQ);
            _hp[2 + ch].SetHighPass(_sr, p.HighPassHz, p.FilterQ);
            _lp[ch].SetLowPass(_sr, p.LowPassHz, p.FilterQ);
            _lp[2 + ch].SetLowPass(_sr, p.LowPassHz, p.FilterQ);
            _peak[ch].SetPeaking(_sr, p.PeakHz, p.PeakQ, p.PeakDb);
        }

        _atk = 1f - MathF.Exp(-1f / (p.CompAttackMs * 0.001f * _sr));
        _rel = 1f - MathF.Exp(-1f / (p.CompReleaseMs * 0.001f * _sr));
        _thr = p.CompThresholdDb;
        _ratioInv = 1f / Math.Max(1f, p.CompRatio);
        _makeup = MathF.Pow(10f, p.MakeupDb / 20f);
        _trim = MathF.Pow(10f, p.OutputTrimDb / 20f);
        _hissGain = p.HissDb <= -119f ? 0f : MathF.Pow(10f, p.HissDb / 20f);

        if (p.HasDropouts && _dropoutCountdown <= 0)
            _dropoutCountdown = (int)(_rng.Range(p.DropoutMinSec, p.DropoutMaxSec) * _sr);
        if (p.HasDrift && _driftCountdown <= 0)
            _driftCountdown = (int)(_rng.Range(p.DriftMinSec, p.DriftMaxSec) * _sr);
        if (!p.HasDrift) _driftRemaining = 0;
        if (!p.HasDropouts) { _dropoutRemaining = 0; _dropTarget = 1f; }
    }

    private float StepDropout(RadioParams p)
    {
        if (_dropoutRemaining > 0)
        {
            if (--_dropoutRemaining == 0)
            {
                _dropTarget = 1f;
                _dropoutCountdown = (int)(_rng.Range(p.DropoutMinSec, p.DropoutMaxSec) * _sr);
            }
        }
        else if (--_dropoutCountdown <= 0)
        {
            _dropoutRemaining = (int)(p.DropoutMs * _rng.Range(0.6f, 1.4f) * _sr / 1000f);
            _dropTarget = MathF.Pow(10f, p.DropoutDb / 20f);
        }

        _dropGain += (_dropTarget - _dropGain) * _dropSmooth;
        return _dropGain;
    }

    private void StepDrift(RadioParams p)
    {
        if (_driftRemaining > 0)
        {
            _driftRemaining--;
            if (++_coefCounter >= CoefUpdateInterval || _driftRemaining == 0)
            {
                _coefCounter = 0;
                var t = 1f - (float)_driftRemaining / _driftTotal;
                var mult = 1f - p.DriftDepth * MathF.Sin(MathF.PI * t);
                var lp = Math.Max(p.HighPassHz + 300f, p.LowPassHz * mult);
                for (var ch = 0; ch < 2; ch++)
                {
                    _lp[ch].SetLowPass(_sr, lp, p.FilterQ);
                    _lp[2 + ch].SetLowPass(_sr, lp, p.FilterQ);
                }
            }

            if (_driftRemaining == 0)
                _driftCountdown = (int)(_rng.Range(p.DriftMinSec, p.DriftMaxSec) * _sr);
        }
        else if (--_driftCountdown <= 0)
        {
            _driftTotal = _driftRemaining = Math.Max(1, (int)(p.DriftMs * _rng.Range(0.7f, 1.3f) * _sr / 1000f));
        }
    }

    private void ApplyTuning(ref float l, ref float r)
    {
        var t = 1f - (float)_tuneRemaining / _tuneTotal; // 0 → 1
        _tuneRemaining--;

        // noise envelope: quick rise, hold, fall; signal comes in during the second half
        var noiseEnv = t < 0.12f ? t / 0.12f : t < 0.5f ? 1f : Math.Max(0f, (1f - t) / 0.5f);
        var sigEnv = t < 0.4f ? 0f : SmoothStep((t - 0.4f) / 0.6f);

        if (++_coefCounter >= CoefUpdateInterval)
        {
            _coefCounter = 0;
            // the "dial" sweeps up and settles
            var centre = t < 0.6f ? 500f + 2400f * (t / 0.6f) : 2900f - 1500f * ((t - 0.6f) / 0.4f);
            _tuneBand.SetBandPass(_sr, centre, 1.6f);
        }

        var n = _tuneBand.Process(_rng.NextBipolar()) * 0.55f;
        var whistleHz = 1900f - 1300f * t;
        _whistlePhase += 2 * Math.PI * whistleHz / _sr;
        if (_whistlePhase > 2 * Math.PI) _whistlePhase -= 2 * Math.PI;
        var whistle = (float)Math.Sin(_whistlePhase) * 0.05f;

        var overlay = (n + whistle) * noiseEnv;
        l = l * sigEnv + overlay;
        r = r * sigEnv + overlay;
    }

    private static float Saturate(float x, RadioParams p)
    {
        var d = p.Drive;
        var shaped = (MathF.Tanh(d * (x + p.SatBias)) - MathF.Tanh(d * p.SatBias)) / MathF.Tanh(d);
        return x + (shaped - x) * p.SatMix;
    }

    private static float SmoothStep(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    internal static float SoftLimit(float x)
    {
        const float knee = 0.9f;
        var a = Math.Abs(x);
        if (a <= knee) return x;
        if (!float.IsFinite(a)) return 0f;
        var y = knee + (1f - knee) * MathF.Tanh((a - knee) / (1f - knee));
        return x < 0 ? -y : y;
    }
}
