using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// A loaded function retains its original boundary document so migration never silently rewrites an approved recipe body or schema.
public sealed record CompactLoadedFunction(CompactFunctionDraft Draft, CompactRecordedCompatibility Compatibility, JsonObject Document);

// A generated extension is one active document for an application/build, independent of its revision counter.
public sealed record CompactFunctionDocument(CompactProgramIdentity Application, int Version, long Revision,
    IReadOnlyList<CompactLoadedFunction> Functions, IReadOnlyList<CompactCaptureCandidate> Candidates, JsonObject Source);

// Durable revisions use the source-byte digest as well as the revision number to detect external edits.
public sealed record CompactFunctionSnapshot(CompactFunctionDocument? Document, long Revision, string? Digest);

// File and definition failures remain typed and do not authorize function execution or recovery overwrites.
public enum CompactFunctionError
{
    InvalidDocument,
    InvalidDefinition,
    DuplicateFunction,
    DuplicateOutput,
    InvalidParameters,
    InvalidAction,
    InvalidSelector,
    InvalidSemanticSelector,
    InvalidContainerPath,
    InvalidWriteParameter,
    InvalidOutput,
    UnknownOutputParameter,
    InvalidCandidate,
    InvalidRevision,
    InvalidPath,
    ByteLimit,
    UnsafeFile,
    StorageUnavailable,
    Locked,
    StaleRevision,
    InvalidJournal,
    ExternalChange,
    CorruptTemporary,
    AlreadyExists
}

// Definition and revision APIs distinguish expected failures from their optional OS or compatibility-layer diagnostic.
public sealed record CompactFunctionAttempt<T>(T? Value, CompactFunctionError? Error = null, string? Detail = null);

// Recovery explicitly distinguishes a clean store from a confirmed commit or retained previous revision.
public enum CompactRevisionRecovery
{
    Clean,
    Committed,
    PreviousRetained
}

// Recovery never reports success without the revision and digest of the actual active file.
public sealed record CompactRevisionRecoveryResult(CompactRevisionRecovery Status, long Revision);

// Private intent records survive interruption around the one atomic active-file replacement.
public sealed record CompactRevisionIntent(string Temporary, string? PreviousDigest, string NextDigest,
    long ExpectedRevision, long NextRevision);

// Regression checkpoints represent actual write boundaries and do not change the production commit protocol.
public enum CompactRevisionCheckpoint
{
    BeforeReplace,
    AfterReplace
}
