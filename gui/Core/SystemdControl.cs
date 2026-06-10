using System.Diagnostics;

namespace PadBridge.Gui.Core;

/// <summary>Controls the padbridge user service via systemctl --user.</summary>
public static class SystemdControl
{
    public const string Unit = "padbridge.service";

    public static Task StartAsync() => RunAsync("start");
    public static Task StopAsync() => RunAsync("stop");

    /// <summary>Returns systemd's state string: active, inactive, failed, ...</summary>
    public static async Task<string> StatusAsync()
    {
        var (_, stdout) = await ExecAsync("is-active", Unit);
        var state = stdout.Trim();
        return state.Length == 0 ? "unknown" : state;
    }

    private static async Task RunAsync(string verb) => await ExecAsync(verb, Unit);

    private static async Task<(int ExitCode, string Stdout)> ExecAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("systemctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--user");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout);
    }
}
