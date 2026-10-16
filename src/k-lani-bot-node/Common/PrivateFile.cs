using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KLaniBot.Common;

// Use case: the node keeps secrets and evidence on disk (audit key and journal, TLS keys, credentials,
// pairing state, screenshots). Other local users must not be able to read them, not even during the
// moment between creating a file and restricting it.
//
// Creating the file and then calling SetUnixFileMode leaves exactly that moment open, and it is
// easy to forget for one of the many writers. Every writer of private state goes through here, so
// the owner-only mode is part of the creating open() call itself.
//
// Windows relies on the ACL of the private node state directory (see PairingClient.CreatePrivateDirectory),
// because a per-file mode does not exist there.
public static class PrivateFile
{
    // A caller distinguishes an inaccessible directory from a failed metadata flush without logging private paths.
    public enum DirectorySyncFailure
    {
        None,
        OpenFailed,
        FlushFailed
    }

    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const int OpenReadOnly = 0;
    private const UnixFileMode OwnerAll = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode GroupAndOthers = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static FileStream OpenWrite(string path, FileMode mode, FileShare share = FileShare.None)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = share
        };
        bool createsFiles = mode is FileMode.CreateNew or FileMode.Create or FileMode.OpenOrCreate or FileMode.Append;
        if (createsFiles && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerReadWrite;
        }
        var stream = new FileStream(path, options);
        RestrictExistingFile(path);
        return stream;
    }

    public static void WriteAllBytes(string path, byte[] content, bool replace = false)
    {
        using var stream = OpenWrite(path, replace ? FileMode.Create : FileMode.CreateNew);
        stream.Write(content);
        stream.Flush(true);
    }

    public static void WriteAllText(string path, string content, bool replace = false)
    {
        WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content), replace);
    }

    // The new content becomes visible in one step, so a reader (or a crash) sees the old file or the
    // new one, never half of a write. The temporary file is private from its first byte as well.
    public static void WriteAllBytesAtomically(string path, byte[] content)
    {
        string temporary = path + "." + Guid.CreateVersion7() + ".tmp";
        try
        {
            WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    // Only the last path component is created with the private mode. Parents keep the caller's
    // defaults: a state directory below a shared parent must not turn that parent private.
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent != null)
        {
            Directory.CreateDirectory(parent);
        }
        Directory.CreateDirectory(path, OwnerAll);
        if (File.GetUnixFileMode(path) != OwnerAll)
        {
            File.SetUnixFileMode(path, OwnerAll);
        }
    }

    // A file that already existed keeps the mode it was created with. Tighten it instead of trusting it,
    // so an older node version or a manual copy cannot leave state readable by other users.
    private static void RestrictExistingFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        if ((File.GetUnixFileMode(path) & GroupAndOthers) != 0)
        {
            File.SetUnixFileMode(path, OwnerReadWrite);
        }
    }

    public static bool TrySyncDirectory(string directory, out DirectorySyncFailure failure)
    {
        failure = DirectorySyncFailure.None;
        if (OperatingSystem.IsWindows())
        {
            return true;
        }
        try
        {
            failure = DirectorySyncFailure.OpenFailed;
            int descriptor = OpenDirectory(directory, OpenReadOnly);
            if (descriptor < 0)
            {
                return false;
            }
            using var handle = new SafeFileHandle((IntPtr)descriptor, true);
            failure = DirectorySyncFailure.FlushFailed;
            if (SyncDirectory(descriptor) != 0)
            {
                return false;
            }
            failure = DirectorySyncFailure.None;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int SyncDirectory(int descriptor);
}
