using KLaniBot.Cartridges;

namespace KLaniBot.Inventory;

// What an app scan reads: the whole inventory, only the target programs, or programs a person names.
public enum ScanMode
{
    // Everything the system lists as installed, each entry checked against the targets and cartridges.
    Inventory,

    // Only the programs named in the target catalogue; nothing else is reported.
    TargetsOnly,

    // The programs a person states by name; the machine is not read.
    Declared
}

// The scanned computer as the report names it: name, system, version and architecture.
public sealed record ScanMachineInfo(string Name, string OsFamily, string OsVersion, string Architecture);

// A cartridge as the report shows it: what it is for, which version and platform it was tested on and how.
public sealed record OfferSummary(
    string CartridgeId,
    string CartridgeVersion,
    string Application,
    string TestedApplicationVersion,
    string Platform,
    string Architecture,
    CartridgeValidation Validation,
    bool ReleaseApproved,
    string[] EntryPoints);

// The best cartridge for a row, how well it fits and what the scan recommends.
public sealed record CartridgeMatchSummary(
    OfferSummary Cartridge,
    CartridgeFit Fit,
    VersionDirection Direction,
    PlatformFit Platform,
    ScanRecommendation Recommendation,
    int OtherCartridges);

// One installed program in the report, with its target and cartridge if any.
public sealed record ScanRow(
    string Name,
    string Version,
    string Publisher,
    string Source,
    string Location,
    ApplicationKind Kind,
    bool? Running,
    string? TargetId,
    string[] Routes,
    CartridgeMatchSummary? Match);

// A target program that was not found on the computer, with the routes and cartridges known for it.
public sealed record MissingTarget(string TargetId, string Name, string[] Routes, OfferSummary[] KnownCartridges);

// The result of the app scan command, written as JSON and rendered as text for the administrator.
public sealed record AppScanReport(
    DateTimeOffset CapturedAt,
    ScanMachineInfo Machine,
    ScanMode Mode,
    int TargetCount,
    int CartridgeCount,
    ScanRow[] Rows,
    MissingTarget[] TargetsNotFound,
    string[] Limitations);
