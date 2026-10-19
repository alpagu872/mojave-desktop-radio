namespace MojaveDesktopRadio.Audio;

/// <summary>
/// Single-producer / single-consumer ring of interleaved stereo frames.
/// The capture thread writes, the render thread reads; no locks.
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly float[] _data;
    private readonly int _capacityFrames;
    private long _writeFrames; // total frames written
    private long _readFrames;  // total frames read

    public FloatRingBuffer(int capacityFrames)
    {
        _capacityFrames = capacityFrames;
        _data = new float[capacityFrames * 2];
    }

    public int AvailableFrames => (int)(Volatile.Read(ref _writeFrames) - Volatile.Read(ref _readFrames));

    /// <summary>Writes stereo frames. On overflow the oldest data is dropped (reader is lagging).</summary>
    public void Write(ReadOnlySpan<float> stereo)
    {
        var frames = stereo.Length / 2;
        var w = Volatile.Read(ref _writeFrames);
        for (var f = 0; f < frames; f++)
        {
            var idx = (int)((w + f) % _capacityFrames) * 2;
            _data[idx] = stereo[2 * f];
            _data[idx + 1] = stereo[2 * f + 1];
        }

        Volatile.Write(ref _writeFrames, w + frames);

        // keep the reader inside the window
        var r = Volatile.Read(ref _readFrames);
        if (w + frames - r > _capacityFrames)
            Interlocked.CompareExchange(ref _readFrames, w + frames - _capacityFrames, r);
    }

    /// <summary>Reads up to dest.Length/2 frames; returns frames read.</summary>
    public int Read(Span<float> dest)
    {
        var want = dest.Length / 2;
        var r = Volatile.Read(ref _readFrames);
        var avail = (int)(Volatile.Read(ref _writeFrames) - r);
        var n = Math.Min(want, avail);
        for (var f = 0; f < n; f++)
        {
            var idx = (int)((r + f) % _capacityFrames) * 2;
            dest[2 * f] = _data[idx];
            dest[2 * f + 1] = _data[idx + 1];
        }

        Volatile.Write(ref _readFrames, r + n);
        return n;
    }

    /// <summary>Drops frames so that at most <paramref name="keepFrames"/> remain (latency / drift control).</summary>
    public int SkipTo(int keepFrames)
    {
        var r = Volatile.Read(ref _readFrames);
        var avail = (int)(Volatile.Read(ref _writeFrames) - r);
        if (avail <= keepFrames) return 0;
        var skip = avail - keepFrames;
        Volatile.Write(ref _readFrames, r + skip);
        return skip;
    }

    public void Clear() => Volatile.Write(ref _readFrames, Volatile.Read(ref _writeFrames));
}
