using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using KLaniBot.Cartridges;
using KLaniBot.Transport;

namespace KLaniBot.Updates;

// Use case: a master distributes a new Windows node executable to its paired nodes. The manifest is signed with
// the key of the master's CA, the same CA the node pinned at pairing, so a node accepts updates only from its
// own master and never through the transport alone. The signature covers version, platform, size, digest and
// validity; the downloaded bytes must match size and digest exactly.
//
// A node only moves forward: the version must be newer than its own, so an old signed manifest cannot take a
// node back to an older build, and a manifest is valid for at most 30 days.
public sealed record NodeUpdateManifest(string Version, string Platform, long Size, string Sha256,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string Signature)
{
    public byte[] SignedBytes()
    {
        string document = JsonSerializer.Serialize(new
        {
            Version,
            Platform,
            Size,
            Sha256,
            IssuedAt,
            ExpiresAt
        });
        return Encoding.UTF8.GetBytes(ProtocolIdentity.NodeUpdateSignatureDomain + "\n" + document);
    }

    // Throws InvalidDataException for an invalid, expired or older update or a non-ECDSA signer, and UnauthorizedAccessException when the
    // signature is not the paired master's.
    public void Validate(string rootBase64, string currentVersion, DateTimeOffset now)
    {
        ValidateFields(currentVersion, now);
        using var root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(rootBase64));
        using ECDsa key = root.GetECDsaPublicKey() ?? throw new InvalidDataException("Update signer is not ECDSA.");
        if (!key.VerifyData(SignedBytes(), Convert.FromBase64String(Signature), HashAlgorithmName.SHA256))
        {
            throw new UnauthorizedAccessException("Update signature does not match the paired master.");
        }
    }

    // Everything but the signature. The update helper checks this much again; the signature was the gate of the node that
    // staged the update, under the identifiers of its own version, which a newer helper may not share.
    // Throws InvalidDataException for an invalid, expired or older update.
    public void ValidateFields(string currentVersion, DateTimeOffset now)
    {
        if (!IsProductVersion(Version) || Platform != "windows-x64" || Size is < 1024 or > 256 * 1024 * 1024 ||
            !System.Text.RegularExpressions.Regex.IsMatch(Sha256, "^[0-9a-f]{64}$") ||
            IssuedAt > now.AddMinutes(5) || ExpiresAt <= now || ExpiresAt > IssuedAt.AddDays(30) ||
            System.Version.Parse(Version) <= System.Version.Parse(currentVersion))
        {
            throw new InvalidDataException("Update is invalid, expired or not newer than this node.");
        }
    }

    public static bool IsProductVersion(string version)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, "^0\\.[0-9]{4}\\.[0-9]{4}$"))
        {
            return false;
        }
        string[] fields = version.Split('.');
        try
        {
            _ = new DateOnly(2000 + int.Parse(fields[1][..2]), int.Parse(fields[1][2..]), int.Parse(fields[2][..2]));
            return int.Parse(fields[2][2..]) is >= 1 and <= 99;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // Throws UnauthorizedAccessException when the bytes differ from the signed manifest.
    public void VerifyFile(string path)
    {
        CartridgeManifest.RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != Size || Convert.ToHexStringLower(SHA256.HashData(stream)) != Sha256)
        {
            throw new UnauthorizedAccessException("Update bytes differ from the signed manifest.");
        }
    }
}
