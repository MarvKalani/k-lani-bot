using System.Text.Json.Nodes;
using KLaniBot.Cartridges;

namespace KLaniBot.Transport.CompactUi;

// A generated extension replaces one active file with CAS; its private intent and lock remain outside exported application directories.
public sealed class CompactRevisionStore
{
    private readonly CompactProgramIdentity _application;
    private readonly CompactRevisionLock _lock;
    private readonly Action<CompactRevisionCheckpoint>? _checkpoint;
    public string FilePath
    {
        get;
    }
    public string RecoveryRoot
    {
        get;
    }
    public string JournalPath
    {
        get;
    }
    public string LockPath
    {
        get;
    }

    private CompactRevisionStore(string file, string root, CompactProgramIdentity application,
        Action<CompactRevisionCheckpoint>? checkpoint)
    {
        FilePath = file;
        RecoveryRoot = root;
        _application = application;
        _checkpoint = checkpoint;
        string key = CompactRevisionFiles.Digest(System.Text.Encoding.UTF8.GetBytes(file))!;
        JournalPath = Path.Combine(root, key + ".json");
        LockPath = Path.Combine(root, key + ".lock");
        _lock = new(LockPath);
    }

    public static bool TryOpen(string file, string recoveryRoot, CompactProgramIdentity application,
        out CompactFunctionAttempt<CompactRevisionStore> attempt, Action<CompactRevisionCheckpoint>? checkpoint = null)
    {
        attempt = new(null, CompactFunctionError.InvalidPath);
        try
        {
            if (!Path.IsPathFullyQualified(file) || !Path.IsPathFullyQualified(recoveryRoot)
                || Path.GetFileName(file) != "generated-ui.json" || string.IsNullOrWhiteSpace(application.Key)
                || string.IsNullOrWhiteSpace(application.Build) || application.Build.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string full = Path.GetFullPath(file);
            if (!CompactProgramStorage.TryRoots(Path.GetDirectoryName(full)!, recoveryRoot, out var roots))
            {
                return false;
            }
            CartridgeManifest.RejectLinks(full);
            var store = new CompactRevisionStore(full, roots.Value!.Learning!, application, checkpoint);
            if (!store.TryRecover(out var recovered))
            {
                attempt = new(null, recovered.Error, recovered.Detail);
                return false;
            }
            if (!store.TryRead(out var read))
            {
                attempt = new(null, read.Error, read.Detail);
                return false;
            }
            attempt = new(store);

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactFunctionError.StorageUnavailable, exception.Message);
            return false;
        }
    }

    public bool TryRead(out CompactFunctionAttempt<CompactFunctionSnapshot> attempt)
    {
        if (!CompactRevisionFiles.TryRead(FilePath, out var source))
        {
            attempt = new(null, source.Error, source.Detail);
            return false;
        }
        if (source.Value == null)
        {
            attempt = new(new(null, 0, null));
            return true;
        }
        if (!TryParse(source.Value, out var document))
        {
            attempt = new(null, document.Error, document.Detail);
            return false;
        }
        attempt = new(new(document.Value!, document.Value!.Revision, CompactRevisionFiles.Digest(source.Value)));

        return true;
    }

    private bool TryParse(byte[] source, out CompactFunctionAttempt<CompactFunctionDocument> attempt)
    {
        try
        {
            return CompactFunctionDocumentCodec.TryRead(JsonNode.Parse(source), _application, out attempt);
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactFunctionError.InvalidDocument, exception.Message);
            return false;
        }
    }

    public bool TryReplace(JsonObject document, long expectedRevision, string? expectedDigest,
        out CompactFunctionAttempt<CompactFunctionSnapshot> attempt)
    {
        if (!_lock.TryAcquire(out var acquired))
        {
            attempt = new(null, acquired.Error, acquired.Detail);
            return false;
        }
        try
        {
            return TryReplaceLocked(document, expectedRevision, expectedDigest, out attempt);
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactFunctionError.StorageUnavailable, exception.Message);
            return false;
        }
        finally
        {
            _lock.TryRelease(out _);
        }
    }

    private bool TryReplaceLocked(JsonObject document, long expectedRevision, string? expectedDigest,
        out CompactFunctionAttempt<CompactFunctionSnapshot> attempt)
    {
        if (!TryRecoverLocked(out var recovered))
        {
            attempt = new(null, recovered.Error, recovered.Detail);
            return false;
        }
        if (!TryRead(out var earlier))
        {
            attempt = earlier;
            return false;
        }
        if (expectedRevision >= CompactFunctionDocumentCodec.MaximumRevision || earlier.Value!.Revision != expectedRevision
            || earlier.Value.Digest != expectedDigest)
        {
            attempt = new(null, CompactFunctionError.StaleRevision);
            return false;
        }
        JsonObject next = document.DeepClone().AsObject();
        next["revision"] = expectedRevision + 1;
        byte[] source = CompactRevisionFiles.Encode(next);
        if (source.Length > CompactRevisionFiles.MaximumBytes)
        {
            attempt = new(null, CompactFunctionError.ByteLimit);
            return false;
        }
        if (!TryParse(source, out var validated))
        {
            attempt = new(null, validated.Error, validated.Detail);
            return false;
        }
        if (!TryTransaction(earlier.Value, validated.Value!, source, out var written))
        {
            attempt = new(null, written.Error, written.Detail);
            return false;
        }

        return TryRead(out attempt);
    }

    private bool TryTransaction(CompactFunctionSnapshot earlier, CompactFunctionDocument next, byte[] source,
        out CompactFunctionAttempt<CompactRevisionRecoveryResult> attempt)
    {
        try
        {
            attempt = WriteTransaction(earlier, next, source);

            return attempt.Error == null;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactFunctionError.StorageUnavailable, exception.Message);
            return false;
        }
    }

    // Throws IOException or UnauthorizedAccessException for native file failures; regression checkpoints may throw Exception.
    private CompactFunctionAttempt<CompactRevisionRecoveryResult> WriteTransaction(CompactFunctionSnapshot earlier,
        CompactFunctionDocument next, byte[] source)
    {
        string temporary = Path.GetFileName(FilePath) + "." + Guid.NewGuid() + ".tmp";
        string path = Path.Combine(Path.GetDirectoryName(FilePath)!, temporary);
        if (!CompactRevisionFiles.TryWriteNew(path, source, out var staged))
        {
            return new(null, staged.Error, staged.Detail);
        }
        var intent = new CompactRevisionIntent(temporary, earlier.Digest, CompactRevisionFiles.Digest(source)!,
            earlier.Revision, next.Revision);
        if (!CompactRevisionFiles.TryWriteNew(JournalPath, CompactRevisionFiles.Encode(Intent(intent)), out var journal))
        {
            return new(null, journal.Error, journal.Detail);
        }
        if (!CompactRevisionFiles.TrySyncDirectory(RecoveryRoot, out var synced))
        {
            return new(null, synced.Error, synced.Detail);
        }
        _checkpoint?.Invoke(CompactRevisionCheckpoint.BeforeReplace);
        CartridgeManifest.RejectLinks(FilePath);
        File.Move(path, FilePath, true);
        if (!CompactRevisionFiles.TrySyncDirectory(Path.GetDirectoryName(FilePath)!, out var committed))
        {
            return new(null, committed.Error, committed.Detail);
        }
        _checkpoint?.Invoke(CompactRevisionCheckpoint.AfterReplace);

        TryRecoverLocked(out var recovered);

        return recovered;
    }

    public bool TryRecover(out CompactFunctionAttempt<CompactRevisionRecoveryResult> attempt)
    {
        if (!File.Exists(JournalPath))
        {
            attempt = new(new(CompactRevisionRecovery.Clean, 0));
            return true;
        }
        if (!_lock.TryAcquire(out var acquired))
        {
            attempt = new(null, acquired.Error, acquired.Detail);
            return false;
        }
        try
        {
            return TryRecoverLocked(out attempt);
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactFunctionError.StorageUnavailable, exception.Message);
            return false;
        }
        finally
        {
            _lock.TryRelease(out _);
        }
    }

    private bool TryRecoverLocked(out CompactFunctionAttempt<CompactRevisionRecoveryResult> attempt)
    {
        if (!CompactRevisionFiles.TryRead(JournalPath, out var source))
        {
            attempt = new(null, source.Error, source.Detail);
            return false;
        }
        if (source.Value == null)
        {
            attempt = new(new(CompactRevisionRecovery.Clean, 0));
            return true;
        }
        if (!TryIntent(source.Value, out var intent))
        {
            attempt = new(null, CompactFunctionError.InvalidJournal);
            return false;
        }
        if (!TryRead(out var current))
        {
            attempt = new(null, current.Error, current.Detail);
            return false;
        }
        string temporary = Path.Combine(Path.GetDirectoryName(FilePath)!, intent!.Temporary);
        if (current.Value!.Digest == intent.NextDigest && current.Value.Revision == intent.NextRevision)
        {
            return TryClearIntent(temporary, CompactRevisionRecovery.Committed, current.Value.Revision, out attempt);
        }
        if (current.Value.Digest != intent.PreviousDigest || current.Value.Revision != intent.ExpectedRevision)
        {
            attempt = new(null, CompactFunctionError.ExternalChange);
            return false;
        }

        return TryRetainPrevious(temporary, current.Value.Revision, intent, out attempt);
    }

    private bool TryRetainPrevious(string temporary, long revision, CompactRevisionIntent intent,
        out CompactFunctionAttempt<CompactRevisionRecoveryResult> attempt)
    {
        if (!CompactRevisionFiles.TryRead(temporary, out var read) || read.Value == null
            || CompactRevisionFiles.Digest(read.Value) != intent.NextDigest || !TryParse(read.Value, out _))
        {
            attempt = new(null, CompactFunctionError.CorruptTemporary);
            return false;
        }

        return TryClearIntent(temporary, CompactRevisionRecovery.PreviousRetained, revision, out attempt);
    }

    private bool TryClearIntent(string temporary, CompactRevisionRecovery result, long revision,
        out CompactFunctionAttempt<CompactRevisionRecoveryResult> attempt)
    {
        try
        {
            attempt = ClearIntent(temporary, result, revision);

            return attempt.Error == null;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactFunctionError.StorageUnavailable, exception.Message);
            return false;
        }
    }

    // Throws IOException or UnauthorizedAccessException when removing private transaction files.
    private CompactFunctionAttempt<CompactRevisionRecoveryResult> ClearIntent(string temporary,
        CompactRevisionRecovery result, long revision)
    {
        CartridgeManifest.RejectLinks(temporary);
        File.Delete(temporary);
        File.Delete(JournalPath);
        if (!CompactRevisionFiles.TrySyncDirectory(RecoveryRoot, out var root))
        {
            return new(null, root.Error, root.Detail);
        }
        if (!CompactRevisionFiles.TrySyncDirectory(Path.GetDirectoryName(FilePath)!, out var directory))
        {
            return new(null, directory.Error, directory.Detail);
        }
        return new(new(result, revision));
    }

    private static JsonObject Intent(CompactRevisionIntent intent)
    {
        return new()
        {
            ["format"] = "k-lani-bot-cartridge-replace/1",
            ["temporary"] = intent.Temporary,
            ["previous_digest"] = intent.PreviousDigest,
            ["next_digest"] = intent.NextDigest,
            ["expected_revision"] = intent.ExpectedRevision,
            ["next_revision"] = intent.NextRevision
        };
    }

    private static bool TryIntent(byte[] source, out CompactRevisionIntent? intent)
    {
        intent = null;
        try
        {
            if (JsonNode.Parse(source) is not JsonObject record || !CompactDiscoveryDocumentCodec.Only(record, "format", "temporary",
                    "previous_digest", "next_digest", "expected_revision", "next_revision")
                || !CompactDiscoveryDocumentCodec.Text(record, "format", out string format) || format != "k-lani-bot-cartridge-replace/1"
                || !CompactDiscoveryDocumentCodec.Text(record, "temporary", out string temporary)
                || Path.GetFileName(temporary) != temporary || !temporary.StartsWith("generated-ui.json.", StringComparison.Ordinal)
                || !temporary.EndsWith(".tmp", StringComparison.Ordinal)
                || !CompactDiscoveryDocumentCodec.Text(record, "next_digest", out string digest)
                || !CompactContractHash.TryDigest(digest, out _)
                || !CompactFunctionDocumentCodec.TryInteger(record["next_revision"], out long next)
                || record["expected_revision"] is not JsonValue expected || !expected.TryGetValue<long>(out long previous)
                || previous < 0 || next != previous + 1)
            {
                return false;
            }
            string? previousDigest = record["previous_digest"]?.GetValue<string>();
            if (previousDigest != null && !CompactContractHash.TryDigest(previousDigest, out _))
            {
                return false;
            }
            intent = new(temporary, previousDigest, digest, previous, next);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
