using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Common;

namespace KLaniBot.Transport.CompactUi;

// Revision files are bounded and nonsymbolic; durable file and directory flushes preserve the active file/intent commit order.
public static class CompactRevisionFiles
{
    public const int MaximumBytes = 1024 * 1024;

    public static bool TryRead(string file, out CompactFunctionAttempt<byte[]> attempt)
    {
        try
        {
            CartridgeManifest.RejectLinks(file);
            if (Directory.Exists(file))
            {
                attempt = new(null, CompactFunctionError.UnsafeFile);
                return false;
            }
            if (!File.Exists(file))
            {
                attempt = new(null);
                return true;
            }
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes)
            {
                attempt = new(null, CompactFunctionError.UnsafeFile);
                return false;
            }
            byte[] content = new byte[(int)stream.Length];
            stream.ReadExactly(content);
            if (stream.ReadByte() != -1)
            {
                attempt = new(null, CompactFunctionError.UnsafeFile);
                return false;
            }
            attempt = new(content);

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactFunctionError.StorageUnavailable, exception.Message);
            return false;
        }
    }

    public static string? Digest(byte[]? source)
    {
        return source == null ? null : Convert.ToHexStringLower(SHA256.HashData(source));
    }

    public static bool TryWriteNew(string file, byte[] source, out CompactFunctionAttempt<string> attempt)
    {
        try
        {
            if (source.Length > MaximumBytes)
            {
                attempt = new(null, CompactFunctionError.ByteLimit);
                return false;
            }
            CartridgeManifest.RejectLinks(file);
            PrivateFile.CreateDirectory(Path.GetDirectoryName(file)!);
            PrivateFile.WriteAllBytes(file, source);
            attempt = new(file);

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactFunctionError.StorageUnavailable, exception.Message);
            return false;
        }
    }

    public static byte[] Encode(JsonNode document)
    {
        return Encoding.UTF8.GetBytes(CompactJsonText.Serialize(document) + "\n");
    }

    public static bool TrySyncDirectory(string directory, out CompactFunctionAttempt<string> attempt)
    {
        if (PrivateFile.TrySyncDirectory(directory, out var failure))
        {
            attempt = new(directory);
            return true;
        }
        if (failure == PrivateFile.DirectorySyncFailure.OpenFailed)
        {
            attempt = new(null, CompactFunctionError.StorageUnavailable, "Cannot open the revision directory for synchronization.");
            return false;
        }
        attempt = new(null, CompactFunctionError.StorageUnavailable, "Cannot synchronize the revision directory.");
        return false;
    }
}
