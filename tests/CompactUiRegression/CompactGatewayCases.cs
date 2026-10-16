using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Startup;
using KLaniBot.Transport.CompactUi;

// Frontend regressions exercise the actual native compact routing against explicitly synthetic transport state and real private storage.
internal static class CompactGatewayCases
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-compact-gateway-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "library"));
        try
        {
            Options(root);
            await CaptureRestartReuseAsync(root);
            await NoLearningAsync(root);
            await NegativeBatchesAsync(root);
            await NamedMainWindowAsync(root);
            await VanishedDemonstrationWindowAsync(root);
            await KeyChordHintAsync(root);
            await FocusedKeyStepAsync(root);
            await LearningTokenAsync(root);
            await ClickOnlySummaryAsync(root);
            await FinalObservationFailureAsync(root);
            await NoParameterFunctionAsync(root);
            await RefusedClickAsync(root);
            await UnverifiedWriteAsync(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Options(string root)
    {
        var compact = new NodeArguments(["--mcp", "--ui-profile", "compact", "--nodes", "registry",
            "--cartridges", Path.Combine(root, "library")]);
        var options = CompactGatewayOptionCodec.Read(compact, NodeCommand.Mcp);
        Require(options.Profile == CompactGatewayProfile.Compact && options.Roots!.Learning == null, "read/operate without learning root");
        Require(CompactGatewayOptionCodec.Read(new(["--mcp", "--nodes", "registry"]), NodeCommand.Mcp).Profile
            == CompactGatewayProfile.Core, "existing no-profile call preserved");
        foreach (string[] arguments in new[]
        {
            new[] { "--mcp", "--ui-profile", "unknown" },
            new[] { "--mcp", "--ui-profile" },
            new[] { "--mcp", "--ui-profile", "compact", "--ui-profile", "compact" },
            new[] { "--mcp", "--learning-root", root },
            new[] { "--mcp", "--ui-profile", "compact", "--nodes", "registry", "--cartridges", "relative" },
            compact.Tokens.Concat(new[] { "--learning-root", Path.Combine(root, "library", "private") }).ToArray(),
            compact.Tokens.Concat(new[] { "--mcp-full-text" }).ToArray(),
            compact.Tokens.Concat(new[] { "--advisor-config", "private" }).ToArray()
        })
        {
            Refused(() => CompactGatewayOptionCodec.Read(new(arguments), NodeCommand.Mcp));
        }
        Refused(() => CommandLine.Select(new(["--call", "--nodes", "registry", "--node", "test", "--ui-profile", "compact"])));
        Require(!Directory.Exists(Path.Combine(root, "library", "private")), "option validation creates no state");
    }

    private static async Task CaptureRestartReuseAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = new CompactStorageRoots(Path.Combine(root, "library"), Path.Combine(root, "learning"));
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await FieldAsync(session);
        var written = await session.CallAsync("ui_act", Write(reference, "training"), CancellationToken.None);
        Require(written["success"]!.GetValue<bool>() && client.Field == "training", "actual routing and independent field oracle agree");
        var learned = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "Programming.learn",
            ["arguments"] = Learn(written["evidence"]!.ToString(), reference)
        }, CancellationToken.None);
        Require(learned["success"]!.GetValue<bool>(), "successful demonstration installed");
        string file = Directory.GetFiles(roots.Library, "generated-ui.json", SearchOption.AllDirectories).Single();
        Require(!File.ReadAllText(file).Contains("training", StringComparison.Ordinal), "training content is absent from exported recipe");
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        await FieldAsync(restarted);
        var selected = new JsonObject { ["function"] = "write_current" };
        var descriptor = await restarted.CallAsync("cartridge_browse", selected, CancellationToken.None);
        Require(descriptor["function"]!["inputSchema"] != null, "only selected schema loaded after restart");
        var reused = await restarted.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "write_current",
            ["arguments"] = new JsonObject { ["pid"] = 10, ["parameters"] = new JsonObject { ["text"] = "reuse" } }
        }, CancellationToken.None);
        Require(reused["success"]!.GetValue<bool>() && reused["outputs"]!["value"]!.ToString() == "reuse" && client.Field == "reuse",
            "restart reuses parameterized recipe and verifies complete output");
        Require(Directory.GetFiles(roots.Library, "generated-ui.json", SearchOption.AllDirectories).Length == 1,
            "one active extension for the program/build, no revision directory");
    }

    // A chord written as one string is refused by the node; the refusal then names each key separately.
    private static async Task KeyChordHintAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true, RefuseChordKeys = true };
        using var session = new CompactGatewaySession(client, "fixture", new(Path.Combine(root, "library"), null), () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        var chord = await session.CallAsync("ui_act", new JsonObject
        {
            ["pid"] = 10,
            ["steps"] = new JsonArray(new JsonObject { ["action"] = "key", ["keys"] = new JsonArray("CTRL+SHIFT+S") })
        }, CancellationToken.None);
        Require(chord["error"]?.ToString() == "Unsupported key: CTRL+SHIFT+S. Name each key separately, "
            + "for example [\"ctrl\", \"shift\", \"s\"].", "a chord written as one string names each key in its refusal");
    }

    // A key step with a ref focuses the window, moves keyboard focus to the element without activating it, then presses the key;
    // a key step without a ref only focuses the window.
    private static async Task FocusedKeyStepAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = new CompactGatewaySession(client, "fixture", WindowRoots(root, "focused-key"), () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await FieldAsync(session);
        var focused = await session.CallAsync("ui_act", new JsonObject
        {
            ["pid"] = 10,
            ["steps"] = new JsonArray(new JsonObject { ["action"] = "key", ["ref"] = reference, ["keys"] = new JsonArray("return") })
        }, CancellationToken.None);
        Tool[] expected = [Tool.FocusWindow, Tool.ClickElement, Tool.Hotkey];
        Require(focused["success"]?.ToString() == "true" && client.Mutations.SequenceEqual(expected),
            "a key step with a ref focuses the window, focuses the element, then sends the key");
        var click = client.MutationArguments[1];
        Require(click["focus_only"]?.GetValue<bool>() == true && click["id"] != null && click["snapshot_id"] != null,
            "the element focus carries the resolved id, the snapshot and focus_only");
        client.Mutations.Clear();
        var plain = await session.CallAsync("ui_act", new JsonObject
        {
            ["pid"] = 10,
            ["steps"] = new JsonArray(new JsonObject { ["action"] = "key", ["keys"] = new JsonArray("Tab") })
        }, CancellationToken.None);
        Require(plain["success"]?.ToString() == "true" && client.Mutations.SequenceEqual([Tool.FocusWindow, Tool.Hotkey]),
            "a key step without a ref only focuses the window and sends the key");
    }

    // A final observation that fails after a finished click names no failed step; the reply carries the phase after_steps.
    private static async Task FinalObservationFailureAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        client.BeforeTree = _ => client.Truncated = client.Mutations.Contains(Tool.ClickElement);
        using var session = new CompactGatewaySession(client, "fixture", WindowRoots(root, "final-observation"), () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await FieldAsync(session);
        var clicked = await session.CallAsync("ui_act", new JsonObject
        {
            ["pid"] = 10,
            ["steps"] = new JsonArray(new JsonObject { ["action"] = "click", ["ref"] = reference })
        }, CancellationToken.None);
        Require(clicked["success"]?.ToString() == "false" && clicked["completed_steps"]?.ToString() == "1"
            && clicked["failed_step"] == null && clicked["phase"]?.ToString() == "after_steps"
            && clicked["failed_step_may_have_changed_ui"]?.ToString() == "true",
            "a final observation that fails after a finished click has no failed step and reports the phase after_steps");
    }

    // A stored function that declares no parameters requires only its pid, and runs when the caller supplies only the pid.
    private static async Task NoParameterFunctionAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = new CompactGatewaySession(client, "fixture", WindowRoots(root, "no-parameters"), () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await FieldAsync(session);
        var learned = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "Programming.learn_read",
            ["arguments"] = new JsonObject
            {
                ["pid"] = 10,
                ["name"] = "read-field",
                ["topic"] = "Use/Reads",
                ["ref"] = reference,
                ["field"] = "value",
                ["output"] = "text"
            }
        }, CancellationToken.None);
        Require(learned["success"]?.ToString() == "true", "an observed field can be learned as a read without parameters");
        var browsed = await session.CallAsync("cartridge_browse", new JsonObject { ["function"] = "read-field", ["pid"] = 10 },
            CancellationToken.None);
        var required = browsed["function"]?["inputSchema"]?["required"]?.AsArray().Select(item => item!.ToString()).ToArray();
        Require(required != null && required.SequenceEqual(new[] { "pid" }), "a function without parameters requires only its pid");
        var called = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "read-field",
            ["arguments"] = new JsonObject { ["pid"] = 10 }
        }, CancellationToken.None);
        Require(called["success"]?.ToString() == "true", "a function without parameters runs when called with its pid alone");
    }

    // A summary of a click-only batch asks for no output; the click's own dispatch and verification complete it.
    private static async Task ClickOnlySummaryAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = WindowRoots(root, "click-summary");
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await FieldAsync(session);
        var clicked = await session.CallAsync("ui_act", new JsonObject
        {
            ["pid"] = 10,
            ["result_mode"] = "summary",
            ["steps"] = new JsonArray(new JsonObject { ["action"] = "click", ["ref"] = reference })
        }, CancellationToken.None);
        Require(clicked["success"]?.ToString() == "true" && clicked["error"] == null,
            "a summary of a click-only batch succeeds without an output");
    }

    // A click the node refuses before dispatch sent no input, so the reply does not claim that the UI changed.
    private static async Task RefusedClickAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true, RefuseClicksBeforeDispatch = true };
        using var session = new CompactGatewaySession(client, "fixture", new(Path.Combine(root, "library"), null), () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await FieldAsync(session);
        var clicked = await session.CallAsync("ui_act", new JsonObject
        {
            ["pid"] = 10,
            ["steps"] = new JsonArray(new JsonObject { ["action"] = "click", ["ref"] = reference })
        }, CancellationToken.None);
        Require(clicked["success"]?.ToString() == "false" && clicked["completed_steps"]?.ToString() == "0"
            && clicked["failed_step_may_have_changed_ui"]?.ToString() == "false", "a click refused before dispatch reports no UI change");
    }

    // A write whose read-back finds the old value finishes no step, and the reply says that the UI may have changed.
    private static async Task UnverifiedWriteAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true, IgnoreTextWrites = true };
        using var session = new CompactGatewaySession(client, "fixture", new(Path.Combine(root, "library"), null), () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await FieldAsync(session);
        var written = await session.CallAsync("ui_act", Write(reference, "unapplied"), CancellationToken.None);
        Require(written["success"]?.ToString() == "false" && written["completed_steps"]?.ToString() == "0"
            && written["failed_step"]?.ToString() == "0" && written["failed_step_may_have_changed_ui"]?.ToString() == "true",
            "a write that fails its read-back finishes no step and reports a possible UI change");
    }

    // A learn call without a token of the current demonstration gets the token wording, before the PID or schema checks.
    private static async Task LearningTokenAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = new CompactGatewaySession(client, "fixture", new(Path.Combine(root, "library"), null), () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        var learning = new JsonObject
        {
            ["name"] = "typed-text",
            ["topic"] = "Use/Test",
            ["parameters"] = new JsonArray(),
            ["outputs"] = new JsonArray(new JsonObject { ["name"] = "value", ["ref"] = "ref-1", ["field"] = "value" })
        };
        var stale = learning.DeepClone().AsObject();
        stale["evidence"] = "stale";
        const string wording = "The evidence token is missing or unknown; pass the evidence value from the latest successful ui_act reply.";
        foreach (var arguments in new[] { learning, stale })
        {
            var refused = await session.CallAsync("cartridge_call", new JsonObject
            {
                ["function"] = "Programming.learn",
                ["arguments"] = arguments.DeepClone()
            }, CancellationToken.None);
            Require(refused["success"]?.ToString() == "false" && refused["error"]?.ToString() == wording,
                "a missing or unknown evidence token is refused with the token wording");
        }
    }

    private static async Task NoLearningAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = new CompactGatewaySession(client, "fixture", new(Path.Combine(root, "library"), null), () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await FieldAsync(session);
        var written = await session.CallAsync("ui_act", Write(reference, "no-save"), CancellationToken.None);
        var failed = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "Programming.learn",
            ["arguments"] = Learn(written["evidence"]!.ToString(), reference)
        }, CancellationToken.None);
        Require(failed["success"]?.ToString() == "false", "absent learning root refuses storage");
        var reused = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "write_current",
            ["arguments"] = new JsonObject { ["pid"] = 10, ["parameters"] = new JsonObject { ["text"] = "readonly-reuse" } }
        }, CancellationToken.None);
        Require(reused["success"]?.ToString() == "true" && client.Field == "readonly-reuse",
            "existing functions remain usable without learning root");
    }

    private static async Task NegativeBatchesAsync(string root)
    {
        foreach (var fault in new[] { NativeAdapterFault.Append, NativeAdapterFault.Empty, NativeAdapterFault.WrongTail })
        {
            var client = new NativeAdapterFixture { Dialog = true, Fault = fault };
            using var session = new CompactGatewaySession(client, "fixture", new(Path.Combine(root, "library"), null), () => "2025-06-18");
            var unseen = await session.CallAsync("ui_act", Write("e1", "never"), CancellationToken.None);
            Require(unseen["success"]?.ToString() == "false" && client.Mutations.Count == 0, "Info is required before input");
            await session.CallAsync("ui_info", new(), CancellationToken.None);
            string reference = await FieldAsync(session);
            var request = Write(reference, fault == NativeAdapterFault.WrongTail ? new string('x', 16000) : "new");
            request["result_mode"] = "summary";
            var failed = await session.CallAsync("ui_act", request, CancellationToken.None);
            Require(failed["success"]?.ToString() == "false" && client.Mutations.Count == 1,
                "full mismatch cannot pass summary mode or retry");
            Require(failed["evidence"] == null, "failed demonstration cannot be learned");
            Refused(() => session.CallAsync("type_text", new(), CancellationToken.None).GetAwaiter().GetResult());
        }
    }

    // Two main windows of one process: a demonstration names its window, and learning, automatic capture and reuse prove only that window.
    private static async Task NamedMainWindowAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true, DuplicateWindow = true };
        var roots = WindowRoots(root, "named");
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await FieldInWindowAsync(session, 101);
        var written = await session.CallAsync("ui_act", WriteInWindow(reference, "named", 101), CancellationToken.None);
        Require(written["success"]!.GetValue<bool>() && client.Field == "named", "a named main window writes through its own handle");
        Require(!(written["mcp"]?.ToJsonString() ?? "").Contains("storage_failed", StringComparison.Ordinal),
            "automatic capture accepts the named demonstration window");
        var learned = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "Programming.learn",
            ["arguments"] = Learn(written["evidence"]!.ToString(), reference)
        }, CancellationToken.None);
        Require(learned["success"]!.GetValue<bool>(), "a named demonstration window teaches a function among two main windows");
        string file = Directory.GetFiles(roots.Library, "generated-ui.json", SearchOption.AllDirectories).Single();
        Require(File.ReadAllText(file).Contains("\"main\"", StringComparison.Ordinal), "the stored selector names the main window kind");
        await ReuseWindowsAsync(client, roots);
    }

    // Reuse binds the window the call names. Without one, two main windows are refused before any input.
    private static async Task ReuseWindowsAsync(NativeAdapterFixture client, CompactStorageRoots roots)
    {
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        int mutations = client.Mutations.Count;
        var unnamed = await restarted.CallAsync("cartridge_call", Reuse(null, "unnamed"), CancellationToken.None);
        Require(unnamed["success"]?.ToString() == "false" && unnamed["error"]?.ToString() == CompactWindowChoice.AmbiguousRefusal
            && client.Mutations.Count == mutations, "reuse without a named window is refused before any input");
        var named = await restarted.CallAsync("cartridge_call", Reuse(101, "reused"), CancellationToken.None);
        Require(named["success"]!.GetValue<bool>() && client.Field == "reused", "reuse with the named window binds and runs");
    }

    // A demonstration whose window disappears before learning is refused, so no function is installed for another window.
    private static async Task VanishedDemonstrationWindowAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true, DuplicateWindow = true };
        var roots = WindowRoots(root, "vanished");
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await FieldInWindowAsync(session, 101);
        var written = await session.CallAsync("ui_act", WriteInWindow(reference, "vanished", 101), CancellationToken.None);
        Require(written["success"]!.GetValue<bool>(), "a demonstration in the second window succeeds before it vanishes");
        client.DuplicateWindow = false;
        var learned = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "Programming.learn",
            ["arguments"] = Learn(written["evidence"]!.ToString(), reference)
        }, CancellationToken.None);
        Require(learned["success"]?.ToString() == "false" && learned["error"]?.ToString() == CompactWindowChoice.DemonstrationWindowRefusal,
            "learning refuses a demonstration window that is no longer current");
    }

    internal static CompactStorageRoots WindowRoots(string root, string name)
    {
        string library = Path.Combine(root, name, "library");
        Directory.CreateDirectory(library);

        return new(library, Path.Combine(root, name, "learning"));
    }

    internal static async Task<string> FieldInWindowAsync(CompactGatewaySession session, long window)
    {
        var observed = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["window"] = window },
            CancellationToken.None);
        var field = observed["elements"]!.AsArray().OfType<JsonObject>().Single(element => element["name"]!.ToString() == "Field");

        return field["ref"]!.ToString();
    }

    internal static JsonObject WriteInWindow(string reference, string text, long window)
    {
        var request = Write(reference, text);
        request["window"] = window;

        return request;
    }

    internal static JsonObject Reuse(long? window, string text)
    {
        var arguments = new JsonObject
        {
            ["pid"] = 10,
            ["parameters"] = new JsonObject { ["text"] = text }
        };
        if (window is long handle)
        {
            arguments["window"] = handle;
        }

        return new JsonObject
        {
            ["function"] = "write_current",
            ["arguments"] = arguments
        };
    }

    internal static async Task<string> FieldAsync(CompactGatewaySession session)
    {
        var observed = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10 }, CancellationToken.None);
        var field = observed["elements"]!.AsArray().OfType<JsonObject>().Single(element => element["name"]!.ToString() == "Field");
        return field["ref"]!.ToString();
    }

    internal static JsonObject Write(string reference, string text)
    {
        return new JsonObject
        {
            ["pid"] = 10,
            ["steps"] = new JsonArray(new JsonObject
            {
                ["action"] = "write",
                ["ref"] = reference,
                ["text"] = text
            })
        };
    }

    internal static JsonObject Learn(string evidence, string reference, string name = "write_current")
    {
        return new JsonObject
        {
            ["evidence"] = evidence,
            ["name"] = name,
            ["topic"] = "Use/Editor",
            ["parameters"] = new JsonArray("text"),
            ["outputs"] = new JsonArray(new JsonObject
            {
                ["name"] = "value",
                ["ref"] = reference,
                ["field"] = "value"
            })
        };
    }

    private static void Refused(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException("Expected compact profile/schema refusal.");
    }

    internal static void Require(bool condition, string name)
    {
        WorkflowEngineSyntheticAdapter.Require(condition, name);
    }
}
