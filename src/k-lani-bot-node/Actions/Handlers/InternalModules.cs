using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using KLaniBot.Common;
using KLaniBot.Guard;

namespace KLaniBot.Actions.Handlers;

// The fs_* tools read and write files for the agent here. Every path goes through the policy first, so nothing outside the workspace or
// through a link is touched; failures are answers, not exceptions.
public class FileSystemModule(NodePolicy policy)
{
    private const int MaximumListedDirectories = 200;
    private const int MaximumListedFiles = 300;
    private const int DefaultReadCharacters = 65536;
    private const int MaximumReadCharacters = 1048576;
    private const int MaximumSearchDepth = 5;
    private const string TimestampFormat = "s";

    public object ListDirectory(string? path)
    {
        string targetPath = string.IsNullOrWhiteSpace(path)
            ? policy.WorkspaceRoot
            : policy.ResolvePath(path);

        if (!Directory.Exists(targetPath))
        {
            return Failure($"Directory does not exist: {targetPath}");
        }

        try
        {
            var dirInfo = new DirectoryInfo(targetPath);
            var entries = new List<object>();
            foreach (var dir in dirInfo.EnumerateDirectories().Take(MaximumListedDirectories))
            {
                entries.Add(DirectoryEntry(dir));
            }
            foreach (var file in dirInfo.EnumerateFiles().Take(MaximumListedFiles))
            {
                entries.Add(FileEntry(file));
            }

            return new
            {
                success = true,
                currentDirectory = targetPath,
                totalCount = entries.Count,
                entries
            };
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
    }

    public object ReadFile(string? path, int? offset = null, int? length = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure("Path must not be empty.");
        }
        string targetPath = policy.ResolvePath(path);
        if (!File.Exists(targetPath))
        {
            return Failure($"File not found: {targetPath}");
        }

        try
        {
            var fileInfo = new FileInfo(targetPath);
            using var reader = new StreamReader(targetPath, Encoding.UTF8);
            int skip = offset ?? 0;
            char[] buffer = new char[Math.Min(length ?? DefaultReadCharacters, MaximumReadCharacters)];
            while (skip-- > 0 && reader.Read() != -1)
            {
            }
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            string content = new(buffer, 0, read);
            return new
            {
                success = true,
                path = targetPath,
                sizeBytes = fileInfo.Length,
                content
            };
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
    }

    public object WriteFile(string? path, string? content, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure("Path must not be empty.");
        }
        string targetPath = policy.ResolvePath(path);

        try
        {
            if (File.Exists(targetPath) && !overwrite)
            {
                return Failure($"File already exists. Set overwrite=true to replace it: {targetPath}");
            }
            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            ReplaceThroughTemporaryFile(targetPath, content ?? "", overwrite);
            var info = new FileInfo(targetPath);

            return new
            {
                success = true,
                message = "File written.",
                path = targetPath,
                sizeBytes = info.Length
            };
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
    }

    // The content is written beside the target and moved over it, so a reader never sees half a file. The path is
    // resolved again right before the move, in case a link appeared in the meantime.
    private void ReplaceThroughTemporaryFile(string targetPath, string content, bool overwrite)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(targetPath)!, ".k-lani-bot-" + Guid.NewGuid());
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            policy.ResolvePath(targetPath);
            File.Move(temporary, targetPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public object SearchFiles(string? path, string? pattern, int maxResults = 50)
    {
        string targetPath = string.IsNullOrWhiteSpace(path)
            ? policy.WorkspaceRoot
            : policy.ResolvePath(path);
        string searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
        if (!Directory.Exists(targetPath))
        {
            return Failure($"Directory does not exist: {targetPath}");
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MaxRecursionDepth = MaximumSearchDepth
            };
            var results = new List<object>();
            foreach (var file in Directory.EnumerateFiles(targetPath, searchPattern, options).Take(maxResults))
            {
                results.Add(SearchHit(new FileInfo(file)));
            }

            return new
            {
                success = true,
                searchPath = targetPath,
                pattern = searchPattern,
                count = results.Count,
                files = results
            };
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
    }

    public object GetMetadata(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure("Path must not be empty.");
        }
        string targetPath = policy.ResolvePath(path);
        bool isFile = File.Exists(targetPath);
        bool isDir = Directory.Exists(targetPath);
        if (!isFile && !isDir)
        {
            return new
            {
                success = false,
                exists = false,
                path = targetPath
            };
        }

        try
        {
            var fsi = isFile ? (FileSystemInfo)new FileInfo(targetPath) : new DirectoryInfo(targetPath);
            return new
            {
                success = true,
                exists = true,
                path = targetPath,
                isDirectory = isDir,
                sizeBytes = isFile ? ((FileInfo)fsi).Length : 0,
                creationTime = fsi.CreationTime.ToString(TimestampFormat),
                lastWriteTime = fsi.LastWriteTime.ToString(TimestampFormat),
                attributes = fsi.Attributes.ToString()
            };
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
    }

    private static object DirectoryEntry(DirectoryInfo dir)
    {
        return new
        {
            name = dir.Name,
            path = dir.FullName,
            isDirectory = true,
            lastModified = dir.LastWriteTime.ToString(TimestampFormat)
        };
    }

    private static object FileEntry(FileInfo file)
    {
        return new
        {
            name = file.Name,
            path = file.FullName,
            isDirectory = false,
            size = file.Length,
            lastModified = file.LastWriteTime.ToString(TimestampFormat)
        };
    }

    private static object SearchHit(FileInfo file)
    {
        return new
        {
            name = file.Name,
            path = file.FullName,
            size = file.Length,
            lastModified = file.LastWriteTime.ToString(TimestampFormat)
        };
    }

    private static object Failure(string error)
    {
        return new
        {
            success = false,
            error
        };
    }
}

// The program that runs a command and the arguments that hand the command to it.
public sealed record ShellInvocation(string Executable, string[] Arguments);

// run_terminal_command runs here, with the platform's own shell and the workspace as working directory, after scope and guard allowed it.
public class TerminalModule(string workspaceRoot)
{
    // Throws ArgumentException for a shell the platform does not offer and PlatformNotSupportedException for an unknown platform.
    public static ShellInvocation SelectShell(string platform, string shell, string command)
    {
        if (platform is "linux" or "macos")
        {
            string selected = shell == "auto" ? platform == "macos" ? "zsh" : "bash" : shell;
            if (selected is not ("sh" or "bash" or "zsh") || platform == "linux" && selected == "zsh")
                throw new ArgumentException("Shell is not supported by this native platform.");
            return new ShellInvocation("/bin/" + selected, ["-c", command]);
        }
        if (platform != "windows")
            throw new PlatformNotSupportedException("Unsupported shell platform.");
        return shell switch
        {
            "cmd" => new ShellInvocation("cmd.exe", ["/d", "/s", "/c", command]),
            "auto" or "powershell" => new ShellInvocation("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command]),
            _ => throw new ArgumentException("Shell is not supported by this native platform.")
        };
    }
    public object RunCommand(string? command, string shell = "auto", int timeoutMs = 15000)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new
            {
                success = false,
                error = "Command is required."
            };
        var selected = SelectShell(
            OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "unknown",
            shell,
            command);
        var result = ProcessRunner.Run(selected.Executable, selected.Arguments, timeoutMs, workspaceRoot);
        return new
        {
            success = result.Success,
            stdout = result.Output,
            stderr = result.Error,
            exitCode = result.ExitCode
        };
    }
}
