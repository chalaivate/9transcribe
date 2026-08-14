using System.Runtime.InteropServices;
using NineTranscribe.Interop;

namespace NineTranscribe.Injection;

/// <summary>
/// Types the text one UTF-16 code unit at a time with <c>KEYEVENTF_UNICODE</c>. Slower than a
/// paste but leaves the clipboard untouched, and it is the only thing that works in terminals
/// where Ctrl+V means something else.
/// </summary>
internal sealed class TypingStrategy
{
    /// <summary>
    /// One SendInput call per chunk. A single huge call is accepted by the API but terminals,
    /// RDP sessions and Electron apps drop or reorder characters when flooded.
    /// </summary>
    private const int ChunkSize = 32;

    internal InsertionResult Insert(string text, int intervalMs, Func<bool> aborted, CancellationToken cancellationToken)
    {
        var pending = new List<InputNative.Input>(ChunkSize * 2);

        foreach (Segment segment in Split(text))
        {
            if (aborted() || cancellationToken.IsCancellationRequested)
            {
                return Cancelled();
            }

            if (segment.IsKey)
            {
                Send(BuildKey(segment.Key));
                Pause(intervalMs);
                continue;
            }

            pending.Clear();
            foreach (char unit in segment.Text)
            {
                // Surrogate pairs go through as two events; the receiving app reassembles them
                // when it translates the messages. Thai is entirely inside the BMP, and its
                // vowels and tone marks are ordinary code points that must stay in logical
                // order — exactly what physical Kedmanee typing produces.
                pending.Add(BuildUnicode(unit, up: false));
                pending.Add(BuildUnicode(unit, up: true));

                if (pending.Count >= ChunkSize * 2)
                {
                    Send(pending.ToArray());
                    pending.Clear();
                    Pause(intervalMs);

                    if (aborted() || cancellationToken.IsCancellationRequested)
                    {
                        return Cancelled();
                    }
                }
            }

            if (pending.Count > 0)
            {
                Send(pending.ToArray());
                Pause(intervalMs);
            }
        }

        return new InsertionResult(InsertionOutcome.Success);
    }

    /// <summary>
    /// Splits into runs of printable text and the keys that must be pressed for real: many apps
    /// ignore a WM_CHAR carriage return and only act on VK_RETURN.
    /// </summary>
    private static IEnumerable<Segment> Split(string text)
    {
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\n' && c != '\r' && c != '\t')
            {
                continue;
            }

            if (i > start)
            {
                yield return Segment.Printable(text[start..i]);
            }

            if (c == '\t')
            {
                yield return Segment.Key(InputNative.VkTab);
            }
            else if (c == '\n')
            {
                yield return Segment.Key(InputNative.VkReturn);
            }
            else if (i + 1 < text.Length && text[i + 1] == '\n')
            {
                // Let the \n of a \r\n pair produce the single Return.
                start = i + 1;
                continue;
            }
            else
            {
                yield return Segment.Key(InputNative.VkReturn);
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return Segment.Printable(text[start..]);
        }
    }

    private static void Pause(int intervalMs)
    {
        if (intervalMs > 0)
        {
            Thread.Sleep(Math.Clamp(intervalMs, 0, 50));
        }
    }

    private static void Send(InputNative.Input[] inputs)
    {
        if (inputs.Length > 0)
        {
            InputNative.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<InputNative.Input>());
        }
    }

    private static InputNative.Input[] BuildKey(ushort vk)
    {
        ushort scan = (ushort)InputNative.MapVirtualKeyW(vk, InputNative.MapVkToVsc);
        return new[]
        {
            NewInput(vk, scan, 0),
            NewInput(vk, scan, InputNative.KeyEventKeyUp),
        };
    }

    private static InputNative.Input BuildUnicode(char unit, bool up) => NewInput(
        vk: 0,
        scan: unit,
        flags: InputNative.KeyEventUnicode | (up ? InputNative.KeyEventKeyUp : 0));

    private static InputNative.Input NewInput(ushort vk, ushort scan, uint flags) => new()
    {
        Type = InputNative.InputKeyboard,
        Data = new InputNative.InputUnion
        {
            Keyboard = new InputNative.KeyboardInput
            {
                Vk = vk,
                Scan = scan,
                Flags = flags,
                Time = 0,
                ExtraInfo = InjectionTag.Value,
            },
        },
    };

    private static InsertionResult Cancelled() => new(
        InsertionOutcome.Cancelled,
        "หยุดพิมพ์กลางคัน เพราะมีการกดแป้นพิมพ์");

    private readonly record struct Segment(string Text, ushort Key, bool IsKey)
    {
        internal static Segment Printable(string text) => new(text, 0, false);

        internal static Segment Key(ushort vk) => new(string.Empty, vk, true);
    }
}
