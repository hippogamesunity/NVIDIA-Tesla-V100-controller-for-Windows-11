using System.Security.Principal;

namespace TeslaV100Controller;

internal static class Program
{
    private static readonly Log Log = new();
    private static readonly SettingsStore Store = new(AppPaths.SettingsFile);
    private static readonly GpuController Gpu = new(Log);
    private static readonly Integration Integration = new(Log);
    private static readonly Recovery Recovery = new(Gpu, Store, Log);

    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows 11 is required."); return 1; }
        Console.Title = "Tesla V100 SXM2 Controller";
        if (args.Length == 1 && args[0] is "--help" or "-h") { Help(); return 0; }
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine("Administrator access is required. Launch the published EXE and accept UAC.");
            return 1;
        }
        try
        {
            if (args.Length == 0) return Menu();
            if (args is ["--status"]) { Status(); return 0; }
            using var operation = new OperationLock();
            switch (args)
            {
                case ["--install"]:
                    Integration.Install(); Store.Save(Store.Load()); return 0;
                case ["--uninstall"]:
                    Integration.Uninstall(); return 0;
                case ["--reset"]:
                    Reset(); return 0;
                case ["--startup"]:
                    Recovery.Startup(); return 0;
                case ["--shutdown"]:
                    Gpu.RemoveDevices(); return 0;
                case ["--mode", var mode]:
                    ChangeMode(ParseMode(mode), apply: true); return 0;
                default:
                    Help(); return 2;
            }
        }
        catch (Exception ex)
        {
            if (args is ["--status"]) Console.Error.WriteLine(ex.Message);
            else Report(ex);
            if (args.Length == 0 && !Console.IsInputRedirected) Pause();
            return 1;
        }
    }

    private static int Menu()
    {
        while (true)
        {
            if (!Console.IsOutputRedirected) Console.Clear();
            Console.WriteLine("ABOUT");
            Console.WriteLine("Some SXM2 adapters fail with Code 10 after a Windows restart.");
            Console.WriteLine("The fix removes the GPU device at shutdown; Windows redetects it on boot.");
            Console.WriteLine("Startup waits for the driver, checks Code 10 and restores the selected mode.");
            Console.WriteLine("GitHub: https://github.com/hippogamesunity/NVIDIA-Tesla-V100-controller-for-Windows-11\n");
            var modes = Status();
            var options = new List<(string Name, Action? Run)>();
            if (Integration.Status != "Installed") options.Add(("Install fix", InstallInteractive));
            if (modes.Count == 0 || !modes.All(x => x == DriverMode.Tcc))
                options.Add(("Switch mode to TCC", () => SwitchModeInteractive(DriverMode.Tcc)));
            if (modes.Count == 0 || !modes.All(x => x == DriverMode.Wddm))
                options.Add(("Switch mode to WDDM", () => SwitchModeInteractive(DriverMode.Wddm)));
            options.Add(("Reset", ResetInteractive));
            options.Add(("Exit", null));
            Console.WriteLine("\nOPTIONS");
            for (int i = 0; i < options.Count; i++) Console.WriteLine($"{i + 1} - {options[i].Name}");
            Console.Write("\nSelect option: ");
            string? input = Console.ReadLine();
            if (input is null) return 0;
            if (!int.TryParse(input, out int selection) || selection < 1 || selection > options.Count) continue;
            var run = options[selection - 1].Run;
            if (run is null) return 0;
            try
            {
                using var operation = new OperationLock();
                run();
            }
            catch (Exception ex) { Report(ex); }
            Pause();
        }
    }

    private static IReadOnlyList<DriverMode?> Status()
    {
        var devices = DeviceApi.Enumerate();
        var modes = new List<DriverMode?>();
        bool rebootRequired = false;
        Console.WriteLine("STATUS");
        if (devices.Count == 0)
            Console.WriteLine("Device: no supported V100 SXM2 detected yet.\nCode: not available\nMode: not available\nRegistry path: not available");
        foreach (var device in devices)
        {
            if (modes.Count > 0) Console.WriteLine();
            var currentMode = Gpu.CurrentMode(device);
            modes.Add(currentMode);
            rebootRequired |= Gpu.RequiresReboot(device, currentMode);
            Console.WriteLine($"Device: {device.Name}");
            Console.WriteLine($"Code: {device.ProblemCode}");
            WriteValue("Mode", ModeName(currentMode), green: currentMode == DriverMode.Wddm);
            Console.WriteLine($"Registry path: {device.RegistryPath}");
        }
        Console.WriteLine($"Log path: {AppPaths.LogFile}");
        var settings = Store.Load();
        bool installed = Integration.Status == "Installed";
        Console.WriteLine("\nSETTINGS");
        WriteValue("Fix device error on startup", installed ? "Enabled" : "Disabled", green: installed);
        Console.WriteLine($"Change device mode on startup: {(installed && settings.DesiredMode != DriverMode.Unchanged ? ModeName(settings.DesiredMode) : "Disabled")}");
        Console.WriteLine($"Reboot required: {(rebootRequired ? "Yes" : "No")}");
        return modes;
    }

    private static void WriteValue(string label, string value, bool green)
    {
        Console.Write($"{label}: ");
        if (!green || Console.IsOutputRedirected) { Console.WriteLine(value); return; }
        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(value);
        }
        finally { Console.ForegroundColor = previous; }
        Console.WriteLine();
    }

    private static string ModeName(DriverMode? mode) => mode switch
    {
        DriverMode.Tcc => "TCC", DriverMode.Wddm => "WDDM", _ => "not available"
    };

    private static void InstallInteractive()
    {
        Integration.Install();
        Store.Save(Store.Load());
        if (DeviceApi.Enumerate().Any(x => x.ProblemCode == 10)) RecoverInteractive();
    }
    private static void RecoverInteractive()
    {
        if (Integration.Status != "Installed") throw new InvalidOperationException("Install Fix before requesting recovery.");
        Console.WriteLine("Save your work. Recovery closes LM Studio, removes the V100 device and restarts Windows.");
        if (Confirm("Restart Windows now?")) Recovery.RestartAfterRemoval(Store.Load(), manual: true);
        else Console.WriteLine("Restart cancelled. Installed settings are retained.");
    }
    private static void SwitchModeInteractive(DriverMode mode)
    {
        if (Confirm($"Close GPU applications. Switch to {mode.ToString().ToUpperInvariant()} by restarting the GPU only?"))
            ChangeMode(mode, apply: true);
    }
    private static void ResetInteractive()
    {
        Console.WriteLine("Reset removes the startup task, shutdown policy and saved settings, and restores the original driver registry values.");
        Console.WriteLine("For changes made before registry backups were available, TCC defaults are restored.");
        if (Confirm("Reset all controller settings?")) Reset();
    }
    private static void Reset()
    {
        Gpu.ResetRegistry();
        Integration.Uninstall();
        Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\V100AutoRecovery", false);
        Gpu.CompleteRegistryReset();
        Log.Write("Reset completed. Driver registry values restored; startup/shutdown fixes and saved settings removed.");
        Log.Write("If Reboot required is yes, save your work and restart Windows to apply the restored driver settings.");
    }
    private static void ChangeMode(DriverMode mode, bool apply)
    {
        var settings = Store.Load();
        settings.DesiredMode = mode;
        Store.Save(settings);
        Log.Write($"Saved startup mode: {mode}.");
        if (Integration.Status != "Installed") Console.WriteLine("Install Fix to restore this preference automatically at boot.");
        if (!apply || mode == DriverMode.Unchanged) return;
        var devices = DeviceApi.Enumerate();
        if (devices.Count == 0) throw new InvalidOperationException("Preference saved; GPU is not present yet. Startup will wait after Fix is installed.");
        foreach (var device in devices) Gpu.ApplyMode(device, mode);
    }
    private static DriverMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "tcc" => DriverMode.Tcc, "wddm" => DriverMode.Wddm, "keep" => DriverMode.Unchanged,
        _ => throw new ArgumentException("Mode must be tcc, wddm or keep.")
    };
    private static bool Confirm(string question)
    {
        Console.Write($"{question} [y/N]: ");
        return string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }
    private static void Pause() { Console.Write("\nPress Enter to continue..."); Console.ReadLine(); }
    private static void Report(Exception error)
    {
        try
        {
            var settings = Store.Load(); settings.LastError = error.Message; Store.Save(settings);
            Log.Write("ERROR: " + error.Message);
        }
        catch { Console.Error.WriteLine(error.Message); }
    }
    private static void Help() => Console.WriteLine("""
        Tesla V100 SXM2 Controller - Windows 11 x64, administrator access required
        No arguments       Interactive menu
        --status           Read GPU, driver, preference and installation status
        --install          Install startup/shutdown integration; does not reboot
        --uninstall        Remove integration and saved preferences; keep current GPU mode
        --reset            Remove integration/settings and restore original driver registry values
        --mode tcc|wddm     Save preference and apply it by restarting the GPU only
        --mode keep         Stop restoring a driver mode at boot
        --startup          SYSTEM boot task: wait for driver, recover Code 10, restore mode
        --shutdown         Local shutdown policy: remove V100 devices, keeping drivers
        --help             Print this help
        """);
}
