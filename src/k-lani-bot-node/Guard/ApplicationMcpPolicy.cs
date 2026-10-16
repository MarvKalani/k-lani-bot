using System.Text.RegularExpressions;

namespace KLaniBot.Guard;

// Use case: the operator lists in policy.json the programs whose own MCP server agents may use on this node, for example
// FilmCraft's "filmcraft-cli mcp". An agent names only the program id; what runs, how its version is read and which of its
// tools count as read-only come from here and never from a request.
public sealed class ApplicationMcpPolicy
{
    private const int MaximumCallTimeoutSeconds = 3600;
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex ToolNamePattern = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant);

    public string Id { get; set; } = "";

    // The program and its arguments; the first entry is an absolute path. There is no shell and no command line string.
    public string[] Command { get; set; } = [];

    // Prints the program's version on its first output line; empty when the program cannot report one.
    public string[] VersionCommand { get; set; } = [];

    // Tools of the program's MCP server that only read. Every other tool counts as a change.
    public string[] ReadTools { get; set; } = [];

    // How long one call may take before the program is stopped; its in-memory state is lost then.
    public int CallTimeoutSeconds { get; set; } = 300;

    // The administrator's choice for this program alone, for example off for a program that works only on copies; absent
    // means the node's own requirement applies.
    public BackupRequirement? BackupRequirement
    {
        get; set;
    }

    // Throws InvalidDataException naming the first setting that is unusable or would let a request choose what runs.
    public void Validate()
    {
        Id ??= "";
        Command ??= [];
        VersionCommand ??= [];
        ReadTools ??= [];
        if (!IdPattern.IsMatch(Id))
            throw new InvalidDataException("ApplicationMcps Id must be 1 to 32 lowercase letters, digits or hyphens.");
        RequireCommand(Command, "Command");
        if (VersionCommand.Length > 0)
            RequireCommand(VersionCommand, "VersionCommand");
        if (ReadTools.Any(name => !ToolNamePattern.IsMatch(name)) || ReadTools.Distinct(StringComparer.Ordinal).Count() != ReadTools.Length)
            throw new InvalidDataException("ApplicationMcps ReadTools must be distinct tool names.");
        if (CallTimeoutSeconds is < 1 or > MaximumCallTimeoutSeconds)
            throw new InvalidDataException("ApplicationMcps CallTimeoutSeconds must be between 1 and 3600.");
        if (BackupRequirement is { } requirement && !Enum.IsDefined(requirement))
            throw new InvalidDataException("ApplicationMcps BackupRequirement must be required or off.");
    }

    public bool IsReadTool(string tool)
    {
        return ReadTools.Contains(tool, StringComparer.Ordinal);
    }

    // Throws InvalidDataException when the command is empty, relative or has an empty argument.
    private static void RequireCommand(string[] command, string name)
    {
        if (command.Length == 0 || !Path.IsPathFullyQualified(command[0]) || command.Any(string.IsNullOrEmpty))
            throw new InvalidDataException("ApplicationMcps " + name + " must start with an absolute program path.");
    }
}
