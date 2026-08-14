using System.Buffers.Binary;

namespace NineTranscribe.Audio;

/// <summary>
/// Builds the container the transcription API is fed: 16 kHz, 16-bit, mono PCM inside a
/// canonical 44-byte RIFF/WAVE header. The header is written by hand rather than through
/// NAudio's <c>WaveFileWriter</c> because that class only patches the RIFF and data sizes
/// when it is disposed, and disposing it also closes the underlying stream.
/// </summary>
public static class WavUtil
{
    public const int SampleRate = 16000;
    public const int BitsPerSample = 16;
    public const int Channels = 1;
    public const int BytesPerSecond = 32000;

    /// <summary>Bytes per sample frame; every PCM offset must be a multiple of this.</summary>
    public const int BlockAlign = 2;

    /// <summary>Size of the RIFF/fmt/data header this class writes.</summary>
    public const int HeaderBytes = 44;

    private const int PcmFormatTag = 1;
    private const int FmtChunkSize = 16;

    /// <summary>Wraps raw little-endian PCM in a WAV header. The PCM is copied, not referenced.</summary>
    public static byte[] CreateWav(ReadOnlySpan<byte> pcm)
    {
        int dataSize = pcm.Length;
        byte[] wav = new byte[HeaderBytes + dataSize];
        Span<byte> header = wav.AsSpan(0, HeaderBytes);

        WriteTag(header[..4], "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), HeaderBytes - 8 + dataSize);
        WriteTag(header.Slice(8, 4), "WAVE");

        WriteTag(header.Slice(12, 4), "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), FmtChunkSize);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(20, 2), PcmFormatTag);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(22, 2), Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24, 4), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28, 4), BytesPerSecond);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(32, 2), BlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(34, 2), BitsPerSample);

        WriteTag(header.Slice(36, 4), "data");
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40, 4), dataSize);

        pcm.CopyTo(wav.AsSpan(HeaderBytes));
        return wav;
    }

    /// <summary>
    /// A valid but silent WAV, used by the settings screen to probe the API key without
    /// asking the user to speak. Longer than a minute is refused: the probe is not a recording.
    /// </summary>
    public static byte[] CreateSilence(TimeSpan duration)
    {
        double seconds = Math.Clamp(duration.TotalSeconds, 0.0, 60.0);
        int bytes = (int)(seconds * BytesPerSecond);
        bytes -= bytes % BlockAlign;
        return CreateWav(new byte[bytes]);
    }

    /// <summary>Length in bytes of <paramref name="milliseconds"/> of this format, sample-aligned.</summary>
    public static int BytesForMilliseconds(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            return 0;
        }

        long bytes = (long)milliseconds * BytesPerSecond / 1000;
        bytes -= bytes % BlockAlign;
        return (int)Math.Min(bytes, int.MaxValue - HeaderBytes);
    }

    private static void WriteTag(Span<byte> destination, string tag)
    {
        for (int i = 0; i < tag.Length; i++)
        {
            destination[i] = (byte)tag[i];
        }
    }
}
