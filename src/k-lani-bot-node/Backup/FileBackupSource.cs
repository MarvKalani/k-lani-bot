using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace KLaniBot.Backup;

// The closed-file adapter bounds memory and detects changed content; it never claims consistency for an open database.
internal static class FileBackupSource
{
    public const int MaximumFiles = 256;
    public const int MaximumEntries = 2048;
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    public const int MaximumTotalBytes = 16 * 1024 * 1024;
    private static readonly HashSet<string> DatabaseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".sqlite", ".sqlite3", ".mkx", ".mkwal", ".mkgroup", ".mdb", ".accdb"
    };

    // Throws BackupFault for unsupported files, links or bounds, and IOException for an unavailable or changing source.
    public static IReadOnlyList<BackupFile> Scan(string source)
    {
        var files = new List<BackupFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(source);
        int entries = 0;
        long bytes = 0;
        while (pending.TryPop(out string? directory))
        {
            BackupPaths.RejectLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                Require(++entries <= MaximumEntries, BackupFailure.LimitExceeded);
                BackupPaths.RejectLinks(path);
                if (Directory.Exists(path))
                {
                    pending.Push(path);
                    continue;
                }
                Require(!DatabaseExtensions.Contains(Path.GetExtension(path)), BackupFailure.UnsupportedSource);
                var file = ReadDescription(source, path);
                Require(names.Add(file.Path), BackupFailure.InvalidScope);
                bytes += file.Bytes;
                files.Add(file);
                Require(files.Count <= MaximumFiles && bytes <= MaximumTotalBytes, BackupFailure.LimitExceeded);
            }
        }

        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    // Throws BackupFault for malformed paths or oversized files; IOException if a reader cannot obtain stable bytes.
    private static BackupFile ReadDescription(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        Require(BackupPaths.ValidRelativeFile(relative), BackupFailure.InvalidScope);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        Require(length <= MaximumFileBytes, BackupFailure.LimitExceeded);
        string digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        Require(stream.Length == length, BackupFailure.SourceChanged);

        return new(relative, length, digest);
    }

    public static string Fingerprint(IReadOnlyList<BackupFile> files)
    {
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(files)));
    }

    // Throws BackupFault if the planned file changed; rethrows Exception after clearing incomplete source bytes.
    public static byte[] Read(string source, BackupFile file)
    {
        string path = Path.Combine(source, file.Path);
        BackupPaths.RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(stream.Length == file.Bytes && file.Bytes <= MaximumFileBytes, BackupFailure.SourceChanged);
        byte[] content = new byte[(int)file.Bytes];
        try
        {
            stream.ReadExactly(content);
            Require(stream.ReadByte() == -1 && Convert.ToHexStringLower(SHA256.HashData(content)) == file.Sha256,
                BackupFailure.SourceChanged);

            return content;
        }
        catch (Exception)
        {
            CryptographicOperations.ZeroMemory(content);
            throw;
        }
    }

    // Throws BackupFault for an unsuccessful internal invariant; public Try entry points retain the typed failure.
    public static void Require([DoesNotReturnIf(false)] bool condition, BackupFailure failure)
    {
        if (!condition)
        {
            throw new BackupFault(failure);
        }
    }
}
