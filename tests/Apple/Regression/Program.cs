using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Transport;

string mode = args[0], node = args[1], registry = args[2], artifacts = Path.GetFullPath(args[3]);
Directory.CreateDirectory(artifacts);
using var log = new StreamWriter(Path.Combine(artifacts, mode + "-actions.jsonl")) { AutoFlush = true };
using var client = new NodeTransportClient(node, registry);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
int? pid = mode == "mac" ? int.Parse(args[4]) : null;
int checks = 0;
async Task<JsonObject> Call(string action, JsonObject? input = null, bool mutation = false)
{
    var data = input ?? new();
    if (mutation)
        data["request_id"] ??= Guid.CreateVersion7().ToString();
    var result = await client.CallAsync(action, data, timeout.Token);
    var logged = (JsonObject)result.DeepClone();
    if (logged["image"] != null)
        logged["image"] = "<PNG saved separately>";
    await log.WriteLineAsync(new JsonObject { ["action"] = action, ["args"] = data.DeepClone(), ["result"] = logged }.ToJsonString());
    return result;
}
void Require(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
async Task<FoundElement> Find(string identifier)
{
    var input = new JsonObject { ["auto_id"] = identifier };
    if (pid != null)
        input["pid"] = pid.Value;
    JsonObject result = new();
    for (int attempt = 0; attempt < 40; attempt++)
    {
        result = await Call("find_elements", input);
        if (result["success"]?.GetValue<bool>() != true || result["total_matches"]?.GetValue<int>() != 0)
            break;
        await Task.Delay(100, timeout.Token);
    }
    Require(result["success"]?.GetValue<bool>() == true
        && result["total_matches"]?.GetValue<int>() == 1, "Unique target missing: " + result.ToJsonString());
    return new FoundElement(
        result["matches"]![0]!["Id"]!.GetValue<int>(),
        result["snapshot_id"]!.ToString(),
        result["matches"]![0]!["Value"]?.ToString() ?? "");
}
async Task Capture(string name)
{
    var result = await Call("take_screenshot");
    Require(result["success"]?.GetValue<bool>() == true, "Screenshot failed: " + result["message"] + result["error"]);
    var png = Convert.FromBase64String(result["image"]!.ToString());
    Require(png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Invalid PNG");
    await File.WriteAllBytesAsync(Path.Combine(artifacts, mode + "-" + name + ".png"), png);
}
try
{
    JsonObject? capabilities = null;
    for (int i = 0; i < 30; i++)
    {
        capabilities = await Call("get_machine_capabilities");
        if (capabilities["success"]?.GetValue<bool>() == true)
            break;
        await Task.Delay(250);
    }
    Require(capabilities?["success"]?.GetValue<bool>() == true, "Node readiness failed.");
    Require(
        capabilities!["capabilities"]!["platform"]!.ToString() == (mode == "mac" ? "macos-ax-quartz" : "ios-simulator-xctest-experimental"),
        "Wrong platform routing.");
    var features = capabilities["capabilities"]!["features"]!.AsArray().Select(f => f!.ToString()).ToHashSet();
    if (!features.Contains("semanticInspect") || !features.Contains("screenshot"))
    {
        if (features.Contains("screenshot"))
            await Capture("permission-check");
        else
        {
            var screenshot = await Call("take_screenshot");
            Require(screenshot["success"]?.GetValue<bool>() == false, "Missing screenshot permission must fail.");
        }
        string message = "BLOCKED: missing macOS permissions: "
            + (!features.Contains("semanticInspect") ? "Accessibility " : "")
            + (!features.Contains("screenshot") ? "Screen Recording" : "")
            + "; transport and platform routing passed.";
        await File.WriteAllTextAsync(Path.Combine(artifacts, mode + "-summary.txt"), message + "\n");
        Console.WriteLine(message);
        return 3;
    }
    await Capture("before");
    var inputTarget = await Find("literal-input");
    string literal = "000123{ENTER}%^$ Grüße 🐙";
    if (mode == "mac")
    {
        var control = await Call("type_text", new()
        {
            ["id"] = inputTarget.Id,
            ["snapshot_id"] = inputTarget.Snapshot,
            ["text"] = "\tMUST NOT TYPE",
            ["clear"] = false
        }, true);
        Require(control["success"]?.GetValue<bool>() == false, "Implicit control key was accepted as literal text.");
        var unchanged = await Call("verify_element", new()
        {
            ["id"] = inputTarget.Id,
            ["snapshot_id"] = inputTarget.Snapshot,
            ["expected_text"] = inputTarget.Value,
            ["timeout_ms"] = 100
        });
        Require(unchanged["success"]?.GetValue<bool>() == true, "Rejected control characters changed the editor.");
    }
    var invalidUuid = await Call("type_text", new()
    {
        ["id"] = inputTarget.Id,
        ["snapshot_id"] = inputTarget.Snapshot,
        ["text"] = "MUST NOT TYPE",
        ["request_id"] = Guid.NewGuid().ToString()
    });
    Require(invalidUuid["success"]?.GetValue<bool>() == false
        && invalidUuid["outcome"]?.ToString() == "not_dispatched", "UUIDv4 mutation must be rejected.");
    var typeArgs = new JsonObject
    {
        ["id"] = inputTarget.Id,
        ["snapshot_id"] = inputTarget.Snapshot,
        ["text"] = literal,
        ["clear"] = mode == "mac",
        ["request_id"] = Guid.CreateVersion7().ToString()
    };
    var typed = await Call("type_text", typeArgs);
    Require(typed["success"]?.GetValue<bool>() == true, "Literal input failed: " + typed.ToJsonString());
    var duplicate = await Call("type_text", (JsonObject)typeArgs.DeepClone());
    Require(duplicate["replayed"]?.GetValue<bool>() == true, "Duplicate operation was not deduplicated.");
    var verified = await Call("verify_element", new()
    {
        ["id"] = inputTarget.Id,
        ["snapshot_id"] = inputTarget.Snapshot,
        ["expected_text"] = literal,
        ["expected_state"] = "enabled",
        ["timeout_ms"] = 1000
    });
    Require(verified["success"]?.GetValue<bool>() == true, "Exact Unicode verification failed: " + verified.ToJsonString());
    var negative = await Call("verify_element", new()
    {
        ["id"] = inputTarget.Id,
        ["snapshot_id"] = inputTarget.Snapshot,
        ["expected_text"] = "wrong",
        ["timeout_ms"] = 100
    });
    Require(negative["success"]?.GetValue<bool>() == false, "False-positive assertion.");
    var save = await Find("save-button");
    var stale = await Call(mode == "mac" ? "click_element" : "mouse_click", new()
    {
        ["id"] = inputTarget.Id,
        ["snapshot_id"] = inputTarget.Snapshot
    }, true);
    Require(stale["success"]?.GetValue<bool>() == false, "Stale snapshot accepted.");
    var saved = await Call(mode == "mac" ? "click_element" : "mouse_click", new()
    {
        ["id"] = save.Id,
        ["snapshot_id"] = save.Snapshot
    }, true);
    Require(saved["success"]?.GetValue<bool>() == true, "Save click failed: " + saved.ToJsonString());
    var savedArgs = new JsonObject { ["auto_id"] = "saved-output", ["expected_text"] = literal, ["timeout_ms"] = 2000 };
    if (pid != null)
        savedArgs["pid"] = pid.Value;
    var output = await Call("verify_element", savedArgs);
    Require(output["success"]?.GetValue<bool>() == true, "Saved output differs: " + output.ToJsonString());
    await Capture("result");
    var modal = await Find("modal-button");
    var opened = await Call(mode == "mac" ? "click_element" : "mouse_click", new()
    {
        ["id"] = modal.Id,
        ["snapshot_id"] = modal.Snapshot
    }, true);
    Require(opened["success"]?.GetValue<bool>() == true, "Modal action failed.");
    var modalArgs = new JsonObject { ["role"] = mode == "mac" ? "Sheet" : "Alert", ["timeout_ms"] = 3000 };
    if (pid != null)
        modalArgs["pid"] = pid.Value;
    var modalObserved = await Call("wait_element", modalArgs);
    Require(modalObserved["success"]?.GetValue<bool>() == true, "Modal not observed: " + modalObserved.ToJsonString());
    await Capture("modal");
    string summary = $"PASS: {checks} checks; {mode}; real node via {capabilities["transport"]}; "
        + "Unicode, UUIDv7 enforcement, deduplication, stale snapshots, exact positive/negative assertions, "
        + "native button, modal and three PNG screenshots.";
    await File.WriteAllTextAsync(Path.Combine(artifacts, mode + "-summary.txt"), summary + "\n");
    Console.WriteLine(summary);
    return 0;
}
catch (Exception ex)
{
    string summary = $"FAIL after {checks} checks: {ex.Message}";
    await File.WriteAllTextAsync(Path.Combine(artifacts, mode + "-summary.txt"), summary + "\n");
    Console.Error.WriteLine(summary);
    return 1;
}
finally { await Call("end_session", new(), true); }

// One element the regression found by its accessibility identifier, with the snapshot it belongs to.
record FoundElement(int Id, string Snapshot, string Value);
