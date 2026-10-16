using KLaniBot.Common;
using KLaniBot.Credentials;

namespace KLaniBot.Backup;

// Trusted maintenance code protects a configured closed file set; live database export and destructive in-place restore stay separate.
public sealed class FileBackupService(BackupLocations locations, CredentialBroker credentials, CredentialUse keyUse,
    TimeProvider? time = null)
{
    private readonly TimeProvider _clock = time ?? TimeProvider.System;

    public bool TryPlan(BackupSourceKind kind, out BackupPlan? plan, out BackupFailure failure)
    {
        return BackupAttempt.Try(() => Plan(kind), out plan, out failure);
    }

    public bool TryCreate(BackupPlan plan, SecretReference key, out BackupReceipt? receipt, out BackupFailure failure)
    {
        return BackupAttempt.Try(() => Create(plan, key), out receipt, out failure);
    }

    public bool TryVerify(Guid id, out BackupReceipt? receipt, out BackupFailure failure)
    {
        return BackupAttempt.Try(() => ReadReceipt(id, false, TimeSpan.Zero), out receipt, out failure);
    }

    public bool TryCheckCurrent(Guid id, TimeSpan maximumAge, out BackupFailure failure)
    {
        return BackupAttempt.Try(() => ReadReceipt(id, true, maximumAge), out BackupReceipt? _, out failure);
    }

    public bool TryRestore(Guid id, string name, out BackupRestoreResult? restored, out BackupFailure failure)
    {
        return BackupAttempt.Try(() => Restore(id, name), out restored, out failure);
    }

    // Throws BackupFault for unsupported scope and IOException for unreadable files.
    private BackupPlan Plan(BackupSourceKind kind)
    {
        FileBackupSource.Require(kind == BackupSourceKind.ClosedFiles, BackupFailure.UnsupportedSource);
        var paths = BackupPaths.Normalize(locations);
        var files = FileBackupSource.Scan(paths.Source);

        return new(Guid.CreateVersion7(), kind, paths.Source, files, FileBackupSource.Fingerprint(files), _clock.GetUtcNow());
    }

    // Throws BackupFault or IOException when a source changes or the encrypted, restore-qualified archive cannot be published.
    private BackupReceipt Create(BackupPlan requested, SecretReference key)
    {
        FileBackupSource.Require(requested.Id.Version == 7, BackupFailure.InvalidPlan);
        var current = Plan(requested.Kind);
        FileBackupSource.Require(BackupPaths.Same(current.Source, requested.Source)
            && current.Fingerprint == requested.Fingerprint, BackupFailure.SourceChanged);
        var paths = BackupPaths.Normalize(locations);
        using var lease = AcquireKey(key);
        using var ownership = LockStore(paths.Archives);
        string archive = ArchivePath(paths.Archives, requested.Id);
        FileBackupSource.Require(!Path.Exists(archive), BackupFailure.DestinationExists);
        var files = new List<BackupFileContent>();
        var payload = new FileBackupPayload(1, requested.Id, current.Source, current.Fingerprint, _clock.GetUtcNow(), false, files);
        try
        {
            foreach (var file in current.Files)
            {
                files.Add(new(file, FileBackupSource.Read(current.Source, file)));
            }
            BackupRestore.Qualify(paths.Restores, payload);
            FileBackupSource.Require(Plan(requested.Kind).Fingerprint == current.Fingerprint, BackupFailure.SourceChanged);
            payload = payload with
            {
                RestoreVerified = true
            };
            Publish(archive, BackupCipher.Encrypt(payload, key, lease.Bytes.Span));

            return ReadReceipt(requested.Id, false, TimeSpan.Zero);
        }
        finally
        {
            BackupPayload.Clear(payload);
        }
    }

    // Throws BackupFault for missing, expired or changed evidence; archive/credential helpers preserve their own failures.
    private BackupReceipt ReadReceipt(Guid id, bool requireCurrent, TimeSpan maximumAge)
    {
        var payload = Read(id);
        try
        {
            BackupDurability.SyncParents(BackupPaths.Normalize(locations).Archives);
            DateTimeOffset now = _clock.GetUtcNow();
            FileBackupSource.Require(payload.CapturedAt <= now, BackupFailure.InvalidArchive);
            if (requireCurrent)
            {
                FileBackupSource.Require(maximumAge > TimeSpan.Zero && now - payload.CapturedAt <= maximumAge, BackupFailure.Expired);
                string current = Plan(BackupSourceKind.ClosedFiles).Fingerprint;
                FileBackupSource.Require(current == payload.Fingerprint, BackupFailure.SourceChanged);
            }

            return new(id, payload.Fingerprint, payload.Files.Count, payload.Files.Sum(entry => entry.File.Bytes),
                payload.CapturedAt, now, payload.RestoreVerified);
        }
        finally
        {
            BackupPayload.Clear(payload);
        }
    }

    // Throws BackupFault or IOException for invalid archives; rethrows Exception after clearing rejected plaintext.
    private FileBackupPayload Read(Guid id)
    {
        var paths = BackupPaths.Normalize(locations);
        string path = ArchivePath(paths.Archives, id);
        BackupPaths.RejectLinks(path);
        // An unknown identifier is no intact archive, not a storage failure.
        FileBackupSource.Require(File.Exists(path), BackupFailure.InvalidArchive);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileBackupSource.Require(stream.Length is > 0 and <= BackupCipher.MaximumArchiveBytes, BackupFailure.InvalidArchive);
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var envelope = BackupCipher.ReadEnvelope(bytes, id);
        using var lease = AcquireKey(envelope.Key);
        var payload = BackupCipher.Decrypt(envelope, lease.Bytes.Span);
        try
        {
            BackupPayload.Validate(payload, id, paths.Source);
            return payload;
        }
        catch (Exception)
        {
            BackupPayload.Clear(payload);
            throw;
        }
    }

    // Throws BackupFault for an unavailable scoped key; no caller can substitute plaintext storage.
    private SecretLease AcquireKey(SecretReference reference)
    {
        FileBackupSource.Require(keyUse.Purpose == CredentialPurpose.BackupEncryption, BackupFailure.MissingKey);
        FileBackupSource.Require(credentials.TryAcquire(reference, keyUse, out var lease, out _), BackupFailure.MissingKey);
        if (lease!.Length != BackupCipher.KeyBytes)
        {
            lease.Dispose();
            throw new BackupFault(BackupFailure.MissingKey);
        }

        return lease;
    }

    // Throws BackupFault or IOException when restore publication cannot safely complete.
    private BackupRestoreResult Restore(Guid id, string name)
    {
        var paths = BackupPaths.Normalize(locations);
        FileBackupSource.Require(BackupPaths.ValidDestination(name), BackupFailure.InvalidScope);
        using var ownership = LockStore(paths.Archives);
        var payload = Read(id);
        try
        {
            return BackupRestore.Create(paths.Restores, name, payload);
        }
        finally
        {
            BackupPayload.Clear(payload);
        }
    }

    // Throws BackupFault for an invalid archive identity.
    private static string ArchivePath(string root, Guid id)
    {
        FileBackupSource.Require(id.Version == 7, BackupFailure.InvalidArchive);

        return Path.Combine(root, id.ToString("D") + ".klbackup");
    }

    // Throws IOException when another process owns the store; the lock file is stable across operations.
    private static FileStream LockStore(string root)
    {
        BackupPaths.RejectLinks(root);
        PrivateFile.CreateDirectory(root);
        string path = Path.Combine(root, "backup.lock");
        BackupPaths.RejectLinks(path);

        return PrivateFile.OpenWrite(path, FileMode.OpenOrCreate);
    }

    // Throws IOException on failed publication; no existing archive is overwritten.
    private static void Publish(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.CreateVersion7() + ".tmp";
        try
        {
            PrivateFile.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, false);
            BackupDurability.SyncParents(Path.GetDirectoryName(path)!);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
