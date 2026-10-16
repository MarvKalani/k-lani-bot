namespace KLaniBot.Transport.CompactUi;

// Output readers use a fresh tree and exact semantic identity; absent, ambiguous or changed-type targets remain unverified.
public static class CompactCapturedOutputs
{
    public static bool TryRead(IReadOnlyList<CompactTreeNode> nodes, CompactStoredSelector selector, CompactOutputField field,
        out CompactCaptureAttempt<CompactOptionalOutput> attempt)
    {
        if (!CompactTreeScope.TryWindowScope(nodes, out var scope))
        {
            attempt = new(null, CompactCaptureError.WindowAmbiguous);
            return false;
        }
        if (!CompactTargetResolver.TryCandidates(scope.Value!, selector.Target(0), true, out var candidates))
        {
            attempt = new(null, CompactCaptureError.TargetTypeChanged);
            return false;
        }
        if (candidates.Value!.Count > 1)
        {
            attempt = new(null, CompactCaptureError.OutputAmbiguous);
            return false;
        }
        CompactTreeNode? node = candidates.Value.SingleOrDefault();
        string? value = node == null ? null : field == CompactOutputField.Name ? node.Name : node.Value;
        attempt = new(new(node != null, value));

        return true;
    }

    public static bool TryRequired(IReadOnlyList<CompactTreeNode> nodes, CompactStoredSelector selector, CompactOutputField field,
        out CompactCaptureAttempt<CompactOptionalOutput> attempt)
    {
        if (!TryRead(nodes, selector, field, out attempt))
        {
            return false;
        }
        if (!attempt.Value!.Exists)
        {
            attempt = new(null, CompactCaptureError.OutputAbsent);
            return false;
        }

        return true;
    }
}
