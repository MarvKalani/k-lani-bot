using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Common;

namespace KLaniBot.Transport.CompactUi;

// Signing keys and receipts are bounded owner-only regular files; reading never repairs permissions or follows symbolic links.
public static class CompactAcceptanceFiles
{
    public const int KeyBytes = 32;
    public const int ReceiptBytes = 64 * 1024;
    public const string KeyName = "receipt-signing.key";
    private const UnixFileMode SharedPermissions = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static bool PrivateDirectory(string directory)
    {
        try
        {
            CartridgeManifest.RejectLinks(directory);

            return Directory.Exists(directory)
                && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(directory) & SharedPermissions) == 0);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryRead(string file, int maximum, out CompactAcceptanceAttempt<byte[]> attempt)
    {
        try
        {
            CartridgeManifest.RejectLinks(file);
            if (!File.Exists(file))
            {
                attempt = new(null, CompactAcceptanceError.UnknownEvidence);
                return false;
            }
            if (Directory.Exists(file) || !OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & SharedPermissions) != 0)
            {
                attempt = new(null, CompactAcceptanceError.UnsafeEvidence);
                return false;
            }
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximum)
            {
                attempt = new(null, CompactAcceptanceError.UnsafeEvidence);
                return false;
            }
            byte[] source = new byte[(int)stream.Length];
            stream.ReadExactly(source);
            if (stream.ReadByte() != -1)
            {
                attempt = new(null, CompactAcceptanceError.UnsafeEvidence);
                return false;
            }
            attempt = new(source);

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactAcceptanceError.UnsafeEvidence, exception.Message);
            return false;
        }
    }

    public static bool TryWriteNew(string file, byte[] content, out CompactAcceptanceAttempt<string> attempt)
    {
        try
        {
            if (content.Length > ReceiptBytes)
            {
                attempt = new(null, CompactAcceptanceError.UnsafeEvidence);
                return false;
            }
            CartridgeManifest.RejectLinks(file);
            PrivateFile.CreateDirectory(Path.GetDirectoryName(file)!);
            PrivateFile.WriteAllBytes(file, content);
            if (!CompactRevisionFiles.TrySyncDirectory(Path.GetDirectoryName(file)!, out var synced))
            {
                attempt = new(null, CompactAcceptanceError.StorageUnavailable, synced.Detail);
                return false;
            }
            attempt = new(file);

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactAcceptanceError.StorageUnavailable, exception.Message);
            return false;
        }
    }
}
