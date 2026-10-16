using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Stored Use functions appear in ui_info only when cartridge_call would run them, and a call that names a listed pid needs no observation.
internal static class CompactGatewayListingCases
{
    private const string UnlistedPidRefusal = "Select a PID from the current ui_info reply before observing or operating it.";

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-compact-listing-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            NamesParameterlessFunctionAlone();
            await ListsStoredFunctionAsync(root);
            await ListsDescriptionAndStepsAsync(root);
            await RefusesLongDescriptionAsync(root);
            await ListsAtMostTwelveNamesAsync(root);
            await DamagedStoreKeepsInfoAsync(root);
            await OtherBuildsHaveNoNamesAsync(root);
            await RunsAfterInfoAsync(root);
            await TwoMainWindowsAsync(root);
            await RefusedCallSelectsNothingAsync(root);
            await UnlistedPidRefusedAsync(root);
            await BrowseNeedsSelectionAsync(root);
            await SameProgramProcessesAsync(root);
            await GeneratedNamesStayOutOfListingAsync(root);
            await NamesStoredFunctionsAsync(root);
            await SaysNoStoredFunctionsAsync(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // A function without parameters is named alone, so no row carries an empty parameter list.
    private static void NamesParameterlessFunctionAlone()
    {
        var bare = new CompactListedFunction("close-tab", null, [], []).ToJson();
        var withParameters = new CompactListedFunction("replace-text-and-save", null, ["text", "encoding"], []).ToJson();
        Require(bare["name"]!.ToString() == "close-tab" && !bare.ContainsKey("parameters") && !bare.ContainsKey("description"),
            "a function without parameters or description leaves both keys out");
        var declared = withParameters["parameters"]!.AsArray().Select(parameter => parameter!.ToString()).ToArray();
        Require(declared.SequenceEqual(new[] { "text", "encoding" }), "a function with parameters names them in their declared order");
    }

    // ui_info names the stored function of the row that runs it, with its parameter names, and carries the one-call hint.
    private static async Task ListsStoredFunctionAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "listed");
        await TeachAsync(client, roots, "write_current", "first", 100);
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var info = await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var listed = Row(info, 100)["functions"]!.AsArray().OfType<JsonObject>()
            .Single(function => function["name"]!.ToString() == "write_current");
        var parameters = listed["parameters"]!.AsArray().Select(parameter => parameter!.ToString()).ToArray();
        Require(parameters.SequenceEqual(new[] { "text" }), "ui_info names the parameters of the stored function in declared order");
        Require(info["next"]?.ToString() == CompactStoredFunctionListing.UsageHint + " " + CompactViewNames.StateHint,
            "ui_info carries the one-call usage hint");
    }

    // A store with more than twelve functions lists the first twelve in ordinal order, and counts the rest from the document itself.
    private static async Task ListsAtMostTwelveNamesAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "capped");
        for (int index = 0; index < 13; index++)
        {
            await TeachAsync(client, roots, $"fn-{index:00}", "text-" + index, 100);
        }
        // Automatic first-use functions are generated and never listed, so the expectation counts only the named ones.
        var expected = StoredUseNames(roots).Where(name => !CompactAutomaticNames.IsGenerated(name))
            .Order(StringComparer.Ordinal).ToArray();
        Require(expected.Length > 12, "the store holds more names than one row lists");
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var info = await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var row = Row(info, 100);
        var listed = row["functions"]!.AsArray().OfType<JsonObject>().Select(function => function["name"]!.ToString()).ToArray();
        Require(listed.SequenceEqual(expected.Take(12)), "ui_info names the first twelve stored functions in ordinal order");
        Require(row["more_functions"]!.GetValue<int>() == expected.Length - 12, "ui_info counts the stored functions it does not name");
    }

    // A damaged stored document leaves its row without functions; ui_info still answers, because the observation itself is unaffected.
    private static async Task DamagedStoreKeepsInfoAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "damaged");
        await TeachAsync(client, roots, "write_current", "first", 100);
        string file = Directory.GetFiles(roots.Library, "generated-ui.json", SearchOption.AllDirectories).Single();
        File.WriteAllText(file, "{ damaged");
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var info = await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        Require(info["success"]!.GetValue<bool>() && info["next"]?.ToString() == CompactViewNames.StateHint
            && !Row(info, 100).ContainsKey("functions"),
            "a damaged stored document leaves ui_info answering without functions");
    }

    // Another build, or an unknown one, is another program, so the stored functions of build 1.0 are never listed for it.
    private static async Task OtherBuildsHaveNoNamesAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "builds");
        await TeachAsync(client, roots, "write_current", "first", 100);
        foreach (string build in new[] { "2.0", "unknown" })
        {
            client.Build = build;
            using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
            var info = await session.CallAsync("ui_info", new(), CancellationToken.None);
            Require(info["next"]?.ToString() == CompactViewNames.StateHint && !Row(info, 100).ContainsKey("functions"),
                "a different build lists no stored function");
        }
    }

    // A call that names a pid from the latest ui_info runs the stored function without a prior observation, and it is verified.
    private static async Task RunsAfterInfoAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "after-info");
        await TeachAsync(client, roots, "write_current", "first", 100);
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var reply = await restarted.CallAsync("cartridge_call", CompactGatewayCases.Reuse(null, "after-info"), CancellationToken.None);
        Require(reply["success"]!.GetValue<bool>() && reply["verified"]!.GetValue<bool>() && client.Field == "after-info",
            "a stored function runs right after ui_info, without observing first");
    }

    // Two main windows of one program: browse lists the function for the window the caller names, and a call without a window is refused.
    private static async Task TwoMainWindowsAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true, DuplicateWindow = true };
        var roots = CompactGatewayCases.WindowRoots(root, "two-windows");
        await TeachAsync(client, roots, "write_current", "first", 100);
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var browse = new JsonObject { ["topic"] = "Use/Editor", ["window"] = 101L };
        var listed = await restarted.CallAsync("cartridge_browse", browse, CancellationToken.None);
        Require(listed["functions"]!.AsArray().OfType<JsonObject>().Any(function => function["name"]!.ToString() == "write_current"),
            "browse lists the stored function of a process with two main windows");
        Require(listed["hint"] == null, "a browse that names a window carries no hint");
        var mutations = client.Mutations.Count;
        var refused = await restarted.CallAsync("cartridge_call", CompactGatewayCases.Reuse(null, "unnamed"), CancellationToken.None);
        Require(refused["success"]?.ToString() == "false" && refused["error"]?.ToString() == CompactWindowChoice.AmbiguousRefusal
            && client.Mutations.Count == mutations, "a call without a window is refused before any input");
        var named = await restarted.CallAsync("cartridge_call", CompactGatewayCases.Reuse(101, "named"), CancellationToken.None);
        Require(named["success"]!.GetValue<bool>() && client.Field == "named", "a call that names the second main window runs");
    }

    // A call refused by two main windows binds nothing and selects no process, so a Use browse without a window still asks for one.
    private static async Task RefusedCallSelectsNothingAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true, DuplicateWindow = true };
        var roots = CompactGatewayCases.WindowRoots(root, "refused");
        await TeachAsync(client, roots, "write_current", "first", 100);
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var mutations = client.Mutations.Count;
        var refused = await restarted.CallAsync("cartridge_call", CompactGatewayCases.Reuse(null, "unnamed"), CancellationToken.None);
        Require(refused["success"]?.ToString() == "false" && refused["error"]?.ToString() == CompactWindowChoice.AmbiguousRefusal
            && client.Mutations.Count == mutations, "two main windows refuse a call without a window before any input");
        var browse = await restarted.CallAsync("cartridge_browse", new JsonObject { ["topic"] = "Use" }, CancellationToken.None);
        Require(browse["hint"]?.ToString() == "Call ui_info first; each window row lists its stored functions with their parameters."
            && !browse["functions"]!.AsArray().OfType<JsonObject>().Any(function => function["name"]!.ToString() == "write_current"),
            "the refused call selected no process, so a Use browse without a window still asks for one");
    }

    // A pid that the latest ui_info did not list is refused for a stored function, with the same words as observation.
    private static async Task UnlistedPidRefusedAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "unlisted");
        await TeachAsync(client, roots, "write_current", "first", 100);
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var mutations = client.Mutations.Count;
        var reply = await restarted.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "write_current",
            ["arguments"] = new JsonObject { ["pid"] = 99, ["parameters"] = new JsonObject { ["text"] = "never" } }
        }, CancellationToken.None);
        Require(reply["success"]?.ToString() == "false" && reply["error"]?.ToString() == UnlistedPidRefusal
            && client.Mutations.Count == mutations, "an unlisted pid is refused before any input");
    }

    // A Use browse needs a selected process: without one, the reply names the call that selects it, and an observation selects one.
    private static async Task BrowseNeedsSelectionAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "browse");
        await TeachAsync(client, roots, "write_current", "first", 100);
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        var unselected = await session.CallAsync("cartridge_browse", new JsonObject { ["topic"] = "Use/Editor" }, CancellationToken.None);
        Require(unselected["functions"]!.AsArray().Count == 0 && unselected["hint"]?.ToString() == CompactStoredFunctionListing.SelectHint,
            "a Use browse without a selected process lists nothing and names the way to select one");
        var byName = await session.CallAsync("cartridge_browse", new JsonObject { ["function"] = "write_current" }, CancellationToken.None);
        Require(byName["success"]?.ToString() == "true" && byName["function"]?["name"]?.ToString() == "write_current",
            "a stored function that ui_info lists resolves by name without a selected process");
        var stillUnselected = await session.CallAsync("cartridge_browse", new JsonObject { ["topic"] = "Use/Editor" },
            CancellationToken.None);
        Require(stillUnselected["hint"]?.ToString() == CompactStoredFunctionListing.SelectHint,
            "resolving a name selects no process, so the Use browse still asks for one");
        var byPid = await session.CallAsync("cartridge_browse", new JsonObject { ["function"] = "write_current", ["pid"] = 10 },
            CancellationToken.None);
        Require(byPid["function"]?["name"]?.ToString() == "write_current",
            "a pid from the ui_info reply names the stored function's program");
        await CompactGatewayCases.FieldAsync(session);
        var selected = await session.CallAsync("cartridge_browse", new JsonObject { ["topic"] = "Use" }, CancellationToken.None);
        Require(selected["functions"]!.AsArray().OfType<JsonObject>().Any(function => function["name"]!.ToString() == "write_current")
            && selected["hint"] == null, "after an observation the Use prefix lists the stored function without a hint");
    }

    // Processes of one program are one program: a bare stored-function name resolves, and a pid names either process.
    private static async Task SameProgramProcessesAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "same-program");
        await TeachAsync(client, roots, "write_current", "first", 100);
        client.DuplicateWindow = true;
        using (var windows = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25"))
        {
            await windows.CallAsync("ui_info", new(), CancellationToken.None);
            var sameProcess = await windows.CallAsync("cartridge_browse", new JsonObject { ["function"] = "write_current" },
                CancellationToken.None);
            Require(sameProcess["function"]?["name"]?.ToString() == "write_current",
                "two main windows of one process resolve a bare stored-function name");
        }
        client.SecondProcess = true;
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        var named = await session.CallAsync("cartridge_browse", new JsonObject { ["function"] = "write_current" }, CancellationToken.None);
        Require(named["function"]?["name"]?.ToString() == "write_current",
            "two processes of one program are one program, so a bare name resolves");
        var byPid = await session.CallAsync("cartridge_browse", new JsonObject { ["function"] = "write_current", ["pid"] = 11 },
            CancellationToken.None);
        Require(byPid["function"]?["name"]?.ToString() == "write_current", "a pid names either process of the program");
    }

    // Demonstrates one write in the window and stores it under the given name, as a person does when teaching a function.
    private static async Task TeachAsync(NativeAdapterFixture client, CompactStorageRoots roots, string name, string text, long window)
    {
        var learned = await LearnAsync(client, roots, name, text, window, null);
        Require(learned["success"]!.GetValue<bool>(), "a successful demonstration installs its named function");
    }

    // Demonstrates one write in the window and asks learning to store it, with the description when one is given.
    private static async Task<JsonObject> LearnAsync(NativeAdapterFixture client, CompactStorageRoots roots, string name, string text,
        long window, string? description)
    {
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await CompactGatewayCases.FieldInWindowAsync(session, window);
        var written = await session.CallAsync("ui_act", CompactGatewayCases.WriteInWindow(reference, text, window),
            CancellationToken.None);
        Require(written["success"]!.GetValue<bool>(), "a demonstration writes before it is taught");
        var arguments = CompactGatewayCases.Learn(written["evidence"]!.ToString(), reference, name);
        if (description != null)
        {
            arguments["description"] = description;
        }

        return await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "Programming.learn",
            ["arguments"] = arguments
        }, CancellationToken.None);
    }

    // A description given at learning survives a restart; ui_info and a browse by name show it with the labels of the stored steps.
    private static async Task ListsDescriptionAndStepsAsync(string root)
    {
        const string description = "Writes the given text into the editor field.";
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "described");
        var learned = await LearnAsync(client, roots, "write_described", "first", 100, description);
        Require(learned["success"]!.GetValue<bool>(), "learning stores a function with its description");
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var info = await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var listed = Row(info, 100)["functions"]!.AsArray().OfType<JsonObject>()
            .Single(function => function["name"]!.ToString() == "write_described");
        var steps = listed["steps"]!.AsArray().Select(step => step!.ToString()).ToArray();
        Require(listed["description"]?.ToString() == description && steps.Length == 1
            && steps[0].StartsWith("write ", StringComparison.Ordinal), "ui_info lists the stored description and the step labels");
        var browsed = await restarted.CallAsync("cartridge_browse", new JsonObject { ["function"] = "write_described" },
            CancellationToken.None);
        Require(browsed["function"]?["description"]?.ToString() == description
            && browsed["function"]?["steps"]?.AsArray().Count == 1, "a browse by name carries the description and the step labels");
    }

    // A description longer than 200 characters refuses the learn with its own sentence, and the function is not stored.
    private static async Task RefusesLongDescriptionAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "long-description");
        var refused = await LearnAsync(client, roots, "write_long", "first", 100, new string('a', 201));
        Require(refused["success"]?.ToString() == "false"
            && refused["error"]?.ToString() == CompactCaptureNames.Error(CompactCaptureError.InvalidDescription),
            "a 201-character description refuses the learn");
        Require(!StoredNames(roots).Contains("write_long"), "a refused learn stores nothing under the taught name");
    }

    // Every stored name in every program store; automatic capture may have stored functions beside a refused one.
    private static string[] StoredNames(CompactStorageRoots roots)
    {
        if (!Directory.Exists(roots.Library))
        {
            return [];
        }
        var files = Directory.GetFiles(roots.Library, "generated-ui.json", SearchOption.AllDirectories);

        return files.SelectMany(file => JsonNode.Parse(File.ReadAllText(file))!["functions"]!.AsArray().OfType<JsonObject>())
            .Select(function => function["name"]!.ToString()).ToArray();
    }

    // The expected names come from the stored document, because automatic capture may store functions beside the taught one.
    private static string[] StoredUseNames(CompactStorageRoots roots)
    {
        string file = Directory.GetFiles(roots.Library, "generated-ui.json", SearchOption.AllDirectories).Single();
        var document = JsonNode.Parse(File.ReadAllText(file))!.AsObject();

        return document["functions"]!.AsArray().OfType<JsonObject>()
            .Where(function => (function["topic"]?.ToString() ?? "").StartsWith("Use/", StringComparison.Ordinal))
            .Select(function => function["name"]!.ToString()).ToArray();
    }

    // Before ui_info a browse without a topic names ui_info; once a window lists a function, cartridge_info and that browse point to
    // the rows, and a browse of one topic does not repeat it.
    private static async Task NamesStoredFunctionsAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "sentence");
        await TeachAsync(client, roots, "write_current", "first", 100);
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var unobserved = await session.CallAsync("cartridge_browse", new(), CancellationToken.None);
        var info = await session.CallAsync("cartridge_info", new(), CancellationToken.None);
        var browsed = await session.CallAsync("cartridge_browse", new(), CancellationToken.None);
        var topical = await session.CallAsync("cartridge_browse", new JsonObject { ["topic"] = "Use" }, CancellationToken.None);
        Require(unobserved["stored_functions"]?.ToString() == CompactStoredFunctionListing.SelectHint
            && info["stored_functions"]?.ToString() == CompactStoredFunctionListing.StoredFunctionsSentence
            && browsed["stored_functions"]?.ToString() == CompactStoredFunctionListing.StoredFunctionsSentence
            && topical["stored_functions"] == null, "cartridge_info and a browse without a topic name where stored functions are listed");
    }

    // After a ui_info that lists no function, every pointer says that nothing is stored and names the state route instead.
    private static async Task SaysNoStoredFunctionsAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = new CompactGatewaySession(client, "fixture", CompactGatewayCases.WindowRoots(root, "none"),
            () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        var info = await session.CallAsync("cartridge_info", new(), CancellationToken.None);
        var browsed = await session.CallAsync("cartridge_browse", new(), CancellationToken.None);
        var topical = await session.CallAsync("cartridge_browse", new JsonObject { ["topic"] = "Use" }, CancellationToken.None);
        string expected = "No stored functions for the current windows; "
            + "read a window with ui_observe {pid, as: \"state\"} and change it with ui_apply.";
        Require(info["stored_functions"]?.ToString() == expected && browsed["stored_functions"]?.ToString() == expected
            && topical["hint"]?.ToString() == expected,
            "after a ui_info without functions, cartridge_info and both browses say that nothing is stored");
    }

    // A generated name is left out of ui_info, yet it browses and runs by name after ui_info, as an automatic capture is reached.
    private static async Task GeneratedNamesStayOutOfListingAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "generated");
        await TeachAsync(client, roots, "write_current", "first", 100);
        await TeachAsync(client, roots, "ui.element.write.c1b6dc7e", "second", 100);
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var info = await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var listed = Row(info, 100)["functions"]!.AsArray().OfType<JsonObject>().Select(function => function["name"]!.ToString()).ToArray();
        Require(listed.SequenceEqual(new[] { "write_current" }), "ui_info lists named stored functions only");
        var browsed = await restarted.CallAsync("cartridge_browse", new JsonObject { ["function"] = "ui.element.write.c1b6dc7e" },
            CancellationToken.None);
        Require(browsed["function"]?["name"]?.ToString() == "ui.element.write.c1b6dc7e",
            "a generated name still browses by name after ui_info");
        var reply = await restarted.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "ui.element.write.c1b6dc7e",
            ["arguments"] = new JsonObject { ["pid"] = 10, ["parameters"] = new JsonObject { ["text"] = "after info" } }
        }, CancellationToken.None);
        Require(reply["success"]!.GetValue<bool>() && reply["verified"]!.GetValue<bool>() && client.Field == "after info",
            "a generated name runs by name right after ui_info");
    }

    private static JsonObject Row(JsonObject info, long window)
    {
        return info["windows"]!.AsArray().OfType<JsonObject>().Single(row => row["window"]!.GetValue<long>() == window);
    }

    private static void Require(bool condition, string name)
    {
        CompactGatewayCases.Require(condition, name);
    }
}
