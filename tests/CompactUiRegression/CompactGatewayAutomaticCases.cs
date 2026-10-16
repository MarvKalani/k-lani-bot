using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Automatic routing tests keep synthetic app state independent from real on-disk capture/restart/reuse behavior.
internal static class CompactGatewayAutomaticCases
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-auto-routing-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await RestartReuseAsync(root);
            await UnknownContractsAsync(root);
            await DriftAsync(root);
            await FailedWritesAsync(root);
            await CandidateAsync(root);
            await StorageFailureAsync(root);
            await NoLearningAsync(root);
            await KeyboardFallbackAsync(root);
            await UnnamedKeyboardRefusalAsync(root);
            await CodedRefusalAsync(root);
            await FreshFallbackAsync(root);
            await ClearAsync(root);
            await UncertainKeyboardAsync(root);
            await PriorWriteDriftAsync(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static CompactGatewaySession Session(NativeAdapterFixture client, string root, string name, bool learning = true)
    {
        string library = Path.Combine(root, name, "library");
        Directory.CreateDirectory(library);

        return new(client, "fixture", new(library, learning ? Path.Combine(root, name, "learning") : null), () => "2025-11-25");
    }

    private static async Task<string> ReferenceAsync(CompactGatewaySession session, string name = "Field")
    {
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        var view = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["full"] = true }, CancellationToken.None);

        return view["elements"]!.AsArray().OfType<JsonObject>().Single(element => element["name"]!.ToString() == name)["ref"]!.ToString();
    }

    private static Task<JsonObject> WriteAsync(CompactGatewaySession session, string reference, string value, bool compact = true)
    {
        var arguments = new JsonObject
        {
            ["pid"] = 10,
            ["steps"] = new JsonArray(new JsonObject { ["action"] = "write", ["ref"] = reference, ["text"] = value })
        };
        if (compact)
        {
            arguments["result_mode"] = "summary";
        }

        return session.CallAsync("ui_act", arguments, CancellationToken.None);
    }

    private static JsonObject Outcome(JsonObject reply)
    {
        Require(reply["mcp"] is JsonObject, "automatic routing must return capture metadata");

        return reply["mcp"]!["functions"]!.AsArray().Single()!.AsObject();
    }

    // A verified generated function is never announced in a reply; its name is found under its topic, as cartridge_browse lists it.
    private static async Task<string> GeneratedNameAsync(CompactGatewaySession session)
    {
        var browsed = await session.CallAsync("cartridge_browse", new JsonObject { ["topic"] = "Use/UI" }, CancellationToken.None);

        return browsed["functions"]!.AsArray().OfType<JsonObject>().Single()["name"]!.ToString();
    }

    private static async Task RestartReuseAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = Session(client, root, "restart");
        string reference = await ReferenceAsync(session);
        var first = await WriteAsync(session, reference, "private training sample");
        Require(first["success"]?.ToString() == "true" && first["mcp"]!["functions"]!.AsArray().Count == 0,
            "first successful use installs without announcing a generated name");
        string name = await GeneratedNameAsync(session);
        string file = Directory.GetFiles(Path.Combine(root, "restart", "library"), "generated-ui.json",
            SearchOption.AllDirectories).Single();
        Require(!File.ReadAllText(file).Contains("private training sample", StringComparison.Ordinal), "no training value in export");
        using var restarted = Session(client, root, "restart", false);
        await ReferenceAsync(restarted);
        var reused = await restarted.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = name,
            ["arguments"] = new JsonObject { ["pid"] = 10, ["parameters"] = new JsonObject { ["text"] = "fresh reuse" } }
        }, CancellationToken.None);
        Require(reused["success"]?.ToString() == "true" && client.Field == "fresh reuse", "restart uses new parameter without authoring");
        string stored = File.ReadAllText(file);
        var repeated = await WriteAsync(session, reference, "routine next value");
        Require(repeated["mcp"]!["functions"]!.AsArray().Count == 0, "routine compact capture omits existing definitions");
        Require(Directory.GetFiles(Path.Combine(root, "restart", "library"), "generated-ui.json", SearchOption.AllDirectories).Length == 1,
            "one active cartridge per application build");
        var legacy = await WriteAsync(session, reference, "routine legacy value", false);
        Require(legacy["mcp"]!["functions"]!.AsArray().Count == 0 && File.ReadAllText(file) == stored,
            "reuse announces nothing and writes no new revision or training contents");
        client.NativeDigest = new('b', 64);
        var changed = await WriteAsync(session, reference, "changed implementation");
        Require(Outcome(changed)["status"]?.ToString() == "needs_verification",
            "changed native writer cannot silently update compatibility");
    }

    private static async Task UnknownContractsAsync(string root)
    {
        foreach (var missing in Enum.GetValues<AutomaticMissingEvidence>())
        {
            var client = new NativeAdapterFixture { Dialog = true };
            ApplyMissing(client, missing);
            string name = "unknown-" + missing;
            using var session = Session(client, root, name);
            var written = await WriteAsync(session, await ReferenceAsync(session), "unpersisted value");
            Require(written["success"]?.ToString() == "true" && Outcome(written)["status"]?.ToString() != "available",
                "successful input does not invent missing " + missing);
            foreach (string file in Directory.GetFiles(Path.Combine(root, name, "library"), "generated-ui.json",
                SearchOption.AllDirectories))
            {
                var document = JsonNode.Parse(File.ReadAllText(file))!;
                Require(document["functions"]!.AsArray().Count == 0 && !File.ReadAllText(file).Contains("unpersisted value"),
                    "missing contracts create no callable function or training export");
            }
        }
    }

    private static void ApplyMissing(NativeAdapterFixture client, AutomaticMissingEvidence missing)
    {
        switch (missing)
        {
            case AutomaticMissingEvidence.Build:
                client.Build = "unknown";
                break;
            case AutomaticMissingEvidence.Implementation:
                client.NativeDigest = "";
                break;
            case AutomaticMissingEvidence.TextSchema:
                client.ExcludeTextSchema = true;
                break;
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    private static async Task DriftAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = Session(client, root, "drift");
        string reference = await ReferenceAsync(session);
        client.BeforeCapabilities = arguments =>
        {
            if (arguments.ContainsKey("action_schema_names"))
            {
                client.ForceField("external change before saving");
            }
        };
        var written = await WriteAsync(session, reference, "demonstrated value");
        Require(written["success"]?.ToString() == "true" && Outcome(written)["status"]?.ToString() == "needs_verification"
            && client.Mutations.Count == 1, "fresh changed output blocks automatic installation without replay");
    }

    private static async Task FailedWritesAsync(string root)
    {
        foreach (var fault in new[] { NativeAdapterFault.Append, NativeAdapterFault.Empty, NativeAdapterFault.WrongTail,
            NativeAdapterFault.LostResponse })
        {
            var client = new NativeAdapterFixture { Dialog = true, Fault = fault };
            string name = "failed-" + fault;
            using var session = Session(client, root, name);
            string text = fault == NativeAdapterFault.WrongTail ? new string('x', 16000) : "new";
            var written = await WriteAsync(session, await ReferenceAsync(session), text);
            Require(written["success"]?.ToString() == "false" && written["mcp"] == null && client.Mutations.Count == 1,
                "unverified write never starts automatic capture");
            Require(Directory.GetFiles(Path.Combine(root, name, "library"), "generated-ui.json",
            SearchOption.AllDirectories).Length == 0,
                "failed input saves nothing");
        }
    }

    private static async Task CandidateAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = Session(client, root, "candidate");
        string reference = await ReferenceAsync(session, "Open");
        var click = await session.CallAsync("ui_act", new JsonObject
        {
            ["pid"] = 10,
            ["steps"] = new JsonArray(new JsonObject { ["action"] = "click", ["ref"] = reference })
        }, CancellationToken.None);
        Require(Outcome(click)["status"]?.ToString() == "needs_verification" && Outcome(click)["material"] != null,
            "dispatch without goal proof remains noncallable material");
        string file = Directory.GetFiles(Path.Combine(root, "candidate", "library"), "generated-ui.json",
            SearchOption.AllDirectories).Single();
        var document = JsonNode.Parse(File.ReadAllText(file))!;
        Require(document["functions"]!.AsArray().Count == 0 && document["candidates"]!.AsArray().Count == 1,
            "candidate is separate from executable definitions");
    }

    private static async Task StorageFailureAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = Session(client, root, "future-storage");
        string reference = await ReferenceAsync(session);
        await WriteAsync(session, reference, "initial demonstrated value");
        string file = Directory.GetFiles(Path.Combine(root, "future-storage", "library"), "generated-ui.json",
            SearchOption.AllDirectories).Single();
        var document = JsonNode.Parse(File.ReadAllText(file))!;
        document["version"] = 999;
        string future = document.ToJsonString();
        File.WriteAllText(file, future);
        var written = await WriteAsync(session, reference, "successful input with unavailable storage");
        Require(written["success"]?.ToString() == "true" && Outcome(written)["status"]?.ToString() == "storage_failed"
            && File.ReadAllText(file) == future && client.Mutations.Count == 2,
            "uncallable future storage does not reverse successful input, overwrite data or replay");
    }

    private static async Task NoLearningAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = Session(client, root, "no-learning", false);
        var written = await WriteAsync(session, await ReferenceAsync(session), "operate without storing");
        Require(written["success"]?.ToString() == "true" && written["mcp"] == null && client.Field == "operate without storing",
            "no learning root keeps ordinary verified operation available");
        Require(Directory.GetFiles(Path.Combine(root, "no-learning", "library"), "generated-ui.json",
            SearchOption.AllDirectories).Length == 0,
            "no learning configuration creates no capture export");
    }

    private static async Task KeyboardFallbackAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true, Fault = NativeAdapterFault.NoTextInterface };
        using var session = Session(client, root, "keyboard");
        var written = await WriteAsync(session, await ReferenceAsync(session), "qualified keyboard value");
        Require(written["success"]?.ToString() == "true" && client.Field == "qualified keyboard value" && client.Mutations.Count == 4,
            "explicit not-dispatched setter refusal qualifies one verified keyboard route");
        string name = await GeneratedNameAsync(session);
        var reused = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = name,
            ["arguments"] = new JsonObject { ["pid"] = 10, ["parameters"] = new JsonObject { ["text"] = "reused keyboard value" } }
        }, CancellationToken.None);
        Require(reused["success"]?.ToString() == "true" && client.Field == "reused keyboard value" && client.Mutations.Count == 8,
            "recorded route reuses four keyboard operations with full output verification");
        var uncertain = new NativeAdapterFixture { Dialog = true, Fault = NativeAdapterFault.UncertainNoText };
        using var failedSession = Session(uncertain, root, "uncertain-interface");
        var failed = await WriteAsync(failedSession, await ReferenceAsync(failedSession), "never via keyboard");
        Require(failed["success"]?.ToString() == "false" && failed["mcp"] == null && uncertain.Mutations.Count == 0,
            "unknown setter outcome cannot qualify keyboard fallback");
    }

    // An unnamed field whose setter reports no native text interface is refused by the direct route; no keyboard fallback is sent.
    private static async Task UnnamedKeyboardRefusalAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true, Fault = NativeAdapterFault.NoTextInterface, UnnamedField = true };
        using var session = Session(client, root, "unnamed-keyboard");
        var failed = await WriteAsync(session, await ReferenceAsync(session, ""), "never via keyboard");
        Require(failed["success"]?.ToString() == "false" && failed["error"]?.ToString() == CompactRecipeTarget.UnnamedTextRefusal
            && client.Mutations.Count == 0, "an unnamed field without a native text interface never takes the keyboard fallback");
    }

    private static async Task CodedRefusalAsync(string root)
    {
        foreach (string code in new[] { "scope_denied", "future_native_refusal" })
        {
            var client = new NativeAdapterFixture { Dialog = true, Fault = NativeAdapterFault.NoTextInterface, NoTextRefusalCode = code };
            using var session = Session(client, root, code);
            var failed = await WriteAsync(session, await ReferenceAsync(session), "not authorized by refusal text");
            Require(failed["success"]?.ToString() == "false" && failed["mcp"] == null && client.Mutations.Count == 0,
                "known or future refusal codes cannot authorize fallback");
        }
    }

    private static async Task FreshFallbackAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true, Fault = NativeAdapterFault.NoTextInterface };
        using var session = Session(client, root, "stale-fallback");
        string reference = await ReferenceAsync(session);
        client.BeforeMutation = _ => client.WrongRole = true;
        var failed = await WriteAsync(session, reference, "cannot target replacement control");
        Require(failed["success"]?.ToString() == "false" && failed["mcp"] == null && client.Mutations.Count == 0,
            "fresh role replacement refuses before any keyboard fallback");
    }

    private static async Task ClearAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = Session(client, root, "clear");
        var cleared = await WriteAsync(session, await ReferenceAsync(session), "");
        Require(cleared["success"]?.ToString() == "true" && client.Field == "" && cleared["mcp"]!["functions"]!.AsArray().Count == 0,
            "explicit empty replacement is a verified clear and announces no generated name");
    }

    private static async Task UncertainKeyboardAsync(string root)
    {
        foreach (bool losePaste in new[] { false, true })
        {
            var client = new NativeAdapterFixture
            {
                Dialog = true,
                Fault = NativeAdapterFault.NoTextInterface,
                LosePasteResponse = losePaste,
                LoseSelectAllResponse = !losePaste
            };
            using var session = Session(client, root, "keyboard-loss-" + losePaste);
            var failed = await WriteAsync(session, await ReferenceAsync(session), "uncertain keyboard input");
            Require(failed["success"]?.ToString() == "false" && failed["mcp"] == null
                && client.Mutations.Count == (losePaste ? 4 : 3),
                "lost keyboard confirmation stops without replay or automatic installation");
        }
    }

    private static async Task PriorWriteDriftAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = Session(client, root, "prior-write-drift");
        string reference = await ReferenceAsync(session);
        client.AfterWrite = () => client.Fault = NativeAdapterFault.NoTextInterface;
        client.BeforeMutation = _ =>
        {
            if (client.Fault == NativeAdapterFault.NoTextInterface)
            {
                client.ForceField("external edit while setter refused");
            }
        };
        var failed = await session.CallAsync("ui_act", new JsonObject
        {
            ["pid"] = 10,
            ["steps"] = new JsonArray(
                new JsonObject { ["action"] = "write", ["ref"] = reference, ["text"] = "first verified value" },
                new JsonObject { ["action"] = "write", ["ref"] = reference, ["text"] = "second value" })
        }, CancellationToken.None);
        Require(failed["success"]?.ToString() == "false" && failed["completed_steps"]?.ToString() == "1"
            && failed["mcp"] == null && client.Mutations.Count == 1,
            "previous field drift is freshly refused before fallback input or replaying completed steps");
    }

    private static void Require(bool condition, string name)
    {
        WorkflowEngineSyntheticAdapter.Require(condition, name);
    }
}

// Missing contract cases must not turn a successful synthetic field write into unsupported compatibility evidence.
internal enum AutomaticMissingEvidence
{
    Build,
    Implementation,
    TextSchema
}
