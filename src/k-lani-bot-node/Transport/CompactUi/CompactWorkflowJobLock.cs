using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Common;

namespace KLaniBot.Transport.CompactUi;

// Job locks bind process lifetime and workflow authority; a current OS-proven dead owner can be recovered without replaying input.
public sealed class CompactWorkflowJobLock(string file, CompactContractDigest authority) : IDisposable
{
    private FileStream? _gate;
    private byte[]? _owned;
    private const int MaximumLockBytes = 1024;

    public bool TryAcquire(out CompactWorkflowJobAttempt<CompactWorkflowJobLock> attempt)
    {
        attempt = new(null, CompactWorkflowJobError.Locked);
        try
        {
            CartridgeManifest.RejectLinks(file + ".gate");
            _gate = PrivateFile.OpenWrite(file + ".gate", FileMode.OpenOrCreate);
            if (!CompactRevisionFiles.TryRead(file, out var source) || source.Value?.Length > MaximumLockBytes)
            {
                attempt = new(null, CompactWorkflowJobError.InvalidLock);
                ReleaseGate();
                return false;
            }
            if (source.Value != null && !TryRemoveOwner(source.Value, out var error))
            {
                attempt = new(null, error);
                ReleaseGate();
                return false;
            }
            var owner = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["process_start"] = CompactProcessLifetime.Observe(Environment.ProcessId).Started,
                ["authority"] = authority.Hex
            };
            byte[] content = CompactRevisionFiles.Encode(owner);
            PrivateFile.WriteAllBytes(file, content);
            _owned = content;
            attempt = new(this);

            return true;
        }
        catch (Exception)
        {
            ReleaseGate();
            return false;
        }
    }

    private bool TryRemoveOwner(byte[] source, out CompactWorkflowJobError error)
    {
        error = CompactWorkflowJobError.InvalidLock;
        try
        {
            if (JsonNode.Parse(source) is not JsonObject owner || !CompactDiscoveryDocumentCodec.Text(owner, "authority", out string scope)
                || scope != authority.Hex || owner["pid"] is not JsonValue pid || !pid.TryGetValue<int>(out int processId) || processId < 1)
            {
                return false;
            }
            CompactProcessObservation process = CompactProcessLifetime.Observe(processId);
            if (process.State == CompactProcessState.Unknown || process.State == CompactProcessState.Live
                && (!CompactDiscoveryDocumentCodec.Text(owner, "process_start", out string start) || process.Started == start))
            {
                error = CompactWorkflowJobError.Locked;
                return false;
            }
            if (!CompactRevisionFiles.TryRead(file, out var current) || !source.AsSpan().SequenceEqual(current.Value))
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

    private void ReleaseGate()
    {
        _gate?.Dispose();
        _gate = null;
    }

    public void Dispose()
    {
        try
        {
            if (_owned != null && CompactRevisionFiles.TryRead(file, out var source)
                && _owned.AsSpan().SequenceEqual(source.Value))
            {
                File.Delete(file);
                CompactRevisionFiles.TrySyncDirectory(Path.GetDirectoryName(file)!, out _);
            }
        }
        catch (Exception)
        {
            // A retained lock is recovered only after its owner lifetime is proven; cleanup never removes foreign ownership.
        }
        finally
        {
            _owned = null;
            ReleaseGate();
        }
    }
}
