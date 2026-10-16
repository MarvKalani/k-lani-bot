using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Proof decoding is finite and verifies isolation, oracle/execution digests and the read-only restriction before issuance or consumption.
public static class CompactAcceptanceCodec
{
    public static bool Valid(CompactAcceptanceProof proof, CompactFunctionDraft function)
    {
        return proof != null && proof.Isolation != null && proof.Verified && Enum.IsDefined(proof.Test)
            && Enum.IsDefined(proof.Isolation.Kind) && !string.IsNullOrEmpty(proof.Isolation.Node)
            && (proof.Test == CompactAcceptanceTest.Functional || function.Steps.All(step => step.Action == CompactRecipeAction.Read))
            && CompactContractHash.TryDigest(proof.Oracle?.Hex, out _) && CompactContractHash.TryDigest(proof.Execution?.Hex, out _);
    }

    public static bool TryProof(JsonNode? node, out CompactAcceptanceProof? proof)
    {
        proof = null;
        if (node is not JsonObject document || !CompactDiscoveryDocumentCodec.Only(document, "test", "isolation", "verified",
                "independent_oracle_digest", "execution_digest")
            || !CompactDiscoveryDocumentCodec.Text(document, "test", out string test) || test is not "functional" and not "read_only"
            || !CompactDiscoveryDocumentCodec.Boolean(document, "verified", out bool verified)
            || document["isolation"] is not JsonObject isolation
            || !CompactDiscoveryDocumentCodec.Only(isolation, "kind", "node", "purpose")
            || !CompactDiscoveryDocumentCodec.Text(isolation, "kind", out string kind)
            || kind is not "private_virtual_desktop" and not "private_sandbox"
            || !CompactDiscoveryDocumentCodec.Text(isolation, "node", out string target) || target.Length == 0
            || !CompactDiscoveryDocumentCodec.Text(isolation, "purpose", out string purpose) || purpose != "synthetic_acceptance"
            || !CompactDiscoveryDocumentCodec.Text(document, "independent_oracle_digest", out string oracle)
            || !CompactContractHash.TryDigest(oracle, out var observed)
            || !CompactDiscoveryDocumentCodec.Text(document, "execution_digest", out string execution)
            || !CompactContractHash.TryDigest(execution, out var effect))
        {
            return false;
        }
        proof = new(test == "functional" ? CompactAcceptanceTest.Functional : CompactAcceptanceTest.ReadOnly,
            new(kind == "private_virtual_desktop" ? CompactAcceptanceIsolationKind.PrivateVirtualDesktop
                : CompactAcceptanceIsolationKind.PrivateSandbox, target), verified, observed!, effect!);

        return true;
    }
}
