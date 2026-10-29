using System.Text;
using TeslaV100Controller;

var tests = new List<(string, Action)>();
const string id = @"PCI\VEN_10DE&DEV_1DB1&SUBSYS_121210DE&REV_A1\4&EXAMPLE";
GpuDevice Device(int code = 0, bool bound = true, bool started = true, string name = "V100") =>
    new(id, name, code, started, bound ? @"{display}\0003" : "", bound ? "nvlddmkm" : "", "00000000:01:00.0");
DeviceObservation Observe(int code = 0, bool bound = true, bool smi = true, bool started = true) =>
    new(Device(code, bound, started), smi);
StartupPolicy Policy() => new(TimeSpan.FromSeconds(300));
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action action) => tests.Add((name, action));

Test("Hardware identity works with the temporary generic controller name", () =>
{
    Check(GpuDevice.IsTarget(id), "16GB SXM2 excluded");
    Check(GpuDevice.IsTarget(id.Replace("1DB1", "1DB5")), "32GB SXM2 excluded");
    Check(!GpuDevice.IsTarget(id.Replace("1DB1", "15F8")), "P100 included");
    Check(!GpuDevice.IsTarget(id.Replace("1DB1", "1DB4")), "Native PCIe V100 included");
    Check(!GpuDevice.IsTarget(id.Replace("PCI", "USB")), "Non-PCI device included");
});
Test("Absent GPU waits rather than rebooting", () => Equal(StartupDecision.Wait, Policy().Evaluate(TimeSpan.Zero, [])));
Test("Generic 3D controller waits for NVIDIA driver binding", () =>
    Equal(StartupDecision.Wait, Policy().Evaluate(TimeSpan.FromSeconds(60), [Observe(28, false, false, false)])));
Test("Code 0 without NVML readiness is not considered initialized", () =>
    Equal(StartupDecision.Wait, Policy().Evaluate(TimeSpan.FromSeconds(60), [Observe(smi: false)])));
Test("NVIDIA binding and NVML are insufficient if the PnP device is not started", () =>
    Equal(StartupDecision.Wait, Policy().Evaluate(TimeSpan.FromSeconds(60), [Observe(started: false)])));
Test("Slow enumeration resolves without recovery", () =>
{
    var policy = Policy();
    Equal(StartupDecision.Wait, policy.Evaluate(TimeSpan.Zero, []));
    Equal(StartupDecision.Wait, policy.Evaluate(TimeSpan.FromSeconds(90), [Observe(28, false, false)]));
    Equal(StartupDecision.Wait, policy.Evaluate(TimeSpan.FromSeconds(120), [Observe(smi: false)]));
    Equal(StartupDecision.Ready, policy.Evaluate(TimeSpan.FromSeconds(150), [Observe()]));
});
Test("Persistent Code 10 must settle for fifteen seconds", () =>
{
    var policy = Policy();
    Equal(StartupDecision.Wait, policy.Evaluate(TimeSpan.FromSeconds(20), [Observe(10, smi: false)]));
    Equal(StartupDecision.Wait, policy.Evaluate(TimeSpan.FromSeconds(34), [Observe(10, smi: false)]));
    Equal(StartupDecision.RecoverCode10, policy.Evaluate(TimeSpan.FromSeconds(35), [Observe(10, smi: false)]));
});
Test("Transient Code 10 clears its stabilization timer", () =>
{
    var policy = Policy();
    policy.Evaluate(TimeSpan.Zero, [Observe(10, smi: false)]);
    Equal(StartupDecision.Ready, policy.Evaluate(TimeSpan.FromSeconds(10), [Observe()]));
    Equal(StartupDecision.Wait, policy.Evaluate(TimeSpan.FromSeconds(20), [Observe(10, smi: false)]));
    Equal(StartupDecision.RecoverCode10, policy.Evaluate(TimeSpan.FromSeconds(35), [Observe(10, smi: false)]));
});
Test("A disappearing GPU resets Code 10 stabilization", () =>
{
    var policy = Policy(); policy.Evaluate(TimeSpan.Zero, [Observe(10, smi: false)]);
    policy.Evaluate(TimeSpan.FromSeconds(14), []);
    Equal(StartupDecision.Wait, policy.Evaluate(TimeSpan.FromSeconds(16), [Observe(10, smi: false)]));
});
Test("Code 10 before NVIDIA binding never requests a reboot", () =>
{
    var policy = Policy(); policy.Evaluate(TimeSpan.Zero, [Observe(10, false, false)]);
    Equal(StartupDecision.Timeout, policy.Evaluate(TimeSpan.FromSeconds(300), [Observe(10, false, false)]));
});
Test("Other PnP errors do not request automatic recovery", () =>
    Equal(StartupDecision.Timeout, Policy().Evaluate(TimeSpan.FromSeconds(300), [Observe(43, smi: false)])));
Test("Pending NVML initialization times out without requesting a reboot", () =>
    Equal(StartupDecision.Timeout, Policy().Evaluate(TimeSpan.FromSeconds(300), [Observe(smi: false)])));
Test("Recovery waits when a second GPU is still loading its driver", () =>
{
    var policy = Policy();
    var other = new DeviceObservation(Device(28, false) with { InstanceId = id + "2" }, false);
    policy.Evaluate(TimeSpan.Zero, [Observe(10, smi: false), other]);
    Equal(StartupDecision.Wait, policy.Evaluate(TimeSpan.FromSeconds(30), [Observe(10, smi: false), other]));
});

const string ownedExe = @"C:\Program Files\TeslaV100Controller\TeslaV100Controller.exe";
bool Own(string exe, string args) => exe == ownedExe && args == "--shutdown";
Test("Policy install preserves foreign scripts and startup sections", () =>
{
    string before = "[Startup]\r\n0CmdLine=other-start.cmd\r\n0Parameters=\r\n[Shutdown]\r\n0CmdLine=other-stop.cmd\r\n0Parameters=keep\r\n";
    string after = PolicyDocument.Update(before, Own, ownedExe, "--shutdown");
    Check(after.Contains("0Parameters=keep"), "Foreign shutdown script changed");
    Check(after.Contains("[Startup]\r\n0CmdLine=other-start.cmd"), "Startup section changed");
    Check(after.Contains("1CmdLine=" + ownedExe), "Occupied index was overwritten");
});
Test("Repeated policy installation does not duplicate entries", () =>
{
    string first = PolicyDocument.Update("", Own, ownedExe, "--shutdown");
    string second = PolicyDocument.Update(first, Own, ownedExe, "--shutdown");
    Equal(1, second.Split(ownedExe).Length - 1);
});
Test("Uninstall preserves foreign entries and removes the owned entry", () =>
{
    string before = "[Shutdown]\r\n0CmdLine=foreign.cmd\r\n0Parameters=keep\r\n";
    string installed = PolicyDocument.Update(before, Own, ownedExe, "--shutdown");
    string removed = PolicyDocument.Update(installed, Own, null, "--shutdown");
    Check(!removed.Contains(ownedExe), "Owned script remains");
    Check(removed.Contains("0CmdLine=foreign.cmd\r\n0Parameters=keep"), "Foreign entry was removed");
});
Test("Uninstall does not own another argument of the same executable", () =>
{
    string before = $"[Shutdown]\r\n0CmdLine={ownedExe}\r\n0Parameters=--other-job\r\n";
    Equal(before, PolicyDocument.Update(before, Own, null, "--shutdown"));
});
Test("Legacy migration recognizes the real encoded desktop workaround", () =>
{
    string code = "$env:V100_FIX_PATH='C:\\Users\\Alice\\Desktop\\Tesla V100 SXM2 4090 Driver Fix.cmd'; & ([scriptblock]::Create([IO.File]::ReadAllText($env:V100_FIX_PATH))) -Shutdown";
    string args = "-NoProfile -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(code));
    Check(Legacy.IsOwned(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", args), "Legacy policy not recognized");
    Check(!Legacy.IsOwned(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", args.Replace("-EncodedCommand", "-Other")), "Unrelated command recognized");
    Check(!Legacy.IsOwned("powershell.exe", "-File C:\\Other\\shutdown.ps1 -Shutdown"), "Foreign PowerShell script recognized");
});
Test("Settings preserve desired mode and reboot guard across process restarts", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "v100-tests-" + Guid.NewGuid());
    string path = Path.Combine(directory, "settings.json");
    try
    {
        new SettingsStore(path).Save(new() { DesiredMode = DriverMode.Wddm, RecoveryPending = true });
        var settings = new SettingsStore(path).Load();
        Equal(DriverMode.Wddm, settings.DesiredMode); Equal(true, settings.RecoveryPending);
        File.WriteAllText(path, "{\"StartupTimeoutSeconds\":0}");
        bool rejected = false;
        try { new SettingsStore(path).Load(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Invalid timeout accepted");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
Test("Windows Task Scheduler accepts the boot task XML without registering it", StartupTask.ValidateDefinition);
Test("A missing scheduled task is an uninstalled state, not a fatal error", () =>
    Equal<string?>(null, StartupTask.Xml("TeslaV100Controller-Tests-Missing-" + Guid.NewGuid())));
void WithStore(Action<SettingsStore> action)
{
    string directory = Path.Combine(Path.GetTempPath(), "v100-recovery-tests-" + Guid.NewGuid());
    try { action(new SettingsStore(Path.Combine(directory, "settings.json"))); }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
Test("Reboot guard is durable before device removal and blocks a second automatic reboot", () => WithStore(store =>
{
    var fake = new FakeGpu { Devices = [Device(10)] };
    fake.OnRemove = () => Check(store.Load().RecoveryPending, "Removal ran before saving the reboot guard");
    new Recovery(fake, store, new Log(_ => { })).RestartAfterRemoval(new(), false);
    Equal(1, fake.Removes); Equal(1, fake.Restarts);
    bool blocked = false;
    try { new Recovery(fake, store, new Log(_ => { })).RestartAfterRemoval(store.Load(), false); }
    catch (InvalidOperationException) { blocked = true; }
    Check(blocked, "Repeated automatic recovery was not blocked");
    Equal(1, fake.Restarts);
}));
Test("Failed device removal never restarts Windows", () => WithStore(store =>
{
    var fake = new FakeGpu { Devices = [Device(10)], OnRemove = () => throw new IOException("Removal failed") };
    bool failed = false;
    try { new Recovery(fake, store, new Log(_ => { })).RestartAfterRemoval(new(), false); }
    catch (IOException) { failed = true; }
    Check(failed, "Removal error was swallowed");
    Equal(0, fake.Restarts); Equal(true, store.Load().RecoveryPending);
}));
Test("Disappearing Code 10 cancels a previously planned restart", () => WithStore(store =>
{
    var fake = new FakeGpu { Devices = [Device()] };
    new Recovery(fake, store, new Log(_ => { })).RestartAfterRemoval(new(), false);
    Equal(0, fake.Removes); Equal(0, fake.Restarts);
}));
Test("Healthy startup restores saved WDDM and clears the previous recovery guard", () => WithStore(store =>
{
    store.Save(new() { DesiredMode = DriverMode.Wddm, RecoveryPending = true });
    var fake = new FakeGpu { Devices = [Device()], Mode = DriverMode.Tcc };
    new Recovery(fake, store, new Log(_ => { })).Startup();
    Equal(DriverMode.Wddm, fake.AppliedMode); Equal(false, store.Load().RecoveryPending);
    Equal(0, fake.Removes); Equal(0, fake.Restarts);
}));
Test("Explicit manual recovery can retry after the automatic guard fired", () => WithStore(store =>
{
    var fake = new FakeGpu { Devices = [Device(10)] };
    store.Save(new() { RecoveryPending = true });
    new Recovery(fake, store, new Log(_ => { })).RestartAfterRemoval(store.Load(), true);
    Equal(1, fake.Restarts);
}));
Test("Native enumeration is read-only and returns only targeted SXM2 devices", () =>
{
    foreach (var device in DeviceApi.Enumerate())
    {
        Check(GpuDevice.IsTarget(device.InstanceId), "Unexpected device enumerated");
        Console.WriteLine($"  Detected {device.Name}: bus={device.BusId}, code={device.ProblemCode}, NVIDIA={device.NvidiaBound}, started={device.Started}");
        Check(device.BusId.StartsWith("00000000:"), "PCI bus address unavailable");
    }
});

int failures = 0;
foreach (var (name, action) in tests)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} checks passed. No devices, policies or tasks were changed.");
return failures == 0 ? 0 : 1;

internal sealed class FakeGpu : IGpuController
{
    public IReadOnlyList<GpuDevice> Devices { get; set; } = [];
    public DriverMode? Mode { get; set; }
    public DriverMode AppliedMode { get; private set; }
    public int Removes { get; private set; }
    public int Restarts { get; private set; }
    public Action? OnRemove { get; set; }
    public IReadOnlyList<GpuDevice> Enumerate() => Devices;
    public DriverMode? CurrentMode(GpuDevice device) => Mode;
    public void ApplyMode(GpuDevice device, DriverMode desired) => AppliedMode = desired;
    public void RemoveDevices() { OnRemove?.Invoke(); Removes++; }
    public void RestartWindows() => Restarts++;
}
