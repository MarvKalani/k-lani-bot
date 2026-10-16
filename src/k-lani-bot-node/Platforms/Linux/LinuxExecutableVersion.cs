using System.Text.RegularExpressions;
using KLaniBot.Common;

namespace KLaniBot.Platforms.Linux;

// A running application's executable is bound to one installed Debian package and its actual package version, never its process caption.
public sealed record LinuxPackageBuild(string Package, string Version);

// Window identity reads use the system package database without launching applications or trusting user-provided version labels.
public sealed class LinuxExecutableVersion
{
    private const string QueryExecutable = "/usr/bin/dpkg-query";
    private const int QueryDeadlineMilliseconds = 3000;
    private const int MaximumMetadataLength = 128;
    private static readonly Regex PackageName = new(@"\A[a-z0-9][a-z0-9+.-]*(?::[a-z0-9][a-z0-9-]*)?\z", RegexOptions.CultureInvariant);
    private static readonly Regex Version = new(@"\A[0-9][a-zA-Z0-9.+:~\-]*\z", RegexOptions.CultureInvariant);
    private const string ApplicationIniFileName = "application.ini";
    private const string AppSectionHeader = "[App]";
    private const string NameKey = "Name=";
    private const string VersionKey = "Version=";
    private const long MaximumApplicationIniBytes = 64 * 1024;
    private static readonly Regex InterpreterName = new(@"\A(python(3(\.[0-9]+)?)?|perl|ruby)\z", RegexOptions.CultureInvariant);
    private static readonly Regex ApplicationName = new(@"\A[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}\z", RegexOptions.CultureInvariant);
    private readonly Func<IReadOnlyList<string>, ProcessResult> _query;
    private readonly Func<string, string?> _resolve;
    private readonly Func<string, string?> _readText;

    public LinuxExecutableVersion()
        : this(arguments => ProcessRunner.Run(QueryExecutable, arguments, QueryDeadlineMilliseconds), ResolveExecutable)
    {
    }

    public LinuxExecutableVersion(
        Func<IReadOnlyList<string>, ProcessResult> query,
        Func<string, string?> resolve,
        Func<string, string?>? readText = null)
    {
        _query = query;
        _resolve = resolve;
        _readText = readText ?? ReadApplicationIni;
    }

    public bool TryRead(string? executable, out LinuxPackageBuild? build)
    {
        return TryRead(executable, Array.Empty<string>(), out build);
    }

    // The command line decides for interpreters: the interpreter's package says nothing about the script it runs.
    public bool TryRead(string? executable, IReadOnlyList<string> commandLine, out LinuxPackageBuild? build)
    {
        build = null;
        try
        {
            string? path = ResolveFullyQualified(executable);
            if (path == null)
            {
                return false;
            }
            if (InterpreterName.IsMatch(Path.GetFileName(path)))
            {
                return TryReadScript(commandLine, out build);
            }

            return TryReadPackage(path, out build) || TryReadApplicationIni(path, out build);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string? ResolveFullyQualified(string? executable)
    {
        if (string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable))
        {
            return null;
        }
        string? path = _resolve(executable);
        if (path == null || !Path.IsPathFullyQualified(path) || path.Contains('\n') || path.Contains('\r'))
        {
            return null;
        }

        return path;
    }

    private bool TryReadScript(IReadOnlyList<string> commandLine, out LinuxPackageBuild? build)
    {
        build = null;
        string? script = commandLine.Skip(1).FirstOrDefault(argument => !argument.StartsWith('-'));
        string? path = ResolveFullyQualified(script);

        return path != null && TryReadPackage(path, out build);
    }

    private bool TryReadPackage(string path, out LinuxPackageBuild? build)
    {
        build = null;
        var owner = _query(new[] { "-S", "--", path });
        if (!owner.Success || !TryPackage(owner.Output, path, out string? package))
        {
            return false;
        }
        var version = _query(new[] { "-W", "-f=${binary:Package}\t${db:Status-Abbrev}\t${Version}\n", "--", package! });

        return version.Success && TryBuild(version.Output, package!, out build);
    }

    // Mozilla archives install no package; their application.ini names the product and its version next to the binary.
    // The record then carries the application name in its Package field.
    private bool TryReadApplicationIni(string executablePath, out LinuxPackageBuild? build)
    {
        build = null;
        string? directory = Path.GetDirectoryName(executablePath);
        if (directory == null)
        {
            return false;
        }
        string? text = _readText(Path.Combine(directory, ApplicationIniFileName));

        return text != null && TryParseApplicationIni(text, out build);
    }

    private static bool TryParseApplicationIni(string text, out LinuxPackageBuild? build)
    {
        build = null;
        string? name = null;
        string? version = null;
        bool inAppSection = false;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                inAppSection = line == AppSectionHeader;
            }
            else if (inAppSection && line.StartsWith(NameKey, StringComparison.Ordinal))
            {
                if (name != null)
                {
                    return false;
                }
                name = line[NameKey.Length..].Trim();
            }
            else if (inAppSection && line.StartsWith(VersionKey, StringComparison.Ordinal))
            {
                if (version != null)
                {
                    return false;
                }
                version = line[VersionKey.Length..].Trim();
            }
        }
        if (name == null || version == null || !ApplicationName.IsMatch(name)
            || version.Length > MaximumMetadataLength || !Version.IsMatch(version))
        {
            return false;
        }
        build = new(name, version);

        return true;
    }

    private static bool TryPackage(string output, string executable, out string? package)
    {
        package = null;
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.LastIndexOf(": ", StringComparison.Ordinal);
            if (separator < 1 || line[(separator + 2)..] != executable)
            {
                return false;
            }
            string owner = line[..separator];
            if (owner.Length > MaximumMetadataLength || !PackageName.IsMatch(owner))
            {
                return false;
            }
            owners.Add(owner);
        }
        if (owners.Count != 1)
        {
            return false;
        }
        package = owners.Single();

        return true;
    }

    private static bool TryBuild(string output, string package, out LinuxPackageBuild? build)
    {
        build = null;
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1)
        {
            return false;
        }
        string[] fields = lines[0].Split('\t');
        if (fields.Length != 3 || fields[0] != package || fields[1] != "ii " || fields[2].Length > MaximumMetadataLength
            || !Version.IsMatch(fields[2]))
        {
            return false;
        }
        build = new(package, fields[2]);

        return true;
    }

    private static string? ResolveExecutable(string executable)
    {
        if (!File.Exists(QueryExecutable) || !File.Exists(executable))
        {
            return null;
        }
        var file = new FileInfo(executable);
        if (file.LinkTarget != null)
        {
            file = file.ResolveLinkTarget(true) as FileInfo;
        }

        return file?.Exists == true ? file.FullName : null;
    }

    private static string? ReadApplicationIni(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumApplicationIniBytes)
            {
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
