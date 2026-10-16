using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Transport;

// Thousands of bundles remain metadata until selected. New bundles and changed manifests must be seen by a live
// library/session without a new MCP catalog, and no application function may run before the matching Info binding.
internal static class CartridgeLibraryCases
{
    private const string BundlePath = "cartridges/programs/outlook-classic/16.0.14334.20918";

    public static async Task<int> RunAsync(string temporaryRoot)
    {
        var checks = new Checks("Cartridge library");
        string root = Path.Combine(temporaryRoot, "cartridge-library");
        Directory.CreateDirectory(root);
        string bundle = CopyBundle(root);
        var library = new GatewayCartridgeLibrary(root);
        JsonObject initial = library.Browse("Catalog");
        checks.That(initial["total"]!.GetValue<int>() == 1 && initial["cartridges"]!.AsArray().Count == 1,
            "initial discovery reads compact metadata without recipe schemas");
        CheckLazyDiscovery(checks, root, library);
        CheckProgramRevisionIdentity(checks, root, library, bundle);
        CheckChangedSelection(checks, bundle);
        await CheckSelectionRoutingAsync(checks, root, library);

        return checks.Passed;
    }

    private static void CheckProgramRevisionIdentity(Checks checks, string root, GatewayCartridgeLibrary library, string current)
    {
        JsonObject oldRevision = JsonNode.Parse(File.ReadAllText(current))!.AsObject();
        oldRevision["version"] = "0.1.0";
        string directory = Path.Combine(root, "previous-revision");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cartridge.json"), oldRevision.ToJsonString());
        JsonObject page = library.Browse("Catalog");
        checks.That(page["cartridges"]!.AsArray().OfType<JsonObject>()
            .Count(entry => entry["application"]!.ToString() == "Microsoft Outlook") == 1,
            "one program build is one cartridge; previous cartridge revisions do not become separate catalog entries");
    }

    private static string CopyBundle(string root)
    {
        string source = Path.Combine(RepositoryLocator.Root(), BundlePath);
        string destination = Path.Combine(root, "outlook");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string copied = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
            File.Copy(file, copied);
        }

        return Path.Combine(destination, "cartridge.json");
    }

    private static void CheckLazyDiscovery(Checks checks, string root, GatewayCartridgeLibrary library)
    {
        const int AdditionalCartridges = 1001;
        for (int index = 0; index < AdditionalCartridges; index++)
        {
            string directory = Path.Combine(root, "metadata-" + index);
            Directory.CreateDirectory(directory);
            var manifest = new JsonObject
            {
                ["format"] = "k-lani-bot.local-macro-bundle.v1",
                ["id"] = "sample." + index,
                ["version"] = "1.0.0",
                ["target"] = new JsonObject
                {
                    ["application"] = "Metadata fixture",
                    ["application_version"] = "1.0",
                    ["tested_platform"] = "windows-x64"
                },
                ["entrypoints"] = new JsonArray(new JsonObject { ["name"] = "not-loaded", ["recipe"] = "absent.json" })
            };
            File.WriteAllText(Path.Combine(directory, "cartridge.json"), manifest.ToJsonString());
        }
        JsonObject current = library.Browse("Catalog");
        checks.That(current["total"]!.GetValue<int>() == AdditionalCartridges + 1 && current["cartridges"]!.AsArray().Count == 20,
            "newly installed metadata for over a thousand cartridges appears in the existing library in bounded pages");
        bool containsSchemas = current.ToJsonString().Contains("inputSchema", StringComparison.Ordinal);
        checks.That(current["next_path"]!.ToString() == "Catalog/20" && !containsSchemas,
            "discovery neither loads absent recipe files nor emits unrelated tool schemas");
        checks.That(library.Browse("Catalog/1000")["cartridges"]!.AsArray().Count == 2,
            "the final page remains bounded and complete");
    }

    private static void CheckChangedSelection(Checks checks, string manifestPath)
    {
        using var selected = new SelectedGatewayCartridge(manifestPath);
        checks.That(selected.Refresh() == CartridgeReload.Unchanged, "an unchanged manifest preserves its selected snapshot");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["version"] = "9.0.0";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        checks.That(selected.Refresh() == CartridgeReload.Replaced && selected.Bundle.CartridgeVersion == "9.0.0",
            "an existing selection detects and validates a new manifest without restarting the library");
    }

    private static async Task CheckSelectionRoutingAsync(Checks checks, string root, GatewayCartridgeLibrary library)
    {
        string registry = CreateRegistry(root);
        using var client = new NodeTransportClient("fixture", registry);
        using var session = new CartridgeMcpSession(library,
            new Dictionary<string, NodeTransportClient> { ["fixture"] = client }, "fixture",
            (_, _, _, _) => throw new Exception("No recipe should run during metadata selection."));
        JsonObject catalog = await session.CallAsync("cartridge_browse", new JsonObject(), CancellationToken.None);
        checks.That(catalog["cartridges"] is JsonArray && catalog["functions"] == null,
            "the initial empty browse discovers a compact catalog without application schemas or network actions");
        string key = catalog["cartridges"]!.AsArray().OfType<JsonObject>().Single(entry =>
            entry["application"]!.ToString() == "Microsoft Outlook")["key"]!.ToString();
        JsonObject selected = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "cartridge_select",
            ["arguments"] = new JsonObject { ["key"] = key }
        }, CancellationToken.None);
        checks.That(selected["next"]!.ToString() == "cartridge_info", "selection requires fresh application Info afterward");
        bool refused = false;
        try
        {
            await session.CallAsync("cartridge_call", new JsonObject { ["function"] = "outlook_move" }, CancellationToken.None);
        }
        catch (ArgumentException)
        {
            refused = true;
        }
        checks.That(refused, "a newly selected cartridge cannot run a mutation under another cartridge's binding");
        var query = new JsonObject { ["path"] = "Info/Coverage" };
        JsonObject coverage = await session.CallAsync("cartridge_browse", query, CancellationToken.None);
        checks.That(coverage["requirements"]!.AsArray().Count <= 20,
            "coverage is inspectable before application access without loading the full requirement inventory");
    }

    private static string CreateRegistry(string root)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=library fixture", key, HashAlgorithmName.SHA256);
        using X509Certificate2 authority = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        byte[] bytes = authority.RawData;
        var document = new JsonObject
        {
            ["version"] = 1,
            ["nodes"] = new JsonObject
            {
                ["fixture"] = new JsonObject
                {
                    ["url"] = "https://127.0.0.1:9",
                    ["root_der_base64"] = Convert.ToBase64String(bytes),
                    ["root_sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    ["token_file"] = Path.Combine(root, "unused")
                }
            }
        };
        string path = Path.Combine(root, "nodes.json");
        File.WriteAllText(path, document.ToJsonString());

        return path;
    }
}
