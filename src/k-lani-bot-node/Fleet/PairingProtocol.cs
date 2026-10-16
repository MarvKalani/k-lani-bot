using System.Globalization;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Common;
using KLaniBot.Transport;

namespace KLaniBot.Fleet;

// The master's pairing settings (pairing-options.json, or the "digits" command). CodeDigits 0 switches the code
// comparison off; approval of the ticket on the master is then the only check that the right node is pairing.
public sealed class PairingOptions
{
    public int CodeDigits { get; set; } = 4;
    public int LifetimeSeconds { get; set; } = 300;
    public int MaxNodes { get; set; } = 1024;
    public int MaxParallelUpdates { get; set; } = 4;

    // Throws ArgumentException when a value is outside its range.
    public void Validate()
    {
        if (CodeDigits is < 0 or > 6 || LifetimeSeconds is < 30 or > 300 || MaxNodes is < 1 or > 10000 || MaxParallelUpdates is < 1 or > 32)
        {
            throw new ArgumentException(
                "CodeDigits must be 0 (disabled) through 6; lifetime 30–300 seconds, MaxNodes 1–10000, MaxParallelUpdates 1–32.");
        }
    }
}

// A pairing request only moves forward. Rejected and Consumed are final, so a ticket is never usable twice.
public enum PairingState
{
    Committed,
    AwaitingApproval,
    Approved,
    Consumed,
    Rejected
}

// Use case: a new node joins a master on the LAN without a preshared secret. The first TLS connection is
// unauthenticated, so someone on the network could sit in the middle. Both sides therefore commit to their
// random nonce before they see the other's, derive a transcript hash over both nonces, the node's key and the
// master's CA, and show a short code from it on both screens. A person compares the two codes and approves the
// exact ticket on the master. A man in the middle would need the codes of two different transcripts to match,
// and the commitments stop it from choosing its nonce after seeing the honest one; with four digits that is one
// guess in ten thousand, and each pairing allows one attempt.
//
// Commitment and transcript hashes carry separate domains from ProtocolIdentity, so a value computed for one
// step cannot be presented in another.
public static class PairingProtocol
{
    // Throws InvalidDataException when the nonce is not 32 bytes.
    public static string Commitment(string nonce, string identity)
    {
        if (Convert.FromBase64String(nonce).Length != 32)
        {
            throw new InvalidDataException("Pairing nonce must contain 32 random bytes.");
        }
        return Hash(ProtocolIdentity.PairingCommitDomain, nonce, identity);
    }

    public static string Transcript(string ticket, string nodeName, string nodeNonce, string masterNonce, string publicKey, string root)
    {
        return Hash(ProtocolIdentity.PairingSessionDomain, ticket, nodeName, nodeNonce, masterNonce, publicKey, root);
    }

    public static string Code(string transcript, int digits)
    {
        new PairingOptions { CodeDigits = digits }.Validate();
        if (digits == 0)
        {
            return "";
        }
        uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Convert.FromHexString(transcript));
        uint combinations = (uint)Math.Pow(10, digits);
        return (value % combinations).ToString("D" + digits, CultureInfo.InvariantCulture);
    }

    public static string Hash(params string[] fields)
    {
        // Length-prefixed JSON strings prevent ambiguous concatenations between identities.
        var document = new JsonArray(fields.Select(field => (JsonNode?)JsonValue.Create(field)).ToArray());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document.ToJsonString())));
    }

    public static bool VerifySignature(string publicKey, string transcript, string signature)
    {
        using var key = ECDsa.Create();
        byte[] encoded = Convert.FromBase64String(publicKey);
        key.ImportSubjectPublicKeyInfo(encoded, out int consumed);
        if (consumed != encoded.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
        {
            return false;
        }
        return key.VerifyData(Encoding.UTF8.GetBytes(transcript), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
    }

    public static bool ValidateCertificate(X509Certificate? certificate, SslPolicyErrors errors, string rootBase64)
    {
        if (certificate == null
            || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }
        using var root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(rootBase64));
        using var peer = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(peer);
    }

    // Throws InvalidDataException for an invalid or oversized frame.
    public static async Task<JsonObject?> ReceiveAsync(WebSocket socket, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(buffer, cancellation);
            if (frame.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (frame.MessageType != WebSocketMessageType.Text || output.Length + frame.Count > 4 * 1024 * 1024)
            {
                throw new InvalidDataException("Invalid or oversized relay frame.");
            }
            output.Write(buffer, 0, frame.Count);
        }
        while (!frame.EndOfMessage);
        return JsonNode.Parse(output.ToArray())!.AsObject();
    }

    // Throws InvalidDataException when the frame exceeds the transport limit.
    public static Task SendAsync(WebSocket socket, JsonObject message, CancellationToken cancellation)
    {
        byte[] content = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (content.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("Relay frame exceeds the transport limit.");
        }
        return socket.SendAsync(content, WebSocketMessageType.Text, true, cancellation);
    }

    public static void WritePrivate(string path, string content, bool replace = false)
    {
        CartridgeManifest.RejectLinks(path);
        PrivateFile.WriteAllText(path, content, replace);
    }
}
