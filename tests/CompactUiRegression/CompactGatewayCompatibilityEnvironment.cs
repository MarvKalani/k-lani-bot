using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// A declared synthetic app/issuer fixture owns independent field state and real private signed files, not installed-app acceptance.
internal sealed class CompactGatewayCompatibilityEnvironment : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "k-lani-compat-routing-" + Guid.NewGuid());
    public NativeAdapterFixture Client { get; } = new() { Dialog = true };
    public CompactStorageRoots Roots
    {
        get;
    }
    public CompactAcceptanceAuthority Authority { get; } = new("fixture-node", "owner", "fixture-connection");
    public CompactProgramIdentity Application { get; } = new("fixture-editor", "1.0");
    public string Name { get; private set; } = "";
    public string File => Directory.GetFiles(Roots.Library, "generated-ui.json", SearchOption.AllDirectories).Single();
    public CompactGatewaySession Session { get; private set; } = null!;

    public CompactGatewayCompatibilityEnvironment()
    {
        Roots = new(Path.Combine(Root, "library"), Path.Combine(Root, "learning"));
        Directory.CreateDirectory(Roots.Library);
    }

    public async Task CaptureAsync(bool acceptance = true)
    {
        Session = CreateSession(acceptance ? new(Path.Combine(Root, "evidence"), Authority) : null);
        await Session.CallAsync("ui_info", new(), CancellationToken.None);
        var view = await Session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10 }, CancellationToken.None);
        string reference = view["elements"]!.AsArray().OfType<JsonObject>()
            .Single(element => element["name"]!.ToString() == "Field")["ref"]!.ToString();
        var written = await Session.CallAsync("ui_act", new JsonObject
        {
            ["pid"] = 10,
            ["result_mode"] = "summary",
            ["steps"] = new JsonArray(new JsonObject { ["action"] = "write", ["ref"] = reference, ["text"] = "synthetic demonstration" })
        }, CancellationToken.None);
        Require(written["success"]?.ToString() == "true" && written["mcp"]!["functions"]!.AsArray().Count == 0,
            "setup first-use capture is verified and not announced");
        var browsed = await Session.CallAsync("cartridge_browse", new JsonObject { ["topic"] = "Use/UI" }, CancellationToken.None);
        Name = browsed["functions"]!.AsArray().OfType<JsonObject>().Single()["name"]!.ToString();
    }

    public CompactGatewaySession CreateSession(CompactAcceptanceSettings? settings, bool learning = true)
    {
        return new(Client, "fixture", learning ? Roots : Roots with
        {
            Learning = null
        }, () => "2025-11-25",
            configuration: new(settings));
    }

    public Task<JsonObject> CallAsync(string function, JsonObject arguments, CompactGatewaySession? session = null)
    {
        return (session ?? Session).CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = function,
            ["arguments"] = arguments
        }, CancellationToken.None);
    }

    public JsonObject Revalidation(CompactAcceptanceReference receipt, long revision = 1)
    {
        return new JsonObject { ["function"] = Name, ["expected_revision"] = revision, ["evidence_ref"] = receipt.Value.ToString() };
    }

    public async Task<CompactAcceptanceReference> IssueSyntheticReceiptAsync()
    {
        var function = new CompactGatewayPrograms(Roots).Get(Application, Name);
        var capabilities = await new CompactNodeAccess(Client, Authority.Node)
            .CapabilitiesAsync(CompactContractHash.RequiredActions(function.Draft), CancellationToken.None);
        var current = new CompactObservedCompatibility(capabilities.Version, CompactCompatibilityWire.Wire("2025-11-25"),
            capabilities.Platform, Application, capabilities.Actions, capabilities.Implementation, CompactGatewayPrograms.Implementation());
        Require(CompactAcceptanceStore.TryCreateForIsolatedRunner(Path.Combine(Root, "evidence"), Roots.Library, Authority, out var store),
            "separate synthetic issuer creates private test evidence");
        var proof = new CompactAcceptanceProof(CompactAcceptanceTest.Functional,
            new(CompactAcceptanceIsolationKind.PrivateVirtualDesktop, Authority.Node), true,
            CompactContractHash.Of(JsonValue.Create(Client.Field)), CompactContractHash.Of(JsonValue.Create(Client.Mutations.Count)));
        Require(store.Value!.TryRecord(function, current, proof, out var receipt), "separate signed synthetic acceptance receipt");

        return receipt.Value!;
    }

    public static void Require(bool condition, string name)
    {
        WorkflowEngineSyntheticAdapter.Require(condition, name);
    }

    public void Dispose()
    {
        Session?.Dispose();
        Directory.Delete(Root, true);
    }
}
