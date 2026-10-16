namespace KLaniBot.Transport.CompactUi;

// Every action resolves the observed target again in its exact window/type/container scope; it never reuses stored coordinates or IDs.
public static class CompactTargetResolver
{
    // Input dispatch uses this check, so a unique disabled target is refused before any mutation is sent.
    public static bool TryResolve(IReadOnlyList<CompactTreeNode> nodes, CompactSemanticTarget target, bool stableIdentity,
        out CompactTreeAttempt<int> attempt)
    {
        if (!TryFindUnique(nodes, target, stableIdentity, out var found))
        {
            attempt = new(default, found.Error);
            return false;
        }
        CompactTreeNode current = found.Value!;
        attempt = current.Enabled ? new(current.Id, null) : new(default, CompactTreeError.TargetDisabled);

        return current.Enabled;
    }

    // Preflight uses this presence check; the caller decides whether a disabled match may stay in the recipe.
    public static bool TryFindUnique(IReadOnlyList<CompactTreeNode> nodes, CompactSemanticTarget target, bool stableIdentity,
        out CompactTreeAttempt<CompactTreeNode> attempt)
    {
        if (!CompactTreeScope.TryWindowScope(nodes, out var scope))
        {
            attempt = new(null, scope.Error);
            return false;
        }
        if (!TryCandidates(scope.Value!, target, stableIdentity, out var candidates))
        {
            attempt = new(null, candidates.Error);
            return false;
        }
        if (candidates.Value!.Count != 1)
        {
            attempt = new(null, CompactTreeError.TargetAbsentOrAmbiguous);
            return false;
        }
        attempt = new(candidates.Value[0], null);

        return true;
    }

    public static bool TryCandidates(IReadOnlyList<CompactTreeNode> nodes, CompactSemanticTarget target, bool stableIdentity,
        out CompactTreeAttempt<IReadOnlyList<CompactTreeNode>> attempt)
    {
        CompactTreeNode[] identified = nodes.Where(node => (target.AutoId.Length == 0 || node.AutoId == target.AutoId)
            && ContainersMatch(nodes, node, target.ContainerPath)).ToArray();
        if (stableIdentity && target.AutoId.Length > 0 && identified.Length > 0
            && !identified.Any(node => node.Role.Name == target.Role.Name))
        {
            attempt = new(null, CompactTreeError.TargetTypeChanged);
            return false;
        }
        CompactTreeNode[] matched = identified.Where(node => node.Role.Name == target.Role.Name
            && CaptionMatches(node, target, stableIdentity)).ToArray();
        attempt = new(matched, null);

        return true;
    }

    private static bool CaptionMatches(CompactTreeNode node, CompactSemanticTarget target, bool stableIdentity)
    {
        if (!string.IsNullOrEmpty(target.NamePrefix))
        {
            return node.Name.StartsWith(target.NamePrefix, StringComparison.Ordinal);
        }
        if ((stableIdentity || target.ContainerPath != null) && target.AutoId.Length > 0)
        {
            return true;
        }

        return node.Name == target.Name;
    }

    private static bool ContainersMatch(IReadOnlyList<CompactTreeNode> nodes, CompactTreeNode node,
        IReadOnlyList<CompactContainerSelector>? expected)
    {
        if (expected == null)
        {
            return true;
        }
        CompactSemanticTarget observed = CompactSemanticIdentity.Target(0, nodes, node, null);

        return observed.ContainerPath!.SequenceEqual(expected);
    }
}
