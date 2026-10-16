using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Transport;
using KLaniBot.Transport.CompactUi;

// Functions of installed program bundles appear in ui_info rows and run by pid, without cartridge_select or cartridge_info.
internal static class InstalledCartridgeCases
{
    private const string NoCartridgeRefusal = "No installed cartridge of this build offers ";

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-installed-cartridges-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await ListsAndRunsByPidAsync(Path.Combine(root, "pair"));
            await RefusesSeveralOffersAsync(Path.Combine(root, "several"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Two programs on one desktop: each row lists its own bundle, and calls alternate by pid with one select and info per switch.
    private static async Task ListsAndRunsByPidAsync(string root)
    {
        WriteBundle(root, "bundle-a", "fixture-editor", "1.0", "write-text", "editor_write_text", ["text", "mode?"]);
        WriteBundle(root, "bundle-b", "second-editor", "2.0", "list-items", "second_list_items", []);
        WriteBundle(root, "bundle-old", "fixture-editor", "9.9", "old", "editor_old", []);
        var catalog = new GatewayProgramCatalog(root).Read();
        string keyA = catalog.Single(program => program.Native?.CartridgeId == "bundle-a").Key;
        string keyB = catalog.Single(program => program.Native?.CartridgeId == "bundle-b").Key;
        Require(catalog.Single(program => program.Key == keyB).Native!.EntryPoints.SequenceEqual(new[] { "second_list_items" }),
            "the catalog names a bundle function by its recipe name");
        var recorder = new NativeRouteRecorder();
        var client = new NativeAdapterFixture
        {
            DuplicateWindow = true,
            SecondProcess = true,
            SecondProcessName = "second-editor",
            SecondBuild = "2.0"
        };
        using var session = new CompactGatewaySession(client, "fixture", new(root, null), () => "2025-06-18", recorder.RouteAsync);
        var info = await session.CallAsync("ui_info", new(), CancellationToken.None);
        RequireRows(info);

        await Call(session, "editor_write_text", 10, "text");
        await Call(session, "second_list_items", 11, null);
        await Call(session, "editor_write_text", 10, "text");
        await Call(session, "editor_write_text", 10, "text");
        Require(recorder.Calls.SequenceEqual(new[]
        {
            "select:" + keyA, "info:10", "run:editor_write_text", "select:" + keyB, "info:11", "run:second_list_items",
            "select:" + keyA, "info:10", "run:editor_write_text", "run:editor_write_text"
        }), "alternating programs by pid selects and binds once per switch: " + string.Join(",", recorder.Calls));

        await RequireRefusalAsync(session, "editor_old", 10, "a bundle of another build is not offered to the pid");
        await RequireRefusalAsync(session, "editor_write_text", 11, "a function of program A is refused for the pid of program B");
        await RequireRefusalAsync(session, "list-items", 11, "the manifest entry name is not callable, only the recipe name");
        await RequireRefusalAsync(session, "unknown_function", 10, "an unknown function by pid names the missing cartridge");
    }

    private static void RequireRows(JsonObject info)
    {
        var rowA = Row(info, 100)["functions"]!.AsArray().OfType<JsonObject>().Single();
        var rowB = Row(info, 101)["functions"]!.AsArray().OfType<JsonObject>().Single();
        Require(rowA["name"]!.ToString() == "editor_write_text" && rowA["description"]!.ToString() == "Writes text.",
            "the row of program A lists its bundle function with its description");
        Require(rowA["parameters"]!.AsArray().Select(parameter => parameter!.ToString()).SequenceEqual(new[] { "text" }),
            "pid and optional parameters are left out of the listed parameters");
        Require(rowB["name"]!.ToString() == "second_list_items" && !rowB.ContainsKey("parameters"),
            "the row of program B lists the recipe name, not the manifest entry name");
        Require(info["next"]!.ToString().Contains(CompactGatewaySession.BundleUsageHint), "ui_info names the direct call by pid");
    }

    // Two bundles of one build offering one function cannot be told apart by the pid; only cartridge_select chooses.
    private static async Task RefusesSeveralOffersAsync(string root)
    {
        WriteBundle(root, "bundle-one", "fixture-editor", "1.0", "write", "editor_write_text", ["text"]);
        WriteBundle(root, "bundle-two", "fixture-editor", "1.0", "write", "editor_write_text", ["text"]);
        var recorder = new NativeRouteRecorder();
        using var session = new CompactGatewaySession(new NativeAdapterFixture(), "fixture", new(root, null), () => "2025-06-18",
            recorder.RouteAsync);
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        var reply = await Call(session, "editor_write_text", 10, "text");
        Require(reply["success"]!.GetValue<bool>() == false && reply["error"]!.ToString().StartsWith("Several cartridges offer")
            && recorder.Calls.Count == 0, "several offers are refused without touching the native route");
    }

    private static async Task RequireRefusalAsync(CompactGatewaySession session, string function, int pid, string name)
    {
        var reply = await Call(session, function, pid, null);
        Require(reply["success"]!.GetValue<bool>() == false && reply["error"]!.ToString() == NoCartridgeRefusal + function + ".", name);
    }

    private static Task<JsonObject> Call(CompactGatewaySession session, string function, int pid, string? text)
    {
        var arguments = new JsonObject { ["pid"] = pid };
        if (text != null)
        {
            arguments["text"] = text;
        }

        return session.CallAsync("cartridge_call", new JsonObject { ["function"] = function, ["arguments"] = arguments },
            CancellationToken.None);
    }

    // Writes a bundle whose single recipe is pinned by size and hash, the way a reviewed bundle is. A parameter ending in ? is optional.
    private static void WriteBundle(string root, string id, string process, string build, string entry, string macro,
        string[] parameters)
    {
        string directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        byte[] bytes = WriteRecipe(directory, macro, parameters);
        var manifest = new JsonObject
        {
            ["format"] = "k-lani-bot.local-macro-bundle.v1",
            ["id"] = id,
            ["version"] = "1",
            ["target"] = new JsonObject
            {
                ["application"] = "Synthetic " + id,
                ["application_version"] = build,
                ["process_name"] = process,
                ["tested_platform"] = "linux-x64"
            },
            ["entrypoints"] = new JsonArray(new JsonObject { ["name"] = entry, ["recipe"] = "recipe.json" }),
            ["files"] = new JsonArray(new JsonObject
            {
                ["path"] = "recipe.json",
                ["bytes"] = bytes.Length,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes))
            })
        };
        File.WriteAllText(Path.Combine(directory, "cartridge.json"), manifest.ToJsonString());
    }

    // The recipe's required integer pid comes first; the other parameters follow, optional when they end in ?.
    private static byte[] WriteRecipe(string directory, string macro, string[] parameters)
    {
        var recipeParameters = new JsonArray(new JsonObject { ["name"] = "pid", ["type"] = "integer", ["required"] = true });
        foreach (string parameter in parameters)
        {
            recipeParameters.Add(new JsonObject
            {
                ["name"] = parameter.TrimEnd('?'),
                ["type"] = "string",
                ["required"] = !parameter.EndsWith('?')
            });
        }
        var recipe = new JsonObject
        {
            ["name"] = macro,
            ["description"] = "Writes text.",
            ["parameters"] = recipeParameters,
            ["steps"] = new JsonArray()
        };
        byte[] bytes = Encoding.UTF8.GetBytes(recipe.ToJsonString());
        File.WriteAllBytes(Path.Combine(directory, "recipe.json"), bytes);

        return bytes;
    }

    private static JsonObject Row(JsonObject info, long window)
    {
        return info["windows"]!.AsArray().OfType<JsonObject>().Single(row => row["window"]!.GetValue<long>() == window);
    }

    private static void Require(bool condition, string name)
    {
        CompactGatewayCases.Require(condition, name);
    }

    // Stands in for the native cartridge session and records which selection, binding and run each call caused.
    private sealed class NativeRouteRecorder
    {
        public List<string> Calls { get; } = [];

        public Task<JsonObject> RouteAsync(string tool, JsonObject arguments, CancellationToken cancellation)
        {
            string function = arguments["function"]?.ToString() ?? "";
            if (tool == "cartridge_info")
            {
                Calls.Add("info:" + arguments["pid"]);
            }
            else if (function == GatewayCartridgeLibrary.SelectFunction)
            {
                Calls.Add("select:" + arguments["arguments"]!["key"]);
            }
            else
            {
                Calls.Add("run:" + function);
            }

            return Task.FromResult(new JsonObject { ["success"] = true });
        }
    }
}
