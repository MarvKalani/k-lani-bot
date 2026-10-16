namespace KLaniBot.Transport.CompactUi;

// Captured selectors strip volatile node/window authority and retain only the observed semantic path needed for fresh resolution.
public static class CompactCapturedSelectors
{
    public static bool TryCreate(CompactSemanticTarget? target, int processId, out CompactCaptureAttempt<CompactStoredSelector> attempt)
    {
        attempt = new(null, CompactCaptureError.OwningWindowMissing);
        if (target == null || target.WindowKind is not CompactWindowKind windowKind)
        {
            return false;
        }
        if (target.ProcessId != processId)
        {
            attempt = new(null, CompactCaptureError.ForeignReference);
            return false;
        }
        if (target.UnverifiedWindow || target.Ambiguous || target.ContainerPath?.Count > CompactCaptureCodec.MaximumSteps)
        {
            attempt = new(null, CompactCaptureError.InvalidSelector);
            return false;
        }
        attempt = new(new(target.Role, target.Name, target.AutoId, windowKind, target.ContainerPath));

        return true;
    }

    public static bool Matches(CompactStoredSelector selector, CompactSemanticTarget target,
        CompactRecipeAction action, CompactRecipeAction recordedAction)
    {
        return selector != null && target != null && action == recordedAction && selector.Role.Name == target.Role.Name
            && selector.WindowKind == target.WindowKind && (target.AutoId.Length > 0 ? selector.AutoId == target.AutoId
                : string.IsNullOrEmpty(selector.AutoId) && selector.Name == target.Name)
            && (selector.ContainerPath ?? []).SequenceEqual(target.ContainerPath ?? []);
    }
}
