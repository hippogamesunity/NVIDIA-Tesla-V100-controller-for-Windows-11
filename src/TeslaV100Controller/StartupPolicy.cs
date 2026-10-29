namespace TeslaV100Controller;

internal enum StartupDecision { Wait, Ready, RecoverCode10, Timeout }
internal sealed record DeviceObservation(GpuDevice Device, bool SmiReady);

// No device mutations here: this decision logic is tested without touching a GPU.
internal sealed class StartupPolicy(TimeSpan timeout, TimeSpan? code10Delay = null)
{
    private readonly Dictionary<string, TimeSpan> errorsSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan stableError = code10Delay ?? TimeSpan.FromSeconds(15);

    public StartupDecision Evaluate(TimeSpan elapsed, IReadOnlyList<DeviceObservation> observations)
    {
        var present = observations.Select(x => x.Device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in errorsSince.Keys.ToArray())
            if (!present.Contains(id)) errorsSince.Remove(id);

        bool recoveryNeeded = false;
        foreach (var observation in observations)
        {
            var device = observation.Device;
            if (device.NvidiaBound && device.ProblemCode == 10)
            {
                if (!errorsSince.ContainsKey(device.InstanceId)) errorsSince[device.InstanceId] = elapsed;
                if (elapsed - errorsSince[device.InstanceId] >= stableError) recoveryNeeded = true;
            }
            else errorsSince.Remove(device.InstanceId);
        }

        // Do not reboot while another GPU is still waiting for its driver.
        bool settled = observations.Count > 0 && observations.All(x => x.Device.NvidiaBound &&
            (x.Device.ProblemCode == 10 || (x.Device.ProblemCode == 0 && x.Device.Started && x.SmiReady)));
        if (recoveryNeeded && settled) return StartupDecision.RecoverCode10;
        if (observations.Count > 0 && observations.All(x => x.Device.NvidiaBound &&
            x.Device.ProblemCode == 0 && x.Device.Started && x.SmiReady)) return StartupDecision.Ready;
        return elapsed >= timeout ? StartupDecision.Timeout : StartupDecision.Wait;
    }
}
