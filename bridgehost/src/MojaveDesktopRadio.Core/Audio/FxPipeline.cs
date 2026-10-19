using MojaveDesktopRadio.Dsp;
using MojaveDesktopRadio.Fx;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MojaveDesktopRadio.Audio;

public sealed record PipelineStats(
    long CapturedFrames, long RenderedFrames, int Underruns, int DriftSkips,
    float InputPeak, float OutputPeak, int BufferedMs, string CaptureFormat, string OutputFormat);

/// <summary>
/// Route device (loopback, RAM only) → ring buffer → <see cref="RadioFxProcessor"/> → gain ramp
/// → (resample) → real output device. Nothing is written to disk.
/// </summary>
public sealed class FxPipeline : IDisposable
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly WasapiOut _output;
    private readonly FloatRingBuffer _ring;
    private readonly RadioFxProcessor _fx;
    private readonly GainRamp _gain;
    private readonly int _captureRate;
    private readonly int _captureChannels;
    private readonly CaptureConverter _converter;
    private readonly int _targetFrames;
    private readonly int _maxFrames;
    private float[] _scratch = new float[4096];
    private bool _started;
    private bool _disposed;

    private long _captured, _rendered;
    private int _underruns, _skips;
    private float _inPeak, _outPeak;

    public string RouteDeviceName { get; }
    public string OutputDeviceName { get; }
    public string OutputDeviceId { get; }

    /// <summary>Raised (on an audio thread) when either device fails; the engine rebuilds the pipeline.</summary>
    public event Action<string>? Faulted;

    public FxPipeline(MMDevice routeDevice, MMDevice outputDevice, FxSettings settings, int latencyMs, float initialGain = 0f)
    {
        RouteDeviceName = routeDevice.FriendlyName;
        OutputDeviceName = outputDevice.FriendlyName;
        OutputDeviceId = outputDevice.ID;

        _capture = new WasapiLoopbackCapture(routeDevice);
        var cf = _capture.WaveFormat;
        _captureRate = cf.SampleRate;
        _captureChannels = Math.Max(1, cf.Channels);
        _converter = CaptureConverter.For(cf);

        _ring = new FloatRingBuffer(_captureRate * 2);
        _targetFrames = _captureRate * 30 / 1000;
        _maxFrames = _captureRate * 160 / 1000;

        _fx = new RadioFxProcessor(_captureRate);
        _fx.Update(settings);
        _gain = new GainRamp(_captureRate, initialGain);

        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null && !_disposed) Faulted?.Invoke($"capture stopped: {e.Exception.Message}");
        };

        ISampleProvider chain = new RadioSampleProvider(this);
        var mix = outputDevice.AudioClient.MixFormat;
        if (mix.SampleRate != _captureRate)
            chain = new WdlResamplingSampleProvider(chain, mix.SampleRate);

        _output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, useEventSync: true, latency: latencyMs);
        _output.Init(new StereoToDeviceProvider(chain, mix));
        _output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null && !_disposed) Faulted?.Invoke($"output stopped: {e.Exception.Message}");
        };

        CaptureFormat = $"{cf.SampleRate} Hz {cf.Channels}ch {cf.BitsPerSample}-bit {cf.Encoding}";
        OutputFormat = $"{mix.SampleRate} Hz {mix.Channels}ch {mix.BitsPerSample}-bit {mix.Encoding}";
    }

    public string CaptureFormat { get; }
    public string OutputFormat { get; }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _capture.StartRecording();
        _output.Play();
        Log.Info($"FX pipeline started: '{RouteDeviceName}' [{CaptureFormat}] → '{OutputDeviceName}' [{OutputFormat}]");
    }

    public void ApplySettings(FxSettings settings) => _fx.Update(settings);

    public void FadeTo(float gain, int ms) => _gain.RampTo(gain, ms);

    public float TargetGain => _gain.Target;
    public bool FadeSettled => _gain.IsSettled;

    public void TriggerTuning(int ms = 480) => _fx.TriggerTuning(ms);

    public PipelineStats GetStats()
    {
        var s = new PipelineStats(Interlocked.Read(ref _captured), Interlocked.Read(ref _rendered),
            _underruns, _skips, _inPeak, _outPeak, _ring.AvailableFrames * 1000 / _captureRate, CaptureFormat, OutputFormat);
        _inPeak = 0;
        _outPeak = 0;
        return s;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        var frames = _converter.FrameCount(e.BytesRecorded, _captureChannels);
        if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];
        var span = _scratch.AsSpan(0, frames * 2);
        _converter.ToStereo(e.Buffer.AsSpan(0, e.BytesRecorded), _captureChannels, span);

        var peak = _inPeak;
        foreach (var v in span) { var a = Math.Abs(v); if (a > peak) peak = a; }
        _inPeak = peak;

        _ring.Write(span);
        Interlocked.Add(ref _captured, frames);
    }

    private int Render(float[] buffer, int offset, int count)
    {
        var span = buffer.AsSpan(offset, count);
        var avail = _ring.AvailableFrames;
        if (avail > _maxFrames)
        {
            _ring.SkipTo(_targetFrames);
            _skips++;
        }

        var got = _ring.Read(span);
        if (got * 2 < count)
        {
            span[(got * 2)..].Clear();
            if (got > 0) _underruns++; // a dry buffer while paused is normal; count only partial reads
        }

        _fx.Process(span);
        _gain.Apply(span, 2);

        var peak = _outPeak;
        foreach (var v in span) { var a = Math.Abs(v); if (a > peak) peak = a; }
        _outPeak = peak;

        Interlocked.Add(ref _rendered, count / 2);
        return count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture.StopRecording(); } catch { /* device may already be gone */ }
        try { _output.Stop(); } catch { }
        try { _capture.Dispose(); } catch { }
        try { _output.Dispose(); } catch { }
        Log.Info($"FX pipeline stopped ('{RouteDeviceName}' → '{OutputDeviceName}')");
    }

    private sealed class RadioSampleProvider(FxPipeline owner) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(owner._captureRate, 2);
        public int Read(float[] buffer, int offset, int count) => owner.Render(buffer, offset, count - count % 2);
    }
}

/// <summary>Maps the stereo processing stream onto the output device's mix format (any channel count).</summary>
internal sealed class StereoToDeviceProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _stereo = new float[4096];

    public StereoToDeviceProvider(ISampleProvider stereoSource, WaveFormat deviceMix)
    {
        _source = stereoSource;
        _channels = Math.Max(1, deviceMix.Channels);
        WaveFormat = _channels == 2
            ? WaveFormat.CreateIeeeFloatWaveFormat(deviceMix.SampleRate, 2)
            : new WaveFormatExtensible(deviceMix.SampleRate, 32, _channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        var frames = count / (4 * _channels);
        if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
        var read = _source.Read(_stereo, 0, frames * 2);
        if (read < frames * 2) Array.Clear(_stereo, read, frames * 2 - read);

        var dest = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * 4 * _channels));
        if (_channels == 2)
        {
            _stereo.AsSpan(0, frames * 2).CopyTo(dest);
        }
        else
        {
            dest.Clear();
            for (var f = 0; f < frames; f++)
            {
                if (_channels == 1)
                {
                    dest[f] = 0.5f * (_stereo[2 * f] + _stereo[2 * f + 1]);
                }
                else
                {
                    dest[f * _channels] = _stereo[2 * f];
                    dest[f * _channels + 1] = _stereo[2 * f + 1];
                }
            }
        }

        return frames * 4 * _channels;
    }
}

/// <summary>Converts a WASAPI capture buffer (float/PCM, N channels) to interleaved stereo float.</summary>
internal sealed class CaptureConverter
{
    private readonly int _bytesPerSample;
    private readonly bool _float;

    private CaptureConverter(int bytesPerSample, bool isFloat)
    {
        _bytesPerSample = bytesPerSample;
        _float = isFloat;
    }

    public static CaptureConverter For(WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        var bps = Math.Max(1, format.BitsPerSample / 8);
        return new CaptureConverter(bps, isFloat && bps == 4);
    }

    public int FrameCount(int bytes, int channels) => bytes / (_bytesPerSample * channels);

    public void ToStereo(ReadOnlySpan<byte> src, int channels, Span<float> dest)
    {
        var frames = dest.Length / 2;
        for (var f = 0; f < frames; f++)
        {
            var baseIdx = f * channels * _bytesPerSample;
            var l = Sample(src, baseIdx);
            var r = channels > 1 ? Sample(src, baseIdx + _bytesPerSample) : l;
            dest[2 * f] = l;
            dest[2 * f + 1] = r;
        }
    }

    private float Sample(ReadOnlySpan<byte> src, int i)
    {
        if (_float) return BitConverter.ToSingle(src.Slice(i, 4));
        return _bytesPerSample switch
        {
            2 => BitConverter.ToInt16(src.Slice(i, 2)) / 32768f,
            3 => ((src[i] << 8 | src[i + 1] << 16 | src[i + 2] << 24) >> 8) / 8388608f,
            4 => BitConverter.ToInt32(src.Slice(i, 4)) / 2147483648f,
            _ => 0f,
        };
    }
}
