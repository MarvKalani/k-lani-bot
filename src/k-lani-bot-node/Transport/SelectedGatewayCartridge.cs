using System.Security.Cryptography;
using KLaniBot.Cartridges;

namespace KLaniBot.Transport;

// A session retains only selected bundles. Replacing a manifest loads validated bytes and invalidates its program
// binding before another function runs; an in-flight recipe retains its original immutable definition.
internal sealed class SelectedGatewayCartridge : IDisposable
{
    private readonly string _path;
    private string _fingerprint;
    public GatewayCartridges Bundle
    {
        get; private set;
    }

    public SelectedGatewayCartridge(string path)
    {
        _path = path;
        _fingerprint = Fingerprint(path);
        Bundle = new GatewayCartridges(path);
    }

    public CartridgeReload Refresh()
    {
        string current = Fingerprint(_path);
        if (current == _fingerprint)
        {
            return CartridgeReload.Unchanged;
        }
        var replacement = new GatewayCartridges(_path);
        GatewayCartridges previous = Bundle;
        Bundle = replacement;
        _fingerprint = current;
        previous.Dispose();

        return CartridgeReload.Replaced;
    }

    // Throws InvalidDataException for an oversized or linked manifest.
    private static string Fingerprint(string path)
    {
        const int MaximumManifestBytes = 256 * 1024;
        CartridgeManifest.RejectLinks(path);
        if (new FileInfo(path).Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("Cartridge manifest exceeds its size limit.");
        }

        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    public void Dispose()
    {
        Bundle.Dispose();
    }
}

// A changed manifest requires a fresh Info binding; an unchanged selection keeps the already checked instance.
public enum CartridgeReload
{
    Unchanged,
    Replaced
}
