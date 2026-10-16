using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Acceptance payloads contain private authority and digests only; application inputs and raw traces remain outside receipt files.
public static class CompactAcceptanceWire
{
    public const string Format = "k-lani-bot-compatibility-acceptance/1";

    public static CompactContractDigest Authority(CompactAcceptanceAuthority authority)
    {
        return CompactContractHash.Of(new JsonObject
        {
            ["node"] = authority.Node,
            ["principal"] = authority.Principal,
            ["connection"] = authority.Connection
        });
    }

    public static JsonObject Proof(CompactAcceptanceProof proof)
    {
        string test = proof.Test switch
        {
            CompactAcceptanceTest.Functional => "functional",
            CompactAcceptanceTest.ReadOnly => "read_only",
            _ => throw new UnreachableException()
        };
        string isolation = proof.Isolation.Kind switch
        {
            CompactAcceptanceIsolationKind.PrivateVirtualDesktop => "private_virtual_desktop",
            CompactAcceptanceIsolationKind.PrivateSandbox => "private_sandbox",
            _ => throw new UnreachableException()
        };

        return new()
        {
            ["test"] = test,
            ["isolation"] = new JsonObject
            {
                ["kind"] = isolation,
                ["node"] = proof.Isolation.Node,
                ["purpose"] = "synthetic_acceptance"
            },
            ["verified"] = proof.Verified,
            ["independent_oracle_digest"] = proof.Oracle.Hex,
            ["execution_digest"] = proof.Execution.Hex
        };
    }

    public static JsonObject Payload(CompactAcceptanceAuthority authority, CompactLoadedFunction function,
        CompactCompatibilityContract current, CompactAcceptanceProof proof, string createdAt)
    {
        return new()
        {
            ["format"] = Format,
            ["authority"] = Authority(authority).Hex,
            ["function_scope"] = CompactContractHash.Function(function.Draft).Hex,
            ["previous_compatibility"] = CompactContractHash.Of(function.Document["compatibility"]).Hex,
            ["compatibility"] = CompactCompatibilityWire.Contract(current),
            ["proof"] = Proof(proof),
            ["created_at"] = createdAt
        };
    }
}
