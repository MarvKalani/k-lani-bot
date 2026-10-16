using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Actions.Handlers;
using KLaniBot.Advisory;
using KLaniBot.Common;
using KLaniBot.Guard;
using KLaniBot.Transport;

// The guarded profile on one approved program: raw input, shells and whole-desktop reads are refused, a semantic action
// is allowed only on the exact approved element, text follows the rule's policy, and macros, traces and the classifier
// cannot widen what the guard allows.
internal static class NodeSecurityCases
{
    public static int Run(string temporary)
    {
        var checks = new Checks("SECURITY");
        CheckExecutionRoles(checks);
        string root = Path.Combine(temporary, "security");
        string workspace = Path.Combine(root, "workspace");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(state);
        CheckWorkerEnvironment(checks, temporary, root, workspace, state);

        using var scenario = new GuardedScenario(checks, workspace, state);
        scenario.CheckRawInputDenied();
        scenario.Observe();
        scenario.CheckClickBoundaries();
        scenario.CheckUnknownAfterSideEffect();
        scenario.CheckGuardedText();
        scenario.CheckMacroGuard();
        scenario.CheckDeltasAndTraces();
        scenario.CheckShellsAndPolicies();
        scenario.CheckClassifier();
        return checks.Passed;
    }

    private static void Denied(Checks checks, Action action, string message)
    {
        bool denied = false;
        try
        {
            action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or InvalidDataException) { denied = true; }
        checks.That(denied, message);
    }

    private static void CheckExecutionRoles(Checks checks)
    {
        checks.That(ExecutionRoleSelector.FromArguments(["--mcp"]) == ExecutionRole.McpController,
            "controller cannot create a node display");
        checks.That(
            ExecutionRoleSelector.FromArguments(["--serve"]) == ExecutionRole.NodeSupervisor,
            "supervisor is not the desktop worker");
        checks.That(
            ExecutionRoleSelector.FromArguments(["--stdio-worker"]) == ExecutionRole.NodeWorker,
            "only worker owns desktop visibility");
        Denied(checks, () => ExecutionRoleSelector.FromArguments(["--mcp", "--stdio-worker"]),
            "ambiguous controller/worker roles rejected");
    }

    private static void CheckWorkerEnvironment(Checks checks, string temporary, string root, string workspace, string state)
    {
        var sanitizedWorker = new ProcessStartInfo();
        sanitizedWorker.Environment.Clear();
        sanitizedWorker.Environment["TEMP"] = workspace;
        sanitizedWorker.Environment["USERPROFILE"] = temporary;
        sanitizedWorker.Environment["SystemRoot"] = @"C:\Windows";
        sanitizedWorker.Environment.Remove("WINDIR");
        sanitizedWorker.Environment["K_LANI_BOT_TEST_SECRET"] = "synthetic";
        WorkerEnvironment.Configure(sanitizedWorker, state, WorkerPlatform.Windows);
        checks.That(sanitizedWorker.Environment.TryGetValue("WINDIR", out string? windowsDirectory) && windowsDirectory == @"C:\Windows",
            "WPF's font directory survives a legacy parent that removed WINDIR but retained SystemRoot");
        string bundleCache = Path.Combine(state, "bundle-cache");
        checks.That(sanitizedWorker.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] == bundleCache
            && Directory.Exists(bundleCache), "Windows bundle extraction uses private node state");
        checks.That(sanitizedWorker.Environment["TEMP"] == workspace
            && sanitizedWorker.Environment["USERPROFILE"] == temporary, "Windows profile and temporary directory survive sanitization");
        checks.That(!sanitizedWorker.Environment.ContainsKey("K_LANI_BOT_TEST_SECRET"), "unrelated secrets are not inherited by worker");
        sanitizedWorker.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = workspace;
        WorkerEnvironment.Configure(sanitizedWorker, state, WorkerPlatform.Windows);
        checks.That(
            sanitizedWorker.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] == workspace,
            "explicit operator bundle cache remains configured");

        var unixWorker = new ProcessStartInfo();
        unixWorker.Environment.Clear();
        WorkerEnvironment.Configure(unixWorker, state, WorkerPlatform.Unix);
        checks.That(
            !unixWorker.Environment.ContainsKey("DOTNET_BUNDLE_EXTRACT_BASE_DIR"),
            "Unix worker does not acquire a Windows extraction policy");
        string linkedState = Path.Combine(root, "linked-state");
        Directory.CreateSymbolicLink(linkedState, state);
        Denied(checks, () => WorkerEnvironment.Configure(
            new ProcessStartInfo { Environment = { ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = "" } },
            linkedState,
            WorkerPlatform.Windows),
            "default bundle cache cannot follow a linked state directory");
    }

    // This test process is the approved program; its button is the one approved element. The steps change the rule and
    // the reported element in place and run in this order.
    private sealed class GuardedScenario : IDisposable
    {
        private readonly Checks _checks;
        private readonly string _workspace;
        private readonly string _state;
        private readonly string _application;
        private readonly string _appHash;
        private readonly UiActionRule _rule;
        private readonly UiGuardOptions _guard;
        private readonly PrincipalPolicy _principal;
        private readonly NodePolicy _policy;
        private readonly UiTargetIdentity _target;
        private readonly FakeUi _ui;
        private readonly ActionDispatcher _dispatcher;
        private readonly RequestContext _caller = new("guarded", new string('g', 32));
        private string _snapshot = "";

        public GuardedScenario(Checks checks, string workspace, string state)
        {
            _checks = checks;
            _workspace = workspace;
            _state = state;
            using var self = Process.GetCurrentProcess();
            _application = self.MainModule!.FileName;
            _appHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_application)));
            _rule = new UiActionRule
            {
                Application = _application,
                ApplicationSha256 = _appHash,
                WindowTitle = "Fixture",
                Role = "Button",
                Name = "button",
                Actions = [Tool.ClickElement]
            };
            _guard = new UiGuardOptions { Mode = UiGuardMode.Guarded, Intent = "Edit the approved synthetic field only", Rules = [_rule] };
            _principal = new PrincipalPolicy(
                "guarded",
                new string('a', 64),
                [Scope.Observe, Scope.Ui, Scope.Shell, Scope.Process, Scope.MacroWrite, Scope.FilesRead, Scope.FilesWrite],
                [],
                DateTimeOffset.UtcNow.AddHours(1))
            {
                UiGuard = _guard
            };
            _policy = new NodePolicy
            {
                BackupRequirement = BackupRequirement.Off,
                WorkspaceRoot = workspace,
                StateRoot = state,
                Principals = [_principal]
            };
            _target = new UiTargetIdentity(Environment.ProcessId, _application, "Fixture", "Button", "button", "", true, true);
            _ui = new FakeUi { Identity = _target };
            _dispatcher = new ActionDispatcher(_ui, _policy);
        }

        public void Dispose()
        {
            _dispatcher.Dispose();
        }

        private JsonObject Call(string action, JsonObject? data = null)
        {
            data ??= new();
            if (ToolContract.Get(action)["mutates"]!.GetValue<bool>())
                data["request_id"] ??= Guid.CreateVersion7().ToString();
            return _dispatcher.Execute(new()
            {
                ["action"] = action,
                ["args"] = data.DeepClone()
            }, _caller);
        }

        private JsonObject Click()
        {
            return new JsonObject
            {
                ["id"] = _ui.Id,
                ["snapshot_id"] = _snapshot
            };
        }

        private JsonObject Type(string text)
        {
            return new JsonObject
            {
                ["id"] = _ui.Id,
                ["snapshot_id"] = _snapshot,
                ["text"] = text
            };
        }

        public void CheckRawInputDenied()
        {
            foreach (var keys in new[] { new[] { "win", "r" }, ["win", "ctrl", "x"], ["ctrl", "i"], ["delete"], ["f2"], ["alt", "tab"] })
                _checks.That(Call("hotkey", new()
                {
                    ["keys"] = new JsonArray(keys.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
                })["outcome"]!.ToString() == "not_dispatched", "raw chord never reaches guarded backend");
            _checks.That(_ui.KeyCalls == 0, "no native key synthesis occurred");
            _checks.That(!Call("run_terminal_command", new()
            {
                ["command"] = "printf never"
            })["success"]!.GetValue<bool>(), "shell capability alone cannot bypass guard");
            _checks.That(!Call("launch_process", new()
            {
                ["path"] = _application
            })["success"]!.GetValue<bool>(), "process capability alone cannot bypass guard");
            _checks.That(!Call("take_screenshot")["success"]!.GetValue<bool>(), "whole desktop observation requires desktop profile");
            _checks.That(!Call("get_ui_tree")["success"]!.GetValue<bool>(), "unbound application read denied");
        }

        // Throws Exception with the answer and both program paths when the approved program cannot be observed.
        public void Observe()
        {
            var observation = Call("get_ui_tree", new()
            {
                ["pid"] = Environment.ProcessId
            });
            if (observation["success"]?.GetValue<bool>() != true)
                throw new Exception(
                    observation.ToJsonString()
                        + " expected="
                        + _application
                        + " actual="
                        + Process.GetProcessById(Environment.ProcessId).MainModule?.FileName);
            _snapshot = observation["snapshot_id"]!.ToString();
        }

        public void CheckClickBoundaries()
        {
            _checks.That(Call("click_element", Click())["success"]!.GetValue<bool>()
                && _ui.Clicks == 1, "exact local semantic action allowed");
            var unboundPostcondition = Click();
            unboundPostcondition["postcondition"] = new JsonObject
            {
                ["name"] = "private field",
                ["expected_state"] = "exists"
            };
            _checks.That(Call("click_element", unboundPostcondition)["outcome"]!.ToString() == "not_dispatched"
                && _ui.Clicks == 1, "postcondition cannot escape the approved observation boundary before mutation");
            _ui.Identity = _target with
            {
                Name = "Delete all"
            };
            _checks.That(
                Call("click_element", Click())["outcome"]!.ToString() == "not_dispatched" && _ui.Clicks == 1,
                "changed element meaning denied");
            _ui.Identity = _target with
            {
                WindowTitle = "Synthetic file picker"
            };
            _checks.That(!Call("click_element", Click())["success"]!.GetValue<bool>(), "different dialog denied");
            _ui.Identity = _target with
            {
                Visible = false
            };
            _checks.That(!Call("click_element", Click())["success"]!.GetValue<bool>(), "hidden control denied");
            _ui.Identity = _target;
            _rule.ApplicationSha256 = new string('0', 64);
            _checks.That(!Call("click_element", Click())["success"]!.GetValue<bool>(), "changed executable digest denied");
            _rule.ApplicationSha256 = _appHash;
        }

        public void CheckUnknownAfterSideEffect()
        {
            _ui.ThrowAfterClick = true;
            var broken = Click();
            var failed = Call("click_element", broken);
            _checks.That(failed["outcome"]!.ToString() == "unknown" && _ui.Clicks == 2, "exception after side effect is unknown");
            Call("click_element", broken);
            _checks.That(_ui.Clicks == 2, "unknown mutation is not replayed");
            _ui.ThrowAfterClick = false;
        }

        public void CheckGuardedText()
        {
            _rule.Actions = [Tool.TypeText];
            _rule.Role = "Edit";
            _rule.Name = "field";
            _rule.TextPolicy = TextInputPolicy.Literal;
            _rule.MaxTextLength = 32;
            _ui.Identity = _target with
            {
                Role = "Edit",
                Name = "field"
            };
            var enter = Type("safe");
            enter["enter"] = true;
            _checks.That(!Call("type_text", enter)["success"]!.GetValue<bool>(), "implicit command key forbidden");
            _checks.That(Call("type_text", Type("000123%{ENTER}"))["success"]!.GetValue<bool>()
                && _ui.SemanticTexts == 1
                && _ui.PhysicalTexts == 0, "guarded text cannot use keyboard fallback");
            _checks.That(!Call("type_text", Type(new string('x', 33)))["success"]!.GetValue<bool>(), "field text budget enforced");

            string model = Path.Combine(_workspace, "cube.stl");
            File.WriteAllText(model, "synthetic");
            _rule.TextPolicy = TextInputPolicy.WorkspacePath;
            _rule.Extensions = [".stl"];
            _rule.MaxTextLength = 4096;
            _checks.That(Call("type_text", Type(model))["success"]!.GetValue<bool>(), "explicit workspace model accepted");
            _checks.That(
                !Call("type_text", Type(Path.Combine(_state, "private.stl")))["success"]!.GetValue<bool>(),
                "picker cannot leave workspace");
            string other = Path.Combine(_workspace, "source.cmd");
            File.WriteAllText(other, "synthetic");
            _checks.That(!Call("type_text", Type(other))["success"]!.GetValue<bool>(), "picker file type constrained");
        }

        public void CheckMacroGuard()
        {
            var macroArgs = new JsonObject
            {
                ["name"] = "guarded-escape",
                ["steps"] = new JsonArray(new JsonObject
                {
                    ["action"] = "hotkey",
                    ["args"] = new JsonObject { ["keys"] = new JsonArray("win", "r") }
                })
            };
            var saved = Call("save_macro", macroArgs);
            _checks.That(saved["success"]!.GetValue<bool>(), "author can store a draft without gaining execution rights");
            var escaped = Call("run_macro", new()
            {
                ["name"] = "guarded-escape",
                ["sha256"] = saved["macro"]!["sha256"]!.ToString()
            });
            _checks.That(!escaped["success"]!.GetValue<bool>() && _ui.KeyCalls == 0, "macro steps retain hard guard");
            var summaries = Call("list_macros", new()
            {
                ["limit"] = 1
            });
            _checks.That(
                summaries["macros"]![0]!["steps"] == null && summaries["macros"]!.AsArray().Count == 1,
                "discovery omits source steps and is paged");
        }

        public void CheckDeltasAndTraces()
        {
            var delta = Call("get_ui_delta", new()
            {
                ["pid"] = Environment.ProcessId,
                ["since_snapshot"] = _snapshot
            });
            _checks.That(delta["tree"] == null && delta["base_available"]!.GetValue<bool>(), "delta never repeats full tree");
            var noBase = Call("get_ui_delta", new()
            {
                ["pid"] = Environment.ProcessId,
                ["since_snapshot"] = "unknown"
            });
            _checks.That(noBase["requires_full_observation"]!.GetValue<bool>()
                && noBase["added"]!.AsArray().Count == 0, "missing delta base requires explicit observation");

            var engine = new MacroEngine(Path.Combine(_state, "isolated-macros.json"));
            var local = new MacroDefinition
            {
                Name = "local-trace",
                Steps = [new() { ["action"] = "get_ui_tree", ["args"] = new JsonObject() }]
            };
            engine.SaveMacro(local);
            var oversizedTree = new JsonObject { ["success"] = true, ["tree"] = new string('x', 12000) };
            var compactRun = engine.RunMacro(local.Name, null, _ => oversizedTree, local.Sha256, "owner", true);
            var compact = JsonSerializer.SerializeToNode(compactRun)!;
            _checks.That(
                compact["results"] == null && compact["trace_available"]!.GetValue<bool>(),
                "bounded trace is explicit and summary is compact");
            var trace = engine.ReadTrace(compact["run_id"]!.ToString(), "owner", 0, 1);
            _checks.That(trace["results"]![0]!["details_omitted"]!.GetValue<bool>(), "large trace detail bounded");
            Denied(_checks, () => engine.ReadTrace(compact["run_id"]!.ToString(), "other", 0, 1), "trace cannot cross principal boundary");
        }

        public void CheckShellsAndPolicies()
        {
            _checks.That(TerminalModule.SelectShell("macos", "auto", "literal").Executable == "/bin/zsh", "macOS uses native shell");
            Denied(_checks, () => TerminalModule.SelectShell("macos", "cmd", "literal"), "no Windows shell fallback on macOS");
            var shell = ProcessRunner.Run("/bin/sh", ["-c", "printf literal; exit 7"], 1000, _workspace);
            _checks.That(!shell.Success && shell.ExitCode == 7 && shell.Output == "literal", "native shell exit and output preserved");
            var watch = Stopwatch.StartNew();
            var timed = ProcessRunner.Run("/bin/sh", ["-c", "sleep 1 & exit 0"], 100, _workspace);
            _checks.That(!timed.Success && watch.ElapsedMilliseconds < 800, "inherited output pipe obeys process deadline");

            var duplicate = new NodePolicy
            {
                BackupRequirement = BackupRequirement.Off,
                WorkspaceRoot = _workspace,
                StateRoot = _state,
                Principals = [_principal, _principal with { Id = "other" }]
            };
            string duplicateFile = Path.Combine(_state, "duplicate.json");
            File.WriteAllText(duplicateFile, JsonSerializer.Serialize(duplicate));
            Denied(_checks, () => NodePolicy.Load(duplicateFile), "shared credential cannot represent separate principals");
            var resources = new SandboxResources { Pids = 512, MemoryMiB = 1536 };
            resources.Validate();
            _checks.That(true, "finite application resource budget accepted");
            Denied(_checks, () => new SandboxResources { Pids = -1 }.Validate(), "unbounded task profile rejected");
        }

        // A calibrated classifier can veto an approved action but never allow a denied one, and it fails closed without
        // a matching calibration report.
        public void CheckClassifier()
        {
            string calibration = Path.Combine(_state, "calibration.json");
            LayaAdvisoryConfig config = WriteCalibratedConfiguration(calibration);
            _rule.Actions = [Tool.ClickElement];
            _rule.Role = "Button";
            _rule.Name = "button";
            _ui.Identity = _target;
            var handler = new GuardClassifierHandler();
            using var model = new NodeRiskClassifier(new LayaAdvisoryEvaluator(config, handler));
            using var protectedDispatcher = new ActionDispatcher(_ui, _policy, classifier: model);

            var denied = CallWith(protectedDispatcher, "hotkey", new()
            {
                ["keys"] = new JsonArray("win", "r")
            });
            _checks.That(!denied["success"]!.GetValue<bool>() && handler.Calls == 0,
                "classifier is never consulted to override hard denial");
            var observation = CallWith(protectedDispatcher, "get_ui_tree", new()
            {
                ["pid"] = Environment.ProcessId
            });
            var click = new JsonObject { ["id"] = _ui.Id, ["snapshot_id"] = observation["snapshot_id"]!.ToString() };
            int before = _ui.Clicks;
            handler.Choice = "block";
            var blocked = CallWith(protectedDispatcher, "click_element", (JsonObject)click.DeepClone());
            _checks.That(!blocked["success"]!.GetValue<bool>() && _ui.Clicks == before, "classifier veto prevents approved action");
            handler.Choice = "pass";
            var permitted = CallWith(protectedDispatcher, "click_element", (JsonObject)click.DeepClone());
            _checks.That(permitted["success"]!.GetValue<bool>() && _ui.Clicks == before + 1,
                "classifier pass only follows local authorization");
            File.WriteAllText(calibration, "{}");
            var uncertain = CallWith(protectedDispatcher, "click_element", (JsonObject)click.DeepClone());
            _checks.That(!uncertain["success"]!.GetValue<bool>() && _ui.Clicks == before + 1, "uncalibrated classifier fails closed");
        }

        private JsonObject CallWith(ActionDispatcher dispatcher, string action, JsonObject args)
        {
            if (ToolContract.Get(action)["mutates"]!.GetValue<bool>())
                args["request_id"] = Guid.CreateVersion7().ToString();
            return dispatcher.Execute(new()
            {
                ["action"] = action,
                ["args"] = args
            }, _caller);
        }

        private LayaAdvisoryConfig WriteCalibratedConfiguration(string calibration)
        {
            string credential = Path.Combine(_state, "classifier-credential");
            File.WriteAllText(credential, new string('x', 40));
            var config = new LayaAdvisoryConfig
            {
                Enabled = true,
                Endpoint = "http://127.0.0.1:8000",
                AllowLoopbackHttp = true,
                CredentialFile = credential,
                ModelRevision = new string('a', 40),
                CalibrationReportFile = calibration
            };
            File.WriteAllText(calibration, new JsonObject
            {
                ["version"] = 1,
                ["model_revision"] = config.ModelRevision,
                ["rubric_sha256"] = CanonicalJson.Hash(NodeRiskClassifier.Questions),
                ["held_out_dataset_sha256"] = new string('b', 64),
                ["sample_count"] = 100,
                ["approved_by_operator"] = true
            }.ToJsonString());
            return config;
        }
    }
}
// Answers the classifier's HTTP call with the verdict a case sets, and counts calls to prove when it is not consulted.
internal sealed class GuardClassifierHandler : HttpMessageHandler
{
    public string Choice = "pass"; public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        var probabilities = new JsonObject
        {
            ["pass"] = Choice == "pass" ? .99 : .005,
            ["block"] = Choice == "block" ? .99 : .005,
            ["review"] = Choice == "review" ? .99 : .005
        };
        return Task.FromResult(
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["answers"] = new JsonObject
                    {
                        ["risk"] = new JsonObject
                        {
                            ["type"] = "choice",
                            ["choice"] = Choice,
                            ["confidence"] = .99,
                            ["probabilities"] = probabilities
                        }
                    },
                    ["usage"] = new JsonObject { ["truncated"] = false }
                }.ToJsonString())
            });
    }
}
