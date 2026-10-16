using KLaniBot.Common;

namespace KLaniBot.Backup;

// Unix archive publication flushes directory metadata and newly created ancestors.
// Windows native qualification remains separate.
internal static class BackupDurability
{
    // Throws BackupFault when the filesystem cannot confirm the directory metadata flush.
    public static void SyncParents(string directory)
    {
        for (string? current = directory; current != null; current = Path.GetDirectoryName(current))
        {
            FileBackupSource.Require(PrivateFile.TrySyncDirectory(current, out _), BackupFailure.StoreFailed);
        }
    }

    // Throws BackupFault or IOException when the newly written restore tree cannot be synchronized.
    public static void SyncTree(string root)
    {
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(directory => directory.Length))
        {
            FileBackupSource.Require(PrivateFile.TrySyncDirectory(directory, out _), BackupFailure.StoreFailed);
        }
        SyncParents(root);
    }
}
