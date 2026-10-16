using System.Security.Cryptography;

namespace KLaniBot.Fleet;

// The master's record of one pairing attempt, from the node's commitment to the issued credential. Every step
// checks the state and the deadline. Reveal and Complete move the state on before their own check, so a failed
// or concurrent attempt has used up the ticket instead of earning another try.
public sealed class PairingRequest
{
    public string Ticket { get; } = Guid.CreateVersion7().ToString();
    public string NodeName
    {
        get;
    }
    public string ClientCommitment
    {
        get;
    }
    public string MasterNonce { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public string Root
    {
        get;
    }
    public string PublicKey { get; private set; } = "";
    public string Transcript { get; private set; } = "";
    public PairingState State { get; private set; } = PairingState.Committed;
    public DateTimeOffset ExpiresAt
    {
        get;
    }
    public PairingOptions Options
    {
        get;
    }

    // Throws ArgumentException for an invalid node name or commitment.
    public PairingRequest(string nodeName, string clientCommitment, string root, PairingOptions options, DateTimeOffset now)
    {
        options.Validate();
        if (!KLaniBot.Startup.NodeName.IsValid(nodeName) ||
            !System.Text.RegularExpressions.Regex.IsMatch(clientCommitment, "^[0-9a-f]{64}$"))
        {
            throw new ArgumentException("Invalid node name or commitment.");
        }
        NodeName = nodeName;
        ClientCommitment = clientCommitment;
        Root = root;
        Options = new PairingOptions { CodeDigits = options.CodeDigits, LifetimeSeconds = options.LifetimeSeconds };
        ExpiresAt = now.AddSeconds(options.LifetimeSeconds);
    }

    // Throws UnauthorizedAccessException when the nonce does not match the commitment or the key is no P-256 key.
    public void Reveal(string nonce, string publicKey, DateTimeOffset now)
    {
        Require(PairingState.Committed, now);
        State = PairingState.Rejected;
        if (PairingProtocol.Commitment(nonce, publicKey) != ClientCommitment)
        {
            throw new UnauthorizedAccessException("Pairing commitment mismatch.");
        }
        using var key = ECDsa.Create();
        byte[] encoded = Convert.FromBase64String(publicKey);
        key.ImportSubjectPublicKeyInfo(encoded, out int consumed);
        if (consumed != encoded.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
        {
            throw new UnauthorizedAccessException("P-256 device identity required.");
        }
        PublicKey = publicKey;
        Transcript = PairingProtocol.Transcript(Ticket, NodeName, nonce, MasterNonce, publicKey, Root);
        State = PairingState.AwaitingApproval;
    }

    public void Approve(DateTimeOffset now)
    {
        Require(PairingState.AwaitingApproval, now);
        State = PairingState.Approved;
    }

    public void Reject()
    {
        State = PairingState.Rejected;
    }

    // Throws UnauthorizedAccessException when the device proof is invalid.
    public void Complete(string signature, DateTimeOffset now)
    {
        Require(PairingState.Approved, now);
        // Consume before validation so concurrent requests cannot obtain another attempt.
        State = PairingState.Consumed;
        if (!PairingProtocol.VerifySignature(PublicKey, Transcript, signature))
        {
            throw new UnauthorizedAccessException("Invalid device proof.");
        }
    }

    // Throws UnauthorizedAccessException when the pairing is expired, consumed or not in the expected state.
    private void Require(PairingState expected, DateTimeOffset now)
    {
        if (State != expected || now >= ExpiresAt)
        {
            throw new UnauthorizedAccessException("Pairing expired, consumed or not approved.");
        }
    }
}
