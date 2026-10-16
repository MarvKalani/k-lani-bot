using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Window-relative semantic identities stabilize references through native ID/caption changes while retaining current lifetime evidence.
public static class CompactSemanticIdentity
{
    // The observed window is the one the tree was read for, so the caption never has to be matched to find the owner.
    public static CompactSemanticTarget Target(int processId, IReadOnlyList<CompactTreeNode> nodes, CompactTreeNode node,
        CompactNativeWindow? observed)
    {
        CompactTreeNode[] parents = nodes.Where(parent => CompactTreeScope.Contains(nodes, parent, node)).ToArray();
        CompactTreeNode? window = parents.FirstOrDefault(parent => CompactTreeScope.IsWindow(parent.Role))
            ?? (CompactTreeScope.IsWindow(node.Role) ? node : null);
        int windowIndex = Array.IndexOf(parents, window);
        CompactTreeNode[] containers = ReferenceEquals(window, node) ? [] : windowIndex >= 0 ? parents.Skip(windowIndex + 1).ToArray()
            : parents.Where(parent => parent.Role.Family != CompactRoleFamily.DesktopRoot).ToArray();
        CompactContainerSelector[] path = containers.Select(parent => new CompactContainerSelector(parent.Role,
            parent.AutoId.Length > 0 ? parent.AutoId : null, parent.AutoId.Length > 0 ? null : parent.Name)).ToArray();

        return new(processId, node.Name, node.Role, node.AutoId, observed?.Kind, path, observed?.Handle, observed?.Started,
            observed == null);
    }

    public static string StableKey(CompactSemanticTarget target)
    {
        var identity = new JsonObject { ["role"] = target.Role.Name };
        if (target.AutoId.Length > 0)
        {
            identity["autoId"] = target.AutoId;
        }
        else if (target.WindowHandle == null || !CompactTreeScope.IsWindow(target.Role))
        {
            identity["name"] = target.Name;
        }
        var path = new JsonArray();
        foreach (CompactContainerSelector container in target.ContainerPath ?? [])
        {
            var part = new JsonObject { ["role"] = container.Role.Name };
            part[container.AutoId != null ? "autoId" : "name"] = container.AutoId ?? container.Name;
            path.Add(part);
        }
        identity["containerPath"] = path;
        if (target.WindowHandle == null)
        {
            if (target.WindowKind is CompactWindowKind kind)
            {
                identity["window"] = CompactWindowKindWords.Text(kind);
            }
        }
        else
        {
            identity["windowHandle"] = target.WindowHandle.Value;
        }

        return CompactJsonText.Serialize(identity);
    }
}
