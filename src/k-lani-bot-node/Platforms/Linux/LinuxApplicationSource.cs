using KLaniBot.Common;
using KLaniBot.Inventory;

namespace KLaniBot.Platforms.Linux;

// Use case: list the graphical programs on a Linux machine and, where the package manager knows them,
// their version. Sources: desktop entries (what is installed), dpkg (version of the owning package),
// snap and flatpak (their own lists). Nothing is started and nothing is changed.
public sealed class LinuxApplicationSource : IApplicationSource
{
    private static readonly string[] SystemDirectories =
    [
        "/usr/share/applications",
        "/usr/local/share/applications",
        "/var/lib/snapd/desktop/applications",
        "/var/lib/flatpak/exports/share/applications"
    ];

    public string OsFamily => "linux";

    public IReadOnlyList<string> Limitations =>
    [
        "Programs with a desktop entry are listed. Command-line programs, libraries, AppImages and unregistered programs are missing.",
        "Versions come from dpkg, snap and flatpak; other package managers (rpm, pacman) are not connected yet."
    ];

    public IReadOnlyList<ApplicationEvidence> Enumerate(IReadOnlyCollection<string> executableHints)
    {
        var entries = ReadDesktopEntries();
        var packageOfPath = OwnersFromDpkg(entries.Where(entry => entry.IsSystem).Select(entry => entry.Path).ToArray());
        var packageVersions = VersionsFromDpkg(packageOfPath.Values.Distinct().ToArray());
        var snaps = ReadSnaps();
        var flatpaks = ReadFlatpaks();
        var evidence = new List<ApplicationEvidence>();
        foreach (var entry in entries)
        {
            evidence.Add(Describe(entry, packageOfPath, packageVersions, snaps, flatpaks));
        }
        return evidence;
    }

    // A desktop entry with the file it came from and whether that is a system location.
    private sealed record FoundEntry(DesktopEntry Entry, string Path, bool IsSystem);

    // A folder of desktop entries and whether it belongs to the system or the user.
    private sealed record EntryDirectory(string Path, bool IsSystem);

    // A snap's version and publisher, as snap lists them.
    private sealed record SnapPackage(string Version, string Publisher);

    private static List<FoundEntry> ReadDesktopEntries()
    {
        var directories = new List<EntryDirectory>();
        foreach (string directory in SystemDirectories)
        {
            directories.Add(new EntryDirectory(directory, true));
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0)
        {
            directories.Add(new EntryDirectory(System.IO.Path.Combine(home, ".local/share/applications"), false));
            directories.Add(new EntryDirectory(System.IO.Path.Combine(home, ".local/share/flatpak/exports/share/applications"), false));
        }
        var found = new List<FoundEntry>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (directory, isSystem) in directories.Where(item => Directory.Exists(item.Path)))
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.desktop").Order(StringComparer.Ordinal))
            {
                string fileName = System.IO.Path.GetFileName(file);
                // An entry in the user's directory overrides the system one with the same file name; the system one is listed first.
                if (!seenNames.Add(fileName) || new FileInfo(file).Length > 256 * 1024)
                {
                    continue;
                }
                var entry = DesktopEntryParser.Parse(fileName, File.ReadAllText(file));
                if (entry != null)
                {
                    found.Add(new FoundEntry(entry, file, isSystem && !directory.StartsWith("/var/lib/", StringComparison.Ordinal)));
                }
            }
        }
        return found;
    }

    private static ApplicationEvidence Describe(
        FoundEntry found,
        Dictionary<string, string> packageOfPath,
        Dictionary<string, string> packageVersions,
        Dictionary<string, SnapPackage> snaps,
        Dictionary<string, string> flatpaks)
    {
        var entry = found.Entry;
        string executable = DesktopEntryParser.ExecutableName(entry.Exec);
        string desktopId = entry.FileName;
        var identifiers = new List<string> { desktopId, desktopId.Replace(".desktop", ""), executable };
        string version = entry.AppImageVersion;
        string publisher = "";
        string source = "desktop-entry";
        string location = DesktopEntryParser.ExecutablePath(entry.Exec);
        if (packageOfPath.TryGetValue(found.Path, out string? package))
        {
            identifiers.Add(package);
            version = packageVersions.GetValueOrDefault(package, "");
            source = "dpkg";
        }
        string snapName = desktopId.Split('_')[0];
        if (found.Path.Contains("/snapd/", StringComparison.Ordinal) && snaps.TryGetValue(snapName, out var snap))
        {
            identifiers.Add(snapName);
            version = snap.Version;
            publisher = snap.Publisher;
            source = "snap";
        }
        string flatpakId = desktopId.Replace(".desktop", "");
        if (flatpaks.TryGetValue(flatpakId, out string? flatpakVersion))
        {
            version = flatpakVersion;
            source = "flatpak";
        }
        if (version.Length == 0 && location.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
        {
            version = AppImageFileVersion(location);
        }

        bool packaged = source != "desktop-entry";
        string[] distinctIdentifiers = identifiers.Where(item => item.Length > 0).Distinct().ToArray();
        return new ApplicationEvidence(entry.Name, version, publisher, source, distinctIdentifiers, ApplicationKind.Application,
            packaged ? "" : location);
    }

    // An AppImage carries its version in the file name when the author put it there ("Tool-2.4.2-x86_64.AppImage").
    private static string AppImageFileVersion(string location)
    {
        ApplicationVersion fromName = ApplicationVersion.Parse(Path.GetFileNameWithoutExtension(location));

        return fromName.IsKnown ? fromName.Raw : "";
    }

    // "libreoffice-calc: /usr/share/applications/libreoffice-calc.desktop" (several owners are joined with commas).
    private static Dictionary<string, string> OwnersFromDpkg(string[] paths)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        if (paths.Length == 0 || !File.Exists("/usr/bin/dpkg-query"))
        {
            return owners;
        }
        var result = ProcessRunner.Run("/usr/bin/dpkg-query", paths.Prepend("-S"), 30000);
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.LastIndexOf(": ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }
            string package = line[..separator].Split(',')[0].Trim();
            owners[line[(separator + 2)..].Trim()] = package.Split(':')[0];
        }
        return owners;
    }

    private static Dictionary<string, string> VersionsFromDpkg(string[] packages)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (packages.Length == 0)
        {
            return versions;
        }
        var arguments = new List<string> { "-W", "-f=${Package}\t${Version}\n" };
        arguments.AddRange(packages);
        var result = ProcessRunner.Run("/usr/bin/dpkg-query", arguments, 30000);
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t');
            if (parts.Length == 2)
            {
                versions[parts[0]] = parts[1];
            }
        }
        return versions;
    }

    private static Dictionary<string, SnapPackage> ReadSnaps()
    {
        var snaps = new Dictionary<string, SnapPackage>(StringComparer.Ordinal);
        if (!File.Exists("/usr/bin/snap"))
        {
            return snaps;
        }
        var result = ProcessRunner.Run("/usr/bin/snap", ["list"], 20000);
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            string[] columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 5)
            {
                snaps[columns[0]] = new SnapPackage(columns[1], columns[4]);
            }
        }
        return snaps;
    }

    private static Dictionary<string, string> ReadFlatpaks()
    {
        var flatpaks = new Dictionary<string, string>(StringComparer.Ordinal);
        string? executable = new[] { "/usr/bin/flatpak", "/usr/local/bin/flatpak" }.FirstOrDefault(File.Exists);
        if (executable == null)
        {
            return flatpaks;
        }
        var result = ProcessRunner.Run(executable, ["list", "--app", "--columns=application,version"], 20000);
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] columns = line.Split('\t');
            if (columns.Length >= 1 && columns[0].Length > 0)
            {
                flatpaks[columns[0].Trim()] = columns.Length > 1 ? columns[1].Trim() : "";
            }
        }
        return flatpaks;
    }
}
