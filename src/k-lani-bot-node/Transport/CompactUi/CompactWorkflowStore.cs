using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Common;

namespace KLaniBot.Transport.CompactUi;

// Private workflow state deduplicates job creation only; native mutation deduplication remains in the operated node's existing ledger.
public sealed class CompactWorkflowStore
{
    public const int MaximumJobs = 100;
    public const int MaximumJobBytes = 1024 * 1024;
    private readonly ICompactWorkflowFunctions _functions;
    private readonly TimeProvider _time;
    private readonly Dictionary<CompactWorkflowJobReference, SemaphoreSlim> _queues = new();
    private readonly object _queueGate = new();
    public string Root
    {
        get;
    }
    public CompactContractDigest Authority
    {
        get;
    }

    private CompactWorkflowStore(string root, CompactContractDigest authority, ICompactWorkflowFunctions functions, TimeProvider? time)
    {
        Root = root;
        Authority = authority;
        _functions = functions;
        _time = time ?? TimeProvider.System;
    }

    public static bool TryOpen(string root, string exports, CompactAcceptanceAuthority authority, ICompactWorkflowFunctions functions,
        out CompactWorkflowJobAttempt<CompactWorkflowStore> attempt, TimeProvider? time = null)
    {
        attempt = new(null, CompactWorkflowJobError.InvalidConfiguration);
        try
        {
            if (authority == null || !ValidAuthority(authority.Node) || !ValidAuthority(authority.Principal)
                || !ValidAuthority(authority.Connection) || !CompactProgramStorage.TryRoots(exports, root, out var roots))
            {
                return false;
            }
            string directory = roots.Value!.Learning!;
            PrivateFile.CreateDirectory(directory);
            attempt = new(new(directory, CompactWorkflowJobWire.Authority(authority), functions, time));

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactWorkflowJobError.StorageUnavailable, exception.Message);
            return false;
        }
    }

    private static bool ValidAuthority(string? value)
    {
        return value != null && value.Length is >= 1 and <= 256;
    }

    public bool TryCreate(string requestId, CompactContractDigest fingerprint, CompactWorkflowDefinition definition,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, out CompactWorkflowJobAttempt<CompactWorkflowJob> attempt)
    {
        attempt = new(null, CompactWorkflowJobError.InvalidRequestId);
        if (requestId == null || requestId.Length is < 1 or > 128)
        {
            return false;
        }
        using var owner = new CompactWorkflowJobLock(Path.Combine(Root, ".create.lock"), Authority);
        if (!owner.TryAcquire(out var acquired))
        {
            attempt = new(null, acquired.Error, acquired.Detail);
            return false;
        }

        return TryCreateLocked(requestId, fingerprint, definition, parameters, out attempt);
    }

    private bool TryCreateLocked(string requestId, CompactContractDigest fingerprint, CompactWorkflowDefinition definition,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, out CompactWorkflowJobAttempt<CompactWorkflowJob> attempt)
    {
        var reference = new CompactWorkflowJobReference(Hash(Authority.Hex + "/" + requestId));
        if (File.Exists(Filename(reference)))
        {
            if (!TryRead(reference, out var existing))
            {
                attempt = new(null, existing.Error, existing.Detail);
                return false;
            }
            attempt = existing.Value!.Job.RequestFingerprint == fingerprint ? new(existing.Value.Job)
                : new(null, CompactWorkflowJobError.RequestConflict);
            return attempt.Error == null;
        }
        if (!TryCapacity(out var capacity))
        {
            attempt = new(null, capacity.Error, capacity.Detail);
            return false;
        }
        string now = Timestamp();
        var job = new CompactWorkflowJob(reference, Authority, fingerprint, definition, parameters, 1, CompactWorkflowStatus.Running,
            [], new Dictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>>(), null,
            new Dictionary<string, CompactWorkflowInstance>(), now, now);
        if (!TryWriteInitial(job, out var saved))
        {
            attempt = new(null, saved.Error, saved.Detail);
            return false;
        }
        attempt = new(saved.Value!.Job);

        return true;
    }

    private bool TryCapacity(out CompactWorkflowJobAttempt<int> attempt)
    {
        try
        {
            int count = Directory.GetFiles(Root, "*.json").Count(file =>
                CompactContractHash.TryDigest(System.IO.Path.GetFileNameWithoutExtension(file), out _));
            attempt = count >= MaximumJobs ? new(0, CompactWorkflowJobError.Full) : new(count);

            return attempt.Error == null;
        }
        catch (Exception exception)
        {
            attempt = new(0, CompactWorkflowJobError.StorageUnavailable, exception.Message);
            return false;
        }
    }

    public bool TryRead(CompactWorkflowJobReference reference, out CompactWorkflowJobAttempt<CompactWorkflowJobSnapshot> attempt)
    {
        if (!CompactContractHash.TryDigest(reference.Digest.Hex, out _) || !CompactRevisionFiles.TryRead(Filename(reference), out var read)
            || read.Value == null || read.Value.Length > MaximumJobBytes)
        {
            attempt = new(null, CompactWorkflowJobError.UnknownJob);
            return false;
        }
        try
        {
            if (!CompactWorkflowJobCodec.TryRead(JsonNode.Parse(read.Value), reference, Authority, _functions, out var job))
            {
                attempt = new(null, job.Error, job.Detail);
                return false;
            }
            attempt = new(new(job.Value!, CompactRevisionFiles.Digest(read.Value)!));

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactWorkflowJobError.ForeignOrInvalidJob, exception.Message);
            return false;
        }
    }

    public bool TryWrite(CompactWorkflowJobSnapshot previous, CompactWorkflowJob next,
        out CompactWorkflowJobAttempt<CompactWorkflowJobSnapshot> attempt)
    {
        if (next.Authority != Authority || next.Reference != previous.Job.Reference)
        {
            attempt = new(null, CompactWorkflowJobError.ForeignJob);
            return false;
        }
        using var owner = new CompactWorkflowJobLock(Filename(previous.Job.Reference) + ".write.lock", Authority);
        if (!owner.TryAcquire(out var acquired))
        {
            attempt = new(null, acquired.Error, acquired.Detail);
            return false;
        }
        return TryWriteLocked(previous, next, out attempt);
    }

    private bool TryWriteLocked(CompactWorkflowJobSnapshot previous, CompactWorkflowJob next,
        out CompactWorkflowJobAttempt<CompactWorkflowJobSnapshot> attempt)
    {
        if (!TryRead(previous.Job.Reference, out var current))
        {
            attempt = current;
            return false;
        }
        if (current.Value!.Job.Revision != previous.Job.Revision || current.Value.Digest != previous.Digest
            || previous.Job.Revision >= CompactFunctionDocumentCodec.MaximumRevision)
        {
            attempt = new(null, CompactWorkflowJobError.StaleRevision);
            return false;
        }
        var updated = next with
        {
            Revision = previous.Job.Revision + 1,
            UpdatedAt = Timestamp()
        };

        return TrySave(updated, false, out attempt);
    }

    private bool TryWriteInitial(CompactWorkflowJob job, out CompactWorkflowJobAttempt<CompactWorkflowJobSnapshot> attempt)
    {
        return TrySave(job, true, out attempt);
    }

    private bool TrySave(CompactWorkflowJob job, bool initial, out CompactWorkflowJobAttempt<CompactWorkflowJobSnapshot> attempt)
    {
        attempt = new(null, CompactWorkflowJobError.StorageUnavailable);
        try
        {
            JsonObject document = CompactWorkflowJobWire.Job(job);
            byte[] source = Encoding.UTF8.GetBytes(CompactJsonText.Serialize(document));
            if (source.Length > MaximumJobBytes)
            {
                attempt = new(null, CompactWorkflowJobError.TooLarge);
                return false;
            }
            if (!CompactWorkflowJobCodec.TryRead(JsonNode.Parse(source), job.Reference, Authority, _functions, out var validated))
            {
                attempt = new(null, validated.Error, validated.Detail);
                return false;
            }
            string file = Filename(job.Reference);
            CartridgeManifest.RejectLinks(file);
            string temporary = file + "." + Guid.NewGuid() + ".tmp";
            if (!TryCommit(temporary, file, source, initial, out var committed))
            {
                attempt = new(null, committed.Error, committed.Detail);
                return false;
            }
            attempt = new(new(validated.Value!, CompactRevisionFiles.Digest(source)!));

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactWorkflowJobError.StorageUnavailable, exception.Message);
            return false;
        }
    }

    private bool TryCommit(string temporary, string file, byte[] source, bool initial, out CompactWorkflowJobAttempt<string> attempt)
    {
        try
        {
            PrivateFile.WriteAllBytes(temporary, source);
            File.Move(temporary, file, overwrite: !initial);
            if (!CompactRevisionFiles.TrySyncDirectory(Root, out var synced))
            {
                attempt = new(null, CompactWorkflowJobError.StorageUnavailable, synced.Detail);
                return false;
            }
            attempt = new(file);

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactWorkflowJobError.StorageUnavailable, exception.Message);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception)
            {
                // An orphaned private temporary is never loaded as active job state.
            }
        }
    }

    public async Task<CompactWorkflowJobAttempt<T>> SerializedAsync<T>(CompactWorkflowJobReference reference,
        Func<CompactWorkflowJobSnapshot, Task<CompactWorkflowJobAttempt<T>>> action, CancellationToken cancellation)
    {
        if (!TryRead(reference, out var existing))
        {
            return new(default, existing.Error, existing.Detail);
        }
        SemaphoreSlim queue;
        lock (_queueGate)
        {
            if (!_queues.TryGetValue(reference, out queue!))
            {
                queue = new(1);
                _queues.Add(reference, queue);
            }
        }
        await queue.WaitAsync(cancellation);
        try
        {
            using var owner = new CompactWorkflowJobLock(Filename(reference) + ".lock", Authority);
            if (!owner.TryAcquire(out var acquired))
            {
                return new(default, acquired.Error, acquired.Detail);
            }
            if (!TryRead(reference, out var read))
            {
                return new(default, read.Error, read.Detail);
            }
            return await action(read.Value!);
        }
        finally
        {
            queue.Release();
        }
    }

    // Throws ArgumentException for a value that is not an opaque SHA-256 job reference.
    public string Filename(CompactWorkflowJobReference reference)
    {
        if (reference == null || !CompactContractHash.TryDigest(reference.Digest.Hex, out _))
        {
            throw new ArgumentException("unknown_job");
        }

        return System.IO.Path.Combine(Root, reference.Digest.Hex + ".json");
    }

    private string Timestamp()
    {
        return _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private static CompactContractDigest Hash(string text)
    {
        return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }
}
