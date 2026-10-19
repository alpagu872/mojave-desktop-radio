namespace MojaveDesktopRadio.Dsp;

/// <summary>
/// Transposed direct form II biquad (RBJ audio EQ cookbook). One instance per channel.
/// Coefficients can be replaced at any time; the state is kept so retuning does not click badly.
/// </summary>
public struct Biquad
{
    private float _b0, _b1, _b2, _a1, _a2;
    private float _z1, _z2;

    public static Biquad Identity() => new() { _b0 = 1f };

    public void SetLowPass(float sampleRate, float freq, float q) => Set(Design(BiquadType.LowPass, sampleRate, freq, q, 0));
    public void SetHighPass(float sampleRate, float freq, float q) => Set(Design(BiquadType.HighPass, sampleRate, freq, q, 0));
    public void SetBandPass(float sampleRate, float freq, float q) => Set(Design(BiquadType.BandPass, sampleRate, freq, q, 0));
    public void SetPeaking(float sampleRate, float freq, float q, float gainDb) => Set(Design(BiquadType.Peaking, sampleRate, freq, q, gainDb));

    public void Set(in BiquadCoefficients c)
    {
        _b0 = c.B0; _b1 = c.B1; _b2 = c.B2; _a1 = c.A1; _a2 = c.A2;
    }

    public void Reset() { _z1 = 0; _z2 = 0; }

    public float Process(float x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        // flush denormals / recover from blow-ups
        if (!float.IsFinite(y)) { Reset(); return 0f; }
        if (Math.Abs(_z1) < 1e-20f) _z1 = 0f;
        if (Math.Abs(_z2) < 1e-20f) _z2 = 0f;
        return y;
    }

    public static BiquadCoefficients Design(BiquadType type, float sampleRate, float freq, float q, float gainDb)
    {
        var nyquist = sampleRate * 0.5f;
        freq = Math.Clamp(freq, 10f, nyquist * 0.95f);
        q = Math.Max(q, 0.05f);

        double w0 = 2 * Math.PI * freq / sampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * q);
        double b0, b1, b2, a0, a1, a2;

        switch (type)
        {
            case BiquadType.LowPass:
                b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case BiquadType.HighPass:
                b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case BiquadType.BandPass: // constant 0 dB peak gain
                b0 = alpha; b1 = 0; b2 = -alpha;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case BiquadType.Peaking:
            {
                double a = Math.Pow(10, gainDb / 40.0);
                b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
                a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
                break;
            }
            default:
                return new BiquadCoefficients(1, 0, 0, 0, 0);
        }

        return new BiquadCoefficients((float)(b0 / a0), (float)(b1 / a0), (float)(b2 / a0), (float)(a1 / a0), (float)(a2 / a0));
    }
}

public enum BiquadType { LowPass, HighPass, BandPass, Peaking }

public readonly record struct BiquadCoefficients(float B0, float B1, float B2, float A1, float A2);
