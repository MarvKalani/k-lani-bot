using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Common;

namespace KLaniBot.Transport.CompactUi;

// Revision writers use one private exclusive-create lock; only a proven dead or replaced owner permits recovery.
public sealed class CompactRevisionLock(string file)
{
    private byte[]? _ownership;
    private FileStream? _gate;

    public bool TryAcquire(out CompactFunctionAttempt<CompactRevisionLock> attempt)
    {
        if (!TryGate())
        {
            attempt = new(null, CompactFunctionError.Locked);
            return false;
        }
        if (!CompactRevisionFiles.TryRead(file, out var read))
        {
            ReleaseGate();
            attempt = new(null, read.Error, read.Detail);
            return false;
        }
        if (read.Value != null && !TryRemoveDeadOwner(read.Value))
        {
            ReleaseGate();
            attempt = new(null, CompactFunctionError.Locked);
            return false;
        }
        var document = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["started"] = CompactProcessLifetime.Observe(Environment.ProcessId).Started
        };
        byte[] ownership = CompactRevisionFiles.Encode(document);
        if (!CompactRevisionFiles.TryWriteNew(file, ownership, out var written))
        {
            ReleaseGate();
            attempt = new(null, File.Exists(file) ? CompactFunctionError.Locked : written.Error, written.Detail);
            return false;
        }
        _ownership = ownership;
        attempt = new(this);

        return true;
    }

    private bool TryGate()
    {
        try
        {
            CartridgeManifest.RejectLinks(file + ".gate");
            PrivateFile.CreateDirectory(Path.GetDirectoryName(file)!);
            _gate = PrivateFile.OpenWrite(file + ".gate", FileMode.OpenOrCreate);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ReleaseGate()
    {
        _gate?.Dispose();
        _gate = null;
    }

    private bool TryRemoveDeadOwner(byte[] source)
    {
        try
        {
            if (JsonNode.Parse(source) is not JsonObject owner || owner["pid"] is not JsonValue value
                || !value.TryGetValue<int>(out int processId) || processId < 1)
            {
                return false;
            }
            CompactProcessObservation current = CompactProcessLifetime.Observe(processId);
            if (current.State == CompactProcessState.Unknown || current.State == CompactProcessState.Live
                && (!CompactDiscoveryDocumentCodec.Text(owner, "started", out string started) || current.Started == started))
            {
                return false;
            }
            if (!CompactRevisionFiles.TryRead(file, out var latest) || !source.AsSpan().SequenceEqual(latest.Value))
            {
                return false;
            }
            File.Delete(file);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryRelease(out CompactFunctionAttempt<string> attempt)
    {
        try
        {
            if (_ownership == null || !CompactRevisionFiles.TryRead(file, out var read)
                || !_ownership.AsSpan().SequenceEqual(read.Value))
            {
                attempt = new(null, CompactFunctionError.Locked);
                return false;
            }
            File.Delete(file);
            _ownership = null;

            return CompactRevisionFiles.TrySyncDirectory(Path.GetDirectoryName(file)!, out attempt);
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactFunctionError.StorageUnavailable, exception.Message);
            return false;
        }
        finally
        {
            ReleaseGate();
        }
    }
}
