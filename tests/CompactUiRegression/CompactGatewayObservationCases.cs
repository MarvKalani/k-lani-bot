using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// A full ui_observe lists onscreen controls only; closed menu items appear when asked for, and a contains search always finds them.
internal static class CompactGatewayObservationCases
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-compact-observation-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await LeanFullViewAsync(root);
            await ContainsFindsClosedItemsAsync(root);
            await OffscreenRequestedAsync(root);
            await StateViewAsync(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // The default view counts the closed menu items it leaves out and names the argument that includes them.
    private static async Task LeanFullViewAsync(string root)
    {
        using var session = await StartAsync(root, "lean");
        var view = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10 }, CancellationToken.None);
        var names = Elements(view).Select(element => element["name"]!.ToString()).ToArray();
        Require(names.Contains("Edit") && !names.Contains("Select All") && !names.Contains("To Uppercase"),
            "a default full view leaves the closed menu items out");
        Require(view["offscreen_omitted"]?.GetValue<int>() == 3 && view["hint"]?.ToString() == CompactViewNames.OffscreenHint,
            "a default full view counts the items it omits and names how to include them");
    }

    // A contains search finds a closed item by its name, and a value search finds the edit field that holds the value.
    private static async Task ContainsFindsClosedItemsAsync(string root)
    {
        using var session = await StartAsync(root, "contains");
        var upper = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["contains"] = "Upper" },
            CancellationToken.None);
        var found = Elements(upper).ToArray();
        Require(found.Length == 1 && found[0]["name"]!.ToString() == "To Uppercase" && found[0]["offscreen"]?.GetValue<bool>() == true,
            "a contains search returns the closed item it names");
        Require(upper["offscreen_omitted"] == null, "a search that includes offscreen items reports no omission");
        var byValue = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["contains"] = "old" },
            CancellationToken.None);
        Require(Elements(byValue).Any(element => element["role"]!.ToString() == "Edit" && element["name"]!.ToString() == "Field"),
            "a contains search matches the value of an edit field");
    }

    // offscreen:true lists every closed item in one full view, without an omission count.
    private static async Task OffscreenRequestedAsync(string root)
    {
        using var session = await StartAsync(root, "requested");
        var view = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["offscreen"] = true },
            CancellationToken.None);
        var names = Elements(view).Select(element => element["name"]!.ToString()).ToArray();
        Require(names.Contains("Select All") && names.Contains("Convert") && names.Contains("To Uppercase"),
            "offscreen:true lists every closed menu item");
        Require(view["offscreen_omitted"] == null && view["hint"] == null, "offscreen:true omits nothing and needs no hint");
    }

    // The state view declares the window as a class, and refuses an unknown cursor or a filter it does not accept.
    private static async Task StateViewAsync(string root)
    {
        using var session = await StartAsync(root, "state");
        var view = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["as"] = "state" },
            CancellationToken.None);
        var declaredLines = (view["declaration"]?.ToString() ?? string.Empty).Split('\n').Select(line => line.TrimEnd());
        Require(view["success"]?.ToString() == "true" && declaredLines.Any(line => line.EndsWith("toUppercase(): void;")),
            "as:state declares the window's menu commands as methods");
        Require(view["shape"]?.ToString().Length == 16, "as:state carries a 16-character shape");
        var unknownPart = await session.CallAsync("ui_observe",
            new JsonObject { ["pid"] = 10, ["as"] = "state", ["cursor"] = "nothing@0" }, CancellationToken.None);
        Require(unknownPart["success"]?.ToString() == "false" && unknownPart["error"]?.ToString() == "unknown_part",
            "as:state answers an unknown cursor with unknown_part");
        bool refused = false;
        try
        {
            var filtered = await session.CallAsync("ui_observe",
                new JsonObject { ["pid"] = 10, ["as"] = "state", ["contains"] = "x" }, CancellationToken.None);
            refused = filtered["success"]?.ToString() == "false";
        }
        catch (ArgumentException)
        {
            refused = true;
        }
        Require(refused, "as:state refuses a filter it does not accept");
    }

    // A fresh gateway whose ui_info names the single window of the fixture, so each observation resolves pid 10.
    private static async Task<CompactGatewaySession> StartAsync(string root, string name)
    {
        var client = new NativeAdapterFixture { ClosedMenu = true, Dialog = true };
        var session = new CompactGatewaySession(client, "fixture", CompactGatewayCases.WindowRoots(root, name), () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);

        return session;
    }

    private static IEnumerable<JsonObject> Elements(JsonObject view)
    {
        return view["elements"]!.AsArray().OfType<JsonObject>();
    }

    private static void Require(bool condition, string name)
    {
        CompactGatewayCases.Require(condition, name);
    }
}
