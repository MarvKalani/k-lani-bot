using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// The observed-form loader accepts the existing structural format explicitly and rejects unknown formats without rewriting them.
public static class CompactDiscoveryDocumentCodec
{
    private static readonly Regex StateIdentifier = new(@"\A[0-9a-f]{16}\z", RegexOptions.CultureInvariant);

    public static bool TryRead(JsonNode? document, CompactProgramIdentity application,
        out CompactDiscoveryAttempt<IReadOnlyDictionary<string, CompactObservedForm>> attempt)
    {
        attempt = new(null, CompactDiscoveryError.InvalidDocument);
        if (document is not JsonObject root || !Only(root, "format", "version", "application", "forms")
            || !Text(root, "format", out string format) || format != CompactDiscoveryWire.Format
            || root["version"] is not JsonValue version || !version.TryGetValue<int>(out int number)
            || number != CompactDiscoveryWire.Version || root["application"] is not JsonObject identity
            || !Text(identity, "key", out string key) || key != application.Key
            || !Text(identity, "version", out string build) || build != application.Build || root["forms"] is not JsonObject forms)
        {
            return false;
        }
        var records = new Dictionary<string, CompactObservedForm>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> entry in forms)
        {
            if (!CompactDiscoveryCodec.ValidName(entry.Key) || !TryForm(entry.Value, out CompactObservedForm? form))
            {
                return false;
            }
            records.Add(entry.Key, form!);
        }
        attempt = new(records);

        return true;
    }

    private static bool TryForm(JsonNode? node, out CompactObservedForm? form)
    {
        form = null;
        if (node is not JsonObject record || !Only(record, "latest", "states", "observed_transitions")
            || !Text(record, "latest", out string latest) || record["states"] is not JsonObject states
            || states.Count is < 1 or > CompactFormScanner.MaximumStates
            || record["observed_transitions"] is not JsonArray transitions)
        {
            return false;
        }
        var known = new Dictionary<string, CompactObservedState>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> entry in states)
        {
            if (!StateIdentifier.IsMatch(entry.Key) || !TryState(entry.Value, out CompactObservedState? state)
                || CompactDiscoveryFields.State(state!.Fields) != entry.Key)
            {
                return false;
            }
            known.Add(entry.Key, state);
        }
        if (!known.ContainsKey(latest) || !TryTransitions(transitions, known.Keys, out var history))
        {
            return false;
        }
        form = new(latest, known, history!);

        return true;
    }

    private static bool TryState(JsonNode? node, out CompactObservedState? state)
    {
        state = null;
        if (node is not JsonObject record || !Only(record, "fields", "compatibility")
            || record["fields"] is not JsonArray fields || fields.Count > CompactFormRuntime.MaximumFields
            || !TryCompatibility(record["compatibility"], out CompactScanCompatibility? compatibility))
        {
            return false;
        }
        var inventory = new List<CompactObservedField>();
        foreach (JsonNode? field in fields)
        {
            if (!CompactDiscoveryFieldCodec.TryRead(field, out CompactObservedField? descriptor))
            {
                return false;
            }
            inventory.Add(descriptor!);
        }
        state = new(inventory, compatibility!);

        return true;
    }

    private static bool TryCompatibility(JsonNode? node, out CompactScanCompatibility? compatibility)
    {
        compatibility = null;
        if (node is not JsonObject record || !Only(record, "node_version", "platform", "application_version")
            || !Text(record, "node_version", out string version) || !Text(record, "platform", out string platform)
            || !Text(record, "application_version", out string application))
        {
            return false;
        }
        compatibility = new(version, platform, application);

        return true;
    }

    private static bool TryTransitions(JsonArray array, IEnumerable<string> states,
        out IReadOnlyList<CompactObservedTransition>? history)
    {
        history = null;
        var transitions = new List<CompactObservedTransition>();
        var known = states.ToHashSet(StringComparer.Ordinal);
        if (array.Count > known.Count * known.Count)
        {
            return false;
        }
        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject record || !Only(record, "from", "to", "cause")
                || !Text(record, "from", out string from) || !known.Contains(from)
                || !Text(record, "to", out string to) || !known.Contains(to) || from == to
                || !Text(record, "cause", out string cause) || cause != CompactDiscoveryNames.TransitionCause)
            {
                return false;
            }
            var transition = new CompactObservedTransition(from, to);
            if (transitions.Contains(transition))
            {
                return false;
            }
            transitions.Add(transition);
        }
        history = transitions;

        return true;
    }

    public static bool Only(JsonObject document, params string[] names)
    {
        return document.All(entry => names.Contains(entry.Key, StringComparer.Ordinal));
    }

    public static bool Text(JsonObject document, string name, out string text)
    {
        text = "";

        return document[name] is JsonValue value && value.TryGetValue(out text!);
    }

    public static bool Boolean(JsonObject document, string name, out bool flag)
    {
        flag = false;

        return document[name] is JsonValue value && value.TryGetValue(out flag);
    }
}
