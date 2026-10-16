using System.Security.Cryptography;

namespace KLaniBot.Backup;

// Decrypted content is validated independently of the envelope before any filename can become a restore target.
internal static class BackupPayload
{
    // Throws BackupFault for mismatched scope, unsupported contents, duplicates or modified file data.
    public static void Validate(FileBackupPayload payload, Guid id, string source)
    {
        FileBackupSource.Require(payload.Format == 1 && payload.Id == id && payload.RestoreVerified
            && payload.Source != null && BackupPaths.Same(payload.Source, source)
            && payload.Files != null && payload.Files.Count <= FileBackupSource.MaximumFiles, BackupFailure.InvalidArchive);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var entry in payload.Files!)
        {
            FileBackupSource.Require(entry?.File != null && entry.Content != null && entry.File.Path != null
                && BackupPaths.ValidRelativeFile(entry.File.Path) && names.Add(entry.File.Path), BackupFailure.InvalidArchive);
            BackupFile file = entry!.File;
            FileBackupSource.Require(file.Bytes >= 0 && file.Bytes <= FileBackupSource.MaximumFileBytes
                && entry.Content.LongLength == file.Bytes
                && Convert.ToHexStringLower(SHA256.HashData(entry.Content)) == file.Sha256, BackupFailure.InvalidArchive);
            bytes += file.Bytes;
        }
        var files = payload.Files.Select(entry => entry.File).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        FileBackupSource.Require(bytes <= FileBackupSource.MaximumTotalBytes
            && FileBackupSource.Fingerprint(files) == payload.Fingerprint, BackupFailure.InvalidArchive);
    }

    public static void Clear(FileBackupPayload? payload)
    {
        foreach (var entry in payload?.Files ?? [])
        {
            if (entry?.Content != null)
            {
                CryptographicOperations.ZeroMemory(entry.Content);
            }
        }
    }
}
