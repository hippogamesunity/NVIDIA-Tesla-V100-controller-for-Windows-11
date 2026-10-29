using System.Diagnostics;

namespace TeslaV100Controller;

internal sealed class Recovery(IGpuController gpu, SettingsStore store, Log log)
{
    public void Startup()
    {
        Settings settings = store.Load();
        var timer = Stopwatch.StartNew();
        var policy = new StartupPolicy(TimeSpan.FromSeconds(settings.StartupTimeoutSeconds));
        string previous = "<initial>";
        log.Write($"Startup: waiting up to {settings.StartupTimeoutSeconds}s for V100 and its NVIDIA driver.");
        while (true)
        {
            var observations = gpu.Enumerate().Select(device => new DeviceObservation(device,
                device.NvidiaBound && device.ProblemCode == 0 && device.Started && gpu.CurrentMode(device) is not null)).ToArray();
            string state = string.Join("; ", observations.Select(x =>
                $"{x.Device.Name}: code={x.Device.ProblemCode}, started={x.Device.Started}, NVIDIA={x.Device.NvidiaBound}, SMI={x.SmiReady}"));
            if (state != previous) { log.Write(state.Length == 0 ? "GPU not enumerated yet." : state); previous = state; }
            switch (policy.Evaluate(timer.Elapsed, observations))
            {
                case StartupDecision.Ready:
                    // PnP code 0 alone is insufficient: we have also verified nvidia-smi.
                    settings.RecoveryPending = false;
                    settings.LastError = null;
                    store.Save(settings);
                    foreach (var observation in observations) gpu.ApplyMode(observation.Device, settings.DesiredMode);
                    log.Write("Startup check completed successfully.");
                    return;
                case StartupDecision.RecoverCode10:
                    RestartAfterRemoval(settings, manual: false);
                    return;
                case StartupDecision.Timeout:
                    throw new TimeoutException("Timed out waiting for a usable NVIDIA driver. No GPU was removed and Windows was not restarted.");
            }
            Thread.Sleep(2000);
        }
    }

    public void RestartAfterRemoval(Settings settings, bool manual)
    {
        if (!gpu.Enumerate().Any(x => x.ProblemCode == 10))
        {
            log.Write("Code 10 no longer present; recovery restart cancelled.");
            return;
        }
        if (settings.RecoveryPending && !manual)
            throw new InvalidOperationException("Code 10 remains after the previous recovery. Reboot loop prevented; inspect the log and recover manually.");
        settings.RecoveryPending = true;
        settings.LastRecovery = DateTimeOffset.UtcNow;
        store.Save(settings);
        gpu.RemoveDevices();
        log.Write("V100 device removed. Requesting Windows restart for re-enumeration.");
        gpu.RestartWindows();
    }
}

internal sealed class OperationLock : IDisposable
{
    private readonly Mutex mutex = new(false, @"Global\TeslaV100Controller-Maintenance");
    private bool acquired;
    public OperationLock()
    {
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { mutex.Dispose(); throw new IOException("Another V100 operation is running. Wait for it to finish."); }
    }
    public void Dispose() { if (acquired) mutex.ReleaseMutex(); acquired = false; mutex.Dispose(); }
}
