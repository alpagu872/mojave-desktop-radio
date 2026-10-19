namespace MojaveDesktopRadio.Dsp;

/// <summary>Allocation-free xorshift32 random source for the audio thread.</summary>
public struct FastRandom
{
    private uint _state;

    public FastRandom(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

    public uint NextUInt()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>Uniform in [-1, 1).</summary>
    public float NextBipolar() => (NextUInt() >> 8) * (2f / 16777216f) - 1f;

    /// <summary>Uniform in [0, 1).</summary>
    public float NextUnit() => (NextUInt() >> 8) * (1f / 16777216f);

    public float Range(float min, float max) => min + (max - min) * NextUnit();
}

/// <summary>Paul Kellet's economy pink noise filter driven by white noise.</summary>
public struct PinkNoise
{
    private float _b0, _b1, _b2;

    public float Next(ref FastRandom rng)
    {
        var white = rng.NextBipolar();
        _b0 = 0.99765f * _b0 + white * 0.0990460f;
        _b1 = 0.96300f * _b1 + white * 0.2965164f;
        _b2 = 0.57000f * _b2 + white * 1.0526913f;
        return (_b0 + _b1 + _b2 + white * 0.1848f) * 0.18f;
    }
}

/// <summary>
/// A smooth random LFO: picks a new random target every period and glides towards it.
/// Used for signal fading and drift so the "damaged radio" never sounds like a fixed tremolo.
/// </summary>
public struct WanderLfo
{
    private float _value, _target, _step;
    private int _samplesLeft;

    public float Next(ref FastRandom rng, float sampleRate, float minHz, float maxHz)
    {
        if (_samplesLeft <= 0)
        {
            var hz = rng.Range(minHz, maxHz);
            _samplesLeft = Math.Max(1, (int)(sampleRate / Math.Max(hz, 0.01f)));
            _target = rng.NextBipolar();
            _step = (_target - _value) / _samplesLeft;
        }

        _samplesLeft--;
        _value += _step;
        return _value;
    }
}
