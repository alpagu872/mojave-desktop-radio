using MojaveDesktopRadio.Fx;

namespace MojaveDesktopRadio.Dsp;

/// <summary>Resolved DSP parameters for one preset (after user overrides are applied).</summary>
public sealed record RadioParams
{
    public bool Bypass { get; init; }

    // Band limiting (each filter is 2 cascaded biquads = 24 dB/oct)
    public float HighPassHz { get; init; } = 280f;
    public float LowPassHz { get; init; } = 3300f;
    public float FilterQ { get; init; } = 0.707f;

    /// <summary>0 = mono, 1 = untouched stereo.</summary>
    public float Width { get; init; } = 0.25f;

    // "Speaker box" resonance
    public float PeakHz { get; init; } = 1400f;
    public float PeakQ { get; init; } = 1.0f;
    public float PeakDb { get; init; } = 3f;

    // Compressor (stereo linked)
    public float CompThresholdDb { get; init; } = -18f;
    public float CompRatio { get; init; } = 3f;
    public float CompAttackMs { get; init; } = 6f;
    public float CompReleaseMs { get; init; } = 140f;
    public float MakeupDb { get; init; } = 4f;

    // Saturation
    public float Drive { get; init; } = 1.6f;
    public float SatMix { get; init; } = 0.5f;
    public float SatBias { get; init; } = 0.04f;

    // Noise (levels before the StaticIntensity scale; 0.5 slider = these values)
    public float HissDb { get; init; } = -90f;
    public float CrackleRate { get; init; }
    public float CrackleLevel { get; init; }

    // Pitch wobble (wow) — gives the short "frequency shift" character
    public float WowDepthMs { get; init; }
    public float WowRateHz { get; init; } = 0.4f;

    // Weak-signal fading (smooth random amplitude), FadeDepth 0.4 => gain wanders 0.6..1.0
    public float FadeDepth { get; init; }
    public float FadeMinHz { get; init; } = 0.08f;
    public float FadeMaxHz { get; init; } = 0.5f;

    // Brief signal dropouts
    public float DropoutMinSec { get; init; }
    public float DropoutMaxSec { get; init; }
    public float DropoutMs { get; init; } = 140f;
    public float DropoutDb { get; init; } = -10f;

    // Tuning drift: low-pass briefly slides down and back
    public float DriftMinSec { get; init; }
    public float DriftMaxSec { get; init; }
    public float DriftDepth { get; init; } = 0.3f;
    public float DriftMs { get; init; } = 500f;

    public float OutputTrimDb { get; init; }

    public bool HasDropouts => DropoutMaxSec > 0;
    public bool HasDrift => DriftMaxSec > 0;

    public static RadioParams ForPreset(RadioPreset preset) => preset switch
    {
        RadioPreset.Clean => new RadioParams { Bypass = true },

        // Clean old speaker: 280 Hz – 3.3 kHz, narrow stereo, gentle comp & warmth
        RadioPreset.Mojave => new RadioParams
        {
            HighPassHz = 280f, LowPassHz = 3300f, Width = 0.25f,
            PeakHz = 1400f, PeakQ = 1.0f, PeakDb = 3f,
            CompThresholdDb = -18f, CompRatio = 3f, MakeupDb = 4f,
            Drive = 1.6f, SatMix = 0.45f,
            HissDb = -66f,
            OutputTrimDb = 1.5f,
        },

        // Narrower band, mono, light hiss and crackle, more drive
        RadioPreset.Wasteland => new RadioParams
        {
            HighPassHz = 380f, LowPassHz = 2800f, Width = 0f,
            PeakHz = 1200f, PeakQ = 1.2f, PeakDb = 5f,
            CompThresholdDb = -20f, CompRatio = 4f, MakeupDb = 6f,
            Drive = 2.6f, SatMix = 0.65f, SatBias = 0.06f,
            HissDb = -46f, CrackleRate = 1.2f, CrackleLevel = 0.06f,
            WowDepthMs = 0.25f, WowRateHz = 0.5f,
            FadeDepth = 0.1f,
            OutputTrimDb = 1f,
        },

        // Weak signal: fading, dropouts, drift, more static — but always intelligible
        RadioPreset.Damaged => new RadioParams
        {
            HighPassHz = 450f, LowPassHz = 2500f, Width = 0f,
            PeakHz = 1100f, PeakQ = 1.4f, PeakDb = 6f,
            CompThresholdDb = -22f, CompRatio = 4.5f, MakeupDb = 7f,
            Drive = 3.2f, SatMix = 0.75f, SatBias = 0.08f,
            HissDb = -38f, CrackleRate = 3.5f, CrackleLevel = 0.12f,
            WowDepthMs = 1.1f, WowRateHz = 0.35f,
            FadeDepth = 0.4f, FadeMinHz = 0.1f, FadeMaxHz = 0.6f,
            DropoutMinSec = 6f, DropoutMaxSec = 14f, DropoutMs = 140f, DropoutDb = -10f,
            DriftMinSec = 5f, DriftMaxSec = 12f, DriftDepth = 0.3f, DriftMs = 550f,
            OutputTrimDb = 0.5f,
        },

        _ => new RadioParams { Bypass = true },
    };

    /// <summary>Applies the user sliders (static, saturation, custom tone) on top of the preset.</summary>
    public static RadioParams Resolve(FxSettings s)
    {
        var p = ForPreset(s.EffectivePreset);
        if (p.Bypass) return p;

        // 0.5 = preset value; 0 = no static; 1 = +6 dB
        var staticScale = s.StaticIntensity * 2f;
        var hissDb = staticScale <= 0.001f ? -120f : p.HissDb + 20f * MathF.Log10(staticScale);
        var driveScale = s.Saturation * 2f;

        p = p with
        {
            HissDb = hissDb,
            CrackleLevel = p.CrackleLevel * staticScale,
            Drive = 1f + (p.Drive - 1f) * driveScale,
            SatMix = Math.Clamp(p.SatMix * (0.4f + 0.6f * driveScale), 0f, 1f),
        };

        if (s.CustomTone)
        {
            var hp = Math.Clamp(s.HighPassHz, 20f, 1500f);
            var lp = Math.Clamp(s.LowPassHz, hp + 200f, 16000f);
            p = p with { HighPassHz = hp, LowPassHz = lp, Width = Math.Clamp(s.StereoWidth, 0f, 1f) };
        }

        return p;
    }
}
