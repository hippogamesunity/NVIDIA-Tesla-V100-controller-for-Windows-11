using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace TeslaV100Controller;

internal static class Legacy
{
    public static bool IsOwned(string command, string arguments)
    {
        string file = command.Trim('"');
        if (file.Equals(AppPaths.InstalledExe, StringComparison.OrdinalIgnoreCase))
            return arguments.Equals("--shutdown", StringComparison.OrdinalIgnoreCase);
        if (file.EndsWith(@"\V100Reset\V100-Shutdown.cmd", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Path.GetFileName(file).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)) return false;
        string code = arguments;
        var encoded = Regex.Match(arguments, @"(?i)-EncodedCommand\s+([A-Za-z0-9+/=]+)");
        if (encoded.Success)
        {
            try { code = Encoding.Unicode.GetString(Convert.FromBase64String(encoded.Groups[1].Value)); }
            catch (FormatException) { return false; }
        }
        return code.TrimEnd().EndsWith("-Shutdown", StringComparison.OrdinalIgnoreCase) &&
               Regex.IsMatch(code, "(?i)[\\\\/](Tesla V100 SXM2 4090 Driver Fix\\.cmd|V100\\.cmd|V100\\.ps1)(?:['\"\\s]|$)");
    }
}

internal sealed class ShutdownPolicy
{
    private const string Cache = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Shutdown";
    private const string Extension = "[{42B5FAAE-6536-11d2-AE5A-0000F87571E3}{40B6664F-4972-11D1-A7CA-0000F87571E3}]";
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "GroupPolicy");
    private static string Ini => Path.Combine(Root, @"Machine\Scripts\scripts.ini");
    public bool Exists => File.Exists(Ini) && Regex.IsMatch(File.ReadAllText(Ini),
        @"(?im)^\d+CmdLine=" + Regex.Escape(AppPaths.InstalledExe) + @"\r?$", RegexOptions.IgnoreCase);

    public void Update(bool enable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Ini)!);
        string content = PolicyDocument.Update(File.Exists(Ini) ? File.ReadAllText(Ini) : "", Legacy.IsOwned,
            enable ? AppPaths.InstalledExe : null, "--shutdown");
        if (string.IsNullOrWhiteSpace(content)) File.Delete(Ini);
        else File.WriteAllText(Ini, content, Encoding.Unicode);

        string gptPath = Path.Combine(Root, "gpt.ini");
        string gpt = File.Exists(gptPath) ? File.ReadAllText(gptPath) : "[General]\r\nVersion=0\r\n";
        if (enable && !gpt.Contains(Extension, StringComparison.OrdinalIgnoreCase))
        {
            var line = Regex.Match(gpt, @"(?im)^gPCMachineExtensionNames=([^\r\n]*)");
            if (line.Success) gpt = gpt.Insert(line.Index + line.Length, Extension);
            else gpt = InsertGeneral(gpt, "gPCMachineExtensionNames=" + Extension);
        }
        // Shared Scripts extension metadata is retained: other startup/logon policies may use it.
        var version = Regex.Match(gpt, @"(?im)^Version=(\d+)");
        uint old = version.Success ? uint.Parse(version.Groups[1].Value) : 0;
        uint next = (old & 0xffff0000) | ((old + 1) & 0xffff);
        gpt = version.Success ? gpt.Remove(version.Index, version.Length).Insert(version.Index, $"Version={next}")
                              : InsertGeneral(gpt, $"Version={next}");
        File.WriteAllText(gptPath, gpt, Encoding.ASCII);

        // Use raw RegistryKey to tolerate the malformed ExecTime value left by an old script.
        using (var root = Registry.LocalMachine.OpenSubKey(Cache, true))
        {
            if (root is not null)
                foreach (string groupName in root.GetSubKeyNames())
                {
                    bool removed = false, empty;
                    using (var group = root.OpenSubKey(groupName, true)!)
                    {
                        foreach (string childName in group.GetSubKeyNames())
                        {
                            bool ours;
                            using (var child = group.OpenSubKey(childName)!)
                                ours = Legacy.IsOwned(child.GetValue("Script") as string ?? "", child.GetValue("Parameters") as string ?? "");
                            if (ours) { group.DeleteSubKeyTree(childName); removed = true; }
                        }
                        empty = group.SubKeyCount == 0;
                    }
                    if (removed && empty) root.DeleteSubKeyTree(groupName);
                }
        }
        if (!enable) return;
        using var cache = Registry.LocalMachine.CreateSubKey(Cache);
        var used = cache.GetSubKeyNames();
        int n = 0;
        while (used.Contains(n.ToString())) n++;
        using var entry = cache.CreateSubKey(n.ToString());
        entry.SetValue("GPO-ID", "LocalGPO");
        entry.SetValue("SOM-ID", "Local");
        entry.SetValue("FileSysPath", Path.Combine(Root, "Machine"));
        entry.SetValue("DisplayName", "Local Group Policy");
        entry.SetValue("GPOName", "Local Group Policy");
        entry.SetValue("PSScriptOrder", 1, RegistryValueKind.DWord);
        using var script = entry.CreateSubKey("0");
        script.SetValue("Script", AppPaths.InstalledExe);
        script.SetValue("Parameters", "--shutdown");
        script.SetValue("IsPowershell", 0, RegistryValueKind.DWord);
    }

    private static string InsertGeneral(string text, string line)
    {
        var section = Regex.Match(text, @"(?im)^\[General\][ \t]*\r?\n");
        return section.Success ? text.Insert(section.Index + section.Length, line + "\r\n")
                               : "[General]\r\n" + line + "\r\n" + text;
    }
}

internal sealed class StartupTask
{
    private static dynamic Folder()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        service.Connect();
        return service.GetFolder(@"\");
    }

    internal static string? Xml(string name)
    {
        try { return (string)Folder().GetTask(name).Xml; }
        catch (FileNotFoundException) { return null; }
        catch (COMException ex) when (ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x8004130F)) { return null; }
    }
    public bool Exists
    {
        get
        {
            string? text = Xml(AppPaths.TaskName);
            if (text is null) return false;
            var xml = XDocument.Parse(text);
            return xml.Descendants().Any(x => x.Name.LocalName == "Command" &&
                x.Value.Equals(AppPaths.InstalledExe, StringComparison.OrdinalIgnoreCase)) &&
                xml.Descendants().Any(x => x.Name.LocalName == "Arguments" && x.Value == "--startup");
        }
    }

    internal static string Definition()
    {
        string exe = SecurityElement.Escape(AppPaths.InstalledExe)!;
        string xml = $$"""
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Tesla V100: wait for NVIDIA initialization, recover Code 10, restore selected driver mode.</Description></RegistrationInfo>
              <Triggers><BootTrigger><Enabled>true</Enabled><Delay>PT10S</Delay></BootTrigger></Triggers>
              <Principals><Principal id="System"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT15M</ExecutionTimeLimit><Enabled>true</Enabled><Hidden>true</Hidden></Settings>
              <Actions Context="System"><Exec><Command>{{exe}}</Command><Arguments>--startup</Arguments></Exec></Actions>
            </Task>
            """;
        return xml;
    }
    public void Install() => Folder().RegisterTask(AppPaths.TaskName, Definition(), 6, "SYSTEM", null, 5, null);
    // TASK_VALIDATE_ONLY: check the actual Windows schema without installing a task.
    public static void ValidateDefinition() => Folder().RegisterTask(AppPaths.TaskName, Definition(), 1, "SYSTEM", null, 5, null);

    public void Uninstall()
    {
        if (Xml(AppPaths.TaskName) is not null) Folder().DeleteTask(AppPaths.TaskName, 0);
    }
    public void RemoveLegacy()
    {
        string? text = Xml("V100-AutoRecovery");
        if (text is null) return;
        var xml = XDocument.Parse(text);
        string command = xml.Descendants().FirstOrDefault(x => x.Name.LocalName == "Command")?.Value ?? "";
        string arguments = xml.Descendants().FirstOrDefault(x => x.Name.LocalName == "Arguments")?.Value ?? "";
        // The old startup task uses the same loader with -Auto, rather than -Shutdown.
        if (Legacy.IsOwned(command, arguments.Replace(" -Auto", " -Shutdown", StringComparison.OrdinalIgnoreCase)) ||
            IsEncodedLegacyStartup(command, arguments)) Folder().DeleteTask("V100-AutoRecovery", 0);
    }
    private static bool IsEncodedLegacyStartup(string command, string args)
    {
        var match = Regex.Match(args, @"(?i)-EncodedCommand\s+([A-Za-z0-9+/=]+)");
        if (!match.Success) return false;
        try
        {
            string code = Encoding.Unicode.GetString(Convert.FromBase64String(match.Groups[1].Value));
            if (!code.TrimEnd().EndsWith("-Auto", StringComparison.OrdinalIgnoreCase)) return false;
            string converted = Convert.ToBase64String(Encoding.Unicode.GetBytes(code[..code.LastIndexOf("-Auto", StringComparison.OrdinalIgnoreCase)] + "-Shutdown"));
            return Legacy.IsOwned(command, "-EncodedCommand " + converted);
        }
        catch (FormatException) { return false; }
    }
}

internal sealed class Integration(Log log)
{
    private readonly ShutdownPolicy policy = new();
    private readonly StartupTask task = new();
    public string Status => task.Exists && policy.Exists ? "Installed" : task.Exists || policy.Exists ? "Partially installed" : "Not installed";

    public void Install()
    {
        string source = Environment.ProcessPath ?? throw new IOException("Executable path is unknown.");
        if (Path.GetFileName(source).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(Path.ChangeExtension(source, ".runtimeconfig.json")))
            throw new InvalidOperationException("Install from the published self-contained EXE, not from dotnet run/build.");
        Directory.CreateDirectory(AppPaths.InstallDirectory);
        Directory.CreateDirectory(AppPaths.DataDirectory);
        ProtectDirectory(AppPaths.InstallDirectory);
        ProtectDirectory(AppPaths.DataDirectory);
        if (!source.Equals(AppPaths.InstalledExe, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, AppPaths.InstalledExe, true);
        task.Install();
        policy.Update(true);
        if (Status != "Installed") throw new IOException("Installation verification failed.");
        task.RemoveLegacy();
        log.Write("Installed: SYSTEM boot task and shutdown policy. Desktop batch integration was replaced if present.");
    }
    private static void ProtectDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var account in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(account, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    public void Uninstall()
    {
        task.Uninstall();
        policy.Update(false);
        task.RemoveLegacy();
        File.Delete(AppPaths.SettingsFile);
        log.Write("Uninstalled: startup task, shutdown policy and saved preferences removed. Current GPU mode is unchanged.");
    }
}
