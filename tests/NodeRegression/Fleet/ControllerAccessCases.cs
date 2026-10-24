using System.Text.Json.Nodes;
using KLaniBot.Fleet;
using KLaniBot.Visibility;

// The master keeps one row per MCP client from the calls it forwards, refuses a paused client's actions but still lets its
// capability reads, end_session and presence through, survives a restart switched off, and keeps its status reply within
// the administration pipe's frame.
internal static class ControllerAccessCases
{
    private static readonly string ClaudeInstance = Guid.CreateVersion7().ToString();
    private static readonly string CodexInstance = Guid.CreateVersion7().ToString();

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Controller access");
        string state = Directory.CreateDirectory(Path.Combine(temporaryRoot, "controller-access-" + Guid.CreateVersion7())).FullName;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        RowsGroupSessionsOfOneGateway(checks, state, now);
        PauseRefusesOnlyThatClient(checks, state, now);
        SwitchOffSurvivesRestart(checks, state, now);
        RowsEndWithCloseOrSilence(checks, state, now);
        StatusStaysWithinTheFrame(checks, state, now);
        return checks.Passed;
    }

    private static ForwardedCall Call(string instance, string node, ForwardedRequest request, string action = "",
        string client = "Claude Code")
    {
        string session = instance + "/" + node;
        return new ForwardedCall(instance, session, node, SessionPresentation.Create(client, "Testbericht prüfen"), request, action);
    }

    private static void RowsGroupSessionsOfOneGateway(Checks checks, string state, DateTimeOffset now)
    {
        var access = new ControllerAccess(state);
        access.Record(Call(ClaudeInstance, "office-pc", ForwardedRequest.Action, "take_screenshot"), now);
        access.Record(Call(ClaudeInstance, "laptop", ForwardedRequest.Heartbeat), now);
        ControllerStatus status = access.Status([new ControllerNodeRow("office-pc", true, "0.2610.0604")], now);

        checks.That(status.Clients.Count == 1 && status.Clients[0].Nodes.SequenceEqual(["laptop", "office-pc"]),
            "the sessions of one gateway process to two nodes are one client row");
        checks.That(status.Clients[0].Client == "Claude Code" && status.Clients[0].Purpose == "Testbericht prüfen",
            "the row shows the client's name and task as reported");
        checks.That(status.Clients[0].State == ControllerClientState.Active, "a client with a recent action is working");
        string session = Guid.CreateVersion7().ToString();
        checks.That(ControllerAccess.InstanceOf(null, session) == session && ControllerAccess.InstanceOf("not-a-uuid", session) == session,
            "an older gateway without an instance header is grouped by its session");
    }

    private static void PauseRefusesOnlyThatClient(Checks checks, string state, DateTimeOffset now)
    {
        var access = new ControllerAccess(state);
        access.Record(Call(ClaudeInstance, "office-pc", ForwardedRequest.Action, "click_element"), now);
        access.Record(Call(CodexInstance, "office-pc", ForwardedRequest.Action, "click_element", "codex"), now);
        IReadOnlyList<NodeSession> closed = access.Pause(ClaudeInstance);

        checks.That(closed.SequenceEqual([new NodeSession("office-pc", ClaudeInstance + "/office-pc")]),
            "pausing names the sessions to close");
        checks.That(access.Decide(Call(ClaudeInstance, "office-pc", ForwardedRequest.Action, "click_element")) == AccessDecision.Refuse,
            "a paused client's action is refused");
        checks.That(access.Decide(Call(CodexInstance, "office-pc", ForwardedRequest.Action, "click_element")) == AccessDecision.Forward,
            "another client keeps working");
        checks.That(access.Decide(Call(ClaudeInstance, "office-pc", ForwardedRequest.Action, "get_machine_capabilities"))
            == AccessDecision.Forward && access.Decide(Call(ClaudeInstance, "office-pc", ForwardedRequest.Action, "end_session"))
            == AccessDecision.Forward, "capability reads and end_session pass while paused");
        checks.That(access.Decide(Call(ClaudeInstance, "office-pc", ForwardedRequest.Heartbeat)) == AccessDecision.Absorb
            && access.Decide(Call(ClaudeInstance, "office-pc", ForwardedRequest.Close)) == AccessDecision.Forward,
            "a paused client's heartbeat is answered by the master, its close reaches the node");
        JsonObject refusal = access.Refusal();
        checks.That(refusal["code"]?.ToString() == "access_paused" && refusal["outcome"]?.ToString() == "not_dispatched"
            && refusal["error"]!.ToString().Contains("paused this MCP client", StringComparison.Ordinal),
            "the refusal says the operator paused this client and that nothing was sent");
        checks.That(access.Status([], now).Clients.Single(row => row.Instance == ClaudeInstance).State == ControllerClientState.Paused,
            "the status shows the client as paused");
        access.Resume(ClaudeInstance);
        checks.That(access.Decide(Call(ClaudeInstance, "office-pc", ForwardedRequest.Action, "click_element")) == AccessDecision.Forward,
            "a resumed client works again");
        checks.That(Refused(() => access.Pause(Guid.CreateVersion7().ToString())), "pausing a client that is not there is refused");
    }

    private static void SwitchOffSurvivesRestart(Checks checks, string state, DateTimeOffset now)
    {
        var access = new ControllerAccess(state);
        access.SwitchBot("off");
        var restarted = new ControllerAccess(state);
        restarted.Record(Call(CodexInstance, "office-pc", ForwardedRequest.Action, "type_text", "codex"), now);

        checks.That(restarted.Bot == BotSwitch.Off, "a restarted master stays switched off");
        checks.That(restarted.Decide(Call(CodexInstance, "office-pc", ForwardedRequest.Action, "type_text")) == AccessDecision.Refuse
            && restarted.Refusal()["error"]!.ToString().Contains("switched k-lani-bot off", StringComparison.Ordinal),
            "a switched-off bot refuses every client and says so");
        checks.That(Refused(() => restarted.SwitchBot("maybe")) && restarted.Bot == BotSwitch.Off,
            "an unknown switch word changes nothing");
        restarted.SwitchBot("on");
        checks.That(new ControllerAccess(state).Bot == BotSwitch.On, "switching on is stored too");
        File.WriteAllText(Path.Combine(state, ControllerAccess.StateFileName), "{ broken");
        checks.That(new ControllerAccess(state).Bot == BotSwitch.On, "a damaged switch file reads as on");
    }

    private static void RowsEndWithCloseOrSilence(Checks checks, string state, DateTimeOffset now)
    {
        var access = new ControllerAccess(state);
        access.Record(Call(ClaudeInstance, "office-pc", ForwardedRequest.Action, "list_windows"), now);
        access.Record(Call(ClaudeInstance, "office-pc", ForwardedRequest.Close), now);
        checks.That(access.Status([], now).Clients.Count == 0, "the gateway's close ends its row");

        access.Record(Call(CodexInstance, "office-pc", ForwardedRequest.Heartbeat), now);
        checks.That(access.Status([], now.AddSeconds(40)).Clients.Single().State == ControllerClientState.Idle,
            "a client that only sends heartbeats is connected and waiting");
        access.Record(Call(ClaudeInstance, "office-pc", ForwardedRequest.Action, "end_session"), now);
        access.Pause(ClaudeInstance);
        List<ControllerClientRow> later = access.Status([], now.AddSeconds(61)).Clients.ToList();
        checks.That(later.Count == 1 && later[0].Instance == ClaudeInstance, "a row ends after a minute without any request");
        checks.That(access.Decide(Call(ClaudeInstance, "office-pc", ForwardedRequest.Action, "click_element")) == AccessDecision.Refuse,
            "a pause outlives the silence after end_session");
    }

    private static void StatusStaysWithinTheFrame(Checks checks, string state, DateTimeOffset now)
    {
        var access = new ControllerAccess(state);
        string longPurpose = new('ü', 500);
        for (int client = 0; client < 80; client++)
        {
            var presentation = SessionPresentation.Create(new string('c', 80), longPurpose);
            var call = new ForwardedCall(Guid.CreateVersion7().ToString(), "session-" + client, "office-pc", presentation,
                ForwardedRequest.Action, "list_windows");
            access.Record(call, now);
        }
        var nodes = Enumerable.Range(0, 40).Select(index => new ControllerNodeRow("node-" + index, index % 2 == 0, "0.2610.0604")).ToList();
        JsonObject reply = access.Status(nodes, now).ToJson();

        checks.That(System.Text.Encoding.UTF8.GetByteCount(reply.ToJsonString()) < 16 * 1024, "the status reply fits the pipe frame");
        checks.That(reply["more_clients"]!.GetValue<int>() > 0 && reply["more_nodes"]!.GetValue<int>() == 0,
            "clients beyond the budget are counted, not sent");
        checks.That(reply["clients"]![0]!["purpose"]!.ToString().Length == 121, "a long purpose is shortened");
        checks.That(ControllerStatus.TryRead(reply, out ControllerStatus read) && read.Clients.Count + read.MoreClients == 80,
            "the tray reads the reply back with every client accounted for");
        var vitals = new NodeVitals(new string('s', 80), 1L << 32, 1L << 34, "/System/Volumes/Data", 1L << 36, 1L << 38, 99.9,
            1L << 30);
        var measured = Enumerable.Range(0, 40).Select(index => new ControllerNodeRow("node-" + index, true, "0.2610.0604", vitals,
            long.MaxValue)).ToList();
        JsonObject crowded = access.Status(measured, now).ToJson();
        checks.That(System.Text.Encoding.UTF8.GetByteCount(crowded.ToJsonString()) < 16 * 1024
            && crowded["more_nodes"]!.GetValue<int>() > 0 && crowded["clients"]!.AsArray().Count > 0,
            "many nodes with vitals fit the frame without crowding out every client");
        checks.That(!ControllerStatus.TryRead(new JsonObject { ["success"] = "yes", ["bot"] = "on" }, out _),
            "a reply of another shape is no status");
    }

    private static bool Refused(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return true;
        }
        return false;
    }
}
