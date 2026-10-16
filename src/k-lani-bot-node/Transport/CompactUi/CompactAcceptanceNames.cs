using System.Diagnostics;

namespace KLaniBot.Transport.CompactUi;

// Receipt failure names remain compatible with the private JS acceptance protocol.
public static class CompactAcceptanceNames
{
    public static string Error(CompactAcceptanceError error, string? detail = null)
    {
        return error switch
        {
            CompactAcceptanceError.InvalidConfiguration => "Configure an authorized private acceptance evidence store.",
            CompactAcceptanceError.InvalidKey => "Invalid receipt signing key.",
            CompactAcceptanceError.UnknownEvidence => "unknown_acceptance_evidence",
            CompactAcceptanceError.UnsafeEvidence => "Acceptance evidence must be private, bounded and nonsymbolic.",
            CompactAcceptanceError.InvalidProof => "Matching isolated functional acceptance is required.",
            CompactAcceptanceError.ForgedEvidence => "forged_acceptance_evidence",
            CompactAcceptanceError.ForeignOrStaleEvidence => "foreign_or_stale_acceptance_evidence",
            CompactAcceptanceError.ScopeChanged => "acceptance_scope_changed: " + detail,
            CompactAcceptanceError.StorageUnavailable => detail ?? "Private acceptance storage is unavailable.",
            CompactAcceptanceError.CaptureEvidenceMissing =>
                CompactCompatibilityNames.Error(CompactCompatibilityError.CaptureEvidenceMissing),
            CompactAcceptanceError.StaleRevision => "stale_cartridge_revision",
            CompactAcceptanceError.UnknownFunction => "Unknown generated function. Browse the selected topic first.",
            CompactAcceptanceError.IncompatibleScope => "Incompatible function scope cannot be widened by revalidation.",
            CompactAcceptanceError.AuthorityChanged => "Acceptance target node authority changed.",
            CompactAcceptanceError.LibraryFailure => detail ?? "Generated function revalidation could not be saved.",
            _ => throw new UnreachableException()
        };
    }
}
