namespace MojaveDesktopRadio.Dsp;

/// <summary>
/// Linear gain ramp used for the 150–350 ms open/close fades and the volume control.
/// The target is set from any thread; the ramp itself runs on the audio thread.
/// </summary>
public sealed class GainRamp
{
    private readonly float _sampleRate;
    private float _current;
    private float _step;
    private int _remaining;
    private volatile Request? _request;

    private sealed record Request(float Target, int Ms);

    public GainRamp(int sampleRate, float initial)
    {
        _sampleRate = sampleRate;
        _current = initial;
        Target = initial;
    }

    public float Target { get; private set; }
    public float Current => _current;

    /// <summary>True once the last requested ramp has finished.</summary>
    public bool IsSettled => _request is null && _remaining == 0;

    public void RampTo(float target, int ms)
    {
        Target = target;
        _request = new Request(target, Math.Max(0, ms));
    }

    /// <summary>Multiplies an interleaved buffer by the ramped gain.</summary>
    public void Apply(Span<float> interleaved, int channels)
    {
        var req = _request;
        if (req is not null)
        {
            _request = null;
            var samples = (int)(req.Ms * _sampleRate / 1000f);
            if (samples <= 0)
            {
                _current = req.Target;
                _remaining = 0;
            }
            else
            {
                _remaining = samples;
                _step = (req.Target - _current) / samples;
            }
        }

        var frames = interleaved.Length / channels;
        if (_remaining == 0)
        {
            if (Math.Abs(_current - 1f) < 1e-6f) return;
            if (_current <= 1e-6f) { interleaved.Clear(); return; }
            for (var i = 0; i < interleaved.Length; i++) interleaved[i] *= _current;
            return;
        }

        for (var f = 0; f < frames; f++)
        {
            if (_remaining > 0)
            {
                _current += _step;
                if (--_remaining == 0) _current = Target;
            }

            for (var c = 0; c < channels; c++)
                interleaved[f * channels + c] *= _current;
        }
    }
}
