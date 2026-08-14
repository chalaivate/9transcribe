using System.Globalization;
using System.Text;
using NineTranscribe.Settings;

namespace NineTranscribe.Hotkeys;

/// <summary>Modifier mask stored in <see cref="HotkeyBinding.Mods"/>.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Win = 8,
}

/// <summary>
/// Turns a binding into text the user can read, and flags the combinations that are known to
/// collide with Thai Windows or with the apps people dictate into.
/// </summary>
public static class HotkeyDisplay
{
    private const string Unset = "(ไม่ได้ตั้ง)";

    public static string Describe(HotkeyBinding binding)
    {
        if (binding is null || !binding.IsEnabled)
        {
            return Unset;
        }

        return Describe((ushort)binding.Vk, (HotkeyModifiers)binding.Mods);
    }

    public static string Describe(ushort vk, HotkeyModifiers mods)
    {
        if (vk == 0 && mods == HotkeyModifiers.None)
        {
            return Unset;
        }

        var builder = new StringBuilder(32);
        Append(builder, mods, HotkeyModifiers.Ctrl, "Ctrl");
        Append(builder, mods, HotkeyModifiers.Shift, "Shift");
        Append(builder, mods, HotkeyModifiers.Alt, "Alt");
        Append(builder, mods, HotkeyModifiers.Win, "Win");

        if (vk != 0)
        {
            if (builder.Length > 0)
            {
                builder.Append(" + ");
            }

            builder.Append(NameOf(vk));
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when the binding is likely to fight with Windows, with the Thai input-language
    /// switchers, or with ordinary typing. <paramref name="warningThai"/> is empty when false.
    /// </summary>
    public static bool IsRisky(ushort vk, HotkeyModifiers mods, out string warningThai)
    {
        bool bare = mods == HotkeyModifiers.None;
        HotkeyModifiers full = mods | ModifierFlagOf(vk);

        if (vk == KeyboardNative.VkOem3)
        {
            warningThai = "ปุ่ม ` (เหนือปุ่ม Tab) เป็นปุ่มสลับภาษาไทย/อังกฤษบน Windows ภาษาไทย";
            return true;
        }

        if (IsModifierKey(vk) && full == (HotkeyModifiers.Alt | HotkeyModifiers.Shift))
        {
            warningThai = "Alt + Shift เป็นคีย์ลัดสลับภาษาแบบเดิมของ Windows";
            return true;
        }

        if (IsModifierKey(vk) && full == (HotkeyModifiers.Ctrl | HotkeyModifiers.Shift))
        {
            warningThai = "Ctrl + Shift เป็นคีย์ลัดสลับผังแป้นพิมพ์แบบเดิมของ Windows";
            return true;
        }

        if ((mods & HotkeyModifiers.Win) != 0 && vk == KeyboardNative.VkSpace)
        {
            warningThai = "Win + Space เป็นคีย์ลัดสลับภาษาของ Windows";
            return true;
        }

        if ((mods & HotkeyModifiers.Win) != 0 && vk == KeyboardNative.VkH)
        {
            warningThai = "Win + H เป็นคีย์ลัดพิมพ์ด้วยเสียงของ Windows";
            return true;
        }

        if (mods == HotkeyModifiers.Ctrl && vk == KeyboardNative.VkSpace)
        {
            warningThai = "Ctrl + Space ใช้เลือกทั้งคอลัมน์ใน Excel และเรียก IntelliSense ใน VS Code";
            return true;
        }

        if (vk == KeyboardNative.VkCapital)
        {
            warningThai = "Caps Lock ถูกใช้สลับภาษาโดยผู้พิมพ์แป้นเกษมณี";
            return true;
        }

        if (bare && vk == KeyboardNative.VkF1)
        {
            warningThai = "F1 เป็นปุ่มเปิดวิธีใช้ของเกือบทุกโปรแกรม";
            return true;
        }

        if (bare && vk == KeyboardNative.VkF5)
        {
            warningThai = "F5 เป็นปุ่มรีเฟรชของเบราว์เซอร์และโปรแกรมทั่วไป";
            return true;
        }

        if (bare && vk == KeyboardNative.VkF9)
        {
            warningThai = "F9 เป็นปุ่มสั่งคำนวณใหม่ของ Excel";
            return true;
        }

        if (bare && vk == KeyboardNative.VkSpace)
        {
            warningThai = "Space ถูกใช้พิมพ์ตลอดเวลา ใช้เป็นคีย์ลัดเดี่ยวไม่ได้";
            return true;
        }

        if (bare && IsPrintable(vk))
        {
            warningThai = "ปุ่มนี้พิมพ์อักษรไทยได้ตามผังแป้นเกษมณี ใช้เป็นคีย์ลัดเดี่ยวจะรบกวนการพิมพ์";
            return true;
        }

        if (bare && IsEditing(vk))
        {
            warningThai = "ปุ่มนี้ใช้แก้ไขข้อความในทุกโปรแกรม ใช้เป็นคีย์ลัดเดี่ยวจะรบกวนการพิมพ์";
            return true;
        }

        warningThai = string.Empty;
        return false;
    }

    public static bool IsModifierKey(ushort vk) => vk switch
    {
        KeyboardNative.VkShift or KeyboardNative.VkControl or KeyboardNative.VkMenu => true,
        KeyboardNative.VkLShift or KeyboardNative.VkRShift => true,
        KeyboardNative.VkLControl or KeyboardNative.VkRControl => true,
        KeyboardNative.VkLMenu or KeyboardNative.VkRMenu => true,
        KeyboardNative.VkLWin or KeyboardNative.VkRWin => true,
        _ => false,
    };

    /// <summary>
    /// Whether a newly captured gesture should hide the key from the foreground app. Only the
    /// main key is ever hidden, so the modifier mask does not change the answer; a modifier as
    /// the main key is never hidden, because a lone modifier is inert in every application and
    /// swallowing it strands it in the down state.
    /// </summary>
    public static bool ShouldSwallowByDefault(ushort vk, HotkeyModifiers mods) =>
        vk != 0 && !IsModifierKey(vk);

    /// <summary>Which modifier bit, if any, the key itself contributes to the current mask.</summary>
    internal static HotkeyModifiers ModifierFlagOf(ushort vk) => vk switch
    {
        KeyboardNative.VkControl or KeyboardNative.VkLControl or KeyboardNative.VkRControl =>
            HotkeyModifiers.Ctrl,
        KeyboardNative.VkShift or KeyboardNative.VkLShift or KeyboardNative.VkRShift =>
            HotkeyModifiers.Shift,
        KeyboardNative.VkMenu or KeyboardNative.VkLMenu or KeyboardNative.VkRMenu =>
            HotkeyModifiers.Alt,
        KeyboardNative.VkLWin or KeyboardNative.VkRWin => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    private static void Append(StringBuilder builder, HotkeyModifiers mods, HotkeyModifiers flag, string name)
    {
        if ((mods & flag) == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(" + ");
        }

        builder.Append(name);
    }

    /// <summary>
    /// Names are hard-coded wherever a Thai layout would change them: under Kedmanee the OS
    /// reports Thai characters for every letter, digit and punctuation key, so a layout-derived
    /// label would rename the user's hotkey the moment they switch input language.
    /// </summary>
    private static string NameOf(ushort vk)
    {
        if (vk >= '0' && vk <= '9')
        {
            return ((char)vk).ToString();
        }

        if (vk >= 'A' && vk <= 'Z')
        {
            return ((char)vk).ToString();
        }

        if (vk >= KeyboardNative.VkF1 && vk <= KeyboardNative.VkF24)
        {
            return "F" + (vk - KeyboardNative.VkF1 + 1).ToString(CultureInfo.InvariantCulture);
        }

        if (vk >= 0x60 && vk <= 0x69)
        {
            return "Num " + (vk - 0x60).ToString(CultureInfo.InvariantCulture);
        }

        string? known = WellKnownName(vk);
        if (known is not null)
        {
            return known;
        }

        string layout = FromKeyboardLayout(vk);
        return layout.Length > 0
            ? layout
            : "VK 0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }

    private static string? WellKnownName(ushort vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0C => "Clear",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "Caps Lock",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2C => "Print Screen",
        0x2D => "Insert",
        0x2E => "Delete",
        0x5D => "Menu",
        0x6A => "Num *",
        0x6B => "Num +",
        0x6C => "Num Separator",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        0x90 => "Num Lock",
        0x91 => "Scroll Lock",
        // Side-specific names are hard-coded because MapVirtualKeyW collapses them to the
        // left-hand scan code, which would label Right Ctrl as "Left Ctrl".
        KeyboardNative.VkLShift => "Left Shift",
        KeyboardNative.VkRShift => "Right Shift",
        KeyboardNative.VkLControl => "Left Ctrl",
        KeyboardNative.VkRControl => "Right Ctrl",
        KeyboardNative.VkLMenu => "Left Alt",
        KeyboardNative.VkRMenu => "Right Alt",
        KeyboardNative.VkLWin => "Left Win",
        KeyboardNative.VkRWin => "Right Win",
        KeyboardNative.VkShift => "Shift",
        KeyboardNative.VkControl => "Ctrl",
        KeyboardNative.VkMenu => "Alt",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        0xE2 => "\\ (OEM102)",
        _ => null,
    };

    private static string FromKeyboardLayout(ushort vk)
    {
        try
        {
            uint scan = KeyboardNative.MapVirtualKeyW(vk, KeyboardNative.MapvkVkToVsc);
            if (scan == 0)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(64);
            int length = KeyboardNative.GetKeyNameTextW((int)(scan << 16), buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString() : string.Empty;
        }
        catch (Exception)
        {
            // Describing a key must never throw: the settings window renders this on every keystroke.
            return string.Empty;
        }
    }

    private static bool IsPrintable(ushort vk)
    {
        if (vk >= '0' && vk <= '9')
        {
            return true;
        }

        if (vk >= 'A' && vk <= 'Z')
        {
            return true;
        }

        return vk is 0xBA or 0xBB or 0xBC or 0xBD or 0xBE or 0xBF
            or 0xC0 or 0xDB or 0xDC or 0xDD or 0xDE or 0xE2;
    }

    private static bool IsEditing(ushort vk) =>
        vk is KeyboardNative.VkBack or KeyboardNative.VkTab or KeyboardNative.VkReturn
            or KeyboardNative.VkDelete;
}
