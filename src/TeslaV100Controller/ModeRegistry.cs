using Microsoft.Win32;
using System.Text.Json;

namespace TeslaV100Controller;

internal sealed record ModeRegistryBackup(string InstanceId, string DriverKey, int? AdapterType, int? GridLicensedFeatures);

// Keep originals outside settings.json so --uninstall cannot discard the reset information.
internal sealed class ModeRegistry(string backupPath)
{
    private List<ModeRegistryBackup> Load() => File.Exists(backupPath)
        ? JsonSerializer.Deserialize<List<ModeRegistryBackup>>(File.ReadAllText(backupPath))
          ?? throw new InvalidDataException("Invalid registry backup.")
        : [];

    private void Save(List<ModeRegistryBackup> backups)
    {
        if (backups.Count == 0) { File.Delete(backupPath); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.WriteAllText(backupPath + ".tmp", JsonSerializer.Serialize(backups, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(backupPath + ".tmp", backupPath, true);
    }

    private static bool Matches(ModeRegistryBackup backup, GpuDevice device) =>
        backup.InstanceId.Equals(device.InstanceId, StringComparison.OrdinalIgnoreCase) &&
        backup.DriverKey.Equals(device.DriverKey, StringComparison.OrdinalIgnoreCase);

    private static int? Read(RegistryKey key, string name)
    {
        if (key.GetValue(name) is null) return null;
        if (key.GetValueKind(name) != RegistryValueKind.DWord)
            throw new InvalidDataException($"{name} is not a DWORD; registry settings were not changed.");
        return (int)key.GetValue(name)!;
    }

    public void Capture(GpuDevice device, RegistryKey key)
    {
        var backups = Load();
        if (backups.Any(x => Matches(x, device))) return;
        backups.Add(new(device.InstanceId, device.DriverKey, Read(key, "AdapterType"), Read(key, "GridLicensedFeatures")));
        Save(backups); // Durable before either driver value is changed.
    }

    public bool Restore(GpuDevice device, RegistryKey key)
    {
        var backups = Load();
        var original = backups.SingleOrDefault(x => Matches(x, device));
        // Older versions/manual workarounds had no backup. Use the TCC defaults observed on this setup.
        int? adapter = original is null ? 2 : original.AdapterType;
        int? grid = original?.GridLicensedFeatures;
        int? currentAdapter = Read(key, "AdapterType"), currentGrid = Read(key, "GridLicensedFeatures");
        bool changed = currentAdapter != adapter || currentGrid != grid;
        Write(key, "AdapterType", adapter);
        Write(key, "GridLicensedFeatures", grid);
        return changed;
    }

    public void VerifyResetTargets(IReadOnlyList<GpuDevice> devices)
    {
        if (Load().Any(backup => !devices.Any(device => Matches(backup, device))))
            throw new IOException("Some backed-up driver keys could not be found. Their backups were retained; reconnect the GPU and retry Reset.");
    }

    public void CompleteReset() => File.Delete(backupPath);

    public static bool RequiresRestart(RegistryKey key, DriverMode? currentMode)
    {
        if (currentMode is null) return false;
        return key.GetValue("AdapterType") switch
        {
            1 => currentMode != DriverMode.Wddm,
            2 or null => currentMode != DriverMode.Tcc,
            _ => false
        };
    }

    private static void Write(RegistryKey key, string name, int? value)
    {
        if (value.HasValue) key.SetValue(name, value.Value, RegistryValueKind.DWord);
        else key.DeleteValue(name, false);
    }
}
