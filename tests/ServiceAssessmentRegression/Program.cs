using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Cartridges;
using KLaniBot.Fleet;
using KLaniBot.Inventory;
using KLaniBot.Transport;
using KLaniBot.Visibility;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception("FAILED: " + name);
    }
    passed++;
}
void Denied(Action action, string name)
{
    try
    {
        action();
    }
    catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
    {
        passed++;
        return;
    }
    throw new Exception("FAILED: " + name);
}
var machine = new InventoryMachine("windows", "10.0.26200", "X64", "X64", 8, "de-DE");
var application = new InstalledApplication(
    PairingProtocol.Hash("fixture"),
    "CAD fixture",
    "5.21.7",
    "Fixture Publisher",
    "fixture-registry");
InventorySource[] sources = [new("fixture-registry", true, null), new("portable-apps", false, "Not collected")];
SystemInventory inventory = SystemInventory.Create(machine, [application], sources, false);
SystemInventory same = SystemInventory.Create(machine, [application], sources.Reverse(), false);
Check(
    inventory.Fingerprint == same.Fingerprint && inventory.SnapshotId != same.SnapshotId,
    "Stable content fingerprint independent of capture ID/time/source order.");
inventory.Verify();
Check(true, "Valid inventory verifies.");
Denied(() => (inventory with { Fingerprint = new string('0', 64) }).Verify(), "Tampered inventory rejected.");
Denied(() => (inventory with { Applications = [application, application] }).Verify(), "Duplicate application IDs rejected.");
Check(SystemInventory.Create(machine, [application with { Version = "5.21.8" }], sources, false).Fingerprint != inventory.Fingerprint,
    "Application version change invalidates inventory binding.");
var target = new CompatibilityTarget("target-fixture", "Synthetic migration target", "windows", "10.0.26200", "X64", ["fixture-registry"]);
var rule = new CompatibilityRule(application.Name, application.Publisher, application.Version, target.Id,
    CompatibilityVerdict.Compatible, "fixture://functional-export-import-test", "Synthetic evidence only.");
CompatibilityReport known = CompatibilityAssessment.Evaluate("fixture-node", inventory, target, [rule]);
Check(
    known.Blockers.Length == 0 && known.Findings.Single().Verdict == CompatibilityVerdict.Compatible,
    "Exact-version catalogue match allows only the explicitly inventoried scope.");
Check(
    CompatibilityAssessment.Evaluate("other-node", inventory, target, [rule]).Fingerprint != known.Fingerprint,
    "Report binds node identity as well as inventory.");
Check(CompatibilityAssessment.Evaluate("fixture-node", inventory, target, []).Findings.Single().Verdict == CompatibilityVerdict.Unknown,
    "Missing catalogue entry is unknown, never automatically compatible.");
Check(CompatibilityAssessment.Evaluate("fixture-node", inventory, target, [rule with { Version = "5.21" }]).Blockers.Length > 0,
    "Version prefix is not compatibility proof.");
Check(CompatibilityAssessment.Evaluate("fixture-node", inventory, target, [rule with { Publisher = "Different" }]).Blockers.Length > 0,
    "Publisher mismatch does not reuse evidence.");
var requiresVmReport = CompatibilityAssessment.Evaluate(
    "fixture-node",
    inventory,
    target,
    [rule with { Verdict = CompatibilityVerdict.RequiresVm }]);
Check(requiresVmReport.Blockers.Length > 0, "VM classification prevents host-ready approval.");
var conditionalReport = CompatibilityAssessment.Evaluate(
    "fixture-node",
    inventory,
    target,
    [rule with { Verdict = CompatibilityVerdict.Conditional }]);
Check(conditionalReport.Blockers.Length > 0, "Conditions require resolution; no blind OK.");
Check(CompatibilityAssessment.Evaluate("fixture-node", inventory with
{
    Truncated = true,
    Fingerprint = SystemInventory.Create(machine, [application], sources, true).Fingerprint
}, target, [rule]).Blockers.Length > 0,
    "Truncated inventory blocks readiness.");
Check(CompatibilityAssessment.Evaluate("fixture-node", inventory, target with
{
    RequiredInventorySources = ["portable-apps"]
}, [rule]).Blockers.Length > 0,
    "Uncollected required source blocks approval.");
Denied(() => CompatibilityAssessment.Evaluate("fixture-node", inventory, target, [rule, rule]), "Ambiguous rules rejected.");
Denied(
    () => CompatibilityAssessment.Evaluate("fixture-node", inventory, target, [rule with { EvidenceReference = "" }]),
    "Compatible label without evidence rejected.");
Check(CompatibilityAssessment.Evaluate("fixture-node", inventory, target with
{
    OsVersion = "changed-build"
}, [rule]).Fingerprint != known.Fingerprint,
    "Changed target invalidates prior report.");
var changedCatalogueReport = CompatibilityAssessment.Evaluate(
    "fixture-node",
    inventory,
    target,
    [rule with { Notes = "Changed review condition" }]);
Check(changedCatalogueReport.Fingerprint != known.Fingerprint, "Changed catalogue invalidates prior report.");
Check(!AccessVisibility.NeedsConsent(Tool.GetMachineCapabilities) && AccessVisibility.NeedsConsent(Tool.CollectInventory),
    "Inventory requires local consent while capability handshake remains available.");
var pages = new InventoryPages();
JsonObject first = pages.Read(new JsonObject { ["limit"] = 1 });
JsonObject next = pages.Read(new JsonObject { ["inventory_id"] = first["inventory_id"]!.ToString(), ["offset"] = 0, ["limit"] = 1 });
Check(first["fingerprint"]!.ToString() == next["fingerprint"]!.ToString(), "Inventory pages retain the original capture.");
Denied(() => pages.Read(new JsonObject { ["offset"] = 1 }), "Cannot mix pagination with a fresh capture.");
Denied(
    () => ExecutionRoleSelector.FromArguments(["--mcp", "--service-check", "file"]),
    "Controller cannot instantiate a local service review.");

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
try
{
    using var client = new TcpClient();
    Task connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
    using TcpClient server = await listener.AcceptTcpClientAsync(deadline.Token);
    await connecting;
    using var serverSocket = WebSocket.CreateFromStream(server.GetStream(), true, null, TimeSpan.FromSeconds(20));
    using var nodeSocket = WebSocket.CreateFromStream(client.GetStream(), false, null, TimeSpan.FromSeconds(20));
    using var relay = new ReverseRelay(serverSocket);
    Task reading = relay.ReadAsync(deadline.Token);
    Task<JsonObject> review = relay.CallAsync(new JsonObject { ["kind"] = "service_check", ["body"] = new JsonObject() }, deadline.Token);
    JsonObject reviewRequest = (await PairingProtocol.ReceiveAsync(nodeSocket, deadline.Token))!;
    Task<JsonObject> action = relay.CallAsync(new JsonObject { ["kind"] = "action" }, deadline.Token);
    await Task.Delay(150, deadline.Token);
    Check(!action.IsCompleted, "MCP action cannot pass the serialized local-review gate.");
    await PairingProtocol.SendAsync(
        nodeSocket,
        new JsonObject { ["id"] = reviewRequest["id"]!.ToString(), ["result"] = new JsonObject { ["success"] = true } },
        deadline.Token);
    await review;
    JsonObject actionRequest = (await PairingProtocol.ReceiveAsync(nodeSocket, deadline.Token))!;
    Check(actionRequest["kind"]!.ToString() == "action", "Queued action only reaches node after local review finishes.");
    await PairingProtocol.SendAsync(
        nodeSocket,
        new JsonObject { ["id"] = actionRequest["id"]!.ToString(), ["result"] = new JsonObject { ["success"] = true } },
        deadline.Token);
    await action;
    relay.Dispose();
    try
    {
        await reading;
    }
    catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
    {
    }
}
finally
{
    listener.Stop();
}
Console.WriteLine($"{passed} service assessment regression checks passed.");
