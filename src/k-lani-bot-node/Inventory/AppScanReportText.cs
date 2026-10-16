using System.Diagnostics;
using System.Text;
using KLaniBot.Cartridges;

namespace KLaniBot.Inventory;

// Use case: the administrator reads the result in a terminal. The text answers three questions in this order:
// what can we operate now, what exists but needs care, and what is missing. The complete list stays in the
// JSON file; the terminal shows every row only on request, because a machine lists hundreds of entries.
public static class AppScanReportText
{
    public static string Render(AppScanReport report, bool showAllRows)
    {
        var text = new StringBuilder();
        var applications = report.Rows.Where(row => row.Kind == ApplicationKind.Application).ToArray();
        int components = report.Rows.Length - applications.Length;
        var machine = report.Machine;
        text.AppendLine($"App scan {machine.Name} ({machine.OsFamily} {machine.OsVersion}, {machine.Architecture})");
        text.Append($"Mode: {ModeText(report.Mode)} · {report.CapturedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC · ");
        text.AppendLine($"Target catalogue: {report.TargetCount} · Cartridges: {report.CartridgeCount}");
        text.AppendLine();
        text.AppendLine(Summary(applications, components, report));
        AppendSection(text, "With cartridge", applications.Where(row => row.Match != null).ToArray(), MatchedLine);
        AppendSection(
            text,
            "Target programs without a matching cartridge (installed here)",
            applications.Where(row => row.Match == null && row.TargetId != null).ToArray(),
            UnmatchedTargetLine);
        AppendTargetsNotFound(text, report);
        AppendOtherApplications(text, applications.Where(row => row.Match == null && row.TargetId == null).ToArray(), showAllRows);
        if (showAllRows && components > 0)
        {
            AppendComponents(text, report, components);
        }
        AppendLimitations(text, report);
        return text.ToString();
    }

    private static void AppendTargetsNotFound(StringBuilder text, AppScanReport report)
    {
        if (report.TargetsNotFound.Length == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine("Target programs not found on this computer");
        foreach (var missing in report.TargetsNotFound)
        {
            text.AppendLine("  " + missing.Name + RoutesText(missing.Routes) + CartridgesText(missing.KnownCartridges));
        }
    }

    // Usually a long list, so only its count is shown unless every row was asked for.
    private static void AppendOtherApplications(StringBuilder text, ScanRow[] others, bool showAllRows)
    {
        if (others.Length == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine($"Other applications without target profile or cartridge: {others.Length}");
        if (!showAllRows)
        {
            text.AppendLine("  (full list: --all or in the JSON file)");
            return;
        }
        foreach (var row in others)
        {
            string version = row.Version.Length > 0 ? " " + ShownVersion(row.Version) : "";
            string running = row.Running == true ? "  [running]" : "";
            text.AppendLine("  " + row.Name + version + running);
        }
    }

    private static void AppendComponents(StringBuilder text, AppScanReport report, int components)
    {
        text.AppendLine();
        text.AppendLine($"Components (runtimes, drivers, updates): {components}");
        foreach (var row in report.Rows.Where(row => row.Kind == ApplicationKind.Component))
        {
            text.AppendLine("  " + row.Name + (row.Version.Length > 0 ? " " + row.Version : ""));
        }
    }

    private static void AppendLimitations(StringBuilder text, AppScanReport report)
    {
        text.AppendLine();
        text.AppendLine("Limits of this scan");
        foreach (string limitation in report.Limitations)
        {
            text.AppendLine("  - " + limitation);
        }
        text.AppendLine("  - Control routes are hints from the target catalogue; check them before use.");
    }

    private static string Summary(ScanRow[] applications, int components, AppScanReport report)
    {
        var matched = applications.Where(row => row.Match != null).ToArray();
        int Count(ScanRecommendation recommendation) => matched.Count(row => row.Match!.Recommendation == recommendation);
        int running = applications.Count(row => row.Running == true);
        var line = new StringBuilder();
        line.Append($"Applications: {applications.Length}");
        if (applications.Any(row => row.Running != null))
        {
            line.Append($" (running: {running})");
        }
        line.Append($" · Components: {components}");
        line.AppendLine();
        line.Append($"With cartridge: {matched.Length} (usable: {Count(ScanRecommendation.UseCartridge)}, ");
        line.Append($"check first: {Count(ScanRecommendation.CheckThenUseCartridge)}, ");
        line.Append($"neighbour version: {Count(ScanRecommendation.TryNeighbourVersion)}, ");
        line.Append($"determine version first: {Count(ScanRecommendation.DetermineVersionFirst)}, ");
        line.Append($"other system: {Count(ScanRecommendation.CheckOtherPlatformCartridge)})");
        line.AppendLine();
        int targetsWithoutCartridge = applications.Count(row => row.Match == null && row.TargetId != null);
        line.Append($"Target program entries without cartridge: {targetsWithoutCartridge} · ");
        line.Append($"Target programs not found: {report.TargetsNotFound.Length}");
        return line.ToString();
    }

    private static void AppendSection(StringBuilder text, string title, ScanRow[] rows, Func<ScanRow, string> line)
    {
        if (rows.Length == 0)
        {
            return;
        }
        text.AppendLine();
        text.AppendLine(title);
        foreach (var row in rows)
        {
            text.AppendLine(line(row));
        }
    }

    // Package versions carry an epoch and a packaging revision ("4:26.2.5.2-0ubuntu0.26.04.1"); a person wants "26.2.5.2".
    private static string ShownVersion(string raw)
    {
        var version = ApplicationVersion.Parse(raw);
        return version.IsKnown ? string.Join('.', version.Components) : raw;
    }

    private static string MatchedLine(ScanRow row)
    {
        var match = row.Match!;
        var cartridge = match.Cartridge;
        string tested = cartridge.TestedApplicationVersion.Length > 0 ? cartridge.TestedApplicationVersion : "?";
        string version = row.Version.Length > 0 ? ShownVersion(row.Version) : "version unknown";
        return $"  {row.Name} {version}{(row.Running == true ? "  [running]" : "")}{LocationText(row)}\n"
            + $"      Cartridge {cartridge.CartridgeId} {cartridge.CartridgeVersion} "
            + $"(tested on {tested}, {cartridge.Platform}-{cartridge.Architecture}, {ValidationText(cartridge.Validation)})\n"
            + $"      Fit: {FitText(match.Fit, match.Direction)}{PlatformText(match.Platform)} "
            + $"→ {RecommendationText(match.Recommendation)}{RoutesText(row.Routes)}";
    }

    private static string UnmatchedTargetLine(ScanRow row)
    {
        string version = row.Version.Length > 0 ? " " + ShownVersion(row.Version) : "";
        string running = row.Running == true ? "  [running]" : "";
        return $"  {row.Name}{version}{running}{LocationText(row)} "
            + $"→ {RecommendationText(ScanRecommendation.AuthorCartridge)}{RoutesText(row.Routes)}";
    }

    private static string LocationText(ScanRow row)
    {
        return row.Location.Length > 0 ? "  [location: " + row.Location + "]" : "";
    }

    private static string CartridgesText(OfferSummary[] offers)
    {
        return offers.Length == 0
            ? ""
            : "  [cartridge available: "
                + string.Join(", ", offers.Select(offer => offer.CartridgeId + " " + offer.CartridgeVersion))
                + "]";
    }

    private static string RoutesText(string[] routes)
    {
        if (routes.Length == 0)
        {
            return "";
        }
        return "  [possible routes: " + string.Join(", ", routes.Select(RouteText)) + "]";
    }

    private static string RouteText(string route)
    {
        return route switch
        {
            "command_line" => "command line",
            "com" => "COM",
            "uno" => "UNO",
            "scripting_api" => "scripting API",
            "applescript" => "AppleScript",
            "file_format" => "file format",
            "config_file" => "configuration file",
            "registry" => "Registry",
            "accessibility" => "accessibility (user interface)",
            _ => route
        };
    }

    private static string ModeText(ScanMode mode)
    {
        return mode switch
        {
            ScanMode.Inventory => "inventory",
            ScanMode.TargetsOnly => "target programs only",
            ScanMode.Declared => "declared programs",
            _ => throw new UnreachableException()
        };
    }

    private static string ValidationText(CartridgeValidation validation)
    {
        return validation switch
        {
            CartridgeValidation.RealAppTested => "tested on the real application",
            CartridgeValidation.ObservationTested => "observation tested only",
            CartridgeValidation.FixtureTested => "tested on a test double only",
            CartridgeValidation.Unreviewed => "unreviewed",
            _ => throw new UnreachableException()
        };
    }

    private static string FitText(CartridgeFit fit, VersionDirection direction)
    {
        string where = direction switch
        {
            VersionDirection.InstalledIsOlder => "installed is older",
            VersionDirection.InstalledIsNewer => "installed is newer",
            VersionDirection.Equal or VersionDirection.Unknown => "",
            _ => throw new UnreachableException()
        };
        return fit switch
        {
            CartridgeFit.Exact => "exactly this version",
            CartridgeFit.WithinDeclaredRange => "within the version range",
            CartridgeFit.NeighbourSameSeries => "neighbour version, same series (" + where + ")",
            CartridgeFit.NeighbourSameMajor => "neighbour version, same major version (" + where + ")",
            CartridgeFit.OtherMajor => "other major version (" + where + ")",
            CartridgeFit.VersionUnknown => "version unknown",
            CartridgeFit.NoCartridge => "no cartridge",
            _ => throw new UnreachableException()
        };
    }

    private static string PlatformText(PlatformFit platform)
    {
        return platform switch
        {
            PlatformFit.OtherSystem => ", other operating system",
            PlatformFit.OtherArchitecture => ", other architecture",
            PlatformFit.Same or PlatformFit.Unknown => "",
            _ => throw new UnreachableException()
        };
    }

    private static string RecommendationText(ScanRecommendation recommendation)
    {
        return recommendation switch
        {
            ScanRecommendation.UseCartridge => "usable",
            ScanRecommendation.CheckThenUseCartridge => "check first (cartridge tested only on a test double or by observation)",
            ScanRecommendation.DetermineVersionFirst => "determine the version (for example under Help → About), then decide",
            ScanRecommendation.TryNeighbourVersion =>
                "test the neighbour version (observation and postconditions first, result only partly reliable)",
            ScanRecommendation.CheckOtherPlatformCartridge => "cartridge is for another system, check whether it carries over",
            ScanRecommendation.AuthorCartridge => "write a cartridge (computer use as author)",
            _ => throw new UnreachableException()
        };
    }
}
