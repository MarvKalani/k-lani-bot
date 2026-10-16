using KLaniBot.Credentials;

namespace KLaniBot.Backup;

// A trusted administrator selects distinct data, archive and recovery roots; model arguments cannot choose arbitrary filesystem targets.
public sealed record BackupLocations(string Source, string Archives, string Restores);

// Closed ordinary files are the first qualified adapter; live databases need their own consistent export implementation.
public enum BackupSourceKind
{
    ClosedFiles,
    ApplicationExport
}

// Backup failures remain explicit and prevent callers from treating a partial archive as permission to mutate source data.
public enum BackupFailure
{
    None,
    UnsupportedSource,
    InvalidScope,
    InvalidPlan,
    SourceChanged,
    LimitExceeded,
    MissingKey,
    StoreFailed,
    InvalidArchive,
    DestinationExists,
    Expired
}

// The source scan describes each file without sending its content to a model or copying it into public evidence.
public sealed record BackupFile(string Path, long Bytes, string Sha256);

// The plan fixes the source fingerprint before any archive is created; creation rereads and compares the current source.
public sealed record BackupPlan(Guid Id, BackupSourceKind Kind, string Source, IReadOnlyList<BackupFile> Files,
    string Fingerprint, DateTimeOffset PlannedAt);

// A receipt distinguishes capture time from verification time so rechecking an old archive cannot make it a fresh backup.
public sealed record BackupReceipt(Guid Id, string Fingerprint, int Files, long Bytes, DateTimeOffset CapturedAt,
    DateTimeOffset VerifiedAt, bool RestoreVerified);

// Restoration publishes a new directory only; replacing production data needs a separate authorized restore workflow.
public sealed record BackupRestoreResult(string Directory, int Files, string Fingerprint);

// File bytes exist only inside the private plaintext buffer and its authenticated encrypted archive.
internal sealed record BackupFileContent(BackupFile File, byte[] Content);

// The authenticated payload binds the archive to the source scope and actual restore qualification.
internal sealed record FileBackupPayload(int Format, Guid Id, string Source, string Fingerprint, DateTimeOffset CapturedAt,
    bool RestoreVerified, IReadOnlyList<BackupFileContent> Files);

// The envelope exposes an opaque key reference, never key material or source filenames.
internal sealed record BackupEnvelope(int Format, Guid Id, SecretReference Key, byte[] Nonce, byte[] Tag, byte[] Ciphertext);

// Internal filesystem/codec helpers retain a typed failure while public Try methods convert it to their result contract.
internal sealed class BackupFault(BackupFailure failure) : Exception(failure.ToString())
{
    public BackupFailure Failure { get; } = failure;
}
