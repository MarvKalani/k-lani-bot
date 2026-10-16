using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// How many installed cartridges of an observed build offer a function.
public enum CompactCartridgeOutcome
{
    None,
    Unique,
    Several
}

// The cartridge a call resolves to; Program is set only for a unique offer.
public sealed record CompactCartridgeChoice(CompactCartridgeOutcome Outcome, GatewayProgramCartridge? Program);

// Use case: ui_info lists the functions of installed program bundles per window row, and cartridge_call resolves a function by the
// pid's window to the one bundle of exactly that process name and build, so no catalog selection is needed between programs.
public sealed class CompactInstalledCartridges
{
    // A window row names this many bundle functions; the count of the rest keeps the reply small.
    public const int FunctionLimit = CompactFunctionSummary.Limit;

    private readonly IReadOnlyList<GatewayProgramCartridge> _programs;

    // An unreadable catalog offers nothing: ui_info stays usable and a call falls back to its other routes.
    public CompactInstalledCartridges(string libraryRoot)
    {
        _programs = ReadPrograms(libraryRoot);
    }

    // Exact process name and application build; a bundle for another build never serves this window.
    public IReadOnlyList<GatewayProgramCartridge> Matching(CompactNativeWindow window)
    {
        return _programs.Where(program => string.Equals(program.ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase)
            && program.Identity.Build == window.Build).ToArray();
    }

    // Several bundles of one build can offer a function; the program selected earlier then decides, otherwise the call is refused.
    public CompactCartridgeChoice Choose(CompactNativeWindow window, string function, GatewayProgramCartridge? selected)
    {
        var offering = Matching(window).Where(program => program.Native!.EntryPoints.Contains(function, StringComparer.Ordinal))
            .ToArray();
        if (offering.Length == 0)
        {
            return new CompactCartridgeChoice(CompactCartridgeOutcome.None, null);
        }
        var chosen = offering.Where(program => program.Key == selected?.Key).ToArray();
        if (chosen.Length == 1)
        {
            return new CompactCartridgeChoice(CompactCartridgeOutcome.Unique, chosen[0]);
        }

        return offering.Length == 1 ? new CompactCartridgeChoice(CompactCartridgeOutcome.Unique, offering[0])
            : new CompactCartridgeChoice(CompactCartridgeOutcome.Several, null);
    }

    // Functions that need a running window; a recipe that starts the program has no row to appear in.
    public IReadOnlyList<GatewayBundleFunction> FunctionsOf(CompactNativeWindow window)
    {
        var functions = new List<GatewayBundleFunction>();
        foreach (var program in Matching(window))
        {
            if (GatewayBundleFunctions.TryRead(program.Native!.Path, out var read))
            {
                functions.AddRange(read.Where(function => !function.StartsProgram));
            }
        }

        return functions.GroupBy(function => function.Name).Select(group => group.First())
            .OrderBy(function => function.Name, StringComparer.Ordinal).ToArray();
    }

    // The row entry of a bundle function: name, description and required parameters, the shape of a stored function without steps.
    public static JsonObject ToJson(GatewayBundleFunction function)
    {
        var json = new JsonObject { ["name"] = function.Name };
        if (function.Description.Length > 0)
        {
            json["description"] = function.Description;
        }
        if (function.RequiredParameters.Count > 0)
        {
            json["parameters"] = new JsonArray(function.RequiredParameters.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        }

        return json;
    }

    private static IReadOnlyList<GatewayProgramCartridge> ReadPrograms(string libraryRoot)
    {
        try
        {
            return new GatewayProgramCatalog(libraryRoot).Read().Where(program => program.Native != null).ToArray();
        }
        catch (Exception exception) when (exception is CompactNodeException or InvalidDataException or JsonException or IOException)
        {
            return [];
        }
    }
}
