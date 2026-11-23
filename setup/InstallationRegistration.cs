using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

internal static class InstallationRegistration
{
    private const string Entry = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\STC AirDesk";
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string PreferencesKey = @"Software\STC\AirDesk\Setup";
    private const string Product = "STC AirDesk";
    private const string LegacyProduct = "STC Camera Game";
    private static readonly string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string MaintenanceDirectory = Path.Combine(LocalData, "STC", "AirDesk", "Maintenance");
    private static readonly string MaintenanceExecutable = Path.Combine(MaintenanceDirectory, "STC-AirDesk-Setup.exe");

    internal static string RegisteredDirectory()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Entry))
            return key == null ? null : key.GetValue("InstallLocation") as string;
    }

    internal static Version RegisteredVersion(string executable)
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Entry))
        {
            string directory = key == null ? null : key.GetValue("InstallLocation") as string;
            Version version;
            return !String.IsNullOrEmpty(directory) &&
                SamePath(AirDeskInstallation.Destination(directory), executable) &&
                Version.TryParse(key.GetValue("DisplayVersion") as string, out version) ? version : null;
        }
    }

    internal static void Register(string executable, Version version, string setup)
    {
        Directory.CreateDirectory(MaintenanceDirectory);
        if (!SamePath(setup, MaintenanceExecutable))
        {
            string staged = Path.Combine(MaintenanceDirectory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(setup, staged);
                if (File.Exists(MaintenanceExecutable)) File.Replace(staged, MaintenanceExecutable, null);
                else File.Move(staged, MaintenanceExecutable);
            }
            finally { File.Delete(staged); }
        }

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Entry))
        {
            key.SetValue("DisplayName", Product);
            key.SetValue("DisplayVersion", version.ToString(3));
            key.SetValue("Publisher", "System Technologies Company");
            key.SetValue("InstallLocation", Path.GetDirectoryName(executable));
            key.SetValue("DisplayIcon", Quote(executable) + ",0");
            key.SetValue("UninstallString", Quote(MaintenanceExecutable) + " --uninstall");
            key.SetValue("ModifyPath", Quote(MaintenanceExecutable) + " --repair");
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)((new FileInfo(executable).Length + new FileInfo(MaintenanceExecutable).Length + 1023) / 1024),
                RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }
    }

    // Run uninstall from a temporary copy so Windows can remove the installed maintenance executable.
    internal static bool StartUninstallCopy()
    {
        if (!SamePath(System.Windows.Forms.Application.ExecutablePath, MaintenanceExecutable)) return false;
        string directory = Path.Combine(Path.GetTempPath(), "STC-AirDesk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string worker = Path.Combine(directory, "STC-AirDesk-Setup.exe");
        try
        {
            File.Copy(MaintenanceExecutable, worker);
            using (Process current = Process.GetCurrentProcess())
            using (Process child = Process.Start(new ProcessStartInfo(worker, "--uninstall " + current.Id) { UseShellExecute = false }))
                return true;
        }
        catch
        {
            File.Delete(worker);
            Directory.Delete(directory);
            throw;
        }
    }

    internal static void Remove(string executable, bool removeSettings)
    {
        if (File.Exists(executable) && AirDeskInstallation.ReadVersion(executable) == null)
            throw new IOException("The registered executable is not an AirDesk application.");
        DeleteIfPresent(executable);

        RemoveShortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), executable);
        RemoveShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Programs), executable);
        using (RegistryKey startup = Registry.CurrentUser.OpenSubKey(StartupKey, true))
        {
            if (startup != null)
            {
                RemoveStartupEntry(startup, Product, executable);
                RemoveStartupEntry(startup, LegacyProduct, executable);
            }
        }
        if (removeSettings)
        {
            DeleteIfPresent(Path.Combine(LocalData, Product, "settings.json"));
            DeleteIfPresent(Path.Combine(LocalData, LegacyProduct, "settings.json"));
        }

        DeleteIfPresent(MaintenanceExecutable);
        if (Directory.Exists(MaintenanceDirectory) && Directory.GetFileSystemEntries(MaintenanceDirectory).Length == 0)
            Directory.Delete(MaintenanceDirectory);
        using (RegistryKey preferences = Registry.CurrentUser.OpenSubKey(PreferencesKey, true))
        {
            if (preferences != null) preferences.DeleteValue("InstallDirectory", false);
        }
        Registry.CurrentUser.DeleteSubKey(Entry, false);
    }

    private static void DeleteIfPresent(string path)
    {
        if (Directory.Exists(Path.GetDirectoryName(path))) File.Delete(path);
    }

    internal static void RemoveShortcut(string folder, string executable)
    {
        if (String.IsNullOrEmpty(folder)) return;
        string link = Path.Combine(folder, Product + ".lnk");
        if (!File.Exists(link)) return;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic shortcut = shell.CreateShortcut(link);
        string target = shortcut.TargetPath;
        if (SamePath(target, executable)) File.Delete(link);
    }

    private static void RemoveStartupEntry(RegistryKey startup, string name, string executable)
    {
        string command = startup.GetValue(name) as string;
        if (SamePath(command == null ? null : command.Trim().Trim('"'), executable))
            startup.DeleteValue(name, false);
    }

    internal static bool SamePath(string first, string second)
    {
        return !String.IsNullOrEmpty(first) && String.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string path) { return "\"" + path + "\""; }
}
