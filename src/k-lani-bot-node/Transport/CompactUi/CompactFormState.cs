using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// One volatile form binds its current metadata/value revisions to the original verified application/window scope.
public sealed class CompactFormState(int processId, CompactSemanticTarget scope)
{
    public CompactFormReference Reference { get; } = new(Guid.NewGuid());
    public int ProcessId { get; } = processId;
    public CompactSemanticTarget Scope { get; private set; } = scope;
    public string Key { get; } = ScopeKey(processId, scope);
    public IReadOnlyList<CompactFormField> Fields { get; private set; } = [];
    public IReadOnlyList<CompactFormFieldReference> Changed { get; private set; } = [];
    public IReadOnlyList<CompactFormFieldReference> Removed { get; private set; } = [];
    public string ContextRevision { get; private set; } = "";
    public string StructureRevision { get; private set; } = "";
    public string SnapshotId { get; private set; } = "";
    public DateTimeOffset Touched
    {
        get; internal set;
    }

    public bool TryRefresh(CompactFormSnapshot snapshot, out CompactFormAttempt<CompactFormState> attempt)
    {
        if (snapshot.ProcessId != ProcessId || Scope.WindowHandle == null || string.IsNullOrEmpty(Scope.ProcessStarted))
        {
            attempt = new(null, CompactFormError.ScopeChanged);
            return false;
        }
        CompactNativeWindow[] windows = snapshot.Windows.Where(window => window.Handle == Scope.WindowHandle
            && window.Started == Scope.ProcessStarted).ToArray();
        if (windows.Length != 1)
        {
            attempt = new(null, CompactFormError.ScopeChanged);
            return false;
        }
        CompactSemanticTarget current = Scope with
        {
            WindowKind = windows[0].Kind
        };
        if (!CompactTargetResolver.TryResolve(snapshot.Nodes, current, true, out var resolved))
        {
            attempt = new(null, CompactFormError.ScopeChanged, CompactTreeNames.Error(resolved.Error!.Value));
            return false;
        }
        if (!TryFields(snapshot, windows[0], resolved.Value, out var fields))
        {
            attempt = new(null, fields.Error);
            return false;
        }
        Update(fields.Value!, snapshot.SnapshotId, current);
        attempt = new(this, null);

        return true;
    }

    private bool TryFields(CompactFormSnapshot snapshot, CompactNativeWindow window, int rootId,
        out CompactFormAttempt<IReadOnlyList<CompactFormField>> attempt)
    {
        CompactTreeNode root = snapshot.Nodes.First(node => node.Id == rootId);
        CompactTreeNode[] selected = snapshot.Nodes.Where(node => (ReferenceEquals(node, root)
            || CompactTreeScope.Contains(snapshot.Nodes, root, node)) && CompactFormFields.TryKind(node.Role, out _)).ToArray();
        if (selected.Length > CompactFormRuntime.MaximumFields)
        {
            attempt = new(null, CompactFormError.FieldBoundExceeded);
            return false;
        }
        var previous = Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        CompactFormField[] fields = selected.Select(node => CompactFormFields.Create(ProcessId, snapshot, window, node, previous))
            .ToArray();
        if (fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != fields.Length)
        {
            attempt = new(null, CompactFormError.AmbiguousField);
            return false;
        }
        attempt = new(fields, null);

        return true;
    }

    private void Update(IReadOnlyList<CompactFormField> fields, string snapshotId, CompactSemanticTarget current)
    {
        var previous = Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        Changed = fields.Where(field => !previous.TryGetValue(field.Key, out CompactFormField? earlier)
            || CompactJsonText.Serialize(CompactFormWire.Field(field)) != CompactJsonText.Serialize(CompactFormWire.Field(earlier)))
            .Select(field => field.Reference).ToArray();
        Removed = Fields.Where(field => !fields.Any(candidate => candidate.Key == field.Key)).Select(field => field.Reference).ToArray();
        Fields = fields;
        StructureRevision = CompactFormWire.StructureFingerprint(fields);
        ContextRevision = CompactFormWire.ContextFingerprint(StructureRevision, Key, fields);
        SnapshotId = snapshotId;
        Scope = current;
    }

    private static string ScopeKey(int processId, CompactSemanticTarget scope)
    {
        var key = new JsonObject { ["pid"] = processId, ["selector"] = JsonNode.Parse(CompactSemanticIdentity.StableKey(scope)) };
        if (scope.ProcessStarted != null)
        {
            key["started"] = scope.ProcessStarted;
        }

        return CompactJsonText.Serialize(key);
    }
}
