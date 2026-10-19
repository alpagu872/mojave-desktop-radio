using MojaveDesktopRadio.Dsp;
using MojaveDesktopRadio.Fx;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MojaveDesktopRadio.Audio;

/// <summary>
/// Plays the procedurally generated tuning sweep on its own (used in Direct mode, where the
/// Spotify audio does not pass through the FX pipeline). No sample files are involved.
/// </summary>
public static class TuningBlip
{
    public static void Play(MMDevice output, int durationMs = 480, float gain = 0.8f)
    {
        try
        {
            var provider = new BlipProvider(48000, durationMs, gain);
            var player = new WasapiOut(output, AudioClientShareMode.Shared, true, 60);
            player.Init(provider);
            player.PlaybackStopped += (_, _) => player.Dispose();
            player.Play();
        }
        catch (Exception ex)
        {
            Log.Debug($"tuning blip failed: {ex.Message}");
        }
    }

    private sealed class BlipProvider : ISampleProvider
    {
        private readonly RadioFxProcessor _fx;
        private readonly float _gain;
        private int _remaining;

        public BlipProvider(int rate, int durationMs, float gain)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
            _fx = new RadioFxProcessor(rate);
            _fx.Update(new FxSettings { Preset = RadioPreset.Wasteland, StaticIntensity = 0.3f });
            _fx.TriggerTuning(durationMs);
            _remaining = rate * durationMs / 1000;
            _gain = gain;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = Math.Min(count / 2, _remaining);
            if (frames <= 0) return 0;
            var span = buffer.AsSpan(offset, frames * 2);
            span.Clear();
            _fx.Process(span);
            for (var i = 0; i < span.Length; i++) span[i] *= _gain;
            _remaining -= frames;
            return frames * 2;
        }
    }
}
