using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Cartridges;
using KLaniBot.Guard;
using KLaniBot.Startup;
using KLaniBot.Transport;

namespace KLaniBot.Platforms.MacOS;

public static partial class MacInstallationCartridge
{
    // One run of one cartridge action. Its report collects every phase and is printed, and kept as last-run.json once the
    // run owns the data root. Everything after the preflight ends in that report, also a failure.
    private sealed class InstallationRun
    {
        private const int DefaultPort = 5443;
        private const int MinimumUnprivilegedPort = 1024;
        private const int MaximumPort = 65535;
        private const int DefaultVerifySeconds = 15;
        private const int MaximumVerifySeconds = 300;
        private static readonly TimeSpan CapabilityPollInterval = TimeSpan.FromMilliseconds(500);

        private readonly string[] _arguments;
        private readonly CartridgeManifest _manifest;
        private readonly string _action;
        private readonly string _operation;
        private readonly InstallationPaths _paths;
        private readonly string _node;
        private readonly int _port;
        private readonly uint _uid;
        private readonly JsonObject _report;
        private readonly JsonArray _phases;
        private bool _ownsData;

        private InstallationRun(
            string[] arguments,
            CartridgeManifest manifest,
            string action,
            string operation,
            InstallationPaths paths,
            string node,
            int port,
            uint uid,
            SystemFacts system)
        {
            _arguments = arguments;
            _manifest = manifest;
            _action = action;
            _operation = operation;
            _paths = paths;
            _node = node;
            _port = port;
            _uid = uid;
            _report = new JsonObject
            {
                ["cartridge"] = manifest.Id,
                ["cartridge_version"] = manifest.Version,
                ["cartridge_sha256"] = manifest.Sha256,
                ["request_id"] = operation,
                ["action"] = action,
                ["macos_version"] = system.Version,
                ["macos_build"] = system.Build,
                ["locale"] = "de-DE",
                ["install_path"] = paths.Bundle,
                ["data_root"] = paths.DataRoot,
                ["uid"] = uid,
                ["node_id"] = node,
                ["enrollment"] = system.Enrollment,
                ["phases"] = new JsonArray(),
                ["operational_ready"] = false
            };
            _phases = _report["phases"]!.AsArray();
        }

        // Throws ArgumentException for missing or invalid options, UnauthorizedAccessException for root, and
        // PlatformNotSupportedException off macOS 27 in German.
        public static InstallationRun Prepare(string[] arguments)
        {
            var manifest = CartridgeManifest.Load(
                Option(arguments, "--cartridge") ?? throw new ArgumentException("--cartridge <manifest> required."));
            string action = Option(arguments, "--cartridge-action") ?? "plan";
            if (action is not ("plan" or "apply" or "verify" or "permissions" or "stop"))
                throw new ArgumentException("Supported cartridge actions: plan, apply, verify, permissions, stop.");
            string operation = Option(arguments, "--request-id") ?? Guid.CreateVersion7().ToString();
            if (!Guid.TryParse(operation, out var id) || id.Version != 7)
                throw new ArgumentException("Administrative operations require a UUIDv7 request ID.");

            var paths = InstallationPaths.From(arguments);
            string node = ChooseNodeName(Option(arguments, "--node"), paths.NodeConfig);
            int port = int.Parse(Option(arguments, "--port") ?? DefaultPort.ToString());
            RequireSeparateRoots(port, paths);
            uint uid = GetUid();
            if (uid == 0)
                throw new UnauthorizedAccessException("Run GUI setup as the logged-in user. A root GUI daemon is not supported.");
            return new InstallationRun(arguments, manifest, action, operation, paths, node, port, uid, RequireSupportedSystem());
        }

        // Throws ArgumentException for a privileged port or roots that contain each other.
        private static void RequireSeparateRoots(int port, InstallationPaths paths)
        {
            if (port is < MinimumUnprivilegedPort or > MaximumPort
                || paths.InstallRoot == paths.DataRoot
                || paths.DataRoot.StartsWith(paths.InstallRoot + "/", StringComparison.Ordinal)
                || paths.InstallRoot.StartsWith(paths.DataRoot + "/", StringComparison.Ordinal))
                throw new ArgumentException("Use a non-privileged port and separate installation/data roots.");

            foreach (string path in new[] { paths.InstallRoot, paths.DataRoot, paths.Agent })
                CartridgeManifest.RejectLinks(path);
        }

        // Throws PlatformNotSupportedException for another macOS version or a system language without German.
        private static SystemFacts RequireSupportedSystem()
        {
            string systemVersion = Run("/usr/bin/sw_vers", "-productVersion");
            string build = Run("/usr/bin/sw_vers", "-buildVersion");
            if (systemVersion.Split('.')[0] != "27")
                throw new PlatformNotSupportedException("This cartridge is validated for macOS 27 only.");
            string languages = Run("/usr/bin/defaults", "read", "-g", "AppleLanguages");
            if (!languages.Contains("de", StringComparison.OrdinalIgnoreCase))
                throw new PlatformNotSupportedException("This is the German macOS cartridge; verify the system language.");

            return new SystemFacts(systemVersion, build, Run("/usr/bin/profiles", "status", "-type", "enrollment"));
        }

        private string? OptionValue(string key)
        {
            return Option(_arguments, key);
        }

        private void Phase(string name, string outcome)
        {
            _phases.Add(new JsonObject { ["step"] = name, ["outcome"] = outcome, ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") });
        }

        private int Finish(string outcome, bool success, int code)
        {
            _report["outcome"] = outcome;
            _report["success"] = success;
            if (_ownsData && _action != "plan")
                PrivateJson(Path.Combine(_paths.DataRoot, "last-run.json"), _report);
            Console.WriteLine(_report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return code;
        }

        public async Task<int> ExecuteAsync()
        {
            try
            {
                Phase("preflight", "verified");
                if (_action == "plan")
                    return Plan();
                if (_action == "stop")
                    return Stop();
                if (_action == "apply")
                    await ApplyAsync();
                else
                    RequireInstallation();
                return await VerifyAsync();
            }
            catch (Exception ex)
            {
                _report["error"] = ex.Message;
                Phase("failure", "no_automatic_privilege_escalation");
                return Finish("failed", false, 2);
            }
        }

        private int Plan()
        {
            if (OptionValue("--source-bundle") is { } source)
            {
                source = Path.GetFullPath(source);
                VerifyBundle(source);
                _report["source_sha256"] = CartridgeManifest.FingerprintBundle(source);
                _report["source_code_requirement"] = CodeRequirement(source);
            }
            _report["privacy_authorization"] = new JsonObject
            {
                ["accessibility"] = "user approval or MDM PPPC",
                ["screen_capture"] = "Apple user consent stays required; MDM can let standard users grant it",
                ["labels"] = _manifest.Document["privacy_labels"]!.DeepClone(),
                ["silent_grant_supported"] = false
            };
            _report["administrator_password_handling"] = "No passwords through MCP, logs or chat. "
                + "Root actions need a separate operating system authorization path.";
            return Finish("planned", true, 0);
        }

        private int Stop()
        {
            var installed = RequireReceipt(_paths.ReceiptPath, _paths.Bundle, _paths.DataRoot, _paths.Agent, _manifest);
            _ownsData = true;
            StopAgent(_uid, _paths.Agent, installed);
            Phase("register_login_agent", "stopped");
            return Finish("stopped", true, 0);
        }

        // verify and permissions act only on an installation this cartridge made.
        private void RequireInstallation()
        {
            RequireReceipt(_paths.ReceiptPath, _paths.Bundle, _paths.DataRoot, _paths.Agent, _manifest);
            VerifyBundle(_paths.Bundle);
            _ownsData = true;
            if (_action == "permissions")
            {
                RequestPermissions(_paths.Bundle, _paths.DataRoot);
                Phase("request_apple_permissions", "requested_from_actual_gui_service");
            }
        }

        private async Task ApplyAsync()
        {
            ApprovedSource source = ApproveSource();
            JsonObject? previous = PrepareRoots(source);
            string? backup = InstallBundle(source, previous);
            VerifyBundle(_paths.Bundle);
            bool provisioned = await ProvisionNodeAsync();
            ProtectState();
            Phase("protect_private_state", "verified_0700_directories_0600_files");
            Phase("provision_node", provisioned ? "initialized_new_credentials" : "verified_existing_credentials_preserved");
            RegisterAgent(source, backup);
            ExportPppc(_paths.Bundle, Path.Combine(_paths.DataRoot, "k-lani-bot-node-pppc.mobileconfig"));
            Phase("export_mdm_profile", "prepared_not_installed");
            if (!_arguments.Contains("--no-permission-prompts"))
            {
                RequestPermissions(_paths.Bundle, _paths.DataRoot);
                Phase("request_apple_permissions", "requested_from_actual_gui_service");
            }
            else
                Phase("request_apple_permissions", "deferred_by_operator");
        }

        // Throws ArgumentException without a source or with a malformed signing identity, and UnauthorizedAccessException when
        // the source differs from the fingerprint plan returned.
        private ApprovedSource ApproveSource()
        {
            string source = Path.GetFullPath(OptionValue("--source-bundle") ?? throw new ArgumentException("--source-bundle required."));
            string pin = OptionValue("--source-sha256")
                ?? throw new ArgumentException("Apply requires the source_sha256 returned by plan.");
            VerifyBundle(source);
            string fingerprint = CartridgeManifest.FingerprintBundle(source);
            byte[] pinBytes = System.Text.Encoding.ASCII.GetBytes(pin);
            byte[] fingerprintBytes = System.Text.Encoding.ASCII.GetBytes(fingerprint);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(pinBytes, fingerprintBytes))
                throw new UnauthorizedAccessException("Source bundle pin mismatch; nothing was installed.");

            string? signing = OptionValue("--signing-identity");
            if (signing != null && !System.Text.RegularExpressions.Regex.IsMatch(signing, "^[A-Fa-f0-9]{40}$"))
                throw new ArgumentException("Signing identity must be an exact certificate fingerprint.");
            return new ApprovedSource(source, fingerprint, signing);
        }

        // An existing installation is only taken over with this cartridge's receipt; its agent is stopped before the update.
        // Throws UnauthorizedAccessException for an unmanaged installation or state at these paths.
        private JsonObject PrepareRoots(ApprovedSource source)
        {
            JsonObject? previous = File.Exists(_paths.ReceiptPath)
                ? RequireReceipt(_paths.ReceiptPath, _paths.Bundle, _paths.DataRoot, _paths.Agent, _manifest)
                : null;
            if (File.Exists(_paths.Agent) && previous == null
                || Directory.Exists(_paths.Bundle) && previous == null
                || Directory.Exists(_paths.DataRoot) && Directory.EnumerateFileSystemEntries(_paths.DataRoot).Any() && previous == null)
                throw new UnauthorizedAccessException("Existing unmanaged installation/state is preserved. Use fresh paths.");
            Directory.CreateDirectory(_paths.InstallRoot);
            Directory.CreateDirectory(_paths.DataRoot);
            Directory.CreateDirectory(_paths.Workspace);
            _ownsData = true;
            PrivateMode(_paths.DataRoot, true);
            PrivateMode(_paths.Workspace, true);

            previous ??= StartedReceipt(source);
            StopAgent(_uid, _paths.Agent, previous);
            return previous;
        }

        private JsonObject StartedReceipt(ApprovedSource source)
        {
            var started = new JsonObject
            {
                ["format"] = 1,
                ["cartridge"] = _manifest.Id,
                ["cartridge_sha256"] = _manifest.Sha256,
                ["bundle"] = _paths.Bundle,
                ["data_root"] = _paths.DataRoot,
                ["launch_agent"] = _paths.Agent,
                ["source_sha256"] = source.Fingerprint,
                ["request_id"] = _operation,
                ["uid"] = _uid,
                ["phase"] = "installation_started"
            };
            PrivateJson(_paths.ReceiptPath, started);
            return started;
        }

        // Returns where the previous bundle was kept, or null. The same source with the same signing identity is reused.
        private string? InstallBundle(ApprovedSource source, JsonObject previous)
        {
            bool reuse = previous["source_sha256"]?.ToString() == source.Fingerprint
                && previous["signing_identity"]?.ToString() == source.SigningIdentity
                && Directory.Exists(_paths.Bundle)
                && previous["installed_sha256"]?.ToString() == CartridgeManifest.FingerprintBundle(_paths.Bundle);
            string? backup = null;
            string stage = Path.Combine(_paths.InstallRoot, ".k-lani-bot-stage-" + _operation + ".app");
            if (!reuse)
            {
                try
                {
                    backup = ReplaceBundle(source, stage);
                }
                finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            }
            Phase("install_verified_bundle", reuse ? "already_verified" : "installed_and_verified");
            return backup;
        }

        // Throws InvalidDataException when the staged copy differs from the approved source; rethrows the Exception of a failed
        // move after putting the previous bundle back.
        private string? ReplaceBundle(ApprovedSource source, string stage)
        {
            CopyBundle(source.Path, stage);
            if (CartridgeManifest.FingerprintBundle(stage) != source.Fingerprint)
                throw new InvalidDataException("Staged bundle differs from approved source.");
            if (source.SigningIdentity != null)
                Run("/usr/bin/codesign", "--force", "--sign", source.SigningIdentity, "--identifier", BundleId, stage);
            VerifyBundle(stage);
            string? backup = KeepPreviousBundle();
            try
            {
                Directory.Move(stage, _paths.Bundle);
            }
            catch
            {
                // Put the previous bundle back for every failure type, so the machine keeps a working app.
                if (backup != null)
                    Directory.Move(backup, _paths.Bundle);
                throw;
            }
            // Only after the new bundle is in place: a failure here must not trigger the restore above.
            if (backup != null)
                PreviousBundles.RemoveSuperseded(Path.GetDirectoryName(backup)!, backup);
            return backup;
        }

        // The previous bundle is kept in the private data root, not beside the app: LaunchServices registers every bundle
        // in an applications folder, and the copy then appeared as a second, indistinguishable entry in the privacy lists.
        private string? KeepPreviousBundle()
        {
            if (!Directory.Exists(_paths.Bundle))
                return null;

            string backups = Path.Combine(_paths.DataRoot, PreviousBundles.DirectoryName);
            Directory.CreateDirectory(backups);
            PrivateMode(backups, true);
            string backup = Path.Combine(backups, BundleName + ".previous-" + _operation);
            Directory.Move(_paths.Bundle, backup);
            return backup;
        }

        // Returns whether new credentials were created; an existing node must already match these paths.
        // Throws InvalidDataException when the existing node's roots or configuration differ.
        private async Task<bool> ProvisionNodeAsync()
        {
            if (!File.Exists(_paths.NodeConfig))
            {
                await InitializeNodeQuietlyAsync();
                return true;
            }

            var policy = NodePolicy.Load(Path.Combine(_paths.State, "policy.json"));
            if (policy.NodeId != _node || policy.StateRoot != _paths.State || policy.WorkspaceRoot != _paths.Workspace)
                throw new InvalidDataException("Existing node roots differ; refusing to rewrite credentials.");
            NodeConfiguration existing = NodeConfiguration.Load(_paths.NodeConfig);
            if (existing.WorkerCommand[0] != _paths.Executable
                || existing.HttpsPort != _port
                || existing.Bind != NodeConfiguration.DefaultBind)
                throw new InvalidDataException("Node configuration differs; explicit migration required.");
            return false;
        }

        // The initializer prints the new credentials; this setup keeps them out of its own output.
        private async Task InitializeNodeQuietlyAsync()
        {
            TextWriter output = Console.Out;
            Console.SetOut(TextWriter.Null);
            try
            {
                string[] initializerArguments =
                [
                    "--init", "--workspace", _paths.Workspace, "--state", _paths.State, "--node", _node, "--port", _port.ToString(),
                    "--scopes", "observe,ui,files.read,files.write,macro.write", "--worker-program", _paths.Executable,
                    // The guarded profile reaches only applications that rules name, and this setup collects no
                    // rules. The operator narrows the profile in policy.json afterwards if wanted.
                    "--ui-guard", "desktop"
                ];
                await NodeInitializer.InitializeAsync(initializerArguments);
            }
            finally
            {
                Console.SetOut(output);
            }
        }

        private void ProtectState()
        {
            var directories = Directory.EnumerateDirectories(_paths.State, "*", SearchOption.AllDirectories).Prepend(_paths.State);
            foreach (string directory in directories)
            {
                CartridgeManifest.RejectLinks(directory);
                PrivateMode(directory, true);
            }
            foreach (string file in Directory.EnumerateFiles(_paths.State, "*", SearchOption.AllDirectories))
            {
                CartridgeManifest.RejectLinks(file);
                PrivateMode(file, false);
            }
        }

        // The receipt is written before the agent starts, so a later stop or verify can prove what this cartridge owns.
        private void RegisterAgent(ApprovedSource source, string? backup)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.Agent)!);
            var plist = AgentPlist(_paths.Executable, _paths.NodeConfig, _paths.State);
            PrivateText(_paths.Agent, plist.ToString());
            string installedHash = CartridgeManifest.FingerprintBundle(_paths.Bundle);
            var receipt = new JsonObject
            {
                ["format"] = 1,
                ["cartridge"] = _manifest.Id,
                ["cartridge_sha256"] = _manifest.Sha256,
                ["bundle"] = _paths.Bundle,
                ["data_root"] = _paths.DataRoot,
                ["launch_agent"] = _paths.Agent,
                ["source_sha256"] = source.Fingerprint,
                ["installed_sha256"] = installedHash,
                ["signing_identity"] = source.SigningIdentity,
                ["code_requirement"] = CodeRequirement(_paths.Bundle),
                ["backup_bundle"] = backup,
                ["request_id"] = _operation,
                ["uid"] = _uid
            };
            PrivateJson(_paths.ReceiptPath, receipt);
            Run("/bin/launchctl", "bootstrap", "gui/" + _uid, _paths.Agent);
            Phase("register_login_agent", "running_as_gui_user");
        }

        // Asks the running node, as its authenticated client, which features work; operational means it can read, operate and
        // capture the screen.
        // Throws ArgumentException for a verification timeout outside 1 to 300 seconds.
        private async Task<int> VerifyAsync()
        {
            _report["code_requirement"] = CodeRequirement(_paths.Bundle);
            _report["permissions_report"] = Path.Combine(_paths.State, "permissions-report.json");
            using var client = new NodeTransportClient(_node, _paths.Registry);
            int seconds = int.Parse(OptionValue("--verify-timeout-seconds") ?? DefaultVerifySeconds.ToString());
            if (seconds is < 1 or > MaximumVerifySeconds)
                throw new ArgumentException("Verification timeout must be 1..300 seconds.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

            JsonObject? capabilities = await PollCapabilitiesAsync(client, deadline.Token);
            if (_report["operational_ready"]!.GetValue<bool>())
                return Finish("verified", true, 0);
            if (capabilities?["success"]?.GetValue<bool>() != true)
            {
                Phase("verify_gui_capabilities", "node_unavailable");
                return Finish("verification_failed", false, 2);
            }
            Phase("verify_gui_capabilities", "awaiting_apple_privacy_authorization");
            _report["next_action"] = _arguments.Contains("--no-permission-prompts")
                ? "Run permissions to request the Apple dialog; then verify."
                :
                    "Confirm the Apple approval in the requested dialog. Then run apply/verify; apply restarts the node's own "
                    + "login service and receives credentials.";
            return Finish("awaiting_os_authorization", false, 3);
        }

        // Returns the last answer, or null when the node never answered; stops early once the node is operational.
        private async Task<JsonObject?> PollCapabilitiesAsync(NodeTransportClient client, CancellationToken deadline)
        {
            JsonObject? capabilities = null;
            while (!deadline.IsCancellationRequested)
            {
                try
                {
                    capabilities = await client.CallAsync(Tool.GetMachineCapabilities, new(), deadline);
                }
                catch (OperationCanceledException) { break; }
                if (capabilities["success"]?.GetValue<bool>() == true && RecordFeatures(capabilities))
                    return capabilities;
                try
                {
                    await Task.Delay(CapabilityPollInterval, deadline);
                }
                catch (OperationCanceledException) { break; }
            }
            return capabilities;
        }

        // Returns whether the node is operational; otherwise names the missing permissions as this macOS shows them.
        private bool RecordFeatures(JsonObject capabilities)
        {
            HashSet<ToolFeature> features = ReportedFeatures(capabilities["capabilities"]!["features"]!.AsArray());
            bool guiReady = features.Contains(ToolFeature.SemanticInspect) && features.Contains(ToolFeature.PhysicalInput)
                && features.Contains(ToolFeature.Screenshot);
            if (guiReady)
            {
                _report["operational_ready"] = true;
                _report["gui_capabilities"] = capabilities["capabilities"]!.DeepClone();
                Phase("verify_gui_capabilities", "verified_from_authenticated_gui_worker");
                return true;
            }

            // The pane names as this macOS shows them; the cartridge pins them for its system language.
            JsonNode privacyLabels = _manifest.Document["privacy_labels"]!;
            var missingPermissions = new List<string>();
            if (!features.Contains(ToolFeature.SemanticInspect))
                missingPermissions.Add(privacyLabels["accessibility"]!.ToString());
            if (!features.Contains(ToolFeature.Screenshot))
                missingPermissions.Add(privacyLabels["screen_capture"]!.ToString());
            _report["missing_privacy_permissions"] = new JsonArray(
                missingPermissions.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            return false;
        }
    }
}
