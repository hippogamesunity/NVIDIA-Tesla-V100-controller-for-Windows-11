using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeslaV100Controller;

internal enum DriverMode { Unchanged, Tcc, Wddm }

internal sealed record GpuDevice(string InstanceId, string Name, int ProblemCode,
    bool Started, string DriverKey, string Service, string BusId)
{
    public bool NvidiaBound => Service.Equals("nvlddmkm", StringComparison.OrdinalIgnoreCase)
                               && !string.IsNullOrWhiteSpace(DriverKey);
    // Match PCI IDs even while Windows calls the GPU "3D Video Controller".
    public static bool IsTarget(string id) =>
        id.StartsWith(@"PCI\VEN_10DE&DEV_1DB1&", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith(@"PCI\VEN_10DE&DEV_1DB5&", StringComparison.OrdinalIgnoreCase);
}

internal sealed class Settings
{
    public DriverMode DesiredMode { get; set; } = DriverMode.Unchanged;
    public int StartupTimeoutSeconds { get; set; } = 300;
    public bool RecoveryPending { get; set; }
    public DateTimeOffset? LastRecovery { get; set; }
    public string? LastError { get; set; }
}

internal static class AppPaths
{
    public const string TaskName = @"TeslaV100Controller-Startup";
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TeslaV100Controller");
    public static string InstalledExe => Path.Combine(InstallDirectory, "TeslaV100Controller.exe");
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TeslaV100Controller");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string LogFile => Path.Combine(DataDirectory, "controller.log");
}

internal sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public Settings Load()
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options)
                    ?? throw new InvalidDataException("Invalid settings file.");
        if (value.StartupTimeoutSeconds is < 30 or > 1800)
            throw new InvalidDataException("Startup timeout must be 30..1800 seconds.");
        if (!Enum.IsDefined(value.DesiredMode)) throw new InvalidDataException("Invalid driver mode.");
        return value;
    }

    public void Save(Settings value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
    }
}

internal sealed class Log(Action<string>? sink = null)
{
    public void Write(string message)
    {
        if (sink is not null) { sink(message); return; }
        string line = $"{DateTimeOffset.Now:O} {message}";
        Console.WriteLine(message);
        Directory.CreateDirectory(AppPaths.DataDirectory);
        if (File.Exists(AppPaths.LogFile) && new FileInfo(AppPaths.LogFile).Length > 2_000_000)
            File.Move(AppPaths.LogFile, AppPaths.LogFile + ".previous", true);
        File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
    }
}
