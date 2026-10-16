using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using KLaniBot.Cartridges;
using KLaniBot.Common;
using KLaniBot.Fleet;
using KLaniBot.Platforms.Windows;

namespace KLaniBot.Updates;

// The master's side of node updates (the "publish" console command). It signs only a Windows x64 executable
// whose embedded product version equals the version the operator typed, so a mistyped version cannot make the
// fleet install a different build under a newer number.
public sealed class NodeUpdatePublisher
{
    private readonly string _directory;
    private readonly X509Certificate2 _root;
    private readonly object _gate = new();

    public NodeUpdatePublisher(string stateDirectory, X509Certificate2 root)
    {
        _directory = Path.Combine(stateDirectory, "updates");
        PairingClient.CreatePrivateDirectory(_directory);
        _root = root;
    }

    public NodeUpdateManifest? Latest()
    {
        lock (_gate)
        {
            string path = Path.Combine(_directory, "windows-x64.json");
            CartridgeManifest.RejectLinks(path);
            return File.Exists(path) ? JsonSerializer.Deserialize<NodeUpdateManifest>(File.ReadAllText(path)) : null;
        }
    }

    private const long MinimumExecutableBytes = 1024;
    private const long MaximumExecutableBytes = 256 * 1024 * 1024;
    private static readonly TimeSpan ManifestLifetime = TimeSpan.FromDays(30);

    // The executable as stored under its version: its SHA-256 and its size.
    private sealed record StoredExecutable(string Digest, long Size);

    // Throws ArgumentException for an invalid version format, InvalidDataException when the executable does not match or no signing key
    // exists, and UnauthorizedAccessException for a downgrade or replaced version.
    public NodeUpdateManifest Publish(string version, string source)
    {
        if (!NodeUpdateManifest.IsProductVersion(version))
        {
            throw new ArgumentException("Publish requires a product version in 0.YYMM.DDNN format.");
        }
        source = Path.GetFullPath(source);
        CartridgeManifest.RejectLinks(source);
        if (WindowsExecutableMetadata.ReadProductVersion(source) != version)
        {
            throw new InvalidDataException("Published version differs from the actual executable version.");
        }
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        RequireWindowsExecutable(input);
        input.Position = 0;

        lock (_gate)
        {
            NodeUpdateManifest? previous = Latest();
            if (previous != null && System.Version.Parse(version) < System.Version.Parse(previous.Version))
            {
                throw new UnauthorizedAccessException("The update channel cannot be downgraded.");
            }
            StoredExecutable stored = Store(input, FileFor(version));
            return SignAndSaveManifest(version, stored);
        }
    }

    // Throws InvalidDataException for anything but a Windows x64 PE executable within the size limits.
    private static void RequireWindowsExecutable(FileStream input)
    {
        using var executable = new PEReader(input, PEStreamOptions.LeaveOpen);

        if (executable.PEHeaders.CoffHeader.Machine != Machine.Amd64 || executable.PEHeaders.PEHeader == null ||
            input.Length is < MinimumExecutableBytes or > MaximumExecutableBytes)
        {
            throw new InvalidDataException("Only Windows-x64 PE executables can be distributed.");
        }
    }

    // A version is stored once; publishing it again is accepted only with the same bytes.
    // Throws UnauthorizedAccessException when the version exists with different bytes.
    private static StoredExecutable Store(FileStream input, string destination)
    {
        string temporary = destination + ".new";
        CartridgeManifest.RejectLinks(temporary);
        using (var output = PrivateFile.OpenWrite(temporary, FileMode.Create))
        {
            input.CopyTo(output);
            output.Flush(true);
        }
        using var staged = File.OpenRead(temporary);
        string digest = Convert.ToHexStringLower(SHA256.HashData(staged));
        long size = staged.Length;
        staged.Close();

        if (File.Exists(destination))
        {
            using var existing = File.OpenRead(destination);
            if (Convert.ToHexStringLower(SHA256.HashData(existing)) != digest)
            {
                File.Delete(temporary);
                throw new UnauthorizedAccessException("A published version cannot be replaced by different bytes.");
            }
            File.Delete(temporary);
        }
        else
        {
            CartridgeManifest.RejectLinks(destination);
            File.Move(temporary, destination);
        }
        return new StoredExecutable(digest, size);
    }

    // Throws InvalidDataException when the master certificate has no private key.
    private NodeUpdateManifest SignAndSaveManifest(string version, StoredExecutable stored)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var manifest = new NodeUpdateManifest(version, "windows-x64", stored.Size, stored.Digest, now, now.Add(ManifestLifetime), "");
        using ECDsa signer = _root.GetECDsaPrivateKey() ?? throw new InvalidDataException("Master signing key unavailable.");
        manifest = manifest with
        {
            Signature = Convert.ToBase64String(signer.SignData(manifest.SignedBytes(), HashAlgorithmName.SHA256))
        };

        string manifestPath = Path.Combine(_directory, "windows-x64.json");
        PairingProtocol.WritePrivate(manifestPath + ".new", JsonSerializer.Serialize(manifest), true);
        CartridgeManifest.RejectLinks(manifestPath);
        File.Move(manifestPath + ".new", manifestPath, true);
        return manifest;
    }

    // Throws ArgumentException for an invalid version.
    public string FileFor(string version)
    {
        if (!NodeUpdateManifest.IsProductVersion(version))
        {
            throw new ArgumentException("Invalid update version.");
        }
        string path = Path.Combine(_directory, "k-lani-bot-node-" + version + ".exe");
        CartridgeManifest.RejectLinks(path);
        return path;
    }
}
