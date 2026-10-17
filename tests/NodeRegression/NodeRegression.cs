using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Actions.Handlers;
using KLaniBot.Advisory;
using KLaniBot.Audit;
using KLaniBot.Guard;
using KLaniBot.Platforms;
using KLaniBot.Transport;

// Entry point of the node regression: runs the core scenario and every suite, and prints how many checks passed.
public static class NodeRegression
{
    // A failed check ends the run with its full message and exit code 1. An exception left unhandled would abort the
    // process, and every red run then leaves a core dump of about 20 MB behind.
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Regression failed: " + exception);

            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--concrete-steps"))
        {
            Console.WriteLine(ConcreteStepCases.Run() + " concrete step checks passed.");
            return 0;
        }
        if (args.Contains("--credentials") || args.Contains("--credential-provider-probe"))
        {
            int checks = args.Contains("--credentials") ? CredentialCases.Run() : CredentialNativeCases.Run();
            Console.WriteLine(checks + " credential checks passed.");
            return 0;
        }
        if (TryRunChildMode(args, out int childExitCode))
        {
            return childExitCode;
        }
        string temp = Path.Combine(Path.GetTempPath(), "k-lani-bot-regression-" + Guid.NewGuid());

        try
        {
            if (args.Contains("--form-input"))
            {
                Console.WriteLine(FormInputCases.Run(temp) + " native form input checks passed.");
                return 0;
            }
            if (args.Contains("--cartridge-metadata"))
            {
                int metadataChecks = await RunCartridgeSuitesAsync(temp);
                metadataChecks += WorkerStartupFailureCases.Run(temp);
                metadataChecks += NodeSecurityCases.Run(temp);
                Console.WriteLine(metadataChecks + " cartridge metadata checks passed.");
                return 0;
            }
            int count = await CoreScenarioCases.RunAsync(temp);
            count += await RunSuitesAsync(temp);
            Console.WriteLine($"{count} node regression checks passed.");
        }
        finally { Directory.Delete(temp, true); }
        return 0;
    }

    private static async Task<int> RunSuitesAsync(string temp)
    {
        int count = 0;
        count += NodeSecurityCases.Run(temp);
        count += WindowInteractionCases.Run(temp);
        count += DialogRoutingCases.Run(temp);
        count += PaceDispatchCases.Run(temp);
        count += await RunCartridgeSuitesAsync(temp);
        count += ApplicationObjectCases.Run(temp);
        count += ConcreteStepCases.Run();
        count += LaunchArgumentCases.Run(temp);
        count += RunUpdateAndJournalSuites(temp);
        count += RunPlatformSuites(temp);
        count += OperationLedgerCases.Run(temp);
        count += ActionOutcomeCases.Run(temp);
        count += ActionFailureCases.Run(temp);
        count += await TransportFailureCases.RunAsync(temp);
        count += await ErrorDiagnosticsCases.RunAsync(temp);
        count += ActionTraceCases.Run(temp);
        count += CommandLineCases.Run();
        count += await StdioWorkerLoopCases.RunAsync(temp);
        count += ActionHandlerTableCases.Run(temp);
        count += DispatcherGoldenCases.Run(temp);
        count += await SupervisorControlFrameCases.RunAsync(temp);
        count += ProcessRunnerCases.Run();
        count += InspectorPageCases.Run();
        count += ProtocolIdentityCases.Run();
        count += AdvisoryProviderCases.Run(temp);
        count += ModalDialogPolicyCases.Run();
        count += GuardClassificationCases.Run(temp);
        count += NodeNameCases.Run();
        count += ScopeCases.Run(temp);
        count += RunMaintenanceCases(temp);
        count += ToolCases.Run();
        count += ToolFeatureCases.Run();
        count += UiTextCases.Run();
        count += NodeConfigurationCases.Run(temp);
        count += WorkerStartupFailureCases.Run(temp);
        count += await MasterAdministrationCases.RunAsync(temp) + MasterBackupRequirementCases.Run(temp);
        return count;
    }

    // The regression starts itself as a child program for some checks: a probe with an open standard input, and a stand-in for a
    // program's own MCP server with its version command.
    private static bool TryRunChildMode(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Contains(ProcessRunnerCases.ProbeArgument))
            exitCode = ProcessRunnerCases.RunProbe();
        else if (args.Contains(FakeApplicationMcp.ServeArgument))
            exitCode = FakeApplicationMcp.Serve();
        else if (args.Contains(FakeApplicationMcp.VersionArgument))
            Console.WriteLine(FakeApplicationMcp.Version);
        else
            return false;
        return true;
    }

    private static int RunMaintenanceCases(string temporaryRoot)
    {
        return CredentialCases.Run() + BackupCases.Run(temporaryRoot) + BackupPolicyCases.Run(temporaryRoot)
            + BackupToolCases.Run(temporaryRoot) + BackupGateCases.Run(temporaryRoot) + GatewayNodeFunctionCases.Run()
            + ApplicationMcpCases.Run(temporaryRoot);
    }

    // The platform backends' pure parts run on every platform: Windows row format, foreground plan and window selection,
    // and the macOS install contract.
    private static int RunPlatformSuites(string temp)
    {
        int count = CommonTreeRowCases.Run(temp);
        count += ForegroundActivationPlanCases.Run();
        count += WindowSelectionCases.Run(temp);
        count += RunMacSuites(temp);
        count += RunLinuxSuites();

        return count;
    }

    // The Linux backend's pure parts run on every platform: address policy and password field refusal.
    private static int RunLinuxSuites()
    {
        int count = DirectConnectionAddressCases.Run();
        count += LinuxPasswordRoleCases.Run();

        return count;
    }

    // The macOS cartridge's pure parts run on every platform: install contract, kept bundles and window identity.
    private static int RunMacSuites(string temp)
    {
        int count = MacLoginAgentContractCases.Run();
        count += MacBundleVersionCases.Run(temp);
        count += PreviousBundlesCases.Run(temp);
        count += MacAccessibilityRoleNameCases.Run();
        count += MacAttributeReadCases.Run();

        return count;
    }

    private static async Task<int> RunCartridgeSuitesAsync(string temporaryRoot)
    {
        int count = GatewayCartridgeCases.Run(temporaryRoot);
        count += CartridgeTopicCases.Run();
        count += await CartridgeLibraryCases.RunAsync(temporaryRoot);
        count += CartridgeCoverageCases.Run();

        return count;
    }

    // An installed node of any earlier version must reach the current one; these suites hold the update path and the journal.
    private static int RunUpdateAndJournalSuites(string temp)
    {
        int count = 0;
        count += UpdatePlanRecoveryCases.Run(temp);
        count += UpdateCompatibilityCases.Run();
        count += EarlierStateCases.Run(temp);
        count += AuditJournalCases.Run(temp);
        count += EarlierJournalCases.Run(temp);
        count += AuditSegmentCases.Run(temp);
        return count;
    }
}
// A desktop backend without a desktop, used by every dispatcher case: it reports observations, counts clicks and input, and can be told to
// fail or refuse at specific points.
internal sealed class FakeUi : IUiService
{
    public UiNativeTextSupport NativeTextSupport { get; set; } = UiNativeTextSupport.Unknown;
    public UiNativeTextSupport ProbeNativeTextSupport(int id)
    {
        return NativeTextSupport;
    }
    public UiFieldMetadata FormMetadata = UiFieldMetadata.Unsupported;
    public int FormSelections;
    public int FormOpenings;
    public UiFieldMetadata ReadFieldMetadata(int id) => FormMetadata;
    public UiOutcome SelectChoice(UiChoiceCommand command)
    {
        FormSelections++;
        return new(true, "Synthetic native selection.");
    }
    public UiOutcome OpenChoices(int id)
    {
        FormOpenings++;
        return new(true, "Synthetic native opening.");
    }
    public int NativeReads;
    public int NativeWrites;
    public ApplicationObjectResult InspectApplication(ApplicationObjectQuery query)
    {
        NativeReads++;
        if (query.Area == ApplicationReadArea.Status)
        {
            return new(true, "synthetic native program status", Status: new(true, [100]));
        }
        return new(true, "synthetic native identity", Info: new(query.ProcessId, "OutlookClassic", "16.0", "16.0.1.2", "en-US",
            true, ["COM"]));
    }
    public ApplicationObjectResult OperateApplication(ApplicationObjectCommand command)
    {
        NativeWrites++;
        return new(true, "synthetic native write", Mutation: new("new-id", "store", "folder", command.Operation.ToString(), true));
    }
    public DesktopSessionInfo? DesktopSession;
    public DesktopSessionInfo? GetDesktopSession() => DesktopSession;
    public object? Observation;
    public Func<object>? ObservationFactory;
    public UiTargetIdentity? Identity;
    public int KeyCalls, PhysicalTexts, SemanticTexts; public bool ThrowAfterClick;
    public Action? AfterClick;
    public bool ScreenshotEnabled
    {
        get; set;
    }
    public int ScreenshotCalls;
    public int PrepareCalls;
    public bool ModalTarget;
    public bool RefuseForeground;
    public WindowInteractionMode LastInteractionMode;
    public void PrepareElementInteraction(int id, WindowInteractionMode mode, ModalDialogMode dialogs, bool decisionRecorded)
    {
        PrepareCalls++;
        LastInteractionMode = mode;
        if ((ModalTarget && (dialogs == ModalDialogMode.Pause || !decisionRecorded))
            || (RefuseForeground && mode == WindowInteractionMode.Foreground))
        {
            throw new InvalidOperationException("Synthetic target blocked before input.");
        }
    }
    public UiOutcome InvokeBackgroundElement(int id)
    {
        return SilentClick(id);
    }
    public UiTargetIdentity? GetTargetIdentity(int id) => Identity;
    public string? WindowText
    {
        get; set;
    }
    public string? ReadWindowText(int id) => WindowText;
    public string? ModalWindowTitle
    {
        get; set;
    }
    public string? ShowingModalWindowTitle(int processId) => ModalWindowTitle;
    // What DetectModalDialog reports. Unknown unless a case sets it, so the cases that do not set it keep the routing they had.
    public ModalDialogDetection ModalDetection { get; set; } = ModalDialogDetection.Unknown;
    public ModalDialogDetection DetectModalDialog(int id) => ModalDetection;
    public UiOutcome SetGuardedText(int id, string text)
    {
        SemanticTexts++;
        Literal = text;
        return new(true, "semantic");
    }
    public int Id, Clicks; public string Literal = "";
    public string Platform => "fixture";
    public HashSet<ToolFeature> ExtraFeatures = new();
    public IReadOnlySet<ToolFeature> Features
    {
        get
        {
            var features = new HashSet<ToolFeature> { ToolFeature.SemanticInspect, ToolFeature.SemanticInvoke, ToolFeature.TextInjection };
            if (ScreenshotEnabled)
            {
                features.Add(ToolFeature.Screenshot);
            }
            features.UnionWith(ExtraFeatures);
            return features;
        }
    }
    public List<WindowInfo> ListWindows() => [];
    public WindowSelectionRequest? LastWindowRequest;
    public bool FocusWindow(WindowSelectionRequest request)
    {
        LastWindowRequest = request;
        return false;
    }
    public object? GetWindowGeometry(int? pid, string? title) => null;
    public GeometryOutcome SetWindowGeometry(int? pid, string? title, string? state, int? w, int? h, int? x, int? y)
        => new(false, "unsupported", null);
    public string GetUiTree(WindowSelectionRequest request, int maxDepth = 4)
    {
        LastWindowRequest = request;
        return $"[{++Id}] Button button";
    }
    public string GetCommonUiTree(WindowSelectionRequest request, int maxDepth = 4)
        => $"[{++Id}] Root \"Desktop\" (autoId: \"\", val: null, enabled: True, offscreen: False)";
    public string GetChapters(int? pid, string? title) => "[K1] panel";
    public string InspectSection(string chapter, int maxDepth = 3)
        => GetUiTree(new WindowSelectionRequest(null, null, null), maxDepth);
    public UiOutcome ClickElement(int id, int wait = 3000)
    {
        Clicks++;
        AfterClick?.Invoke();
        if (ThrowAfterClick)
            throw new IOException("Synthetic failure after dispatch.");
        return new(true, "invoked");
    }
    public UiOutcome SilentClick(int id) => ClickElement(id);
    public int Focuses;
    public UiOutcome FocusElement(int id)
    {
        Focuses++;
        return new(true, "focused");
    }
    public ClickOutcome MouseClick(int id, string button = "left", int? x = null, int? y = null) => new(false, "unsupported", null);
    public UiOutcome TypeText(int id, string text, bool clear, bool enter, int wait)
    {
        PhysicalTexts++;
        Literal = text;
        return new(true, "typed");
    }
    public UiOutcome SetText(int id, string text, int wait = 3000) => TypeText(id, text, true, false, wait);
    public int LaunchCalls;
    public string? LaunchedPath;
    public IReadOnlyList<string>? LaunchedArguments;
    public UiOutcome LaunchApp(string path, IReadOnlyList<string> args)
    {
        LaunchCalls++;
        LaunchedPath = path;
        LaunchedArguments = args;
        return new(true, "launched");
    }
    public object GetVisualTree(int? pid, string? title, int maxDepth = 4)
        => ObservationFactory?.Invoke() ?? Observation ?? new VisualTreeNode { Id = ++Id, Name = "button", Role = "Button" };
    public ElementCheckOutcome WaitForElement(
        string? name,
        string? autoId = null,
        string? role = null,
        int? pid = null,
        string? title = null,
        string state = "exists",
        int timeout = 5000)
        => new(false, "unsupported", null);
    public ClipboardOutcome GetClipboard() => new(false, "unsupported", null);
    public UiOutcome SetClipboard(string text) => new(false, "unsupported");
    public UiOutcome PasteClipboard(int? id = null) => new(false, "unsupported");
    public ElementCheckOutcome VerifyElement(
        int? id,
        string? name,
        string? autoId,
        string? role,
        int? pid,
        string? title,
        string? state,
        string? text,
        int timeout = 5000)
    {
        VerifyTimeout = timeout;
        return new(text == null || text == Literal, "checked", null);
    }
    public int VerifyTimeout
    {
        get; private set;
    }
    public UiOutcome CloseWindow(int? pid, string? title) => new(false, "unsupported");
    public UiOutcome MouseMove(int x, int y) => new(false, "unsupported");
    public UiOutcome DragAndDrop(int x, int y, int x2, int y2) => new(false, "unsupported");
    public UiOutcome KeyChord(string[] keys)
    {
        KeyCalls++;
        return new(false, "unsupported");
    }
    public ScreenshotOutcome Screenshot()
    {
        ScreenshotCalls++;
        return ScreenshotEnabled
            ? new(
                true,
                "fixture",
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="))
            : new(false, "unsupported", null);
    }
}

// Stands in for the advisory provider's HTTP endpoint; a case can make it truncate its evidence or invent a label.
internal sealed class ClassifierFixture : HttpMessageHandler
{
    public bool Truncated, InventedLabel; public int RequestVersion;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        RequestVersion = Guid.Parse(request.Headers.GetValues("X-Request-ID").Single()).Version;
        var response = new JsonObject
        {
            ["answers"] = new JsonObject
            {
                ["quality"] = new JsonObject
                {
                    ["type"] = "choice",
                    ["choice"] = InventedLabel ? "invented" : "pass",
                    ["confidence"] = 0.99,
                    ["probabilities"] = new JsonObject { ["pass"] = 0.99, ["fail"] = 0.01 }
                }
            },
            ["usage"] = new JsonObject { ["truncated"] = Truncated }
        };
        return Task.FromResult(
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json")
            });
    }
}
