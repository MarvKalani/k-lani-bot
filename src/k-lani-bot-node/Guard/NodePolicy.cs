using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KLaniBot.Actions;
using KLaniBot.Common;
using KLaniBot.Localization;
using KLaniBot.Visibility;

namespace KLaniBot.Guard;

// One identity that may call this node: an AI client, a controller or a person's tool. Only the SHA-256 of its
// bearer token is stored, so the policy file never holds a usable credential. Applications are the programs
// launch_process may start (package names on Android); like Scopes they do not accept "*", every capability is granted
// by name. Guard settings that are not stated are the guarded profile: the broad desktop profile is something the
// operator chooses, never a default.
public record PrincipalPolicy(string Id, string TokenSha256, Scope[] Scopes, string[] Applications, DateTimeOffset ExpiresAt)
{
    public UiGuardOptions UiGuard { get; init; } = new();

    // Per listed program, the argument forms launch_process accepts (see ArgumentPatterns); a program without forms takes any.
    public Dictionary<string, string[][]> ApplicationArguments { get; init; } = new();
}
// Who is asking, as established by the transport after authentication. The session id ties consent, the
// visibility indicator and end_session to one client connection.
public record RequestContext(string PrincipalId, string SessionId);

// Use case: the operator decides in a local file (policy.json, written by --init) who may use this machine,
// for what, and where the agent may read and write files. Everything an agent does passes through Authorize,
// ResolvePath or ResolveApplication. The file is outside the agent's reach: the state root must not lie
// inside the workspace, because fs_write_file could otherwise rewrite the journal, the ledger or the policy
// beside them.
//
// Principals are re-read from the file on every lookup, so the operator can revoke a token or narrow a scope
// while the node runs and the next request already sees it. Roots and the node id are fixed at start: moving
// them under a running node would split the journal and the workspace check, so that needs a restart.
public sealed class NodePolicy
{
    [System.Text.Json.Serialization.JsonIgnore] public string SourcePath { get; private set; } = "";
    public string NodeId { get; set; } = "local";
    public string WorkspaceRoot { get; set; } = "";
    public string StateRoot { get; set; } = "";
    public PrincipalPolicy[] Principals { get; set; } = [];
    public VisibilityOptions Visibility { get; set; } = new();
    // How long a finished mutating request is remembered so that a retry gets the recorded answer instead of a second OS action.
    public int OperationWindowHours { get; set; } = (int)OperationLedger.DefaultWindow.TotalHours;
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan OperationWindow => TimeSpan.FromHours(OperationWindowHours);
    public TraceRetention TraceRetention { get; set; } = TraceRetention.Rotate;
    // How much of a dialog's text is journaled next to an agent's decision.
    public DialogTextEvidence DialogTextEvidence { get; set; } = DialogTextEvidence.Excerpt;
    // When set, every click, typed text, hotkey, drag or paste must name the result it expects, so an agent waits for
    // a loading application instead of acting on a screen that is still changing.
    public bool RequirePostcondition
    {
        get; set;
    }
    // Who handles a dialog the agent asks to operate, by the class the advisor gives it.
    public DialogDeciders DialogDeciders { get; set; } = new();
    // The language of the desktop texts on this node; null means the system language.
    public UiLanguage? UiLanguage
    {
        get; set;
    }
    public string? GuardClassifierConfigFile
    {
        get; set;
    }
    // Absent means the backup tools answer that backup is not configured on this node.
    public BackupPolicy? Backup
    {
        get; set;
    }
    // Read again for every change, so an administrator's switch takes effect without a restart.
    public BackupRequirement BackupRequirement { get; set; } = BackupRequirement.Required;
    // Programs whose own MCP server agents may use; empty means the app_mcp tools are not offered.
    public ApplicationMcpPolicy[] ApplicationMcps { get; set; } = [];
    // Reads and checks policy.json without changing anything on disk.
    public static Result<NodePolicy, ReadProblem> Read(string path)
    {
        Result<NodePolicy, ReadProblem> parsed = Parse(path);

        return parsed.Match(policy => Validate(policy, path), problem => problem);
    }

    // Throws InvalidDataException naming the problem. Creates the state directory of a policy that passed.
    public static NodePolicy Load(string path)
    {
        NodePolicy policy = Read(path).Match(valid => valid, problem => throw new InvalidDataException(problem.Describe()));

        Directory.CreateDirectory(policy.StateRoot);
        return policy;
    }

    private static Result<NodePolicy, ReadProblem> Validate(NodePolicy policy, string path)
    {
        policy.SourcePath = Path.GetFullPath(path);
        policy.WorkspaceRoot = Path.GetFullPath(policy.WorkspaceRoot);
        policy.StateRoot = Path.GetFullPath(policy.StateRoot);

        ReadProblem? problem = SettingsProblem(policy) ?? RootsProblem(policy) ?? PrincipalsProblem(policy) ?? ClassifierProblem(policy)
            ?? BackupProblem(policy) ?? ApplicationMcpProblem(policy);
        return problem == null ? policy : problem;
    }

    private static ReadProblem? SettingsProblem(NodePolicy policy)
    {
        if (!Enum.IsDefined(policy.TraceRetention))
            return Refused("TraceRetention must be rotate or stop.");
        if (!Enum.IsDefined(policy.BackupRequirement))
            return Refused("BackupRequirement must be required or off.");
        if (!Enum.IsDefined(policy.DialogTextEvidence))
            return Refused("DialogTextEvidence must be excerpt or hash_only.");
        if (policy.DialogDeciders == null || !policy.DialogDeciders.IsValid())
            return Refused("DialogDeciders must name agent, person or blocked for every dialog class.");
        if (policy.OperationWindow < OperationLedger.MinimumWindow || policy.OperationWindow > OperationLedger.MaximumWindow)
            return Refused("OperationWindowHours must be between 1 and 720.");
        return RefusalOf(policy.Visibility.Validate);
    }

    private static ReadProblem? RootsProblem(NodePolicy policy)
    {
        bool stateIsInsideWorkspace = policy.StateRoot == policy.WorkspaceRoot
            || policy.StateRoot.StartsWith(policy.WorkspaceRoot + Path.DirectorySeparatorChar, PathComparison);
        if (!Directory.Exists(policy.WorkspaceRoot) || stateIsInsideWorkspace)
            return Refused("State must be outside the agent workspace; workspace must exist.");
        return null;
    }

    private static ReadProblem? PrincipalsProblem(NodePolicy policy)
    {
        if (policy.Principals.Length == 0
            || HasDuplicateIdentities(policy.Principals)
            || policy.Principals.Any(GrantsWildcardOrHasMalformedTokenHash))
            return Refused("Policy requires distinct principals and explicit capabilities.");
        foreach (var principal in policy.Principals)
        {
            if (RefusalOf(() => RequireGuardSettings(principal)) is { } guardProblem)
                return guardProblem;
            if (RefusalOf(() => ArgumentPatterns.Validate(principal)) is { } argumentProblem)
                return argumentProblem;
        }
        return null;
    }

    // The classifier configuration decides which service judges the agent's actions. If the agent could
    // write it, or a link inside the workspace could point at it, the agent could choose its own judge.
    private static ReadProblem? ClassifierProblem(NodePolicy policy)
    {
        if (policy.GuardClassifierConfigFile == null)
            return null;
        policy.GuardClassifierConfigFile = Path.GetFullPath(policy.GuardClassifierConfigFile);
        bool classifierIsInsideWorkspace = policy.GuardClassifierConfigFile == policy.WorkspaceRoot
            || policy.GuardClassifierConfigFile.StartsWith(policy.WorkspaceRoot + Path.DirectorySeparatorChar, PathComparison);
        if (classifierIsInsideWorkspace)
            return Refused("Guard classifier configuration must be outside the agent workspace.");
        try
        {
            for (string? part = policy.GuardClassifierConfigFile; part != null; part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    return Refused("Guard classifier configuration links are forbidden.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ReadProblem(ReadProblemKind.Unreadable, "Guard classifier configuration: " + exception.GetType().Name);
        }
        return null;
    }

    private static ReadProblem? BackupProblem(NodePolicy policy)
    {
        if (policy.Backup == null)
            return null;
        return RefusalOf(() => policy.Backup.Validate(policy.WorkspaceRoot, policy.StateRoot, policy.SourcePath));
    }

    private static ReadProblem? ApplicationMcpProblem(NodePolicy policy)
    {
        policy.ApplicationMcps ??= [];
        if (policy.ApplicationMcps.Any(program => program == null))
            return Refused("ApplicationMcps entries must be objects.");
        if (policy.ApplicationMcps.Select(program => program.Id).Distinct(StringComparer.Ordinal).Count() != policy.ApplicationMcps.Length)
            return Refused("ApplicationMcps must have distinct ids.");
        foreach (ApplicationMcpPolicy program in policy.ApplicationMcps)
        {
            if (RefusalOf(program.Validate) is { } problem)
                return problem;
        }
        return null;
    }

    public ApplicationMcpPolicy? FindApplicationMcp(string id)
    {
        return ApplicationMcps.FirstOrDefault(program => program.Id == id);
    }

    private static ReadProblem Refused(string detail)
    {
        return new ReadProblem(ReadProblemKind.Refused, detail);
    }

    // The settings types check themselves by throwing InvalidDataException; here that becomes a problem of the file.
    private static ReadProblem? RefusalOf(Action check)
    {
        try
        {
            check();
            return null;
        }
        catch (InvalidDataException exception)
        {
            return Refused(exception.Message);
        }
    }

    // Throws UnauthorizedAccessException when the file became unreadable or its roots changed.
    private PrincipalPolicy[] CurrentPrincipals()
    {
        if (SourcePath == "")
            return Principals;
        // A policy that became unreadable while the node runs denies the request instead of failing it.
        NodePolicy current = Parse(SourcePath).Match(
            parsed => parsed,
            problem => throw new UnauthorizedAccessException("Policy unavailable: " + problem.Describe()));
        bool rootsChanged = current.NodeId != NodeId
            || Path.GetFullPath(current.WorkspaceRoot) != WorkspaceRoot
            || Path.GetFullPath(current.StateRoot) != StateRoot
            || (Backup == null ? current.Backup != null : !Backup.SameRootsAndKey(current.Backup));
        if (rootsChanged)
            throw new UnauthorizedAccessException("Root changes require a controlled restart.");
        return current.Principals;
    }
    // Throws UnauthorizedAccessException when the policy file became unreadable; the change is then refused. A program's own
    // setting replaces the node's for changes made through that program's MCP server.
    public BackupRequirement CurrentBackupRequirement(string? applicationMcp = null)
    {
        NodePolicy current = SourcePath == "" ? this : Parse(SourcePath).Match(
            parsed => parsed,
            problem => throw new UnauthorizedAccessException("Policy unavailable: " + problem.Describe()));
        BackupRequirement? program = applicationMcp == null ? null
            : (current.ApplicationMcps ?? []).FirstOrDefault(entry => entry?.Id == applicationMcp)?.BackupRequirement;
        BackupRequirement chosen = program ?? current.BackupRequirement;
        return Enum.IsDefined(chosen) ? chosen : BackupRequirement.Required;
    }

    // Throws UnauthorizedAccessException for duplicate identities or an expired or revoked principal.
    public PrincipalPolicy Get(string id)
    {
        var principals = CurrentPrincipals();
        if (HasDuplicateIdentities(principals))
            throw new UnauthorizedAccessException("Shared or duplicate principal identities are forbidden.");
        var selected = principals.SingleOrDefault(p => p.Id == id && p.ExpiresAt > DateTimeOffset.UtcNow)
            ?? throw new UnauthorizedAccessException("Principal expired or revoked.");
        RequireGuardSettings(selected);
        return selected;
    }
    // Throws UnauthorizedAccessException for a token that matches no principal.
    public string Authenticate(string token)
    {
        if (token.Length < 32 || token.Length > 256)
            throw new UnauthorizedAccessException("Invalid credential.");
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        foreach (var principal in CurrentPrincipals())
            if (CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(principal.TokenSha256)))
                return Get(principal.Id).Id;
        throw new UnauthorizedAccessException("Invalid credential.");
    }
    // Throws UnauthorizedAccessException for an invalid session id or a scope the principal lacks.
    public void Authorize(RequestContext context, Scope scope)
    {
        if (context.SessionId.Length is < 16 or > 128 || !context.SessionId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new UnauthorizedAccessException("Invalid session.");
        if (!Get(context.PrincipalId).Scopes.Contains(scope))
            throw new UnauthorizedAccessException($"Capability '{WireNames<Scope>.Of(scope)}' denied.");
    }
    // Throws UnauthorizedAccessException for a path outside the workspace or through a link.
    public string ResolvePath(string? input)
    {
        string full = Path.GetFullPath(input ?? ".", WorkspaceRoot);
        if (full != WorkspaceRoot && !full.StartsWith(WorkspaceRoot + Path.DirectorySeparatorChar, PathComparison))
            throw new UnauthorizedAccessException("Path is outside the approved workspace.");
        // Reject every link component, including junctions and dangling links. The workspace
        // must also be owned by the isolated worker account: this check is not an OS sandbox.
        for (string? part = full; part != null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Links are not permitted.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }
    // Throws UnauthorizedAccessException for a program not on the principal's allow-list.
    public string ResolveApplication(RequestContext context, string path)
    {
        string full = Path.GetFullPath(path);
        if (!Get(context.PrincipalId).Applications.Any(a => string.Equals(Path.GetFullPath(a), full, PathComparison)))
            throw new UnauthorizedAccessException("Executable is not allowlisted.");
        return full;
    }

    // Throws UnauthorizedAccessException when the program has argument forms and the arguments match none of them.
    public void AuthorizeArguments(RequestContext context, string program, IReadOnlyList<string> arguments)
    {
        string[][]? forms = ArgumentPatterns.FormsFor(Get(context.PrincipalId), program);

        if (forms != null && !forms.Any(form => ArgumentPatterns.Matches(form, arguments)))
            throw new UnauthorizedAccessException("These arguments are not allowed for this program.");
    }

    private static readonly JsonSerializerOptions PolicyJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static Result<NodePolicy, ReadProblem> Parse(string path)
    {
        try
        {
            NodePolicy? policy = JsonSerializer.Deserialize<NodePolicy>(File.ReadAllText(path), PolicyJsonOptions);
            return policy == null ? new ReadProblem(ReadProblemKind.NotValid, "The policy is empty.") : policy;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // No path: at run time this text reaches the AI client.
            return new ReadProblem(ReadProblemKind.Unreadable, "policy file: " + exception.GetType().Name);
        }
        catch (JsonException exception)
        {
            return new ReadProblem(ReadProblemKind.NotValid, "Policy is not valid JSON for this node: " + exception.Message);
        }
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // JSON can still set the property to null ("uiGuard": null). That is refused rather than read as a profile.
    // Throws InvalidDataException when the guard settings are null or invalid.
    private static void RequireGuardSettings(PrincipalPolicy principal)
    {
        if (principal.UiGuard is null)
            throw new InvalidDataException("Guard settings of a principal must not be null; omit them for the guarded profile.");
        principal.UiGuard.Validate();
    }

    // Two principals sharing an id or a token hash would make a credential ambiguous about who is acting.
    private static bool HasDuplicateIdentities(PrincipalPolicy[] principals)
    {
        bool duplicateIds = principals.Select(p => p.Id).Distinct().Count() != principals.Length;
        bool duplicateTokens = principals.Select(p => p.TokenSha256).Distinct().Count() != principals.Length;
        return duplicateIds || duplicateTokens;
    }

    private static bool GrantsWildcardOrHasMalformedTokenHash(PrincipalPolicy principal)
    {
        bool malformedHash = !System.Text.RegularExpressions.Regex.IsMatch(principal.TokenSha256, "^[0-9a-f]{64}$");
        return malformedHash || principal.Applications.Contains("*");
    }
}
