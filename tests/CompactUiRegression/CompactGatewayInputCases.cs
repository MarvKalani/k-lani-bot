using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Transport.CompactUi;

// ui_input operates only a window without an accessibility tree: it binds clicks to the latest screenshot of an unmoved window,
// sends input by handle with the journaled reason and no decision, surfaces node refusals and never feeds learning.
internal static class CompactGatewayInputCases
{
    private const string Reason = "egui canvas without accessibility";

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-compact-input-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await LookAsync(root);
            await ShotRulesAsync(root);
            await InputByHandleAsync(root);
            await GuardRefusalAsync(root);
            await TreePresentAsync(root);
            await ObserveNamesRouteAsync(root);
            await LearningEndsAsync(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Without steps the reply is a screenshot with its shot and the window's rectangle; nothing is sent to the application.
    private static async Task LookAsync(string root)
    {
        var fixture = await StartAsync(root, "look");
        using var session = fixture.Session;
        var node = fixture.Node;
        var reply = await InputAsync(session, null, null);
        Require(reply["success"]?.GetValue<bool>() == true && reply["semantic_route"]?.ToString() == "no_accessibility_tree",
            "a window without a tree is accepted and its reason named");
        Require(reply["image"]?.ToString() == NoTreeNodeFixture.Image && reply["mime_type"]?.ToString() == "image/png",
            "the reply carries the fresh screenshot");
        Require(reply["bounds"]?["width"]?.GetValue<int>() == 1280 && reply["shot"]?.ToString().Length == 32,
            "the reply names the rectangle and a shot id");
        Require(node.Mutations.Count == 0, "a look sends no input");
    }

    // A click needs the latest shot, the same rectangle and a point inside the window; each refusal comes before any input.
    private static async Task ShotRulesAsync(string root)
    {
        var fixture = await StartAsync(root, "shots");
        using var session = fixture.Session;
        var node = fixture.Node;
        await RequireArgumentRefusalAsync(session, Steps(Click(5, 5)), "a click without shot is refused while reading");
        string first = (await InputAsync(session, null, null))["shot"]!.ToString();
        await InputAsync(session, null, null);
        var stale = await InputAsync(session, first, Steps(Click(5, 5)));
        Require(stale["error"]?.ToString() == CompactInputNames.ShotRequiredRefusal, "an older shot is refused");
        string second = (await InputAsync(session, null, null))["shot"]!.ToString();
        var outside = await InputAsync(session, second, Steps(Click(1280, 5)));
        Require(outside["error"]?.ToString() == CompactInputNames.OutsideRefusal, "a point outside the window is refused");
        string third = (await InputAsync(session, null, null))["shot"]!.ToString();
        node.Left = 10;
        var moved = await InputAsync(session, third, Steps(Click(50, 5)));
        Require(moved["error"]?.ToString() == CompactInputNames.MovedRefusal, "a window that moved since the shot is refused");
        Require(node.Mutations.Count == 0, "no refused click sends input");
        await RequireArgumentRefusalAsync(session, Steps(Key("tab"), Click(1, 1)), "a click after the first step is refused");
    }

    // The window is focused by handle without title, the click and pasted text carry the journaled reason and no decision.
    private static async Task InputByHandleAsync(string root)
    {
        var fixture = await StartAsync(root, "send");
        using var session = fixture.Session;
        var node = fixture.Node;
        string shot = (await InputAsync(session, null, null))["shot"]!.ToString();
        var steps = Steps(Click(120, 468, "right"), new JsonObject { ["action"] = "type", ["text"] = "/workspace" }, Key("enter"));
        var reply = await InputAsync(session, shot, steps);
        Require(reply["success"]?.GetValue<bool>() == true && reply["completed_steps"]?.GetValue<int>() == 3, "three steps were sent");
        Require(node.Mutations.Select(call => call.Tool).SequenceEqual(
            [Tool.FocusWindow, Tool.MouseClick, Tool.SetClipboard, Tool.Hotkey, Tool.Hotkey]), "focus, click, paste and key in order");
        var focus = node.Mutations[0].Arguments;
        Require(focus["window"]?.GetValue<long>() == 100 && focus["title"] == null, "the window is focused by handle, never by title");
        var click = node.Mutations[1].Arguments;
        Require(click["x"]?.GetValue<int>() == 120 && click["button"]?.ToString() == "right"
            && click["last_resort_reason"]?.ToString() == "no_accessibility_tree: " + Reason, "the click carries its journaled reason");
        Require(node.Mutations.All(call => call.Arguments["decision"] == null), "no decision is forwarded with raw input");
        Require(node.Mutations[3].Arguments["keys"]!.AsArray().Select(key => key!.ToString()).SequenceEqual(["ctrl", "v"]),
            "typed text is pasted from the clipboard");
        Require(reply["shot"]?.ToString() != shot, "every call issues a new shot");
    }

    // A guarded node refuses raw keys; the refusal surfaces, the step counts as not completed and the shot is spent.
    private static async Task GuardRefusalAsync(string root)
    {
        var fixture = await StartAsync(root, "guard");
        using var session = fixture.Session;
        var node = fixture.Node;
        string shot = (await InputAsync(session, null, null))["shot"]!.ToString();
        node.RefuseHotkey = true;
        var refused = await InputAsync(session, null, Steps(Key("ctrl", "a")));
        Require(refused["success"]?.GetValue<bool>() == false && refused["error"]!.ToString().Contains("guarded mode")
            && refused["completed_steps"]?.GetValue<int>() == 0, "the node's guard refusal surfaces");
        var click = await InputAsync(session, shot, Steps(Click(5, 5)));
        Require(click["error"]?.ToString() == CompactInputNames.ShotRequiredRefusal, "a failed call spends the earlier shot");
    }

    // A window with a tree belongs to ui_observe and ui_act; ui_input refuses it before any input.
    private static async Task TreePresentAsync(string root)
    {
        var fixture = await StartAsync(root, "tree");
        using var session = fixture.Session;
        var node = fixture.Node;
        node.WithTree = true;
        var reply = await InputAsync(session, null, Steps(Key("enter")));
        Require(reply["error"]?.ToString() == CompactInputNames.TreePresentRefusal && node.Mutations.Count == 0,
            "a window with a tree is refused");
    }

    // The no-tree refusal of ui_observe names the last-resort route, and ui_info lists it.
    private static async Task ObserveNamesRouteAsync(string root)
    {
        var fixture = await StartAsync(root, "observe");
        using var session = fixture.Session;
        var view = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10 }, CancellationToken.None);
        Require(view["success"]?.GetValue<bool>() == false && view["error"]!.ToString().EndsWith(CompactInputNames.NoTreeHint),
            "ui_observe names ui_input for a window without a tree");
        var info = await session.CallAsync("ui_info", new(), CancellationToken.None);
        Require(info["routes"]!.AsArray().Any(route => route?.ToString() == CompactInputNames.Route), "ui_info lists the route");
    }

    // Any ui_input call ends the current demonstration, so Programming.learn never stores raw input or coordinates.
    private static async Task LearningEndsAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = new CompactStorageRoots(Path.Combine(root, "learn", "library"), Path.Combine(root, "learn", "learning"));
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await CompactGatewayCases.FieldAsync(session);
        var written = await session.CallAsync("ui_act", CompactGatewayCases.Write(reference, "training"), CancellationToken.None);
        await session.CallAsync("ui_input", new JsonObject { ["window"] = 100L, ["reason"] = Reason }, CancellationToken.None);
        var learned = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "Programming.learn",
            ["arguments"] = CompactGatewayCases.Learn(written["evidence"]!.ToString(), reference)
        }, CancellationToken.None);
        Require(learned["success"]?.GetValue<bool>() != true, "learning refuses after a ui_input call");
    }

    private static async Task<NoTreeSession> StartAsync(string root, string name)
    {
        var node = new NoTreeNodeFixture();
        var session = new CompactGatewaySession(node, "fixture", CompactGatewayCases.WindowRoots(root, name), () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);

        return new NoTreeSession(session, node);
    }

    private static Task<JsonObject> InputAsync(CompactGatewaySession session, string? shot, JsonArray? steps)
    {
        var arguments = new JsonObject { ["window"] = 100L, ["reason"] = Reason };
        if (shot != null)
        {
            arguments["shot"] = shot;
        }
        if (steps != null)
        {
            arguments["steps"] = steps;
        }

        return session.CallAsync("ui_input", arguments, CancellationToken.None);
    }

    private static async Task RequireArgumentRefusalAsync(CompactGatewaySession session, JsonArray steps, string name)
    {
        bool refused = false;
        try
        {
            await InputAsync(session, null, steps);
        }
        catch (ArgumentException)
        {
            refused = true;
        }
        Require(refused, name);
    }

    private static JsonArray Steps(params JsonObject[] steps)
    {
        return new JsonArray(steps.Select(step => (JsonNode?)step).ToArray());
    }

    private static JsonObject Click(int x, int y, string? button = null)
    {
        var click = new JsonObject { ["action"] = "click", ["x"] = x, ["y"] = y };
        if (button != null)
        {
            click["button"] = button;
        }

        return click;
    }

    private static JsonObject Key(params string[] keys)
    {
        return new JsonObject { ["action"] = "key", ["keys"] = new JsonArray(keys.Select(key => (JsonNode?)key).ToArray()) };
    }

    private static void Require(bool condition, string name)
    {
        CompactGatewayCases.Require(condition, name);
    }
}

// A gateway session together with the fake node behind it, so a case can change the node and read what it received.
internal sealed record NoTreeSession(CompactGatewaySession Session, NoTreeNodeFixture Node);
