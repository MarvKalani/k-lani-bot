namespace KLaniBot.Transport.CompactUi;

// Every field writer verifies the complete current native value; append, empty or clipped-prefix matches are never successful replacement.
public static class CompactWritePostcondition
{
    public static bool TryCheck(IReadOnlyList<CompactTreeNode> fresh, CompactStoredSelector selector, string expected,
        out CompactCaptureAttempt<string> attempt)
    {
        if (!CompactCapturedOutputs.TryRequired(fresh, selector, CompactOutputField.Value, out var current))
        {
            attempt = new(null, current.Error);
            return false;
        }
        if (current.Value!.Value != expected)
        {
            attempt = new(null, CompactCaptureError.WrittenValueMismatch);
            return false;
        }
        attempt = new(current.Value.Value);

        return true;
    }
}
