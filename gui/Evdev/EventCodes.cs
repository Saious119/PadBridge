namespace PadBridge.Gui.Evdev;

public static partial class EventCodes
{
    // Lazy: static initializer order across partial-class files is undefined,
    // and this table depends on the generated NameToCode dictionary.
    private static Dictionary<int, string>? _friendly;
    private static Dictionary<int, string> Friendly => _friendly ??= new()
    {
        [NameToCode["BTN_SOUTH"]] = "South (A)",
        [NameToCode["BTN_EAST"]] = "East (B)",
        [NameToCode["BTN_NORTH"]] = "North (X)",
        [NameToCode["BTN_WEST"]] = "West (Y)",
        [NameToCode["BTN_TL"]] = "LB",
        [NameToCode["BTN_TR"]] = "RB",
        [NameToCode["BTN_TL2"]] = "LT",
        [NameToCode["BTN_TR2"]] = "RT",
        [NameToCode["BTN_SELECT"]] = "Select",
        [NameToCode["BTN_START"]] = "Start",
        [NameToCode["BTN_MODE"]] = "Guide",
        [NameToCode["BTN_THUMBL"]] = "L-Stick Click",
        [NameToCode["BTN_THUMBR"]] = "R-Stick Click",
        [NameToCode["KEY_LEFTBRACE"]] = "[",
        [NameToCode["KEY_RIGHTBRACE"]] = "]",
        [NameToCode["KEY_SEMICOLON"]] = ";",
        [NameToCode["KEY_APOSTROPHE"]] = "'",
        [NameToCode["KEY_COMMA"]] = ",",
        [NameToCode["KEY_DOT"]] = ".",
        [NameToCode["KEY_SLASH"]] = "/",
        [NameToCode["KEY_BACKSLASH"]] = "\\",
        [NameToCode["KEY_MINUS"]] = "-",
        [NameToCode["KEY_EQUAL"]] = "=",
        [NameToCode["KEY_GRAVE"]] = "`",
        [NameToCode["KEY_LEFTSHIFT"]] = "Left Shift",
        [NameToCode["KEY_RIGHTSHIFT"]] = "Right Shift",
        [NameToCode["KEY_LEFTCTRL"]] = "Left Ctrl",
        [NameToCode["KEY_RIGHTCTRL"]] = "Right Ctrl",
        [NameToCode["KEY_LEFTALT"]] = "Left Alt",
        [NameToCode["KEY_RIGHTALT"]] = "Right Alt",
        [NameToCode["KEY_LEFTMETA"]] = "Left Meta",
        [NameToCode["KEY_RIGHTMETA"]] = "Right Meta",
    };

    /// <summary>Short human label for a code, e.g. "F13", ";", "South (A)".</summary>
    public static string FriendlyName(int code)
    {
        if (Friendly.TryGetValue(code, out var f)) return f;
        if (!CodeToName.TryGetValue(code, out var name)) return $"0x{code:X}";

        var bare = name.StartsWith("KEY_") ? name[4..] :
                   name.StartsWith("BTN_") ? name[4..] : name;
        if (bare.Length == 1) return bare;                       // letters, digits
        if (name.StartsWith("KEY_F") && bare.Length <= 3 &&
            int.TryParse(bare.AsSpan(1), out _)) return bare;    // F1..F24
        return bare.Length > 1
            ? char.ToUpperInvariant(bare[0]) + bare[1..].ToLowerInvariant().Replace('_', ' ')
            : bare;
    }

    /// <summary>Full label including the raw evdev name, e.g. "BTN_TRIGGER_HAPPY1".</summary>
    public static string CanonicalName(int code) =>
        CodeToName.TryGetValue(code, out var name) ? name : $"0x{code:X}";
}
