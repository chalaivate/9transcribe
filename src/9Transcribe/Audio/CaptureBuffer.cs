namespace NineTranscribe.Audio;

/// <summary>
/// The audio of one recording session, addressed by absolute byte offsets from the start of the
/// session — the same offsets <see cref="VadMonitor"/> reports. Audio that has already been
/// handed over can be dropped from the front, so a long dictation holds roughly one sentence of
/// audio instead of everything said since the key went down.
/// <para>Not thread-safe; <see cref="AudioRecorder"/> only touches it under its state lock.</para>
/// </summary>
public sealed class CaptureBuffer
{
    /// <summary>A dropped prefix at least this large is always reclaimed.</summary>
    public const int CompactThresholdBytes = 1024 * 1024;

    private readonly int _initialCapacity;
    private byte[] _data;
    private int _count;

    public CaptureBuffer(int initialCapacity = 512 * 1024)
    {
        _initialCapacity = Math.Max(1024, initialCapacity);
        _data = new byte[_initialCapacity];
    }

    /// <summary>Absolute offset of the oldest byte still held.</summary>
    public long Origin { get; private set; }

    /// <summary>Absolute offset just past the newest byte; the session's length so far.</summary>
    public long End => Origin + _count;

    /// <summary>Bytes currently held in memory.</summary>
    public int HeldBytes => _count;

    public int Capacity => _data.Length;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        EnsureCapacity(_count + bytes.Length);
        bytes.CopyTo(_data.AsSpan(_count));
        _count += bytes.Length;
    }

    /// <summary>
    /// The bytes between two absolute offsets, clamped to what is still held. The span aliases
    /// the buffer and is only valid until the next <see cref="Append"/> or <see cref="DiscardBefore"/>.
    /// </summary>
    public ReadOnlySpan<byte> Slice(long start, long end)
    {
        long from = Math.Clamp(start, Origin, End);
        long to = Math.Clamp(end, from, End);
        return _data.AsSpan((int)(from - Origin), (int)(to - from));
    }

    /// <summary>
    /// Lets go of everything before <paramref name="offset"/>. The copy is skipped while the
    /// dropped prefix is small next to what would have to move, so a cut every few seconds
    /// costs almost nothing; the bytes are reclaimed at the next cut that makes it worthwhile.
    /// </summary>
    public void DiscardBefore(long offset)
    {
        long target = Math.Clamp(offset, Origin, End);
        int drop = (int)(target - Origin);
        if (drop == 0)
        {
            return;
        }

        int keep = _count - drop;
        if (drop < CompactThresholdBytes && drop < keep)
        {
            return;
        }

        _data.AsSpan(drop, keep).CopyTo(_data);
        _count = keep;
        Origin = target;

        // A burst of unbroken speech can have grown the array; give it back once it is mostly empty.
        int wanted = Math.Max(_initialCapacity, _count * 2);
        if (_data.Length > _initialCapacity && _data.Length >= wanted * 2)
        {
            byte[] smaller = new byte[wanted];
            _data.AsSpan(0, _count).CopyTo(smaller);
            _data = smaller;
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _data.Length)
        {
            return;
        }

        int next = Math.Max(required, _data.Length * 2);
        byte[] larger = new byte[next];
        _data.AsSpan(0, _count).CopyTo(larger);
        _data = larger;
    }
}
