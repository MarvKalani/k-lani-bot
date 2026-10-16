using System.Text.Json.Nodes;
using KLaniBot.Transport;
using KLaniBot.Transport.CompactUi;

// Synthetic program metadata exercises real file discovery, catalog merging and gateway routing without claiming real
// application acceptance.
internal static class ProgramCatalogCases
{
    public static async Task RunAsync()
    {
        using var test = new NativeAdapterEnvironment();
        var function = test.Write();
        string root = Path.Combine(test.Root, "catalog");
        Directory.CreateDirectory(root);
        MetadataAndVariants(root, function);
        LegacyWrites(Path.Combine(test.Root, "legacy"), function);
        DuplicateAndFuture(Path.Combine(test.Root, "negative"), function);
        await SelectionAsync(Path.Combine(test.Root, "selected"), function);
    }

    private static void MetadataAndVariants(string root, CompactLoadedFunction function)
    {
        Native(root, "native", "fixture-variant", "fixture-editor", "1.0", "2.0");
        Native(root, "old-native", "fixture-variant", "fixture-editor", "1.0", "1.0");
        Generated(root, "legacy/plain-version", new("fixture-editor", "1.0"), function);
        var library = new GatewayCartridgeLibrary(root);
        var page = library.Browse("Catalog");
        Require(page["total"]!.GetValue<int>() == 1, "one native plus generated program/build entry");
        var entry = page["cartridges"]!.AsArray().Single()!.AsObject();
        Require(entry["cartridge_version"]!.ToString() == "2.0" && entry["generated_revision"]!.GetValue<long>() == 1,
            "native revision and generated revision remain metadata");
        Require(!page.ToJsonString().Contains("inputSchema", StringComparison.Ordinal), "discovery does not emit child schemas");
        Require(Path.GetFileName(library.Resolve(entry["key"]!.ToString())) == "cartridge.json", "native bundle remains resolvable");
        Native(root, "another-build", "fixture-variant", "fixture-editor", "2.0", "1.0");
        Require(library.Browse("Catalog")["total"]!.GetValue<int>() == 2, "different build stays a different cartridge");
        var identityOne = new CompactProgramIdentity("a:b", "c");
        var identityTwo = new CompactProgramIdentity("a", "b:c");
        Require(GatewayProgramCatalog.GeneratedKey(identityOne) != GatewayProgramCatalog.GeneratedKey(identityTwo),
            "opaque keys prevent delimiter collision");
        Native(root, "different-variant", "second-variant", "fixture-editor", "1.0", "1.0");
        var catalog = new GatewayProgramCatalog(root);
        Reject(() => catalog.Generated(new("fixture-editor", "1.0")), "multiple variants never inferred from one process alias");
    }

    private static void LegacyWrites(string root, CompactLoadedFunction function)
    {
        Directory.CreateDirectory(root);
        var identity = new CompactProgramIdentity("fixture-editor", "1.0");
        string file = Generated(root, "programs/fixture-editor/1.0", identity, function, version: 1);
        var programs = new CompactGatewayPrograms(new(root, root + "-private"));
        Require(programs.Read(identity).Single().Draft.Name == function.Draft.Name, "legacy directory discovered from stored identity");
        var writable = programs.Writable(identity);
        var contract = function.Compatibility.Contracts!;
        Require(writable.TryInstall(function.Draft with
        {
            Name = "second"
        }, contract with
        {
            FunctionScope = CompactContractHash.Function(function.Draft with
            {
                Name = "second"
            })
        }, out _), "write migrates active v1 document in place");
        Require(Directory.GetFiles(root, "generated-ui.json", SearchOption.AllDirectories).Length == 1,
            "migration creates no second active cartridge or revision folder");
        var migrated = JsonNode.Parse(File.ReadAllText(file))!;
        Require(migrated["version"]!.GetValue<int>() == 2 && migrated["revision"]!.GetValue<long>() == 2, "v2 revision replaces same file");
        Require(programs.Read(identity).Count == 2, "both verified functions retained");
    }

    private static void DuplicateAndFuture(string root, CompactLoadedFunction function)
    {
        Directory.CreateDirectory(root);
        var identity = new CompactProgramIdentity("fixture-editor", "1.0");
        string first = Generated(root, "one", identity, function);
        string second = Generated(root, "two", identity, function);
        var catalog = new GatewayProgramCatalog(root);
        Require(catalog.Read().Single().Generated!.Status == CompactGeneratedCatalogStatus.Ambiguous,
            "duplicate active files are explicit");
        Reject(() => new CompactGatewayPrograms(new(root, root + "-private")).Writable(identity),
            "duplicate write never picks one revision");
        File.Delete(second);
        var future = JsonNode.Parse(File.ReadAllText(first))!.AsObject();
        future["version"] = 999;
        File.WriteAllText(first, future.ToJsonString());
        string unchanged = File.ReadAllText(first);
        Require(catalog.Read().Single().Generated!.Status == CompactGeneratedCatalogStatus.UnsupportedVersion,
            "future version exposed as unsupported");
        Reject(() => new CompactGatewayPrograms(new(root, root + "-private")).Read(identity), "future document cannot execute");
        Require(File.ReadAllText(first) == unchanged, "future document was neither migrated nor overwritten");
        File.Delete(first);
        File.CreateSymbolicLink(first, Path.Combine(root, "missing-target"));
        Reject(() => catalog.Read(), "dangling active file link is rejected without following it");
    }

    private static async Task SelectionAsync(string root, CompactLoadedFunction function)
    {
        Directory.CreateDirectory(root);
        Generated(root, "one", new("fixture-editor", "1.0"), function);
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = new CompactGatewaySession(client, "fixture", new(root, null), () => "2025-06-18");
        var catalog = await session.CallAsync("cartridge_browse", new JsonObject { ["topic"] = "Catalog" }, CancellationToken.None);
        string key = catalog["cartridges"]!.AsArray().Single()!["key"]!.ToString();
        await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "cartridge_select",
            ["arguments"] = new JsonObject { ["key"] = key }
        }, CancellationToken.None);
        var beforeInfo = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "write",
            ["arguments"] = new JsonObject { ["pid"] = 10, ["parameters"] = new JsonObject { ["text"] = "never" } }
        }, CancellationToken.None);
        Require(beforeInfo["success"]?.ToString() == "false" && client.Mutations.Count == 0,
            "catalog selection does not grant application access");
        var info = await session.CallAsync("cartridge_info", new(), CancellationToken.None);
        Require(info["success"]?.ToString() == "true" && info["selected"]?.ToString() == key,
            "selected generated program gets actual Info");
        var schema = await session.CallAsync("cartridge_browse", new JsonObject { ["function"] = "write" }, CancellationToken.None);
        Require(schema["function"]?["inputSchema"] != null, "only requested function schema loaded after Info");
        var identity = new CompactProgramIdentity("fixture-editor", "1.0");
        var bound = new CompactGatewayPrograms(new(root, null)).BoundIdentity(new(10, "fixture-editor", "1.0", "Editor", 100, "start",
            CompactWindowKind.Main));
        Require(bound == identity,
            "legacy identity remains unchanged");
    }

    private static void Native(string root, string directory, string key, string process, string build, string revision)
    {
        string path = Path.Combine(root, directory);
        Directory.CreateDirectory(path);
        var manifest = new JsonObject
        {
            ["format"] = "k-lani-bot.local-macro-bundle.v1",
            ["id"] = key,
            ["version"] = revision,
            ["target"] = new JsonObject
            {
                ["application"] = "Synthetic editor",
                ["application_version"] = build,
                ["process_name"] = process,
                ["tested_platform"] = "linux-x64"
            },
            ["entrypoints"] = new JsonArray(new JsonObject { ["name"] = "native-fixture", ["recipe"] = "not-loaded.json" })
        };
        File.WriteAllText(Path.Combine(path, "cartridge.json"), manifest.ToJsonString());
    }

    private static string Generated(string root, string directory, CompactProgramIdentity application, CompactLoadedFunction function,
        int version = 2)
    {
        string path = Path.Combine(root, directory);
        Directory.CreateDirectory(path);
        string file = Path.Combine(path, "generated-ui.json");
        var document = new JsonObject
        {
            ["format"] = CompactFunctionDocumentCodec.Format,
            ["version"] = version,
            ["revision"] = 1,
            ["application"] = new JsonObject { ["key"] = application.Key, ["version"] = application.Build },
            ["functions"] = new JsonArray(function.Document.DeepClone()),
            ["candidates"] = new JsonArray()
        };
        File.WriteAllText(file, document.ToJsonString());

        return file;
    }

    private static void Reject(Action action, string name)
    {
        try
        {
            action();
        }
        catch (CompactNodeException)
        {
            return;
        }
        throw new InvalidOperationException("Expected catalog refusal: " + name);
    }

    private static void Require(bool condition, string name)
    {
        WorkflowEngineSyntheticAdapter.Require(condition, name);
    }
}
