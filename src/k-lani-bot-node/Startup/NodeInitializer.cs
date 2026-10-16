using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Common;
using KLaniBot.Guard;
using KLaniBot.Localization;
using KLaniBot.Transport;
using KLaniBot.Visibility;

namespace KLaniBot.Startup;

// Use case: an operator sets up a node on this machine with --init. It writes a fresh private state directory with the
// policy, the operator credential, the TLS certificates, the registry entry and the worker command. Existing state is never
// overwritten, so a mistyped command cannot replace credentials that controllers already use.
public static class NodeInitializer
{
    private const string DefaultBindAddress = "127.0.0.1";
    private const int DefaultPort = 5443;
    private const string SandboxPolicyPath = "/run/k-lani-bot-policy.json";
    private const uint WindowsSandboxUserId = 10001;
    private static readonly TimeSpan CredentialLifetime = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonFormat = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    // The user and group the actuator container runs as on Linux.
    private static class UnixUser
    {
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")] public static extern uint GetUid();
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getgid")] public static extern uint GetGid();
    }

    // Everything --init needs, checked and read from the command line.
    private sealed record Request(
        string Workspace,
        string State,
        string Node,
        string Bind,
        IPAddress Address,
        int Port,
        Scope[] Scopes,
        VisibilityOptions Visibility,
        UiLanguage? UiLanguage,
        int OperationWindowHours,
        UiGuardOptions UiGuard,
        string? ClassifierConfigFile,
        string[] AllowedApplications,
        string? SandboxImageDigest,
        SandboxResources SandboxResources,
        IReadOnlyList<string> DeviceArguments,
        BackupRequirement BackupRequirement,
        NodeArguments Arguments);

    // Throws ArgumentException when an option is missing or invalid or the state directory is not empty.
    public static Task InitializeAsync(string[] args)
    {
        Request request = ReadRequest(new NodeArguments(args));

        PrepareDirectories(request);
        string credential = CreateCredential();
        NodePolicy policy = CreatePolicy(request, credential);
        WritePrivate(request, "policy.json", JsonSerializer.Serialize(policy, JsonFormat));
        WritePrivate(request, "credential", credential);
        WriteRegistry(request);
        string workerPolicyPath = WriteWorkerPolicy(request, policy);
        List<string> workerCommand = BuildWorkerCommand(request, workerPolicyPath);
        WriteNodeConfiguration(request, workerCommand);

        PrintSummary(request);
        return Task.CompletedTask;
    }

    // Every option is checked before anything is written, so a mistake leaves no half-made state behind.
    // Throws ArgumentException naming the first missing or invalid option.
    private static Request ReadRequest(NodeArguments arguments)
    {
        string workspace = Path.GetFullPath(arguments.Option("--workspace") ?? throw new ArgumentException("--workspace required."));
        string state = Path.GetFullPath(arguments.Option("--state") ?? throw new ArgumentException("--state required."));
        string node = NodeName.Require(arguments.Option("--node")
            ?? NodeName.SuggestForThisComputer()
            ?? throw new ArgumentException("--node required: this computer's name gives no usable node name."));
        string bind = arguments.Option("--bind") ?? DefaultBindAddress;
        int port = arguments.GetPort("--port", DefaultPort);
        Scope[] scopes = ReadScopes(arguments.Option("--scopes") ?? "observe");

        RequireSeparateRootsAndValidAddress(workspace, state, bind);
        RequireEmptyState(state);
        RequireClassifierOutsideSandbox(arguments);

        return new Request(
            workspace,
            state,
            node,
            bind,
            IPAddress.Parse(bind),
            port,
            scopes,
            ReadVisibility(arguments),
            ReadUiLanguage(arguments),
            arguments.GetIntInRange("--operation-window-hours", (int)OperationLedger.DefaultWindow.TotalHours, 1, 720),
            ReadUiGuard(arguments),
            arguments.Option("--guard-classifier") is { } classifier ? Path.GetFullPath(classifier) : null,
            arguments.Values("--application").Select(Path.GetFullPath).Concat(arguments.Values("--package")).ToArray(),
            ReadSandboxImageDigest(arguments),
            SandboxResources.Load(arguments.Option("--sandbox-resources")),
            ReadDeviceArguments(arguments),
            ReadBackupRequirement(arguments.Option("--backup-requirement")),
            arguments);
    }

    // Required unless the administrator writes off explicitly; any other word is refused before anything is written.
    // Throws ArgumentException for a word that is neither required nor off.
    private static BackupRequirement ReadBackupRequirement(string? word)
    {
        return word switch
        {
            null or "required" => BackupRequirement.Required,
            "off" => BackupRequirement.Off,
            _ => throw new ArgumentException("--backup-requirement must be required or off.")
        };
    }

    // Throws ArgumentException for a word that is no scope, for eval and the backup scopes (granted only by editing the
    // policy, which also names the backup roots) and for a list without observe, which every session needs.
    private static Scope[] ReadScopes(string commaSeparated)
    {
        var scopes = new List<Scope>();
        foreach (string word in commaSeparated.Split(','))
        {
            if (!WireNames<Scope>.TryParse(word, out Scope scope)
                || scope is Scope.Eval or Scope.BackupRead or Scope.BackupCreate or Scope.BackupRestore
                    or Scope.ApplicationRead or Scope.ApplicationCall)
            {
                throw new ArgumentException("Unknown scope '" + word + "' in --scopes.");
            }
            scopes.Add(scope);
        }

        if (!scopes.Contains(Scope.Observe))
        {
            throw new ArgumentException("--scopes must include observe.");
        }
        return scopes.Distinct().ToArray();
    }

    // The state holds the policy and keys; inside the workspace the agent could rewrite them with fs_write_file.
    // Throws ArgumentException when that is the case or --bind is no IP address.
    private static void RequireSeparateRootsAndValidAddress(string workspace, string state, string bind)
    {
        bool stateInsideWorkspace = state == workspace
            || state.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        bool validRequest = IPAddress.TryParse(bind, out _) && !stateInsideWorkspace;
        if (!validRequest)
        {
            throw new ArgumentException("A valid --bind address and a state directory outside the workspace are required.");
        }
    }

    // Throws ArgumentException when the state directory is not empty.
    private static void RequireEmptyState(string state)
    {
        if (Directory.Exists(state) && Directory.EnumerateFileSystemEntries(state).Any())
        {
            throw new ArgumentException("Existing state is preserved. Use a fresh state directory.");
        }
    }

    // A network-isolated worker cannot reach a classifier; opening the sandbox's network for it would defeat the sandbox.
    // Throws ArgumentException when a classifier is combined with a sandboxed worker.
    private static void RequireClassifierOutsideSandbox(NodeArguments arguments)
    {
        if (arguments.Option("--sandbox-image") != null && arguments.Option("--guard-classifier") != null)
        {
            throw new ArgumentException(
                "Network-isolated workers cannot contact a classifier. Run a guarded desktop worker "
                + "with a separately isolated IP service; never weaken the sandbox network for inference.");
        }
    }

    // Throws ArgumentException for an unknown consent mode.
    private static VisibilityOptions ReadVisibility(NodeArguments arguments)
    {
        if (!ConsentModeNames.TryParse(arguments.Option("--consent-mode") ?? "ask", out ConsentMode consentMode))
        {
            throw new ArgumentException("--consent-mode must be ask or unattended.");
        }
        var visibility = new VisibilityOptions { ConsentMode = consentMode, ScreenBorder = !arguments.Has("--no-screen-border") };
        visibility.Validate();
        return visibility;
    }

    // Without --ui-language the node shows the system language. Throws ArgumentException for another word than en or de.
    private static UiLanguage? ReadUiLanguage(NodeArguments arguments)
    {
        string? word = arguments.Option("--ui-language");
        if (word == null)
        {
            return null;
        }

        if (!WireNames<UiLanguage>.TryParse(word, out UiLanguage language))
        {
            throw new ArgumentException("--ui-language must be en or de.");
        }
        return language;
    }

    // Throws ArgumentException for an unknown guard mode or invalid guard rules.
    private static UiGuardOptions ReadUiGuard(NodeArguments arguments)
    {
        if (!Enum.TryParse(arguments.Option("--ui-guard") ?? "guarded", true, out UiGuardMode guardMode))
        {
            throw new ArgumentException("Unknown UI guard mode.");
        }
        var uiGuard = new UiGuardOptions { Mode = guardMode, Intent = arguments.Option("--guard-intent") ?? "" };
        uiGuard.Validate();
        return uiGuard;
    }

    private static void PrepareDirectories(Request request)
    {
        Directory.CreateDirectory(request.Workspace);
        PrivateFile.CreateDirectory(request.State);
#if WINDOWS
        RestrictToCurrentUser(request.State);
#endif
    }

#if WINDOWS
    // Windows inherits the parent's permissions; the state gets an explicit list with only the current user.
    private static void RestrictToCurrentUser(string directory)
    {
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            currentUser,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit
                | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
#endif

    // 32 random bytes in URL-safe Base64, so the token fits a header without escaping.
    private static string CreateCredential()
    {
        string base64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // The policy stores only the credential's hash, so the policy file never holds a usable token.
    private static NodePolicy CreatePolicy(Request request, string credential)
    {
        string credentialHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
        var operatorPrincipal = new PrincipalPolicy(
            "operator",
            credentialHash,
            request.Scopes,
            request.AllowedApplications,
            DateTimeOffset.UtcNow.Add(CredentialLifetime))
        {
            UiGuard = request.UiGuard
        };

        return new NodePolicy
        {
            GuardClassifierConfigFile = request.ClassifierConfigFile,
            NodeId = request.Node,
            WorkspaceRoot = request.Workspace,
            StateRoot = request.State,
            Visibility = request.Visibility,
            UiLanguage = request.UiLanguage,
            OperationWindowHours = request.OperationWindowHours,
            BackupRequirement = request.BackupRequirement,
            Principals = [operatorPrincipal]
        };
    }

    // A controller trusts this node through the CA pinned in the registry, not through any public authority.
    private static void WriteRegistry(Request request)
    {
        using var certificates = new ServerCertificates(request.State, request.Address);
        bool isIpv6 = request.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        string urlHost = isIpv6 ? "[" + request.Bind + "]" : request.Bind;
        var nodeEntry = new JsonObject
        {
            ["url"] = $"https://{urlHost}:{request.Port}",
            ["root_sha256"] = Convert.ToHexStringLower(SHA256.HashData(certificates.Root.RawData)),
            ["root_der_base64"] = Convert.ToBase64String(certificates.Root.RawData),
            ["token_file"] = Path.Combine(request.State, "credential")
        };

        var registry = new JsonObject { ["version"] = 1, ["nodes"] = new JsonObject { [request.Node] = nodeEntry } };

        WritePrivate(request, "nodes.json", registry.ToJsonString(JsonFormat));
    }

    // Returns the policy path as the worker will see it: inside a sandbox the file is mounted at a fixed place.
    private static string WriteWorkerPolicy(Request request, NodePolicy policy)
    {
        bool sandbox = request.SandboxImageDigest != null;
        var workerPolicy = new NodePolicy
        {
            NodeId = request.Node,
            WorkspaceRoot = sandbox ? "/workspace" : request.Workspace,
            StateRoot = sandbox ? "/state" : Path.Combine(request.State, "actuator"),
            Principals = policy.Principals,
            Visibility = request.Visibility,
            UiLanguage = request.UiLanguage,
            OperationWindowHours = request.OperationWindowHours,
            BackupRequirement = request.BackupRequirement,
            GuardClassifierConfigFile = policy.GuardClassifierConfigFile
        };

        WritePrivate(request, "worker-policy.json", JsonSerializer.Serialize(workerPolicy, JsonFormat));

        return sandbox ? SandboxPolicyPath : Path.Combine(request.State, "worker-policy.json");
    }

    private static List<string> BuildWorkerCommand(Request request, string workerPolicyPath)
    {
        NodeArguments arguments = request.Arguments;
        var command = new List<string>();
        if (request.SandboxImageDigest is { } digest)
        {
            command.AddRange(BuildSandboxCommand(request, digest));
        }
        else if (arguments.Option("--worker-program") is { } program)
        {
            command.Add(program);
            command.AddRange(arguments.Values("--worker-arg"));
        }
        else
        {
            command.AddRange(CurrentExecutable());
        }

        command.AddRange(request.DeviceArguments);
        command.AddRange(["--stdio-worker", "--policy", workerPolicyPath]);
        return command;
    }

    // Under "dotnet k-lani-bot-node.dll" the worker must be started the same way.
    // Throws InvalidOperationException when the executable path is unknown.
    private static IEnumerable<string> CurrentExecutable()
    {
        string executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        yield return executablePath;
        if (Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
        }
    }

    // Throws ArgumentException when a device option is missing.
    private static IReadOnlyList<string> ReadDeviceArguments(NodeArguments arguments)
    {
        var device = new List<string>();
        if (arguments.Has("--android"))
        {
            string serial = arguments.Option("--serial") ?? throw new ArgumentException("--serial required.");
            device.AddRange(["--android", "--serial", serial, "--adb", arguments.Option("--adb") ?? "adb"]);
        }
        if (arguments.Option("--ios-bridge") is { } iosBridge)
        {
            string iosBundle = arguments.Option("--ios-bundle") ?? throw new ArgumentException("--ios-bundle required.");
            device.AddRange(["--ios-bridge", Path.GetFullPath(iosBridge), "--ios-bundle", iosBundle]);
        }
        return device;
    }

    // The image is pinned by its digest, so a later re-tag cannot swap the worker; it must already exist locally.
    // Throws ArgumentException when the image is not pinned by digest or not present locally.
    private static string? ReadSandboxImageDigest(NodeArguments arguments)
    {
        if (arguments.Option("--sandbox-image") is not { } image)
        {
            return null;
        }
        string docker = arguments.Option("--docker") ?? "docker";
        var localImage = ProcessRunner.Run(docker, ["image", "inspect", "--format", "{{.Id}}", image], 10000);
        string digest = localImage.Output.Trim();
        if (!localImage.Success || !System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$"))
        {
            throw new ArgumentException("Sandbox image must already be provisioned locally; no implicit download or mutable runtime tag.");
        }
        return digest;
    }

    private static string[] BuildSandboxCommand(Request request, string digest)
    {
        string docker = request.Arguments.Option("--docker") ?? "docker";
        uint userId = OperatingSystem.IsWindows() ? WindowsSandboxUserId : UnixUser.GetUid();
        uint groupId = OperatingSystem.IsWindows() ? WindowsSandboxUserId : UnixUser.GetGid();
        string workerPolicyFile = Path.Combine(request.State, "worker-policy.json");

        return SandboxCommand(docker, digest, request.SandboxResources, request.Workspace, workerPolicyFile, userId, groupId);
    }

    // The container is the isolation boundary for an unattended worker, so every restriction is listed
    // explicitly: no network, read-only root, no capabilities, bounded memory, processes and scratch space.
    private static string[] SandboxCommand(
        string dockerProgram,
        string image,
        SandboxResources resources,
        string workspace,
        string workerPolicyFile,
        uint userId,
        uint groupId)
    {
        return
        [
            dockerProgram, "run", "--pull", "never", "--rm",
            "--name", "k-lani-bot-" + Guid.CreateVersion7(),
            "--label", "k-lani-bot.managed=true",
            "--init",
            "--user", $"{userId}:{groupId}",
            "--read-only",
            "--network", "none",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", resources.Pids.ToString(),
            "--memory", resources.MemoryMiB + "m",
            "--cpus", resources.Cpus.ToString(),
            "--shm-size", resources.SharedMemoryMiB + "m",
            "--tmpfs", $"/tmp:rw,nosuid,size={resources.TemporaryMiB}m,uid={userId},gid={groupId}",
            "--tmpfs", $"/state:rw,noexec,nosuid,size=64m,uid={userId},gid={groupId}",
            "--mount", $"type=bind,src={workspace},dst=/workspace",
            "--mount", $"type=bind,src={workerPolicyFile},dst={SandboxPolicyPath},readonly",
            "-i", image
        ];
    }

    private static void WriteNodeConfiguration(Request request, List<string> workerCommand)
    {
        var configuration = new NodeConfiguration(
            Path.Combine(request.State, "policy.json"),
            workerCommand,
            request.Bind,
            request.Port,
            NodeConfiguration.DefaultActionTimeoutMilliseconds);

        WritePrivate(request, "node.json", configuration.ToJson().ToJsonString(JsonFormat));
    }

    private static void PrintSummary(Request request)
    {
        var summary = new JsonObject
        {
            ["policy"] = Path.Combine(request.State, "policy.json"),
            ["registry"] = Path.Combine(request.State, "nodes.json"),
            ["node_config"] = Path.Combine(request.State, "node.json"),
            ["credential_file"] = Path.Combine(request.State, "credential"),
            ["worker_policy"] = Path.Combine(request.State, "worker-policy.json"),
            ["backup_requirement"] = JsonSerializer.SerializeToNode(request.BackupRequirement)
        };
        Console.WriteLine(summary.ToJsonString());
    }

    private static void WritePrivate(Request request, string fileName, string content)
    {
        PrivateFile.WriteAllText(Path.Combine(request.State, fileName), content);
    }
}
