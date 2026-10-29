using System.Diagnostics;
using Microsoft.Win32;

namespace TeslaV100Controller;

internal interface IGpuController
{
    IReadOnlyList<GpuDevice> Enumerate();
    DriverMode? CurrentMode(GpuDevice device);
    void ApplyMode(GpuDevice device, DriverMode desired);
    void RemoveDevices();
    void RestartWindows();
}

internal sealed class GpuController(Log log) : IGpuController
{
    public IReadOnlyList<GpuDevice> Enumerate() => DeviceApi.Enumerate();
    private static string? SmiPath => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvidia-smi.exe")
    }.FirstOrDefault(File.Exists);

    public DriverMode? CurrentMode(GpuDevice device)
    {
        if (SmiPath is not { } smi || string.IsNullOrEmpty(device.BusId)) return null;
        try
        {
            var result = Commands.Run(smi, ["-i", device.BusId, "--query-gpu=driver_model.current", "--format=csv,noheader,nounits"], 8);
            if (result.ExitCode != 0) return null;
            string mode = result.Output.Trim();
            if (mode.Equals("WDDM", StringComparison.OrdinalIgnoreCase)) return DriverMode.Wddm;
            if (mode.Equals("TCC", StringComparison.OrdinalIgnoreCase)) return DriverMode.Tcc;
        }
        catch (TimeoutException) { }
        return null;
    }

    public void ApplyMode(GpuDevice device, DriverMode desired)
    {
        if (desired == DriverMode.Unchanged || CurrentMode(device) == desired) return;
        if (!device.NvidiaBound || device.ProblemCode != 0 || !device.Started)
            throw new InvalidOperationException("Wait for a working NVIDIA driver before changing mode.");
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + device.DriverKey, true)
                        ?? throw new IOException("GPU driver registry key not found.");
        if (!(key.GetValue("DriverDesc") as string ?? "").Contains("V100", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Driver key no longer belongs to V100; operation cancelled.");

        // Empirically verified WDDM workaround on a bare-metal SXM2 V100 with 582.99.
        // No nvidia-smi -dm fallback: that driver returns Not Supported with exit code 0.
        key.SetValue("AdapterType", desired == DriverMode.Wddm ? 1 : 2, RegistryValueKind.DWord);
        if (desired == DriverMode.Wddm) key.SetValue("GridLicensedFeatures", 7, RegistryValueKind.DWord);
        else key.DeleteValue("GridLicensedFeatures", false);
        log.Write($"Applying {desired} to {device.InstanceId}; restarting this GPU only.");
        var restart = Commands.Pnp("/restart-device", device.InstanceId);
        log.Write(restart.Output);
        if (restart.ExitCode is not (0 or 3010)) throw new IOException($"GPU restart failed: {restart.ExitCode}.");

        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(90))
        {
            var current = DeviceApi.Enumerate().FirstOrDefault(x => x.InstanceId.Equals(device.InstanceId, StringComparison.OrdinalIgnoreCase));
            if (current is { ProblemCode: 0, Started: true } && CurrentMode(current) == desired)
            {
                log.Write($"Verified: {current.Name} is working in {desired}.");
                return;
            }
            Thread.Sleep(2000);
        }
        throw new IOException($"Mode was requested, but a working {desired} GPU was not verified. Windows was not rebooted.");
    }

    public void RemoveDevices()
    {
        var devices = DeviceApi.Enumerate();
        if (devices.Count == 0) { log.Write("No matching V100 SXM2 present; nothing to remove."); return; }
        CloseLmStudio();
        foreach (var device in devices)
        {
            log.Write($"Removing device only, keeping the driver package: {device.InstanceId}");
            var result = Commands.Pnp("/remove-device", device.InstanceId);
            log.Write(result.Output);
            if (result.ExitCode is not (0 or 3010)) throw new IOException($"Device removal failed: {result.ExitCode}. Restart cancelled.");
        }
    }

    private static void CloseLmStudio()
    {
        // Same bounded, path-restricted cleanup as the original desktop script.
        var result = Commands.PowerShell("""
            $all=@(Get-CimInstance Win32_Process)
            $selected=@{}
            foreach($p in $all){
              if(($p.Name -ieq 'LM Studio.exe' -and $p.ExecutablePath -match '(?i)\\LM Studio\\LM Studio\.exe$') -or
                 ($p.ExecutablePath -match '(?i)\\\.lmstudio\\' -and $p.Name -match '^(?i:llama-server|llmster|lms)\.exe$')){
                $selected[[int]$p.ProcessId]=$p
              }
            }
            do {
              $added=$false
              foreach($p in $all){
                if(!$selected.ContainsKey([int]$p.ProcessId) -and $selected.ContainsKey([int]$p.ParentProcessId) -and
                   $p.CreationDate -ge $selected[[int]$p.ParentProcessId].CreationDate){
                  $selected[[int]$p.ProcessId]=$p; $added=$true
                }
              }
            }while($added)
            foreach($p in $selected.Values){
              $live=Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
              if($live -and $live.MainWindowHandle -ne 0){[void]$live.CloseMainWindow()}
            }
            if($selected.Count){Start-Sleep -Seconds 2}
            foreach($p in $selected.Values){
              $live=Get-CimInstance Win32_Process -Filter ('ProcessId={0}' -f $p.ProcessId)
              if($live -and $live.CreationDate -eq $p.CreationDate){Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop}
            }
            """);
        if (result.ExitCode != 0) throw new IOException("LM Studio could not be closed. GPU removal cancelled. " + result.Output);
    }

    public void RestartWindows()
    {
        var result = Commands.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"), ["/r", "/t", "0"]);
        if (result.ExitCode != 0) throw new IOException("Windows restart request failed: " + result.Output);
    }
}
