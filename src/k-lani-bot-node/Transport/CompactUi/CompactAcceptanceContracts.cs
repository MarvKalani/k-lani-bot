namespace KLaniBot.Transport.CompactUi;

// Private receipts bind acceptance to the authorized node, principal and connection, separately from a model subscription.
public sealed record CompactAcceptanceAuthority(string Node, string Principal, string Connection);

// Only a functional test can accept writers; read-only acceptance is limited to read-only functions.
public enum CompactAcceptanceTest
{
    Functional,
    ReadOnly
}

// Reviewed acceptance runs use a private virtual desktop or private sandbox rather than the operator's physical session.
public enum CompactAcceptanceIsolationKind
{
    PrivateVirtualDesktop,
    PrivateSandbox
}

// The acceptance target is explicitly synthetic and isolated; its node identity must match the authorized receipt issuer.
public sealed record CompactAcceptanceIsolation(CompactAcceptanceIsolationKind Kind, string Node);

// An issuer receives independent oracle and execution digests, never stores training values and never trusts an MCP caller's approved flag.
public sealed record CompactAcceptanceProof(CompactAcceptanceTest Test, CompactAcceptanceIsolation Isolation,
    bool Verified, CompactContractDigest Oracle, CompactContractDigest Execution);

// An opaque receipt reference identifies a private signed file rather than a path, mutable success flag or native input target.
public sealed record CompactAcceptanceReference(Guid Value);

// Revalidation distinguishes forged, foreign, stale and changed-scope evidence before any library mutation.
public enum CompactAcceptanceError
{
    InvalidConfiguration,
    InvalidKey,
    UnknownEvidence,
    UnsafeEvidence,
    InvalidProof,
    ForgedEvidence,
    ForeignOrStaleEvidence,
    ScopeChanged,
    StorageUnavailable,
    CaptureEvidenceMissing,
    StaleRevision,
    UnknownFunction,
    IncompatibleScope,
    AuthorityChanged,
    LibraryFailure
}

// Receipt APIs report finite failed layers and optional existing protocol/OS detail without approving input or a retry.
public sealed record CompactAcceptanceAttempt<T>(T? Value, CompactAcceptanceError? Error = null, string? Detail = null);

// One accepted receipt updates compatibility only; its function body and previously approved selectors remain unchanged.
public sealed record CompactAcceptedCompatibility
{
    public CompactAcceptanceReference Reference
    {
        get;
    }
    public CompactCompatibilityContract Contract
    {
        get;
    }

    internal CompactAcceptedCompatibility(CompactAcceptanceReference reference, CompactCompatibilityContract contract)
    {
        Reference = reference;
        Contract = contract;
    }
}
