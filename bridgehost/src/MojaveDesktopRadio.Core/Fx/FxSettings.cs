namespace MojaveDesktopRadio.Fx;

public enum RadioPreset
{
    Clean = 0,
    Mojave = 1,
    Wasteland = 2,
    Damaged = 3,
}

/// <summary>User-facing FX options (mirrors the MCM page). Immutable; replaced as a whole.</summary>
public sealed record FxSettings
{
    public bool Enabled { get; init; } = true;
    public RadioPreset Preset { get; init; } = RadioPreset.Mojave;

    /// <summary>0..1 output gain of the processed signal (routed mode only).</summary>
    public float Volume { get; init; } = 1.0f;

    /// <summary>0..1, 0.5 = preset default amount of hiss/crackle.</summary>
    public float StaticIntensity { get; init; } = 0.5f;

    /// <summary>0..1, 0.5 = preset default drive.</summary>
    public float Saturation { get; init; } = 0.5f;

    /// <summary>When true LowPassHz / HighPassHz / StereoWidth override the preset values.</summary>
    public bool CustomTone { get; init; }

    public float LowPassHz { get; init; } = 3300f;
    public float HighPassHz { get; init; } = 280f;

    /// <summary>0 = mono, 1 = original stereo.</summary>
    public float StereoWidth { get; init; } = 0.2f;

    public int FadeMs { get; init; } = 250;

    /// <summary>Short tuning/static sweep when the station is switched on (Wasteland/Damaged by default, any preset if enabled).</summary>
    public bool TuningFx { get; init; } = true;

    public RadioPreset EffectivePreset => Enabled ? Preset : RadioPreset.Clean;

    public bool WantsProcessing => EffectivePreset != RadioPreset.Clean;

    public FxSettings Sanitized() => this with
    {
        Volume = Math.Clamp(Volume, 0f, 1.5f),
        StaticIntensity = Math.Clamp(StaticIntensity, 0f, 1f),
        Saturation = Math.Clamp(Saturation, 0f, 1f),
        LowPassHz = Math.Clamp(LowPassHz, 800f, 16000f),
        HighPassHz = Math.Clamp(HighPassHz, 20f, 1500f),
        StereoWidth = Math.Clamp(StereoWidth, 0f, 1f),
        FadeMs = Math.Clamp(FadeMs, 0, 3000),
        Preset = Enum.IsDefined(Preset) ? Preset : RadioPreset.Mojave,
    };

    public static string DisplayKey(RadioPreset p) => p switch
    {
        RadioPreset.Clean => "Clean",
        RadioPreset.Mojave => "MojaveRadio",
        RadioPreset.Wasteland => "WastelandRadio",
        RadioPreset.Damaged => "DamagedRadio",
        _ => "Clean",
    };

    public static bool TryParsePreset(string? text, out RadioPreset preset)
    {
        preset = RadioPreset.Clean;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
        switch (t)
        {
            case "0": case "clean": preset = RadioPreset.Clean; return true;
            case "1": case "mojave": case "mojaveradio": preset = RadioPreset.Mojave; return true;
            case "2": case "wasteland": case "wastelandradio": preset = RadioPreset.Wasteland; return true;
            case "3": case "damaged": case "damagedradio": preset = RadioPreset.Damaged; return true;
            default: return false;
        }
    }
}
