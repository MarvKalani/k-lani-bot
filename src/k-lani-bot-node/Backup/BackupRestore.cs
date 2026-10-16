using KLaniBot.Common;

namespace KLaniBot.Backup;

// Recovery writes and verifies a private staging directory, then publishes only to a new name under the trusted recovery root.
internal static class BackupRestore
{
    // Throws BackupFault for an existing or invalid destination; IOException for unavailable storage or failed publication.
    public static BackupRestoreResult Create(string root, string name, FileBackupPayload payload)
    {
        FileBackupSource.Require(BackupPaths.ValidDestination(name), BackupFailure.InvalidScope);
        BackupPaths.RejectLinks(root);
        string destination = Path.Combine(root, name);
        FileBackupSource.Require(!Path.Exists(destination), BackupFailure.DestinationExists);
        PrivateFile.CreateDirectory(root);
        string staging = Path.Combine(root, "stage-" + Guid.CreateVersion7());
        PrivateFile.CreateDirectory(staging);
        try
        {
            WriteFiles(staging, payload.Files);
            FileBackupSource.Require(FileBackupSource.Fingerprint(FileBackupSource.Scan(staging)) == payload.Fingerprint,
                BackupFailure.InvalidArchive);
            BackupDurability.SyncTree(staging);
            Directory.Move(staging, destination);
            BackupDurability.SyncParents(root);

            return new(destination, payload.Files.Count, payload.Fingerprint);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    // Throws IOException when the isolated recovery probe cannot be written, reread or removed.
    public static void Qualify(string root, FileBackupPayload payload)
    {
        var restored = Create(root, "verify-" + Guid.CreateVersion7(), payload);
        Directory.Delete(restored.Directory, true);
        BackupDurability.SyncParents(root);
    }

    // Throws BackupFault for an invalid relative name and IOException for a failed private file write.
    private static void WriteFiles(string root, IReadOnlyList<BackupFileContent> files)
    {
        foreach (var entry in files)
        {
            FileBackupSource.Require(BackupPaths.ValidRelativeFile(entry.File.Path), BackupFailure.InvalidArchive);
            string path = Path.Combine(root, entry.File.Path);
            PrivateFile.CreateDirectory(Path.GetDirectoryName(path)!);
            PrivateFile.WriteAllBytes(path, entry.Content);
        }
    }
}
