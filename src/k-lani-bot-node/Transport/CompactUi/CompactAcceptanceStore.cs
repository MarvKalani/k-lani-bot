using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// A configured private receipt store reads signed isolated evidence.
// MCP revalidation only references existing files and never issues receipts.
public sealed class CompactAcceptanceStore
{
    private readonly byte[] _key;
    private readonly TimeProvider _time;
    public string Root
    {
        get;
    }
    public CompactAcceptanceAuthority Authority
    {
        get;
    }

    private CompactAcceptanceStore(string root, CompactAcceptanceAuthority authority, byte[] key, TimeProvider? timeProvider)
    {
        Root = root;
        Authority = authority;
        _key = key;
        _time = timeProvider ?? TimeProvider.System;
    }

    public static bool TryOpen(string root, string exports, CompactAcceptanceAuthority authority,
        out CompactAcceptanceAttempt<CompactAcceptanceStore> attempt, TimeProvider? timeProvider = null)
    {
        attempt = new(null, CompactAcceptanceError.InvalidConfiguration);
        if (authority == null || string.IsNullOrEmpty(authority.Node) || string.IsNullOrEmpty(authority.Principal)
            || string.IsNullOrEmpty(authority.Connection) || !CompactProgramStorage.TryRoots(exports, root, out var roots))
        {
            return false;
        }
        string file = Path.Combine(roots.Value!.Learning!, CompactAcceptanceFiles.KeyName);
        if (!CompactAcceptanceFiles.PrivateDirectory(roots.Value.Learning!))
        {
            attempt = new(null, CompactAcceptanceError.UnsafeEvidence);
            return false;
        }
        if (!CompactAcceptanceFiles.TryRead(file, CompactAcceptanceFiles.KeyBytes, out var read))
        {
            attempt = new(null, read.Error, read.Detail);
            return false;
        }
        if (read.Value!.Length != CompactAcceptanceFiles.KeyBytes)
        {
            attempt = new(null, CompactAcceptanceError.InvalidKey);
            return false;
        }
        attempt = new(new(roots.Value.Learning!, authority, read.Value, timeProvider));

        return true;
    }

    public static bool TryCreateForIsolatedRunner(string root, string exports, CompactAcceptanceAuthority authority,
        out CompactAcceptanceAttempt<CompactAcceptanceStore> attempt, TimeProvider? timeProvider = null)
    {
        if (!CompactProgramStorage.TryRoots(exports, root, out var roots) || authority == null
            || string.IsNullOrEmpty(authority.Node) || string.IsNullOrEmpty(authority.Principal)
            || string.IsNullOrEmpty(authority.Connection))
        {
            attempt = new(null, CompactAcceptanceError.InvalidConfiguration);
            return false;
        }
        string file = Path.Combine(roots.Value!.Learning!, CompactAcceptanceFiles.KeyName);
        if (!File.Exists(file) && !CompactAcceptanceFiles.TryWriteNew(file,
                RandomNumberGenerator.GetBytes(CompactAcceptanceFiles.KeyBytes), out var created))
        {
            attempt = new(null, created.Error, created.Detail);
            return false;
        }

        return TryOpen(root, exports, authority, out attempt, timeProvider);
    }

    public bool TryRecord(CompactLoadedFunction function, CompactObservedCompatibility current, CompactAcceptanceProof proof,
        out CompactAcceptanceAttempt<CompactAcceptanceReference> attempt)
    {
        if (!CompactAcceptanceCodec.Valid(proof, function.Draft) || proof.Isolation.Node != Authority.Node)
        {
            attempt = new(null, CompactAcceptanceError.InvalidProof);
            return false;
        }
        if (!CompactCompatibility.TryCapture(function.Draft, current, out var compatibility))
        {
            attempt = new(null, CompactAcceptanceError.CaptureEvidenceMissing);
            return false;
        }
        string createdAt = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        JsonObject payload = CompactAcceptanceWire.Payload(Authority, function, compatibility.Value!, proof, createdAt);
        var reference = new CompactAcceptanceReference(Guid.NewGuid());
        var record = new JsonObject { ["payload"] = payload, ["signature"] = Sign(payload) };
        string file = Path.Combine(Root, reference.Value + ".json");
        if (!CompactAcceptanceFiles.TryWriteNew(file, CompactRevisionFiles.Encode(record), out var written))
        {
            attempt = new(null, written.Error, written.Detail);
            return false;
        }
        attempt = new(reference);

        return true;
    }

    public bool TryRead(CompactAcceptanceReference reference, CompactLoadedFunction function, CompactObservedCompatibility current,
        out CompactAcceptanceAttempt<CompactAcceptedCompatibility> attempt)
    {
        string file = Path.Combine(Root, reference.Value.ToString() + ".json");
        if (!CompactAcceptanceFiles.TryRead(file, CompactAcceptanceFiles.ReceiptBytes, out var read))
        {
            attempt = new(null, read.Error, read.Detail);
            return false;
        }
        try
        {
            return TryPayload(reference, JsonNode.Parse(read.Value!), function, current, out attempt);
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactAcceptanceError.ForgedEvidence, exception.Message);
            return false;
        }
    }

    private bool TryPayload(CompactAcceptanceReference reference, JsonNode? node, CompactLoadedFunction function,
        CompactObservedCompatibility current, out CompactAcceptanceAttempt<CompactAcceptedCompatibility> attempt)
    {
        attempt = new(null, CompactAcceptanceError.ForgedEvidence);
        if (node is not JsonObject record || !CompactDiscoveryDocumentCodec.Only(record, "payload", "signature")
            || record["payload"] is not JsonObject payload || !CompactDiscoveryDocumentCodec.Text(record, "signature", out string signature)
            || !CompactContractHash.TryDigest(signature, out _) || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(signature), Convert.FromHexString(Sign(payload))))
        {
            return false;
        }
        if (!Matches(payload, function))
        {
            attempt = new(null, CompactAcceptanceError.ForeignOrStaleEvidence);
            return false;
        }
        if (!CompactCompatibilityCodec.TryRecorded(payload["compatibility"], out var captured) || captured.Value!.Contracts == null)
        {
            return false;
        }
        if (!CompactAcceptanceCodec.TryProof(payload["proof"], out var proof) || !CompactAcceptanceCodec.Valid(proof!, function.Draft)
            || proof!.Isolation.Node != Authority.Node)
        {
            attempt = new(null, CompactAcceptanceError.InvalidProof);
            return false;
        }
        CompactCompatibilityVerdict verdict = CompactCompatibility.Assess(function.Draft, captured.Value, current);
        if (verdict.Status != CompactCompatibilityStatus.Reusable)
        {
            attempt = new(null, CompactAcceptanceError.ScopeChanged,
                CompactCompatibilityNames.Verdict(verdict)["reason"]!.GetValue<string>());
            return false;
        }
        attempt = new(new(reference, captured.Value.Contracts));

        return true;
    }

    private bool Matches(JsonObject payload, CompactLoadedFunction function)
    {
        return CompactDiscoveryDocumentCodec.Text(payload, "format", out string format) && format == CompactAcceptanceWire.Format
            && CompactDiscoveryDocumentCodec.Text(payload, "authority", out string authority)
            && authority == CompactAcceptanceWire.Authority(Authority).Hex
            && CompactDiscoveryDocumentCodec.Text(payload, "function_scope", out string scope)
            && scope == CompactContractHash.Function(function.Draft).Hex
            && CompactDiscoveryDocumentCodec.Text(payload, "previous_compatibility", out string previous)
            && previous == CompactContractHash.Of(function.Document["compatibility"]).Hex;
    }

    private string Sign(JsonNode payload)
    {
        byte[] digest = Encoding.UTF8.GetBytes(CompactContractHash.Of(payload).Hex);

        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, digest));
    }
}
