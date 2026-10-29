using System.Diagnostics;
using System.Text;

namespace TeslaV100Controller;

internal sealed record CommandResult(int ExitCode, string Output);
internal static class Commands
{
    public static CommandResult Run(string file, IEnumerable<string> arguments, int timeoutSeconds = 90)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException($"Cannot start {file}.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill(true);
            process.WaitForExit();
            throw new TimeoutException($"{Path.GetFileName(file)} exceeded {timeoutSeconds}s; device state must be rechecked.");
        }
        return new(process.ExitCode, stdout.GetAwaiter().GetResult().Trim() + "\n" + stderr.GetAwaiter().GetResult().Trim());
    }
    public static CommandResult PowerShell(string script, int timeoutSeconds = 90) => Run(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';\n" + script))], timeoutSeconds);
    public static CommandResult Pnp(string action, string id) => Run(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe"), [action, id], 120);
}
