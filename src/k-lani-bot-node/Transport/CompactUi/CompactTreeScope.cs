namespace KLaniBot.Transport.CompactUi;

// Browser/document compaction prioritizes a visible modal and refuses to silently choose between multiple application roots.
public static class CompactTreeScope
{
    public static bool TryApplicationBranch(IReadOnlyList<CompactTreeNode> nodes, string? rootName,
        out CompactTreeAttempt<IReadOnlyList<CompactTreeNode>> attempt)
    {
        CompactTreeNode[] dialogs = nodes.Where(node => node.Role.Family == CompactRoleFamily.Dialog && !node.Offscreen).ToArray();
        if (dialogs.Length > 0)
        {
            CompactTreeNode[] visible = nodes.Where(node => dialogs.Any(dialog => ReferenceEquals(dialog, node)
                || Contains(nodes, dialog, node))).ToArray();
            attempt = new(visible, null);
            return true;
        }
        CompactTreeNode[] documents = nodes.Where(node => node.Role.Name == "document web" && node.Name.Length > 0).ToArray();
        CompactTreeNode[] roots = documents.Where(node => !documents.Any(parent => !ReferenceEquals(parent, node)
            && parent.Depth < node.Depth && Contains(nodes, parent, node))).ToArray();
        CompactTreeNode[] candidates = string.IsNullOrEmpty(rootName) ? roots : roots.Where(node => node.Name == rootName).ToArray();
        if (candidates.Length > 1 || !string.IsNullOrEmpty(rootName) && candidates.Length != 1)
        {
            attempt = new(null, CompactTreeError.RootAmbiguousOrAbsent, string.Join(", ", roots.Select(node => node.Name)));
            return false;
        }
        attempt = new(candidates.Length == 0 ? nodes
            : nodes.Where(node => ReferenceEquals(node, candidates[0]) || Contains(nodes, candidates[0], node)).ToArray(), null);

        return true;
    }

    public static bool Contains(IReadOnlyList<CompactTreeNode> nodes, CompactTreeNode parent, CompactTreeNode candidate)
    {
        int start = IndexOf(nodes, parent);
        int target = IndexOf(nodes, candidate);
        if (start < 0 || target <= start)
        {
            return false;
        }
        for (int index = start + 1; index <= target; index++)
        {
            if (nodes[index].Depth <= parent.Depth)
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOf(IReadOnlyList<CompactTreeNode> nodes, CompactTreeNode node)
    {
        for (int index = 0; index < nodes.Count; index++)
        {
            if (ReferenceEquals(nodes[index], node))
            {
                return index;
            }
        }

        return -1;
    }

    // A tree observed for one native window has one outermost window row; rows nested under it, such as a UWP frame's inner
    // CoreWindow with the same caption, belong to that window. A tree without any window row is used whole.
    public static bool TryWindowScope(IReadOnlyList<CompactTreeNode> nodes, out CompactTreeAttempt<IReadOnlyList<CompactTreeNode>> attempt)
    {
        CompactTreeNode[] windows = nodes.Where(node => IsWindow(node.Role)).ToArray();
        if (windows.Length == 0)
        {
            attempt = new(nodes, null);
            return true;
        }
        int outermostDepth = windows.Min(node => node.Depth);
        CompactTreeNode[] outermost = windows.Where(node => node.Depth == outermostDepth).ToArray();
        if (outermost.Length != 1)
        {
            attempt = new(null, CompactTreeError.WindowIdentityChanged);
            return false;
        }
        CompactTreeNode owner = outermost[0];
        attempt = new(nodes.Where(node => ReferenceEquals(node, owner) || Contains(nodes, owner, node)).ToArray(), null);

        return true;
    }

    public static bool IsWindow(CompactRole role)
    {
        return role.Family is CompactRoleFamily.Window or CompactRoleFamily.Dialog;
    }
}
