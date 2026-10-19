using MojaveDesktopRadio.Audio;
using MojaveDesktopRadio.Dsp;
using MojaveDesktopRadio.Fx;
using Xunit;

namespace MojaveDesktopRadio.Tests;

public class DspTests
{
    private const int Rate = 48000;

    private static float[] Tone(float hz, float seconds, float amp = 0.5f)
    {
        var frames = (int)(Rate * seconds);
        var buf = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = amp * MathF.Sin(2 * MathF.PI * hz * i / Rate);
            buf[2 * i] = v;
            buf[2 * i + 1] = v;
        }

        return buf;
    }

    private static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var v in s) sum += v * v;
        return Math.Sqrt(sum / s.Length);
    }

    private static float[] Run(RadioPreset preset, float[] input, float staticIntensity = 0f)
    {
        var fx = new RadioFxProcessor(Rate);
        fx.Update(new FxSettings { Preset = preset, StaticIntensity = staticIntensity });
        var buf = (float[])input.Clone();
        for (var off = 0; off < buf.Length; off += 1024)
            fx.Process(buf.AsSpan(off, Math.Min(1024, buf.Length - off)));
        return buf;
    }

    [Fact]
    public void Clean_is_bit_identical()
    {
        var input = Tone(440, 0.5f);
        var output = Run(RadioPreset.Clean, input, 1f);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Disabled_fx_behaves_like_clean()
    {
        var fx = new RadioFxProcessor(Rate);
        fx.Update(new FxSettings { Enabled = false, Preset = RadioPreset.Damaged });
        var input = Tone(440, 0.2f);
        var buf = (float[])input.Clone();
        fx.Process(buf);
        Assert.Equal(input, buf);
    }

    [Theory]
    [InlineData(RadioPreset.Mojave)]
    [InlineData(RadioPreset.Wasteland)]
    [InlineData(RadioPreset.Damaged)]
    public void Radio_presets_remove_bass_and_treble_but_keep_mids(RadioPreset preset)
    {
        var mid = Rms(Run(preset, Tone(1000, 1f)).AsSpan(Rate));
        var bass = Rms(Run(preset, Tone(60, 1f)).AsSpan(Rate));
        var treble = Rms(Run(preset, Tone(10000, 1f)).AsSpan(Rate));

        Assert.True(mid > 0.05, $"1 kHz should pass ({mid})");
        Assert.True(bass < mid * 0.1, $"60 Hz should be strongly attenuated ({bass} vs {mid})");
        Assert.True(treble < mid * 0.05, $"10 kHz should be strongly attenuated ({treble} vs {mid})");
    }

    [Theory]
    [InlineData(RadioPreset.Mojave)]
    [InlineData(RadioPreset.Wasteland)]
    [InlineData(RadioPreset.Damaged)]
    public void Output_never_clips_or_produces_nan(RadioPreset preset)
    {
        var loud = Tone(800, 2f, 4f); // +12 dBFS input
        var fx = new RadioFxProcessor(Rate);
        fx.Update(new FxSettings { Preset = preset, StaticIntensity = 1f, Saturation = 1f });
        fx.TriggerTuning();
        for (var off = 0; off < loud.Length; off += 512)
            fx.Process(loud.AsSpan(off, Math.Min(512, loud.Length - off)));

        Assert.All(loud, v => Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1.0f));
    }

    [Fact]
    public void Damaged_stays_intelligible()
    {
        // Over 20 s (several dropouts / drifts), the 1 kHz content must never vanish for long.
        var input = Tone(1000, 20f);
        var output = Run(RadioPreset.Damaged, input);
        var window = Rate / 10 * 2; // 100 ms
        var reference = Rms(Run(RadioPreset.Mojave, Tone(1000, 2f)).AsSpan(Rate));
        for (var off = Rate * 2; off + window <= output.Length; off += window)
        {
            var r = Rms(output.AsSpan(off, window));
            Assert.True(r > reference * 0.15, $"signal too weak at {off / 2.0 / Rate:0.0}s: {r} vs {reference}");
        }
    }

    [Fact]
    public void Mono_presets_collapse_stereo()
    {
        var frames = Rate;
        var buf = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = 0.4f * MathF.Sin(2 * MathF.PI * 1000 * i / Rate);
            buf[2 * i] = v;
            buf[2 * i + 1] = -v; // pure side signal
        }

        var outBuf = Run(RadioPreset.Wasteland, buf);
        Assert.True(Rms(outBuf.AsSpan(Rate)) < 0.02, "a pure side signal must vanish in mono");
    }

    [Fact]
    public void Gain_ramp_reaches_target()
    {
        var g = new GainRamp(Rate, 0f);
        g.RampTo(1f, 250);
        var buf = new float[Rate * 2];
        Array.Fill(buf, 1f);
        g.Apply(buf, 2);
        Assert.Equal(0f, buf[0], 2);
        Assert.Equal(1f, buf[^1], 3);
        Assert.True(g.IsSettled);
    }

    [Fact]
    public void Ring_buffer_drops_oldest_on_overflow_and_skips_for_drift()
    {
        var ring = new FloatRingBuffer(100);
        var data = Enumerable.Range(0, 300).Select(i => (float)i).ToArray(); // 150 frames
        ring.Write(data);
        Assert.Equal(100, ring.AvailableFrames);
        var dest = new float[2];
        ring.Read(dest);
        Assert.Equal(100f, dest[0]); // frame 50 is the oldest kept
        ring.SkipTo(10);
        Assert.Equal(10, ring.AvailableFrames);
    }

    [Fact]
    public void Soft_limiter_is_bounded_and_transparent_below_knee()
    {
        Assert.Equal(0.5f, RadioFxProcessor.SoftLimit(0.5f));
        Assert.True(RadioFxProcessor.SoftLimit(10f) <= 1f);
        Assert.True(RadioFxProcessor.SoftLimit(-10f) >= -1f);
        Assert.Equal(0f, RadioFxProcessor.SoftLimit(float.NaN));
    }

    [Fact]
    public void Custom_tone_overrides_preset_filters()
    {
        var p = RadioParams.Resolve(new FxSettings { Preset = RadioPreset.Mojave, CustomTone = true, LowPassHz = 6000, HighPassHz = 120, StereoWidth = 1 });
        Assert.Equal(6000, p.LowPassHz);
        Assert.Equal(120, p.HighPassHz);
        Assert.Equal(1, p.Width);
    }
}
