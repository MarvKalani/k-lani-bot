using System.Text.Json;

namespace KLaniBot.Transport.CompactUi;

// Use case: ui_info calls this once per observation. For each application instance whose known build has no cartridge, but whose
// process variant has stored functions for another build, it reads the needed window trees once and probes those functions.
// It never sends input, never changes a stored document and never makes an old-build function callable.
public sealed class CompactBuildDriftListing(CompactNodeAccess node, CompactGatewayPrograms programs)
{
    // A variant rarely keeps more than a few qualified builds; the newest ones by build text are reported.
    public const int BuildLimit = 3;

    // Throws only for cancellation; an unreadable catalog, document or tree leaves the instance without a report.
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<CompactBuildDriftReport>>> ListAsync(
        IReadOnlyList<CompactNativeWindow> windows, CancellationToken cancellation)
    {
        var reports = new Dictionary<long, IReadOnlyList<CompactBuildDriftReport>>();
        IReadOnlyList<GatewayProgramCartridge> catalog = ReadCatalog();
        if (catalog.Count == 0)
        {
            return reports;
        }
        foreach (CompactNativeWindow instance in Instances(windows))
        {
            if (!CompactGatewayLearning.TryIdentity(instance, out var current) || HasCartridge(catalog, current))
            {
                continue;
            }
            var sources = Sources(catalog, current);
            if (sources.Count == 0)
            {
                continue;
            }
            var assessed = await AssessAsync(instance, current, sources, windows, cancellation);
            if (assessed.Count > 0)
            {
                reports.Add(instance.Handle, assessed);
            }
        }

        return reports;
    }

    // One row per application instance carries the report: its main window when there is one, otherwise its first window.
    private static IReadOnlyList<CompactNativeWindow> Instances(IReadOnlyList<CompactNativeWindow> windows)
    {
        return windows.Where(window => window.Started != null)
            .GroupBy(window => new CompactDriftInstanceKey(window.ProcessId, window.Started!))
            .Select(group => group.FirstOrDefault(window => window.Kind == CompactWindowKind.Main) ?? group.First()).ToArray();
    }

    // A native bundle or a generated document for exactly this build is a qualified cartridge; its own listing applies instead.
    private static bool HasCartridge(IReadOnlyList<GatewayProgramCartridge> catalog, CompactProgramIdentity current)
    {
        return catalog.Any(entry => entry.Identity.Build == current.Build && IsVariant(entry, current.Key)
            || entry.Generated?.Application == current);
    }

    private static IReadOnlyList<CompactGeneratedCatalogEntry> Sources(IReadOnlyList<GatewayProgramCartridge> catalog,
        CompactProgramIdentity current)
    {
        return catalog.Where(entry => entry.Generated is { Status: CompactGeneratedCatalogStatus.Available, FunctionCount: > 0 }
                && IsVariant(entry, current.Key) && entry.Generated.Application.Build != current.Build)
            .Select(entry => entry.Generated!).OrderByDescending(entry => entry.Application.Build, StringComparer.Ordinal)
            .Take(BuildLimit).ToArray();
    }

    private static bool IsVariant(GatewayProgramCartridge entry, string processName)
    {
        return entry.ProcessName == processName || entry.Identity.Key == processName || entry.Generated?.Application.Key == processName;
    }

    private async Task<IReadOnlyList<CompactBuildDriftReport>> AssessAsync(CompactNativeWindow instance, CompactProgramIdentity current,
        IReadOnlyList<CompactGeneratedCatalogEntry> sources, IReadOnlyList<CompactNativeWindow> windows, CancellationToken cancellation)
    {
        var functionsByBuild = new Dictionary<CompactProgramIdentity, IReadOnlyList<CompactFunctionDraft>>();
        foreach (CompactGeneratedCatalogEntry source in sources)
        {
            var functions = ReadFunctions(source.Application);
            if (functions.Count > 0)
            {
                functionsByBuild.Add(source.Application, functions);
            }
        }
        if (functionsByBuild.Count == 0)
        {
            return [];
        }
        var kinds = CompactBuildDriftProbe.CheckedKinds(functionsByBuild.Values.SelectMany(functions => functions));
        var view = new CompactDriftView(instance, windows, await ReadTreesAsync(instance, windows, kinds, cancellation));

        return functionsByBuild.Select(source => new CompactBuildDriftReport(source.Key, current,
            source.Value.Select(function => CompactBuildDriftProbe.Assess(function, view)).ToArray())).ToArray();
    }

    // Only demonstrated functions are callable for their own build, so only those are worth a revalidation.
    private IReadOnlyList<CompactFunctionDraft> ReadFunctions(CompactProgramIdentity source)
    {
        try
        {
            return programs.Read(source).Where(CompactGatewayPrograms.IsDemonstrated).Select(function => function.Draft)
                .OrderBy(function => function.Name, StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (Unreadable(exception))
        {
            return [];
        }
    }

    // A kind with no window, several windows or an unreadable tree gets no tree; the probe names that window reason per step.
    private async Task<IReadOnlyDictionary<CompactWindowKind, CompactNativeObservation>> ReadTreesAsync(CompactNativeWindow instance,
        IReadOnlyList<CompactNativeWindow> windows, IReadOnlySet<CompactWindowKind> kinds, CancellationToken cancellation)
    {
        var trees = new Dictionary<CompactWindowKind, CompactNativeObservation>();
        foreach (CompactWindowKind kind in kinds)
        {
            var matches = windows.Where(window => window.ProcessId == instance.ProcessId && window.Started == instance.Started
                && window.Kind == kind).ToArray();
            if (matches.Length != 1)
            {
                continue;
            }
            try
            {
                trees.Add(kind, await node.ObserveAsync(new(matches[0].ProcessId, matches[0].Handle), false, cancellation));
            }
            catch (CompactNodeException)
            {
                // The probe reports this window as unproved instead of failing ui_info.
            }
        }

        return trees;
    }

    private IReadOnlyList<GatewayProgramCartridge> ReadCatalog()
    {
        try
        {
            return new GatewayProgramCatalog(programs.LibraryRoot).Read();
        }
        catch (Exception exception) when (Unreadable(exception))
        {
            return [];
        }
    }

    // Each reader reports damage with one of these exceptions; damage means no report, not a failed ui_info.
    private static bool Unreadable(Exception exception)
    {
        return exception is CompactNodeException or InvalidDataException or JsonException or IOException;
    }
}

// Windows of one application instance share its process and start time.
internal sealed record CompactDriftInstanceKey(int ProcessId, string Started);
