using ImGuiNET;

namespace Replay.Mobile;

/// <summary>跨 Android、Unity 和 Viewer 设置的规范化键名映射。</summary>
internal static class ReplayKeyMap
{
    internal static bool TryParse(string? value, out ImGuiKey key)
    {
        key = ImGuiKey.None;
        string name = value?.Trim() ?? string.Empty;
        if (name.Length == 0) return false;
        string normalized = name.Replace(" ", string.Empty).Replace("-", string.Empty);
        normalized = normalized.ToLowerInvariant() switch
        {
            "alpha0" => "_0", "alpha1" => "_1", "alpha2" => "_2", "alpha3" => "_3",
            "alpha4" => "_4", "alpha5" => "_5", "alpha6" => "_6", "alpha7" => "_7",
            "alpha8" => "_8", "alpha9" => "_9", "0" => "_0", "1" => "_1",
            "2" => "_2", "3" => "_3", "4" => "_4", "5" => "_5", "6" => "_6",
            "7" => "_7", "8" => "_8", "9" => "_9", "return" or "numpadenter" => "Enter",
            "esc" => "Escape", "del" or "forwarddelete" => "Delete",
            "pgup" or "pageup" => "PageUp", "pgdn" or "pgdown" or "pagedown" => "PageDown",
            "equals" => "Equal", "leftcontrol" => "LeftCtrl", "rightcontrol" => "RightCtrl",
            "backquote" => "GraveAccent", "lbracket" => "LeftBracket", "rbracket" => "RightBracket",
            "quote" => "Apostrophe", "plus" or "keypadplus" or "numpadadd" => "KeypadAdd",
            "keypadminus" or "numpadsubtract" => "KeypadSubtract", "numpaddivide" => "KeypadDivide",
            "numpadmultiply" => "KeypadMultiply", "numpaddecimal" => "KeypadDecimal",
            "numpadequal" => "KeypadEqual", _ => normalized,
        };
        if (!Enum.TryParse(normalized, true, out key)) return false;
        return key != ImGuiKey.None
            && !normalized.Equals("NamedKey_BEGIN", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("NamedKey_END", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("NamedKey_COUNT", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryMapAndroidKeyCode(int keyCode, out ImGuiKey key)
    {
        key = keyCode switch
        {
            >= 7 and <= 16 => ParseRequired("_" + (keyCode - 7)),
            >= 29 and <= 54 => ParseRequired(((char)('A' + keyCode - 29)).ToString()),
            3 or 122 => ImGuiKey.Home, 4 or 111 => ImGuiKey.Escape,
            19 => ImGuiKey.UpArrow, 20 => ImGuiKey.DownArrow, 21 => ImGuiKey.LeftArrow,
            22 => ImGuiKey.RightArrow, 61 => ImGuiKey.Tab, 62 => ImGuiKey.Space,
            66 => ImGuiKey.Enter, 67 => ImGuiKey.Backspace, 68 => ImGuiKey.GraveAccent,
            69 => ImGuiKey.Minus, 70 => ImGuiKey.Equal, 71 => ImGuiKey.LeftBracket,
            72 => ImGuiKey.RightBracket, 73 => ImGuiKey.Backslash, 74 => ImGuiKey.Semicolon,
            75 => ImGuiKey.Apostrophe, 76 => ImGuiKey.Slash, 55 => ImGuiKey.Comma,
            56 => ImGuiKey.Period, 57 => ImGuiKey.LeftAlt, 58 => ImGuiKey.RightAlt,
            59 => ImGuiKey.LeftShift, 60 => ImGuiKey.RightShift, 81 => ImGuiKey.KeypadAdd,
            82 => ImGuiKey.Menu, 92 => ImGuiKey.PageUp, 93 => ImGuiKey.PageDown,
            112 => ImGuiKey.Delete, 113 => ImGuiKey.LeftCtrl, 114 => ImGuiKey.RightCtrl,
            115 => ImGuiKey.CapsLock, 116 => ImGuiKey.ScrollLock, 117 => ImGuiKey.LeftSuper,
            118 => ImGuiKey.RightSuper, 120 => ImGuiKey.PrintScreen, 121 => ImGuiKey.Pause,
            123 => ImGuiKey.End, 124 => ImGuiKey.Insert, 143 => ImGuiKey.NumLock,
            >= 131 and <= 142 => ParseRequired("F" + (keyCode - 130)),
            >= 144 and <= 153 => ParseRequired("Keypad" + (keyCode - 144)),
            154 => ImGuiKey.KeypadDivide, 155 => ImGuiKey.KeypadMultiply,
            156 => ImGuiKey.KeypadSubtract, 157 => ImGuiKey.KeypadAdd,
            158 => ImGuiKey.KeypadDecimal, 159 => ImGuiKey.Comma,
            160 => ImGuiKey.KeypadEnter, 161 => ImGuiKey.KeypadEqual,
            _ => ImGuiKey.None,
        };
        return key != ImGuiKey.None;
    }

    internal static bool TryMapUnityKeyCode(ImGuiKey key, out int keyCode)
    {
        if (key >= ImGuiKey._0 && key <= ImGuiKey._9)
        { keyCode = 48 + (int)(key - ImGuiKey._0); return true; }
        if (key >= ImGuiKey.A && key <= ImGuiKey.Z)
        { keyCode = 97 + (int)(key - ImGuiKey.A); return true; }
        if (key >= ImGuiKey.F1 && key <= ImGuiKey.F15)
        { keyCode = 282 + (int)(key - ImGuiKey.F1); return true; }
        if (key >= ImGuiKey.F16 && key <= ImGuiKey.F24)
        { keyCode = 670 + (int)(key - ImGuiKey.F16); return true; }
        if (key >= ImGuiKey.Keypad0 && key <= ImGuiKey.Keypad9)
        { keyCode = 256 + (int)(key - ImGuiKey.Keypad0); return true; }
        keyCode = key switch
        {
            ImGuiKey.Tab => 9, ImGuiKey.LeftArrow => 276, ImGuiKey.RightArrow => 275,
            ImGuiKey.UpArrow => 273, ImGuiKey.DownArrow => 274, ImGuiKey.PageUp => 280,
            ImGuiKey.PageDown => 281, ImGuiKey.Home => 278, ImGuiKey.End => 279,
            ImGuiKey.Insert => 277, ImGuiKey.Delete => 127, ImGuiKey.Backspace => 8,
            ImGuiKey.Space => 32, ImGuiKey.Enter => 13, ImGuiKey.Escape => 27,
            ImGuiKey.LeftCtrl => 306, ImGuiKey.LeftShift => 304, ImGuiKey.LeftAlt => 308,
            ImGuiKey.LeftSuper => 310, ImGuiKey.RightCtrl => 305, ImGuiKey.RightShift => 303,
            ImGuiKey.RightAlt => 307, ImGuiKey.RightSuper => 309, ImGuiKey.Menu => 319,
            ImGuiKey.Apostrophe => 39, ImGuiKey.Comma => 44, ImGuiKey.Minus => 45,
            ImGuiKey.Period => 46, ImGuiKey.Slash => 47, ImGuiKey.Semicolon => 59,
            ImGuiKey.Equal => 61, ImGuiKey.LeftBracket => 91, ImGuiKey.Backslash => 92,
            ImGuiKey.RightBracket => 93, ImGuiKey.GraveAccent => 96, ImGuiKey.CapsLock => 301,
            ImGuiKey.ScrollLock => 302, ImGuiKey.NumLock => 300, ImGuiKey.PrintScreen => 316,
            ImGuiKey.Pause => 19, ImGuiKey.KeypadDecimal => 266, ImGuiKey.KeypadDivide => 267,
            ImGuiKey.KeypadMultiply => 268, ImGuiKey.KeypadSubtract => 269,
            ImGuiKey.KeypadAdd => 270, ImGuiKey.KeypadEnter => 271, ImGuiKey.KeypadEqual => 272,
            _ => 0,
        };
        return keyCode != 0;
    }

    internal static string GetBindingName(ImGuiKey key)
        => key == ImGuiKey.None || key is ImGuiKey.NamedKey_END or ImGuiKey.NamedKey_COUNT
            ? string.Empty
            : key == ImGuiKey.NamedKey_BEGIN ? "Tab" : key.ToString();

    internal static IEnumerable<ImGuiKey> CapturableKeys()
    {
        for (ImGuiKey key = ImGuiKey.NamedKey_BEGIN; key < ImGuiKey.NamedKey_END; key++)
        {
            if (key is ImGuiKey.ModCtrl or ImGuiKey.ModShift or ImGuiKey.ModAlt or ImGuiKey.ModSuper)
                continue;
            if (key >= ImGuiKey.MouseLeft && key <= ImGuiKey.MouseWheelY) continue;
            if (key >= ImGuiKey.GamepadStart && key <= ImGuiKey.GamepadRStickDown) continue;
            yield return key;
        }
    }

    private static ImGuiKey ParseRequired(string name)
        => TryParse(name, out ImGuiKey key) ? key : ImGuiKey.None;
}
