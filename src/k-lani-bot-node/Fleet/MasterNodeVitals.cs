using System.Text.Json.Nodes;
using KLaniBot.Common;
using KLaniBot.Transport;

namespace KLaniBot.Fleet;

// Use case: the master asks every connected node that understands it for its vital signs, right after it connects and then
// at the operator's poll interval, keeps the last answer for the controller tray and list_nodes and writes the history. A
// poll is sent only while the node's relay is idle, so it never delays an action. Last contact is the node's last answer to
// any call. It lives in memory: after a master restart a node that has not reconnected reports no contact since the master
// started.
internal sealed class MasterNodeVitals(string stateDirectory)
{
    // The node answers from memory without waiting; a dispatched call that times out ends the relay, so the limit is generous.
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(20);
    private readonly object _gate = new();
    private readonly Dictionary<string, NodeVitals> _vitals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _contacts = new(StringComparer.Ordinal);
    private readonly NodeVitalsHistory _history = new(stateDirectory);

    public JsonObject HistorySummary(string node, TimeSpan period)
    {
        return _history.Summary(node, period, DateTimeOffset.UtcNow);
    }

    // Writes the operator's report of these nodes privately into the state directory and returns its path.
    public string WriteReport(IEnumerable<string> nodes, TimeSpan period)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var histories = nodes.Select(node => new NodeHistory(node, _history.Samples(node, period, now))).ToList();
        string path = Path.Combine(stateDirectory, NodeVitalsReport.FileName);
        PrivateFile.WriteAllBytesAtomically(path, System.Text.Encoding.UTF8.GetBytes(NodeVitalsReport.Build(histories, period, now)));
        return path;
    }

    // Right after a connection the relay may still carry the master's first message to the node, so the first poll retries
    // briefly instead of leaving the node without vitals until the next round.
    public async Task PollOnConnectAsync(string node, ReverseRelay relay, CancellationToken cancellation)
    {
        NodeVitalsSettings settings = NodeVitalsSettings.Read(stateDirectory);
        for (int attempt = 0; attempt < 10 && !await PollAsync(node, relay, settings, cancellation); attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
        }
    }

    // Returns whether the request was sent. A failed poll only leaves the last vitals in place: a node that disconnects
    // mid-poll has its relay disposed, and that must not end the master's poll loop.
    public async Task<bool> PollAsync(string node, ReverseRelay relay, NodeVitalsSettings settings, CancellationToken cancellation)
    {
        JsonObject? reply;
        try
        {
            reply = await relay.CallIfIdleAsync(new JsonObject { ["kind"] = "vitals" }, cancellation, AnswerTimeout);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or IOException
            or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException)
        {
            return true;
        }
        if (reply == null)
        {
            return false;
        }
        bool answered = reply["success"] is JsonValue success && success.TryGetValue(out bool succeeded) && succeeded;
        if (answered && NodeVitals.TryRead(reply["vitals"]) is { } vitals)
        {
            lock (_gate)
            {
                _vitals[node] = vitals;
            }
            _history.Record(node, vitals, settings.Alerts(vitals), settings, DateTimeOffset.UtcNow);
        }
        return true;
    }

    // Offline nodes keep their last contact time but no vitals, which would be stale.
    public void Disconnected(string node, DateTimeOffset lastContact)
    {
        lock (_gate)
        {
            _contacts[node] = lastContact;
            _vitals.Remove(node);
        }
    }

    public ControllerNodeRow Row(string node, string version, ReverseRelay? relay, NodeVitalsSettings limits, DateTimeOffset now)
    {
        lock (_gate)
        {
            DateTimeOffset? contact = relay?.LastContact ?? (_contacts.TryGetValue(node, out DateTimeOffset last) ? last : null);
            long? seconds = contact is { } heard ? Math.Max(0, (long)(now - heard).TotalSeconds) : null;
            NodeVitals? vitals = relay != null ? _vitals.GetValueOrDefault(node) : null;
            NodeAlerts alerts = vitals == null ? NodeAlerts.None : limits.Alerts(vitals);
            return new ControllerNodeRow(node, relay != null, version, vitals, seconds, alerts);
        }
    }
}
