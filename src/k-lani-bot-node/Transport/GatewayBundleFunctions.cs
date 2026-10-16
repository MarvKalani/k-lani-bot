using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions.Handlers;

namespace KLaniBot.Transport;

// One function of a local macro bundle under the name its recipe is registered by; the manifest entry name is not callable.
// RequiredParameters excludes pid, which the caller passes separately; only required ones are named so a window row stays short.
public sealed record GatewayBundleFunction(string Name, string Description, IReadOnlyList<string> RequiredParameters,
    bool StartsProgram);

// Use case: the program catalog and ui_info need the callable names and parameters of an installed bundle without loading it into
// a gateway session. Every recipe is read through the same pinned-file check that loading applies, so a modified bundle lists nothing.
public static class GatewayBundleFunctions
{
    private const string PidParameter = "pid";

    // Returns false for a damaged, modified or unsupported bundle; the caller then lists no function of it.
    public static bool TryRead(string manifestPath, out IReadOnlyList<GatewayBundleFunction> functions)
    {
        functions = [];
        try
        {
            functions = ReadAll(manifestPath);

            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException
            or InvalidOperationException or NullReferenceException or ArgumentException)
        {
            return false;
        }
    }

    // Throws InvalidDataException for unpinned or changed recipes and JsonException for unreadable ones.
    private static IReadOnlyList<GatewayBundleFunction> ReadAll(string manifestPath)
    {
        JsonObject manifest = GatewayCartridges.ReadManifest(manifestPath);
        string directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var functions = new List<GatewayBundleFunction>();
        foreach (JsonObject entry in manifest["entrypoints"]!.AsArray().OfType<JsonObject>())
        {
            if (entry["testing_only"]?.GetValue<bool>() == true)
            {
                continue;
            }
            byte[] bytes = GatewayCartridges.ReadPinnedFile(manifest, directory, entry["recipe"]!.ToString(),
                GatewayCartridges.MaximumRecipeBytes);
            functions.Add(Describe(JsonSerializer.Deserialize<MacroDefinition>(bytes)!));
        }

        return functions;
    }

    private static GatewayBundleFunction Describe(MacroDefinition macro)
    {
        string[] required = macro.Parameters.Where(parameter => parameter.Required && parameter.Name != PidParameter)
            .Select(parameter => parameter.Name).ToArray();

        return new GatewayBundleFunction(macro.Name, macro.Description, required, GatewayRecipe.StartsProgram(macro));
    }
}
