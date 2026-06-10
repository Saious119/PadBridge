namespace PadBridge.Gui.Evdev;

/// <summary>A /dev/input/event* device and its EV_KEY capabilities.</summary>
public sealed class InputDeviceInfo
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<int> SupportedKeyCodes { get; init; }

    /// <summary>Looks like a gamepad/joystick: exposes codes in the BTN_JOYSTICK..BTN_TRIGGER_HAPPY ranges.</summary>
    public bool IsGamepad => SupportedKeyCodes.Any(c =>
        (c >= 0x120 && c <= 0x13e) || (c >= 0x2c0 && c <= 0x2e7));

    public override string ToString() => Name;

    public static List<InputDeviceInfo> Enumerate()
    {
        var devices = new List<InputDeviceInfo>();
        foreach (var path in Directory.GetFiles("/dev/input", "event*")
                     .OrderBy(p => int.TryParse(p.AsSpan("/dev/input/event".Length), out var n) ? n : 0))
        {
            var fd = Libc.Open(path, Libc.O_RDONLY | Libc.O_NONBLOCK);
            if (fd < 0) continue;
            try
            {
                var nameBuf = new byte[256];
                Libc.Ioctl(fd, Libc.EVIOCGNAME(nameBuf.Length), nameBuf);
                var name = System.Text.Encoding.UTF8.GetString(nameBuf)
                    .TrimEnd('\0');

                var typeBits = new byte[4];
                Libc.Ioctl(fd, Libc.EVIOCGBIT(0, typeBits.Length), typeBits);
                bool hasKeys = (typeBits[0] & (1 << Libc.EV_KEY)) != 0;
                if (!hasKeys) continue;

                var keyBits = new byte[Libc.KEY_MAX / 8 + 1];
                Libc.Ioctl(fd, Libc.EVIOCGBIT(Libc.EV_KEY, keyBits.Length), keyBits);
                var codes = new List<int>();
                for (int code = 0; code <= Libc.KEY_MAX; code++)
                    if ((keyBits[code / 8] & (1 << (code % 8))) != 0)
                        codes.Add(code);

                devices.Add(new InputDeviceInfo
                {
                    Path = path,
                    Name = name,
                    SupportedKeyCodes = codes,
                });
            }
            finally
            {
                Libc.Close(fd);
            }
        }
        return devices;
    }
}
