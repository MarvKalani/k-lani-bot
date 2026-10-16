using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using KLaniBot.Cartridges;
using KLaniBot.Common;

namespace KLaniBot.Inventory;

// Use case: the command line of the application scanner (--scan-apps). It wires the pieces together:
// where the list of installed programs comes from (this machine, a stored inventory of another node, or
// names typed by the administrator), which catalogues to compare with, and where the report goes.
public static class AppScanCommand
{
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static int Run(string[] args, TextWriter output)
    {
        string cartridgeDirectory = Option(args, "--cartridges") ?? DefaultCartridgeDirectory();
        string? targetsFile = Option(args, "--targets") ?? DefaultTargetsFile(cartridgeDirectory);
        var cartridges = CartridgeCatalogue.Load(cartridgeDirectory);
        var targets = targetsFile == null ? [] : ApplicationTargetCatalogue.Load(targetsFile);
        bool targetsOnly = args.Contains("--targets-only");
        AppScanReport report;
        if (Option(args, "--declared") is { } declared)
        {
            report = ScanDeclared(args, declared, targets, cartridges);
        }
        else if (Option(args, "--inventory-file") is { } inventoryFile)
        {
            report = ScanStoredInventory(inventoryFile, targetsOnly, targets, cartridges);
        }
        else
        {
            report = ScanThisMachine(targetsOnly, targets, cartridges);
        }
        output.Write(AppScanReportText.Render(report, args.Contains("--all")));
        if (Option(args, "--output") is { } reportFile)
        {
            WriteReport(report, reportFile, output);
        }
        return 0;
    }

    private static AppScanReport ScanThisMachine(
        bool targetsOnly,
        IReadOnlyList<ApplicationTarget> targets,
        IReadOnlyList<CartridgeOffer> cartridges)
    {
        var source = ApplicationSources.ForCurrentSystem();
        var executableHints = targets.SelectMany(target => target.Windows.Executables).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var installed = source.Enumerate(executableHints);
        var machine = new ScanMachineInfo(
            Environment.MachineName,
            source.OsFamily,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString());
        var mode = targetsOnly ? ScanMode.TargetsOnly : ScanMode.Inventory;
        return AppScanner.Scan(machine, mode, installed, targets, cartridges, RunningProcesses.Names(), source.Limitations);
    }

    // Throws InvalidDataException when the stored inventory is empty or invalid.
    private static AppScanReport ScanStoredInventory(
        string file,
        bool targetsOnly,
        IReadOnlyList<ApplicationTarget> targets,
        IReadOnlyList<CartridgeOffer> cartridges)
    {
        CartridgeManifest.RejectLinks(file);
        var inventory = JsonSerializer.Deserialize<SystemInventory>(File.ReadAllText(file), SystemInventory.JsonOptions)
            ?? throw new InvalidDataException("The stored inventory is empty.");
        inventory.Verify();
        var installed = inventory.Applications
            .Select(application => new ApplicationEvidence(
                application.Name,
                application.Version,
                application.Publisher,
                application.Source,
                [],
                ApplicationKindClassifier.Classify(application.Name)))
            .ToArray();
        var machine = new ScanMachineInfo(
            "inventory " + inventory.CapturedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm") + " UTC",
            inventory.Machine.OsFamily,
            inventory.Machine.OsVersion,
            inventory.Machine.OsArchitecture);
        var incompleteSources = inventory.Sources.Where(item => !item.Complete && item.Limitation != null);
        var limitations = incompleteSources.Select(item => item.Name + ": " + item.Limitation).ToList();
        limitations.Add(
            "The stored inventory holds no technical identifiers; target programs and cartridges are matched by name only. "
            + "Whether a program is running cannot be told from it.");
        var mode = targetsOnly ? ScanMode.TargetsOnly : ScanMode.Inventory;
        return AppScanner.Scan(machine, mode, installed, targets, cartridges, null, limitations, inventory.CapturedAt);
    }

    private static AppScanReport ScanDeclared(
        string[] args,
        string declared,
        IReadOnlyList<ApplicationTarget> targets,
        IReadOnlyList<CartridgeOffer> cartridges)
    {
        string system = Option(args, "--system") ?? CurrentSystemName();
        string architecture = Option(args, "--architecture") ?? RuntimeInformation.OSArchitecture.ToString();
        var machine = new ScanMachineInfo("declared computer", system, "unknown", architecture);
        string[] limitations =
        [
            "The programs were declared, not checked on the computer. Whether and in which version they are installed is not shown."
        ];
        return AppScanner.Scan(machine, ScanMode.Declared, DeclaredApplications.Parse(declared), targets, cartridges, null, limitations);
    }

    private static void WriteReport(AppScanReport report, string file, TextWriter output)
    {
        string path = Path.GetFullPath(file);
        CartridgeManifest.RejectLinks(path);
        PrivateFile.CreateDirectory(Path.GetDirectoryName(path)!);
        PrivateFile.WriteAllText(path, JsonSerializer.Serialize(report, ReportJson), replace: true);
        output.WriteLine();
        output.WriteLine("Full report (private, contains the list of installed software): " + path);
    }

    // Throws ArgumentException when no cartridges directory is given or present.
    private static string DefaultCartridgeDirectory()
    {
        if (Directory.Exists("cartridges"))
        {
            return "cartridges";
        }
        throw new ArgumentException("--cartridges <directory> required (no 'cartridges' directory in the current folder).");
    }

    private static string? DefaultTargetsFile(string cartridgeDirectory)
    {
        string candidate = Path.Combine(cartridgeDirectory, "targets.json");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string CurrentSystemName()
    {
        if (OperatingSystem.IsWindows())
            return "windows";
        if (OperatingSystem.IsMacOS())
            return "macos";
        return "linux";
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

// Use case: whether a program is running right now. Only the names of running processes are read; command
// lines, window titles and users are not, because the scan asks "is it up?" and not "what is it doing?".
public static class RunningProcesses
{
    public static IReadOnlySet<string> Names()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                names.Add(process.ProcessName.ToLowerInvariant());
            }
        }
        return names;
    }
}
