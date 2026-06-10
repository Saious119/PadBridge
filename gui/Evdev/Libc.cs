using System.Runtime.InteropServices;

namespace PadBridge.Gui.Evdev;

internal static class Libc
{
    internal const int O_RDONLY = 0x0;
    internal const int O_NONBLOCK = 0x800;
    internal const short POLLIN = 0x1;
    internal const short POLLERR = 0x8;
    internal const short POLLHUP = 0x10;

    // sizeof(struct input_event) on 64-bit: timeval(16) + type(2) + code(2) + value(4)
    internal const int InputEventSize = 24;
    internal const int EventTypeOffset = 16;
    internal const int EventCodeOffset = 18;
    internal const int EventValueOffset = 20;

    internal const int EV_KEY = 0x01;
    internal const int EV_REL = 0x02;
    internal const int KEY_MAX = 0x2ff;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short REvents;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    internal static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "close")]
    internal static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    internal static extern nint Read(int fd, byte[] buf, nint count);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static extern int Ioctl(int fd, nuint request, byte[] buf);

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    internal static extern int Poll([In, Out] PollFd[] fds, nuint nfds, int timeout);

    // _IOC(_IOC_READ, 'E', nr, size) — Linux ioctl request encoding on x86_64/arm64
    private static nuint Ioc(uint nr, uint size) =>
        (nuint)((2u << 30) | (size << 16) | ((uint)'E' << 8) | nr);

    internal static nuint EVIOCGNAME(int len) => Ioc(0x06, (uint)len);
    internal static nuint EVIOCGBIT(int ev, int len) => Ioc((uint)(0x20 + ev), (uint)len);
}
