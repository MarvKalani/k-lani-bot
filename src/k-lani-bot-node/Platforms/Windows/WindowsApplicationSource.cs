using System.Diagnostics;
using System.Runtime.Versioning;
using KLaniBot.Inventory;
using Microsoft.Win32;

namespace KLaniBot.Platforms.Windows;

// Use case: list the programs a Windows machine has registered, and find an Office-style program that is
// not registered as installed software but only through its App Paths entry (WINWORD.EXE, EXCEL.EXE).
// Registry reads only; Win32_Product is never used because its query can start MSI repairs.
[SupportedOSPlatform("windows")]
public sealed class WindowsApplicationSource : IApplicationSource
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const int MaximumEntries = 8192;

    public string OsFamily => "windows";

    public IReadOnlyList<string> Limitations =>
    [
        "Registered programs are listed (uninstall entries of the machine and the signed-in user). Store apps, portable "
        + "programs and other user profiles are missing.",
        "Applications with an App Paths entry are found only when the target catalogue names their executable."
    ];

    public IReadOnlyList<ApplicationEvidence> Enumerate(IReadOnlyCollection<string> executableHints)
    {
        var evidence = new List<ApplicationEvidence>();
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                ReadRegistrations(hive, view, evidence);
            }
        }
        foreach (string executable in executableHints.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ApplicationEvidence? registered = ReadAppPath(executable);
            if (registered != null)
            {
                evidence.Add(registered);
            }
        }
        return evidence;
    }

    private static void ReadRegistrations(RegistryHive hive, RegistryView view, List<ApplicationEvidence> evidence)
    {
        string source = hive + "/" + view;
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? registrations = root.OpenSubKey(UninstallKey, writable: false);
            if (registrations == null)
            {
                return;
            }
            foreach (string identifier in registrations.GetSubKeyNames().Order(StringComparer.Ordinal).Take(MaximumEntries))
            {
                using RegistryKey? entry = registrations.OpenSubKey(identifier, writable: false);
                string name = ReadText(entry, "DisplayName");
                if (name.Length == 0)
                {
                    continue;
                }
                bool systemComponent = entry?.GetValue("SystemComponent") is int flag && flag == 1;
                bool hasParent = ReadText(entry, "ParentKeyName").Length > 0;
                var kind = systemComponent || hasParent ? ApplicationKind.Component : ApplicationKindClassifier.Classify(name);
                evidence.Add(new ApplicationEvidence(
                    name,
                    ReadText(entry, "DisplayVersion"),
                    ReadText(entry, "Publisher"),
                    source,
                    [identifier],
                    kind,
                    ReadText(entry, "InstallLocation")));
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // A source that cannot be read is simply absent from the list; the report states that the scan is partial.
        }
    }

    private static ApplicationEvidence? ReadAppPath(string executable)
    {
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(AppPathsKey + "\\" + executable, writable: false);
            string path = (key?.GetValue(null) as string ?? "").Trim('"');
            if (path.Length == 0 || !File.Exists(path))
            {
                continue;
            }
            var file = FileVersionInfo.GetVersionInfo(path);
            string name = string.IsNullOrWhiteSpace(file.ProductName) ? Path.GetFileNameWithoutExtension(executable) : file.ProductName!;
            string version = file.ProductVersion ?? file.FileVersion ?? "";
            return new ApplicationEvidence(
                name,
                version,
                file.CompanyName ?? "",
                "app-paths",
                [executable],
                ApplicationKind.Application,
                path);
        }
        return null;
    }

    private static string ReadText(RegistryKey? key, string name)
    {
        string value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        return value.Length > 512 ? value[..512] : value;
    }
}
