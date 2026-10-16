using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Startup;
using KLaniBot.Transport;
using KLaniBot.Visibility;

namespace KLaniBot.Fleet;

// The master's outstanding enrollment invitations. They live only in memory and only as token hashes: a master
// restart voids every invitation, and the state directory never holds a token that could enroll a node. Each
// invitation is consumed by its first use, even when the device proof that follows is wrong.
public sealed class FleetInvitationStore
{
    private readonly Dictionary<string, JsonObject> _invitations = new();
    private readonly object _gate = new();

    // Throws ArgumentException for an invalid node name or consent mode and InvalidOperationException when too many invitations are open.
    public JsonObject Invite(string node, string master, string root, ConsentMode consentMode, DateTimeOffset now)
    {
        if (!NodeName.IsValid(node) || !Enum.IsDefined(consentMode))
        {
            throw new ArgumentException("Invite requires a valid node name and ask/unattended mode.");
        }
        string consentModeName = ConsentModeNames.ToProtocolName(consentMode);
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset expires = now.AddHours(1);
        lock (_gate)
        {
            foreach (string key in _invitations.Where(entry => DateTimeOffset.Parse(entry.Value["expires_at"]!.ToString()) <= now)
                .Select(entry => entry.Key).ToArray())
            {
                _invitations.Remove(key);
            }
            if (_invitations.Count >= 10000)
            {
                throw new InvalidOperationException("Too many outstanding deployment invitations.");
            }
            _invitations[Hash(token)] = new JsonObject
            {
                ["node"] = node,
                ["challenge"] = challenge,
                ["consent_mode"] = consentModeName,
                ["expires_at"] = expires.ToString("O")
            };
        }
        return new JsonObject
        {
            ["version"] = 1,
            ["node"] = node,
            ["master"] = master,
            ["root"] = root,
            ["token"] = token,
            ["challenge"] = challenge,
            ["consent_mode"] = consentModeName,
            ["expires_at"] = expires.ToString("O")
        };
    }

    // Throws UnauthorizedAccessException when the invitation is invalid, expired or used, or the device proof fails.
    public JsonObject Consume(string token, string node, string publicKey, string signature, DateTimeOffset now)
    {
        lock (_gate)
        {
            string digest = Hash(token);
            if (!_invitations.Remove(digest, out JsonObject? invitation) || invitation["node"]!.ToString() != node ||
                DateTimeOffset.Parse(invitation["expires_at"]!.ToString()) <= now)
            {
                throw new UnauthorizedAccessException("Invitation is invalid, expired or already used.");
            }
            string proof = PairingProtocol.Hash(ProtocolIdentity.FleetProofDomain, node, invitation["challenge"]!.ToString(), publicKey);
            if (!PairingProtocol.VerifySignature(publicKey, proof, signature))
            {
                throw new UnauthorizedAccessException("Invalid enrollment device proof.");
            }
            return invitation;
        }
    }

    private static string Hash(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

}
