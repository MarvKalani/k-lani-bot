using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Visibility;

namespace KLaniBot.Fleet;

// What reached the master on a node route: an action, or a session's heartbeat or close.
internal enum ForwardedRequest
{
    Action,
    Heartbeat,
    Close
}

// What the master does with a forwarded request: pass it on, refuse it, or answer a paused client's heartbeat itself.
internal enum AccessDecision
{
    Forward,
    Refuse,
    Absorb
}

// One request on a node route as the master saw it. Instance groups the sessions of one gateway process; Action is the
// tool name of an action and empty for presence events.
internal sealed record ForwardedCall(
    string Instance,
    string Session,
    string Node,
    SessionPresentation Presentation,
    ForwardedRequest Request,
    string Action);

// A node session of a paused client, which the master closes on that node.
internal sealed record NodeSession(string Node, string Session);

// Use case: the controller tray on the operator's computer shows which MCP clients use the bot through this master, and the
// person pauses one of them or switches the bot off. Every gateway call to a paired node passes the master, so the master
// keeps the client rows and decides the refusal itself. Rows and pauses live in memory and end with the gateway; only the
// on/off switch is stored, so a restarted master stays off. Callers hold the master's lock.
internal sealed class ControllerAccess
{
    private const string StateFile = "controller-access.json";
    private const int StatusBudgetBytes = 12 * 1024;
    private const int MaximumShownPurpose = 120;
    private const int MaximumShownNodesPerClient = 4;
    // Three missed heartbeats (every 20 seconds) end a row whose gateway went away without saying goodbye. A paused row stays
    // much longer: a gateway stops its heartbeats after end_session, and a pause must not lapse just because the client waited.
    private static readonly TimeSpan RowLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PausedRowLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly Dictionary<string, ClientRow> _rows = new(StringComparer.Ordinal);
    private BotSwitch _bot;

    // Mutable state of one client row; ControllerClientRow is its reported form.
    private sealed class ClientRow(string instance)
    {
        public string Instance { get; } = instance;
        public SessionPresentation Presentation { get; set; } = SessionPresentation.Create(null, null);
        public Dictionary<string, string> SessionsByNode { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset LastSeen
        {
            get; set;
        }
        public DateTimeOffset LastAction { get; set; } = DateTimeOffset.MinValue;
        public bool Paused
        {
            get; set;
        }
    }

    public ControllerAccess(string stateDirectory)
    {
        _path = Path.Combine(stateDirectory, StateFile);
        _bot = ReadSwitch(_path);
    }

    public static string StateFileName => StateFile;

    public BotSwitch Bot => _bot;

    // A damaged or missing file means on: the switch only exists to stop clients, and a broken file must not hide that the
    // operator can switch off again.
    private static BotSwitch ReadSwitch(string path)
    {
        if (!File.Exists(path))
        {
            return BotSwitch.On;
        }
        string? word = null;
        try
        {
            word = JsonNode.Parse(File.ReadAllText(path))?["bot"]?.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
        }

        bool known = ControllerStatus.TryParseBot(word, out BotSwitch stored);
        return known ? stored : BotSwitch.On;
    }

    // An instance that is no UUID (an older gateway sends none) is replaced by the session, so such a gateway shows one row
    // per node session instead of none.
    public static string InstanceOf(string? header, string session)
    {
        return Guid.TryParse(header, out Guid instance) ? instance.ToString() : session;
    }

    public void Record(ForwardedCall call, DateTimeOffset now)
    {
        Expire(now);
        if (call.Request == ForwardedRequest.Close)
        {
            Forget(call);
            return;
        }
        if (!_rows.TryGetValue(call.Instance, out ClientRow? row))
        {
            row = new ClientRow(call.Instance);
            _rows.Add(call.Instance, row);
        }

        row.Presentation = call.Presentation;
        row.SessionsByNode[call.Node] = call.Session;
        row.LastSeen = now;
        if (call.Request == ForwardedRequest.Action)
        {
            row.LastAction = now;
        }
    }

    // A close ends that node's session; the row ends with its last session.
    private void Forget(ForwardedCall call)
    {
        if (!_rows.TryGetValue(call.Instance, out ClientRow? row))
        {
            return;
        }
        row.SessionsByNode.Remove(call.Node);
        if (row.SessionsByNode.Count == 0)
        {
            _rows.Remove(call.Instance);
        }
    }

    private void Expire(DateTimeOffset now)
    {
        string[] expired = _rows.Values
            .Where(row => now - row.LastSeen > (row.Paused ? PausedRowLifetime : RowLifetime))
            .Select(row => row.Instance)
            .ToArray();
        foreach (string instance in expired)
        {
            _rows.Remove(instance);
        }
    }

    // Capability reads keep tools/list complete, end_session releases the desktop lease at once, and a close lets the
    // node drop its indicator; none of them operates the node.
    public AccessDecision Decide(ForwardedCall call)
    {
        bool paused = _bot == BotSwitch.Off || (_rows.TryGetValue(call.Instance, out ClientRow? row) && row.Paused);
        if (!paused || call.Request == ForwardedRequest.Close)
        {
            return AccessDecision.Forward;
        }
        if (call.Request == ForwardedRequest.Heartbeat)
        {
            return AccessDecision.Absorb;
        }

        bool exempt = ToolNames.TryParse(call.Action, out Tool tool) && tool is Tool.GetMachineCapabilities or Tool.EndSession;
        return exempt ? AccessDecision.Forward : AccessDecision.Refuse;
    }

    // The text the AI reads in its failed tool result; it says who stopped it and what to do.
    public JsonObject Refusal()
    {
        string message = _bot == BotSwitch.Off
            ? "The operator switched k-lani-bot off in the controller tray. Nothing was sent to the node. "
                + "Stop and tell the user; do not retry until they switch it on again."
            : "The operator paused this MCP client in the controller tray. Nothing was sent to the node. "
                + "Stop and tell the user; do not retry until they resume it.";
        return ActionResults.Failure(ActionErrorCode.AccessPaused, ActionOutcome.NotDispatched, message);
    }

    // Returns the node sessions to close. Throws ArgumentException for an instance that has no row.
    public IReadOnlyList<NodeSession> Pause(string instance)
    {
        ClientRow row = RequireRow(instance);
        row.Paused = true;

        return row.SessionsByNode.Select(entry => new NodeSession(entry.Key, entry.Value)).ToList();
    }

    // Throws ArgumentException for an instance that has no row.
    public void Resume(string instance)
    {
        RequireRow(instance).Paused = false;
    }

    // Throws ArgumentException for an instance that has no row.
    private ClientRow RequireRow(string instance)
    {
        return _rows.TryGetValue(instance, out ClientRow? row)
            ? row
            : throw new ArgumentException("No MCP client with this instance uses the bot; see controller-status.");
    }

    // Throws ArgumentException for a word other than on or off; the stored switch is unchanged then.
    public void SwitchBot(string word)
    {
        if (!ControllerStatus.TryParseBot(word, out BotSwitch wanted))
        {
            throw new ArgumentException("Switch the bot on or off.");
        }

        var document = new JsonObject { ["bot"] = ControllerStatus.WireName(wanted) };
        PairingProtocol.WritePrivate(_path, document.ToJsonString(), true);
        _bot = wanted;
    }

    public ControllerStatus Status(IReadOnlyList<ControllerNodeRow> nodes, DateTimeOffset now)
    {
        Expire(now);
        var clients = _rows.Values.OrderBy(row => row.Instance, StringComparer.Ordinal).Select(row => Report(row, now)).ToList();
        int budget = StatusBudgetBytes;

        List<ControllerNodeRow> shownNodes = TakeWithinBudget(nodes, ControllerStatus.NodeJson, ref budget);
        List<ControllerClientRow> shownClients = TakeWithinBudget(clients, ControllerStatus.ClientJson, ref budget);
        return new ControllerStatus(_bot, shownNodes, nodes.Count - shownNodes.Count, shownClients, clients.Count - shownClients.Count);
    }

    private static ControllerClientRow Report(ClientRow row, DateTimeOffset now)
    {
        ControllerClientState state = row.Paused
            ? ControllerClientState.Paused
            : now - row.LastAction <= ActiveWindow ? ControllerClientState.Active : ControllerClientState.Idle;
        string purpose = row.Presentation.Purpose;
        string shownPurpose = purpose.Length > MaximumShownPurpose ? purpose[..MaximumShownPurpose] + "…" : purpose;
        var nodes = row.SessionsByNode.Keys.Order(StringComparer.Ordinal).Take(MaximumShownNodesPerClient).ToList();
        int seconds = (int)Math.Max(0, (now - row.LastSeen).TotalSeconds);

        return new ControllerClientRow(row.Instance, row.Presentation.Client, shownPurpose, nodes, state, seconds);
    }

    // The administration pipe refuses frames over 16 KiB; entries beyond the budget are counted, not sent.
    private static List<T> TakeWithinBudget<T>(IEnumerable<T> entries, Func<T, JsonObject> toJson, ref int budget)
    {
        var taken = new List<T>();
        foreach (T entry in entries)
        {
            int size = Encoding.UTF8.GetByteCount(toJson(entry).ToJsonString());
            if (size > budget)
            {
                break;
            }
            budget -= size;
            taken.Add(entry);
        }
        return taken;
    }
}
