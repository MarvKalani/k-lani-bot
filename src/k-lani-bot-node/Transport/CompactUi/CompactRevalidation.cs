namespace KLaniBot.Transport.CompactUi;

// One signed receipt updates only a function's compatibility, atomically advancing the active cartridge revision.
public sealed record CompactRevalidationResult(string Function, long Revision, CompactAcceptanceReference Evidence);

// Revalidation reads existing signed private evidence; it does not execute input, issue receipts or widen an incompatible function.
public sealed class CompactRevalidation(CompactFunctionLibrary library, CompactAcceptanceStore evidence)
{
    public bool TryAccept(string function, long expectedRevision, CompactAcceptanceReference reference,
        string observedNode, CompactObservedCompatibility current, out CompactAcceptanceAttempt<CompactRevalidationResult> attempt)
    {
        if (!library.TryReload(out var loaded))
        {
            attempt = new(null, CompactAcceptanceError.LibraryFailure, CompactFunctionNames.Error(loaded.Error!.Value, loaded.Detail));
            return false;
        }
        if (library.Revision != expectedRevision)
        {
            attempt = new(null, CompactAcceptanceError.StaleRevision);
            return false;
        }
        if (!library.TryGet(function, out var selected))
        {
            attempt = new(null, CompactAcceptanceError.UnknownFunction);
            return false;
        }
        if (observedNode != evidence.Authority.Node)
        {
            attempt = new(null, CompactAcceptanceError.AuthorityChanged);
            return false;
        }
        if (CompactCompatibility.Assess(selected.Value!.Draft, selected.Value.Compatibility, current).Status
            == CompactCompatibilityStatus.Incompatible)
        {
            attempt = new(null, CompactAcceptanceError.IncompatibleScope);
            return false;
        }

        return TrySave(function, expectedRevision, reference, selected.Value, current, out attempt);
    }

    private bool TrySave(string function, long expectedRevision, CompactAcceptanceReference reference, CompactLoadedFunction selected,
        CompactObservedCompatibility current, out CompactAcceptanceAttempt<CompactRevalidationResult> attempt)
    {
        if (!evidence.TryRead(reference, selected, current, out var accepted))
        {
            attempt = new(null, accepted.Error, accepted.Detail);
            return false;
        }
        if (!library.TryReplaceCompatibility(function, expectedRevision, accepted.Value!, out string? error))
        {
            attempt = new(null, error == "stale_cartridge_revision" ? CompactAcceptanceError.StaleRevision
                : CompactAcceptanceError.LibraryFailure, error);
            return false;
        }
        attempt = new(new(function, library.Revision, reference));

        return true;
    }
}
