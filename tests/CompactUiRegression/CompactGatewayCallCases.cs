using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// A stored function's reply names its steps and reports the window's change since the call began, offscreen controls included.
internal static class CompactGatewayCallCases
{
    private const string Untitled = "Untitled* - Editor";

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-compact-call-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await NamesStepsAndChangesAsync(root);
            await SummaryOmitsChangesAsync(root);
            await WriteStepOmitsTextAsync(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // The learned Select All, To Uppercase and Save steps name their targets by container path, and the change list names the Save item
    // that became disabled while closed, the window row whose unsaved marker went away, and the field that Save filled.
    private static async Task NamesStepsAndChangesAsync(string root)
    {
        var client = MenuClient();
        var roots = CompactGatewayCases.WindowRoots(root, "steps");
        await LearnSaveAsync(client, roots, "select-upper-save");
        Reset(client);
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var reply = await restarted.CallAsync("cartridge_call", Call("select-upper-save", null), CancellationToken.None);
        Require(reply["success"]?.GetValue<bool>() == true && reply["verified"]?.GetValue<bool>() == true,
            "the learned three-step function runs and verifies its output");
        var steps = reply["steps"]!.AsArray().Select(step => step!.ToString()).ToArray();
        Require(steps.SequenceEqual(new[] { "click Edit > Select All", "click Edit > Convert > To Uppercase", "click File > Save" }),
            "the reply names each step by its stored container path");
        var changes = reply["ui"]!.AsObject();
        Require(changes["kind"]?.ToString() == "delta", "the reply reports the change since the call began");
        var changed = Rows(changes, "changed");
        Require(changed.Any(row => row["name"]?.ToString() == "Save" && row["disabled"]?.GetValue<bool>() == true
            && row["offscreen"]?.GetValue<bool>() == true), "a closed menu item that became disabled is reported");
        Require(changed.Any(row => row["role"]?.ToString() == "Window" && row["name"]?.ToString() == "Untitled - Editor"),
            "the window row whose unsaved marker went away is reported");
        Require(changed.Any(row => row["name"]?.ToString() == "Field" && row["value"]?.ToString() == "saved"),
            "the field that the step filled is reported");
        Require(!changed.Any(row => row["name"]?.ToString() is "Select All" or "To Uppercase"),
            "a closed menu item that did not change is not reported");
    }

    // Summary mode keeps the steps and the verified outputs, omits the change list, and asks for an observation as ui_act does.
    private static async Task SummaryOmitsChangesAsync(string root)
    {
        var client = MenuClient();
        var roots = CompactGatewayCases.WindowRoots(root, "summary");
        await LearnSaveAsync(client, roots, "select-upper-save");
        Reset(client);
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var reply = await restarted.CallAsync("cartridge_call", Call("select-upper-save", "summary"), CancellationToken.None);
        Require(reply["ui"] != null && reply["observation_required"] == null && reply["steps"]!.AsArray().Count == 3,
            "summary mode keeps the steps and a small change list without requesting an observation");
    }

    // A write step names only its target: the typed text, which may be a password, never appears in the reply's steps.
    private static async Task WriteStepOmitsTextAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "write");
        using (var teaching = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25"))
        {
            await teaching.CallAsync("ui_info", new(), CancellationToken.None);
            string reference = await CompactGatewayCases.FieldAsync(teaching);
            var written = await teaching.CallAsync("ui_act", CompactGatewayCases.WriteInWindow(reference, "first text", 100),
                CancellationToken.None);
            var learned = await teaching.CallAsync("cartridge_call", new JsonObject
            {
                ["function"] = "Programming.learn",
                ["arguments"] = CompactGatewayCases.Learn(written["evidence"]!.ToString(), reference, "write-field")
            }, CancellationToken.None);
            Require(learned["success"]?.GetValue<bool>() == true, "the write demonstration is learned");
        }
        using var restarted = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var reply = await restarted.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "write-field",
            ["arguments"] = new JsonObject { ["pid"] = 10, ["parameters"] = new JsonObject { ["text"] = "secret second text" } }
        }, CancellationToken.None);
        Require(reply["steps"]!.AsArray().Select(step => step!.ToString()).SequenceEqual(new[] { "write Field" }),
            "a write step names its target and never its text");
        Require(!reply["steps"]!.ToJsonString().Contains("secret", StringComparison.Ordinal), "the steps never contain the typed value");
    }

    // The demonstration and the learned function share one closed Edit menu, a File menu with Save, and an edit field that Save fills.
    private static NativeAdapterFixture MenuClient()
    {
        return new NativeAdapterFixture { ClosedMenu = true, FileMenu = true, Dialog = true, Title = Untitled };
    }

    // The learned run starts from the state the demonstration started from: unsaved, with the field holding its original text.
    private static void Reset(NativeAdapterFixture client)
    {
        client.Title = Untitled;
        client.MenuSaved = false;
        client.ForceField("old");
    }

    // Demonstrates Select All, To Uppercase and Save, then learns them with the field that Save changes as the goal output.
    private static async Task LearnSaveAsync(NativeAdapterFixture client, CompactStorageRoots roots, string name)
    {
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        var view = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["offscreen"] = true }, CancellationToken.None);
        var elements = view["elements"]!.AsArray().OfType<JsonObject>().ToArray();
        string Reference(string element, string role = "Edit") =>
            elements.Single(row => row["name"]!.ToString() == element && row["role"]!.ToString() == role)["ref"]!.ToString();
        var demonstration = await session.CallAsync("ui_act", new JsonObject
        {
            ["pid"] = 10,
            ["steps"] = new JsonArray(Click(Reference("Select All", "menu item")), Click(Reference("To Uppercase", "menu item")),
                Click(Reference("Save", "menu item")))
        }, CancellationToken.None);
        Require(demonstration["success"]?.GetValue<bool>() == true, "the three-step demonstration runs");
        var learned = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "Programming.learn",
            ["arguments"] = new JsonObject
            {
                ["evidence"] = demonstration["evidence"]!.ToString(),
                ["name"] = name,
                ["topic"] = "Use/Editor",
                ["parameters"] = new JsonArray(),
                ["outputs"] = new JsonArray(new JsonObject { ["name"] = "shown", ["ref"] = Reference("Field"), ["field"] = "value" })
            }
        }, CancellationToken.None);
        Require(learned["success"]?.GetValue<bool>() == true, "the demonstration is learned with its changed field as the goal");
    }

    private static JsonObject Click(string reference)
    {
        return new JsonObject { ["action"] = "click", ["ref"] = reference };
    }

    private static JsonObject Call(string function, string? resultMode)
    {
        var arguments = new JsonObject { ["pid"] = 10 };
        if (resultMode != null)
        {
            arguments["result_mode"] = resultMode;
        }

        return new JsonObject { ["function"] = function, ["arguments"] = arguments };
    }

    private static IEnumerable<JsonObject> Rows(JsonObject changes, string key)
    {
        return changes[key]!.AsArray().OfType<JsonObject>();
    }

    private static void Require(bool condition, string name)
    {
        CompactGatewayCases.Require(condition, name);
    }
}
