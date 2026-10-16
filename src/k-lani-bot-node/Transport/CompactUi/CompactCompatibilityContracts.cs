using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Compatibility proofs use validated SHA-256 digests, never names or caller-asserted success flags as fingerprints.
public sealed record CompactContractDigest(string Hex);

// Wire editions preserve the observed boundary value even when a newer, untested edition must be refused.
public enum CompactWireEdition
{
    November2025,
    June2025,
    March2025,
    November2024,
    Other
}

// The controller records the actual negotiated wire edition independently from the node product version.
public sealed record CompactMcpWireVersion(CompactWireEdition Edition, string Value);

// Accessibility functions record whether their actual route additionally relies on guarded keyboard input.
public enum CompactCompatibilityAccess
{
    Accessibility,
    AccessibilityWithKeyboard
}

// Existing v1 records remain explicitly distinct from fingerprint-based contracts.
public enum CompactCompatibilityFormat
{
    Legacy,
    Contracts2
}

// Native descriptors retain arbitrary extension names at the protocol boundary; only known required actions affect a recipe.
public sealed record CompactActionSchemas(IReadOnlyDictionary<Tool, CompactContractDigest> Known,
    IReadOnlyDictionary<string, CompactContractDigest> Extensions);

// Modern function compatibility binds native and gateway implementations, selected action schemas and exact semantic scope.
public sealed record CompactCompatibilityContract(string NodeVersion, CompactMcpWireVersion Wire, string Platform,
    CompactProgramIdentity Application, CompactCompatibilityAccess Access, CompactActionSchemas Actions,
    CompactContractDigest Native, CompactContractDigest Gateway, CompactContractDigest SelectorScope,
    CompactContractDigest FunctionScope, Guid? Acceptance = null);

// Loaders select one explicit format and never infer modern evidence from a legacy node version string.
public sealed record CompactRecordedCompatibility(CompactCompatibilityFormat Format,
    CompactScanCompatibility? Legacy = null, CompactCompatibilityContract? Contracts = null);

// Missing observation fields stay unknown, independently of node access and model-client login.
public sealed record CompactObservedCompatibility(string? NodeVersion, CompactMcpWireVersion? Wire, string? Platform,
    CompactProgramIdentity? Application, CompactActionSchemas? Actions, CompactContractDigest? Native, CompactContractDigest? Gateway);

// Assessment is finite and does not authorize input until the result is reusable.
public enum CompactCompatibilityStatus
{
    Reusable,
    NeedsRevalidation,
    Incompatible
}

// Named reasons identify the changed contract layer without widening a function's application or selector scope.
public enum CompactCompatibilityReason
{
    IdentityUnknown,
    ApplicationScopeChanged,
    FunctionScopeChanged,
    WireVersionUnknown,
    WireVersionChanged,
    ActionContractsUnknown,
    RequiredActionAbsent,
    RequiredActionSchemaChanged,
    ImplementationUnknown,
    ImplementationChanged,
    VerifiedContractUnchanged,
    LegacyApplicationScopeChanged,
    LegacyContractEvidenceMissing,
    LegacyExactVersionOnly,
    TestedNativeApiRangeMissing,
    NativeApiVersionUnknown,
    NativeApiBuildOutsideTestedRange,
    RequiredNativeApiProbeMissing,
    TestedNativeApiRangeAndProbes,
    InvalidRecordedContract
}

// One assessment may identify a required action or application API capability, without exposing an entire tool inventory.
public sealed record CompactCompatibilityVerdict(CompactCompatibilityStatus Status, CompactCompatibilityReason Reason,
    Tool? Action = null, string? Capability = null);

// Invalid compatibility input is rejected before any capture, installation, revalidation or native input.
public enum CompactCompatibilityError
{
    CaptureEvidenceMissing,
    UnsupportedContract,
    InvalidApplication,
    InvalidFingerprints,
    InvalidAcceptance,
    MissingDescriptors,
    AmbiguousDescriptors
}

// Compatibility readers and capture functions return typed failures for ordinary incomplete evidence.
public sealed record CompactCompatibilityAttempt<T>(T? Value, CompactCompatibilityError? Error = null);
