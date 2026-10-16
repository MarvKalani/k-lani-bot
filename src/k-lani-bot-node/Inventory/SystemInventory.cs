using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KLaniBot.Fleet;
using Microsoft.Win32;

namespace KLaniBot.Inventory;

// One installed program as an inventory source lists it.
public sealed record InstalledApplication(string Id, string Name, string Version, string Publisher, string Source);
// One place the inventory read, whether it was complete and what it cannot see.
public sealed record InventorySource(string Name, bool Complete, string? Limitation);
// The machine the inventory was taken on.
public sealed record InventoryMachine(string OsFamily, string OsVersion, string OsArchitecture, string ProcessArchitecture,
    int LogicalProcessors, string UiLanguage);
// A snapshot of the installed programs, as collect_inventory returns it and the master's assessment reads it. Its fingerprint ties an
// assessment to exactly this state.
public sealed record SystemInventory(string SnapshotId, DateTimeOffset CapturedAt, InventoryMachine Machine,
    InstalledApplication[] Applications, InventorySource[] Sources, bool Truncated, string Fingerprint)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SystemInventory Create(InventoryMachine machine, IEnumerable<InstalledApplication> applications,
        IEnumerable<InventorySource> sources, bool truncated)
    {
        InstalledApplication[] ordered = applications.OrderBy(application => application.Id, StringComparer.Ordinal).ToArray();
        InventorySource[] coverage = sources.OrderBy(source => source.Name, StringComparer.Ordinal).ToArray();
        string canonical = JsonSerializer.Serialize(new
        {
            machine,
            applications = ordered,
            sources = coverage,
            truncated
        }, JsonOptions);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
        return new SystemInventory(
            Guid.CreateVersion7().ToString(),
            DateTimeOffset.UtcNow,
            machine,
            ordered,
            coverage,
            truncated,
            fingerprint);
    }

    // Throws InvalidDataException when the fingerprint or the bounded schema does not hold.
    public void Verify()
    {
        if (!HasBoundedSchema() || Create(Machine, Applications, Sources, Truncated).Fingerprint != Fingerprint)
        {
            throw new InvalidDataException("Inventory fingerprint or bounded schema is invalid.");
        }
    }

    // A snapshot arrives from a remote node, so every member may be null and every size is untrusted.
    // The checks run in this order so that each one only touches members already proven present.
    private bool HasBoundedSchema()
    {
        if (Machine == null || Applications == null || Sources == null)
        {
            return false;
        }
        bool applicationsComplete = !Applications.Any(application =>
            application == null
            || application.Id == null
            || application.Name == null
            || application.Version == null
            || application.Publisher == null);
        bool sourcesComplete = !Sources.Any(source => source == null || source.Name == null);
        if (!applicationsComplete || !sourcesComplete)
        {
            return false;
        }
        bool snapshotIdIsVersion7 = Guid.TryParse(SnapshotId, out Guid identifier) && identifier.Version == 7;
        bool countsWithinLimits = Applications.Length <= 4096 && Sources.Length <= 32;
        if (!snapshotIdIsVersion7 || !countsWithinLimits)
        {
            return false;
        }
        bool sourceNamesUnique = Sources.Select(source => source.Name).Distinct().Count() == Sources.Length;
        bool applicationFieldsWithinLimits = !Applications.Any(application =>
            application.Id.Length != 64
            || application.Name.Length is < 1 or > 512
            || application.Version.Length > 512
            || application.Publisher.Length > 512);
        bool applicationIdsUnique = Applications.Select(application => application.Id).Distinct().Count() == Applications.Length;
        return sourceNamesUnique && applicationFieldsWithinLimits && applicationIdsUnique;
    }
}

// collect_inventory and the app scan read the installed programs here: the uninstall registrations on Windows, desktop entries and bundles
// elsewhere, each with what it cannot see.
public static class SystemInventoryCollector
{
    private const int MaximumApplications = 4096;
    private const int MaximumRegistrations = 8192;

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static SystemInventory Collect()
    {
        var machine = new InventoryMachine(
            DescribeOperatingSystemFamily(),
            Environment.OSVersion.Version.ToString(),
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            System.Globalization.CultureInfo.CurrentUICulture.Name);
        var applications = new List<InstalledApplication>();
        var sources = new List<InventorySource>();
        bool truncated = false;
        if (OperatingSystem.IsWindows())
        {
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    ReadWindowsApplications(hive, view, applications, sources, ref truncated);
                }
            }
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var source = ApplicationSources.ForCurrentSystem();
            AddListedApplications(source, applications, ref truncated);
            // Never complete: a desktop entry or an application bundle is only one of the ways a program
            // gets onto a machine.
            sources.Add(new InventorySource("installed-applications", false, string.Join(" ", source.Limitations)));
        }
        else
        {
            sources.Add(new InventorySource("installed-applications", false, "This collector has no source for this operating system."));
        }
        sources.Add(new InventorySource(
            "portable-store-drivers-other-users",
            false,
            "Portable executables, Store packages, drivers, plug-ins, data formats and other user profiles "
            + "require additional collectors and tests."));
        return SystemInventory.Create(machine, applications, sources, truncated);
    }

    private static string DescribeOperatingSystemFamily()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }
        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }
        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }
        return "unknown";
    }

    private static void AddListedApplications(IApplicationSource source, List<InstalledApplication> applications, ref bool truncated)
    {
        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source.Enumerate([]))
        {
            if (applications.Count >= MaximumApplications)
            {
                truncated = true;
                return;
            }
            string key = item.Location + "|" + (item.Identifiers.FirstOrDefault() ?? item.Name);
            string id = PairingProtocol.Hash("application-registration/v1", item.Source, key);
            if (item.Name.Length > 0 && knownIds.Add(id))
            {
                applications.Add(new InstalledApplication(
                    id,
                    Clip(item.Name, ref truncated),
                    Clip(item.Version, ref truncated),
                    Clip(item.Publisher, ref truncated),
                    item.Source));
            }
        }
    }

    private static string Clip(string value, ref bool truncated)
    {
        if (value.Length <= 512)
        {
            return value;
        }
        truncated = true;
        return value[..512];
    }

    // Returns false when the application limit stopped the listing before every registration was read.
    [SupportedOSPlatform("windows")]
    private static bool AddRegistrations(RegistryKey registrations, string source, List<InstalledApplication> applications,
        ref bool truncated)
    {
        foreach (string identifier in registrations.GetSubKeyNames().Order(StringComparer.Ordinal))
        {
            if (applications.Count >= MaximumApplications)
            {
                return false;
            }
            using RegistryKey? application = registrations.OpenSubKey(identifier, writable: false);
            string name = ReadText(application, "DisplayName", ref truncated);
            if (name == "")
            {
                continue;
            }
            // Never use Win32_Product: its inventory query may trigger MSI repair on a production machine.
            string id = PairingProtocol.Hash("application-registration/v1", source, identifier);
            applications.Add(new InstalledApplication(id, name, ReadText(application, "DisplayVersion", ref truncated),
                ReadText(application, "Publisher", ref truncated), source));
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void ReadWindowsApplications(RegistryHive hive, RegistryView view, List<InstalledApplication> applications,
        List<InventorySource> sources, ref bool truncated)
    {
        string source = hive + "/" + view;
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? registrations = root.OpenSubKey(UninstallKey, writable: false);
            if (registrations == null)
            {
                sources.Add(new InventorySource(source, true, "No uninstall registrations at this source."));
                return;
            }
            if (registrations.SubKeyCount > MaximumRegistrations)
            {
                truncated = true;
                sources.Add(new InventorySource(source, false, "Registration enumeration limit reached."));
                return;
            }
            if (!AddRegistrations(registrations, source, applications, ref truncated))
            {
                truncated = true;
                sources.Add(new InventorySource(source, false, "Application limit reached."));
                return;
            }
            sources.Add(new InventorySource(source, true, null));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            sources.Add(new InventorySource(source, false, "Source unavailable: " + exception.GetType().Name));
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ReadText(RegistryKey? key, string name, ref bool truncated)
    {
        string value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        if (value.Length > 512)
        {
            truncated = true;
            return value[..512];
        }
        return value;
    }
}
