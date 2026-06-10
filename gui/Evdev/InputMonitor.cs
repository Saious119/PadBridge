namespace PadBridge.Gui.Evdev;

/// <summary>
/// Watches a set of evdev devices on a background thread and raises
/// KeyEvent for every EV_KEY event (press=1, release=0, repeat=2).
/// </summary>
public sealed class InputMonitor : IDisposable
{
    public record struct Key(string DevicePath, string DeviceName, int Code, int Value);

    public event Action<Key>? KeyEvent;

    private Thread? _thread;
    private volatile bool _running;
    private List<InputDeviceInfo> _devices = new();

    public void Start(IEnumerable<InputDeviceInfo> devices)
    {
        Stop();
        _devices = devices.ToList();
        if (_devices.Count == 0) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "evdev-monitor" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join();
        _thread = null;
    }

    private void Run()
    {
        var fds = new List<(int Fd, InputDeviceInfo Dev)>();
        foreach (var dev in _devices)
        {
            var fd = Libc.Open(dev.Path, Libc.O_RDONLY | Libc.O_NONBLOCK);
            if (fd >= 0) fds.Add((fd, dev));
        }

        var buf = new byte[Libc.InputEventSize];
        try
        {
            while (_running && fds.Count > 0)
            {
                var pollFds = fds.Select(f => new Libc.PollFd { Fd = f.Fd, Events = Libc.POLLIN }).ToArray();
                var r = Libc.Poll(pollFds, (nuint)pollFds.Length, 200);
                if (r < 0) break;
                if (r == 0) continue;

                for (int i = fds.Count - 1; i >= 0; i--)
                {
                    var revents = pollFds[i].REvents;
                    if ((revents & (Libc.POLLERR | Libc.POLLHUP)) != 0)
                    {
                        Libc.Close(fds[i].Fd);
                        fds.RemoveAt(i);
                        continue;
                    }
                    if ((revents & Libc.POLLIN) == 0) continue;

                    while (Libc.Read(fds[i].Fd, buf, buf.Length) == buf.Length)
                    {
                        var type = BitConverter.ToUInt16(buf, Libc.EventTypeOffset);
                        if (type != Libc.EV_KEY) continue;
                        var code = BitConverter.ToUInt16(buf, Libc.EventCodeOffset);
                        var value = BitConverter.ToInt32(buf, Libc.EventValueOffset);
                        KeyEvent?.Invoke(new Key(fds[i].Dev.Path, fds[i].Dev.Name, code, value));
                    }
                }
            }
        }
        finally
        {
            foreach (var (fd, _) in fds) Libc.Close(fd);
        }
    }

    public void Dispose() => Stop();
}
