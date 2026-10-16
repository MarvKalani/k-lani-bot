using System.Text.Json.Nodes;
using System.Text;

namespace KLaniBot.Transport.CompactUi;

// The form scanner persists observed structural states only; it does not infer constraints, causes or executable functions.
public sealed class CompactFormScanner(CompactObservedFormStore? store = null)
{
    public const int MaximumStates = 32;
    private IReadOnlyDictionary<string, CompactObservedForm> _forms = store?.Forms
        ?? new Dictionary<string, CompactObservedForm>(StringComparer.Ordinal);
    public bool IsPersistent => store != null;

    public bool TryScan(IReadOnlyList<CompactTreeNode> nodes, CompactScanOptions options, CompactNativeWindow observedWindow,
        CompactScanCompatibility compatibility, out CompactDiscoveryAttempt<CompactScanResult> attempt)
    {
        if (!CompactFormScan.TryObserve(nodes, options, observedWindow, out var observed))
        {
            attempt = new(null, observed.Error, observed.Detail);
            return false;
        }
        CompactScanObservation observation = observed.Value!;
        CompactObservedForm? previous = _forms.GetValueOrDefault(options.Form);
        var states = new Dictionary<string, CompactObservedState>(previous?.States
            ?? new Dictionary<string, CompactObservedState>(), StringComparer.Ordinal);
        if (!states.ContainsKey(observation.State) && states.Count >= MaximumStates)
        {
            attempt = new(null, CompactDiscoveryError.StateBoundExceeded);
            return false;
        }
        states[observation.State] = new(observation.Fields, compatibility);
        var history = previous?.Transitions.ToList() ?? [];
        var transition = new CompactObservedTransition(previous?.Latest ?? observation.State, observation.State);
        if (transition.From != transition.To && !history.Contains(transition))
        {
            history.Add(transition);
        }
        var forms = new Dictionary<string, CompactObservedForm>(_forms, StringComparer.Ordinal)
        {
            [options.Form] = new(observation.State, states, history)
        };
        if (!TrySave(forms, out var saved))
        {
            attempt = new(null, saved.Error, saved.Detail);
            return false;
        }
        _forms = store?.Forms ?? forms;
        attempt = new(new(observation, states.Count, Changes(previous?.States[previous.Latest].Fields ?? [], observation.Fields)));

        return true;
    }

    private bool TrySave(Dictionary<string, CompactObservedForm> forms, out CompactDiscoveryAttempt<CompactObservedFormStore> attempt)
    {
        if (store != null)
        {
            return store.TryReplace(forms, out attempt);
        }
        JsonObject document = CompactDiscoveryWire.Document(new("session", "volatile"), forms);
        if (Encoding.UTF8.GetByteCount(CompactJsonText.Serialize(document)) > CompactObservedFormStore.MaximumDocumentBytes)
        {
            attempt = new(null, CompactDiscoveryError.DocumentBoundExceeded);
            return false;
        }
        attempt = new(null);

        return true;
    }

    public bool TryRead(CompactFormLookup lookup, out CompactDiscoveryAttempt<JsonObject> attempt)
    {
        if (!CompactDiscoveryCodec.ValidName(lookup.Form))
        {
            attempt = new(null, CompactDiscoveryError.InvalidLookup);
            return false;
        }
        CompactObservedForm? form = _forms.GetValueOrDefault(lookup.Form);
        string? state = lookup.State ?? form?.Latest;
        if (form == null || state == null || !form.States.ContainsKey(state))
        {
            attempt = new(null, CompactDiscoveryError.UnknownState);
            return false;
        }
        attempt = new(CompactDiscoveryWire.Read(lookup.Form, state, form));

        return true;
    }

    private static CompactScanChanges Changes(IReadOnlyList<CompactObservedField> before, IReadOnlyList<CompactObservedField> after)
    {
        var earlier = Index(before);
        var current = Index(after);

        return new(current.Keys.Where(key => !earlier.ContainsKey(key)).ToArray(),
            earlier.Keys.Where(key => !current.ContainsKey(key)).ToArray(),
            after.Where(field => earlier.TryGetValue(field.Key, out CompactObservedField? previous)
                && CompactJsonText.Serialize(CompactDiscoveryWire.Field(previous, true))
                != CompactJsonText.Serialize(CompactDiscoveryWire.Field(field, true))).Select(field => field.Key).ToArray());
    }

    private static Dictionary<string, CompactObservedField> Index(IReadOnlyList<CompactObservedField> fields)
    {
        var indexed = new Dictionary<string, CompactObservedField>(StringComparer.Ordinal);
        foreach (CompactObservedField field in fields)
        {
            indexed[field.Key] = field;
        }

        return indexed;
    }
}
