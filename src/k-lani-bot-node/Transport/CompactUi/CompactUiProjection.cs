using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KLaniBot.Transport.CompactUi;

// Projection keeps observed semantic targets on the host; published references are re-resolved against a fresh native tree before input.
public sealed class CompactUiProjection(TimeProvider? timeProvider = null)
{
    // Per-context target state can be discarded with its view context without retaining native input IDs.
    private sealed class Targets(CompactViewContext context, int processId)
    {
        public CompactViewContext Context { get; } = context;
        public int ProcessId { get; } = processId;
        public Dictionary<string, string> Identities { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, CompactSemanticTarget> Current { get; set; } = new(StringComparer.Ordinal);
    }

    private readonly CompactViewStore _views = new(timeProvider);
    private readonly Dictionary<CompactViewKey, Targets> _targets = new();
    private readonly Dictionary<string, CompactViewKey> _exposed = new(StringComparer.Ordinal);
    private int _nextReference = 1;
    public IReadOnlySet<string> ExposedReferences => _exposed.Keys.ToHashSet(StringComparer.Ordinal);

    public bool TryObserve(int processId, IReadOnlyList<CompactTreeNode> nodes, CompactProjectionOptions options,
        out CompactProjectionAttempt attempt)
    {
        if (nodes == null || options == null || options.MaximumCharacters < 0)
        {
            attempt = new(null, null, CompactViewError.InvalidPageArguments);
            return false;
        }
        CompactViewContext context = _views.Context(ContextKey(processId, options));
        Prune();
        if (!_targets.TryGetValue(context.Key, out Targets? targets))
        {
            targets = new(context, processId);
            _targets[context.Key] = targets;
        }
        if (!CompactTreeScope.TryApplicationBranch(nodes, options.Root, out var branch))
        {
            attempt = new(null, branch.Error, null, branch.Detail);
            return false;
        }
        IReadOnlyList<CompactViewRow> rows = Project(nodes, branch.Value!, options, targets);
        string stamp = Stamp(branch.Value!);
        CompactViewOptions page = (options.Page ?? new()) with
        {
            IncludeOffscreen = options.IncludeOffscreen
        };
        if (!_views.TryPage(context, rows, stamp, page, out var paged))
        {
            Prune();
            attempt = new(null, null, paged.Error);
            return false;
        }
        foreach (CompactViewRow row in paged.Value!.Elements.Concat(paged.Value.Changed))
        {
            _exposed[row.Reference] = context.Key;
        }
        Prune();
        attempt = new(paged.Value, null, null);

        return true;
    }

    private IReadOnlyList<CompactViewRow> Project(IReadOnlyList<CompactTreeNode> all, IReadOnlyList<CompactTreeNode> selected,
        CompactProjectionOptions options, Targets targets)
    {
        var current = new Dictionary<string, CompactSemanticTarget>(StringComparer.Ordinal);
        var active = new Dictionary<string, string>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<CompactViewRow>();
        foreach (CompactTreeNode node in selected)
        {
            if (!CompactElementProjection.IsProjected(all, node, options.Contains))
            {
                continue;
            }
            CompactSemanticTarget target = CompactSemanticIdentity.Target(targets.ProcessId, all, node, options.ObservedWindow);
            string identity = CompactSemanticIdentity.StableKey(target);
            int occurrence = occurrences.GetValueOrDefault(identity);
            occurrences[identity] = occurrence + 1;
            string key = identity + ":" + occurrence.ToString(CultureInfo.InvariantCulture);
            string reference = targets.Identities.GetValueOrDefault(key) ?? "e" + (_nextReference++).ToString(CultureInfo.InvariantCulture);
            active[key] = reference;
            current[reference] = target;
            rows.Add(CompactElementProjection.Row(reference, node, options.MaximumCharacters));
        }
        foreach (string reference in current.Keys.ToArray())
        {
            CompactSemanticTarget target = current[reference];
            current[reference] = target with
            {
                Ambiguous = occurrences[CompactSemanticIdentity.StableKey(target)] > 1
            };
        }
        targets.Identities = active;
        targets.Current = current;

        return rows;
    }

    public bool TryTarget(string reference, int processId, out CompactTargetAttempt attempt)
    {
        _views.Expire();
        Prune();
        if (reference == null || !_exposed.TryGetValue(reference, out CompactViewKey? key)
            || !_targets.TryGetValue(key, out Targets? context)
            || !context.Current.TryGetValue(reference, out CompactSemanticTarget? target) || target.ProcessId != processId)
        {
            attempt = new(null, CompactReferenceError.Unknown);
            return false;
        }
        if (target.Ambiguous || target.UnverifiedWindow)
        {
            attempt = new(null, target.Ambiguous ? CompactReferenceError.Ambiguous : CompactReferenceError.UnverifiedWindow);
            return false;
        }
        attempt = new(target, null);

        return true;
    }

    private void Prune()
    {
        foreach (CompactViewKey key in _targets.Keys.ToArray())
        {
            if (!_views.IsCurrent(_targets[key].Context))
            {
                _targets.Remove(key);
            }
        }
        foreach (string reference in _exposed.Keys.ToArray())
        {
            if (!_targets.TryGetValue(_exposed[reference], out Targets? context) || !context.Current.ContainsKey(reference))
            {
                _exposed.Remove(reference);
            }
        }
    }

    public void ResetApplication(int processId)
    {
        foreach (Targets context in _targets.Values.Where(target => target.ProcessId == processId).ToArray())
        {
            _views.Invalidate(context.Context.Key);
        }
        Prune();
    }

    public void HideUnseen(IReadOnlySet<string> visibleReferences, int processId)
    {
        foreach (string reference in _exposed.Keys.ToArray())
        {
            if (!visibleReferences.Contains(reference))
            {
                _exposed.Remove(reference);
            }
        }
        foreach (Targets targets in _targets.Values.Where(context => context.ProcessId == processId))
        {
            targets.Context.ForceFull = true;
            _views.InvalidateCursors(targets.Context.Key);
        }
    }

    public void Clear()
    {
        _views.Clear();
        _targets.Clear();
        _exposed.Clear();
    }

    private static CompactViewKey ContextKey(int processId, CompactProjectionOptions options)
    {
        string scope = System.Text.Json.JsonSerializer.Serialize(new
        {
            options.Node,
            ProcessId = processId,
            options.Instance,
            options.Root,
            options.IncludeOffscreen,
            options.Contains,
            options.MaximumCharacters
        });

        return new(scope);
    }

    private static string Stamp(IReadOnlyList<CompactTreeNode> nodes)
    {
        string frame = System.Text.Json.JsonSerializer.Serialize(nodes.Select(node => new
        {
            node.Depth,
            Role = node.Role.Name,
            node.Name,
            node.AutoId,
            node.Value,
            node.Enabled,
            node.Offscreen
        }));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(frame)));
    }
}
