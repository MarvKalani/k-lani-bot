using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// A read-only form scan separates structural discovery from live text and never sends an application input.
public static class CompactFormScan
{
    private static readonly Regex TextRoles = new("heading|static|text|label|status|output",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The tree was read for the observed window, so its outermost window row is the form's scope and every field takes that kind.
    public static bool TryObserve(IReadOnlyList<CompactTreeNode> nodes, CompactScanOptions options, CompactNativeWindow observed,
        out CompactDiscoveryAttempt<CompactScanObservation> attempt)
    {
        if (!CompactTreeScope.TryWindowScope(nodes, out var window))
        {
            attempt = new(null, CompactDiscoveryError.WindowAbsentOrAmbiguous);
            return false;
        }
        IReadOnlyList<CompactTreeNode> scoped = window.Value!;
        if (!CompactTreeScope.TryApplicationBranch(scoped, options.Root, out var branch))
        {
            attempt = new(null, CompactDiscoveryError.RootAbsentOrAmbiguous, branch.Detail);
            return false;
        }
        if (branch.Value!.Count == 0)
        {
            attempt = new(null, CompactDiscoveryError.ScopeAbsent);
            return false;
        }
        CompactTreeNode[] controls = branch.Value.Where(node => CompactDiscoveryFields.TryKind(node.Role, out _)).ToArray();
        if (controls.Length > CompactFormRuntime.MaximumFields)
        {
            attempt = new(null, CompactDiscoveryError.ControlBoundExceeded);
            return false;
        }
        CompactObservedField[] fields = controls.Select(node => CompactDiscoveryFields.Create(nodes, node, observed.Kind)).ToArray();
        CompactScanContents? contents = options.Contents || options.Result.Mode == CompactResultMode.Values
            ? ReadContents(branch.Value, controls, fields,
                options.Result.Mode == CompactResultMode.Legacy ? options.MaximumCharacters : int.MaxValue)
            : null;
        attempt = new(new(options.Form, observed, CompactDiscoveryFields.State(fields), fields, contents));

        return true;
    }

    private static CompactScanContents ReadContents(IReadOnlyList<CompactTreeNode> selected, CompactTreeNode[] controls,
        IReadOnlyList<CompactObservedField> descriptors, int maximum)
    {
        CompactScanContent[] fields = controls.Select((node, index) => new CompactScanContent(descriptors[index].Key,
            CompactDiscoveryFields.Clip(node.Name, CompactDiscoveryFields.MaximumNameCharacters),
            node.Value == null ? null : CompactDiscoveryFields.Clip(node.Value, maximum),
            node.Value?.Length > maximum)).ToArray();
        CompactTreeNode[] texts = selected.Where(node => !controls.Contains(node) && TextRoles.IsMatch(node.Role.Name)
            && node.Name.Length > 0).ToArray();

        return new(fields, texts.Take(CompactFormRuntime.MaximumFields)
            .Select(node => CompactDiscoveryFields.Clip(node.Name, maximum)).ToArray(),
            texts.Length > CompactFormRuntime.MaximumFields);
    }
}
