using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Platforms;

namespace KLaniBot.Guard;

// How the guarded profile treats a tool. The classification is the single place that decides it.
public enum GuardClassification
{
    // Not about the desktop (protocol, files, macros, fleet). Only the principal's scopes limit it.
    ScopeOnly,

    // Reads or focuses one application. The application must be approved by path and executable hash.
    ApplicationBoundRead,

    // Lists windows. The dispatcher drops every window whose application is not approved.
    FilteredApplicationList,

    // Acts on one observed element. A local rule must name application, window, element and action.
    SemanticTarget,

    // Raw keys, coordinates, clipboard writes, window moves, shell and process start: never in the guarded profile.
    BlockedInGuardedMode,

    // Reads the whole desktop or the clipboard: never in the guarded profile.
    WholeDesktopReadBlocked
}

// Use case: an operator lets an AI agent work in one specific application (say, a slicer or a legacy
// accounting program) and wants everything else on that machine to stay out of reach, even if the agent
// holds the "ui" scope. The guarded profile expresses that with local rules the agent cannot change.
// The broad "desktop" profile skips this class on purpose and is the operator's explicit choice.
//
// A tool that is not classified here is refused in the guarded profile. Adding a tool to the contract
// without deciding how the guard treats it must never widen what an agent may do; a test enforces that
// every contract tool has an entry.
public static class UiActionGuard
{
    private const string NoUniqueRule = "No unique local authorization for this application/window/element/action.";

    private static readonly Dictionary<Tool, GuardClassification> Classifications = BuildClassifications();

    public static IReadOnlyDictionary<Tool, GuardClassification> AllClassifications => Classifications;

    // Throws UnauthorizedAccessException for a tool the guard has no classification for.
    public static GuardClassification Classify(Tool action)
    {
        if (!Classifications.TryGetValue(action, out GuardClassification classification))
        {
            throw new UnauthorizedAccessException("The guarded profile has no classification for this action and refuses it.");
        }
        return classification;
    }

    // Throws UnauthorizedAccessException when the guarded profile does not allow the tool or its target.
    public static void Authorize(
        NodePolicy policy,
        PrincipalPolicy principal,
        IUiService uiService,
        Tool action,
        JsonObject arguments)
    {
        var guardOptions = principal.UiGuard;
        if (guardOptions.Mode == UiGuardMode.Desktop)
        {
            return;
        }
        guardOptions.Validate();
        switch (Classify(action))
        {
            case GuardClassification.BlockedInGuardedMode:
                throw new UnauthorizedAccessException(
                    "Node guard blocks raw keys, coordinates, shell and process authority in guarded mode.");
            case GuardClassification.WholeDesktopReadBlocked:
                throw new UnauthorizedAccessException("Guarded mode does not grant whole-desktop or clipboard observation.");
            case GuardClassification.ApplicationBoundRead:
                AuthorizeApplicationBoundRead(guardOptions, uiService, action, arguments);
                return;
            case GuardClassification.SemanticTarget:
                AuthorizeSemanticTarget(policy, guardOptions, uiService, action, arguments);
                return;
            case GuardClassification.FilteredApplicationList:
            case GuardClassification.ScopeOnly:
                return;
        }
    }

    public static bool AllowsApplication(UiGuardOptions guardOptions, int processId)
    {
        try
        {
            AuthorizeApplication(guardOptions, ExecutablePathOfProcess(processId));
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Throws UnauthorizedAccessException when the target is not actionable, no unique rule allows it, or the rule requires a
    // decision the request does not carry.
    public static void AuthorizeTarget(
        NodePolicy policy,
        UiGuardOptions guardOptions,
        UiTargetIdentity target,
        Tool action,
        JsonObject arguments)
    {
        guardOptions.Validate();
        bool actionable = target.Visible && target.Enabled && Path.IsPathFullyQualified(target.ExecutablePath);
        if (!actionable)
        {
            throw new UnauthorizedAccessException("Guard target is not actionable or its executable is unknown.");
        }
        string executable = Path.GetFullPath(target.ExecutablePath);
        UiActionRule rule = FindUniqueRule(guardOptions, target, action, executable);
        if (rule.RequiresDecision && arguments["decision"] is not JsonObject)
        {
            throw new UnauthorizedAccessException("This control requires a recorded decision before every action.");
        }
        if (action == Tool.TypeText)
        {
            AuthorizeText(policy, rule, arguments);
        }
    }

    // Whether the unique rule for this target demands a recorded decision on every action, with or without a dialog. A target that
    // no single rule resolves counts as demanding one, so the caller keeps its conservative path. Only the guarded profile has rules.
    public static bool RuleRequiresDecision(UiGuardOptions guardOptions, UiTargetIdentity target, Tool action)
    {
        if (guardOptions.Mode != UiGuardMode.Guarded)
        {
            return false;
        }
        if (!Path.IsPathFullyQualified(target.ExecutablePath))
        {
            return true;
        }
        try
        {
            string executable = Path.GetFullPath(target.ExecutablePath);
            return FindUniqueRule(guardOptions, target, action, executable).RequiresDecision;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    // Throws UnauthorizedAccessException when the observed process is unknown or not approved.
    private static void AuthorizeApplicationBoundRead(UiGuardOptions guardOptions, IUiService uiService, Tool action, JsonObject arguments)
    {
        if (arguments["id"] is { } element)
        {
            var identity = uiService.GetTargetIdentity(element.GetValue<int>())
                ?? throw new UnauthorizedAccessException("Unknown observation target.");
            AuthorizeApplication(guardOptions, identity.ExecutablePath);
            return;
        }
        if (action is Tool.GetUiTree or Tool.FocusWindow or Tool.GetWindowGeometry && arguments["window"] is { } windowHandle)
        {
            AuthorizeWindowApplication(guardOptions, uiService, windowHandle.GetValue<long>());
            return;
        }
        int processId = arguments["pid"]?.GetValue<int>()
            ?? throw new UnauthorizedAccessException("Guarded observation requires an explicit approved process.");
        AuthorizeApplication(guardOptions, ExecutablePathOfProcess(processId));
    }

    // A native handle names one window, and the selection refuses a process that does not own it, so the program that owns
    // the handle is the one the guard judges. Throws UnauthorizedAccessException for an unknown handle, before anything is read.
    private static void AuthorizeWindowApplication(UiGuardOptions guardOptions, IUiService uiService, long handle)
    {
        WindowInfo window = uiService.ListWindows().FirstOrDefault(candidate => candidate.WindowHandle == handle)
            ?? throw new UnauthorizedAccessException("Unknown window handle.");
        AuthorizeApplication(guardOptions, ExecutablePathOfProcess(window.ProcessId));
    }

    // Throws UnauthorizedAccessException when the input is not a literal replacement or the target cannot be identified.
    private static void AuthorizeSemanticTarget(
        NodePolicy policy,
        UiGuardOptions guardOptions,
        IUiService uiService,
        Tool action,
        JsonObject arguments)
    {
        bool appends = arguments["clear"]?.GetValue<bool>() == false;
        bool pressesEnter = arguments["enter"]?.GetValue<bool>() == true;
        if (pressesEnter || appends)
        {
            throw new UnauthorizedAccessException(
                "Guarded text replaces a literal field value; command keys and implicit concatenation are forbidden.");
        }
        int elementId = arguments["id"]?.GetValue<int>()
            ?? throw new UnauthorizedAccessException("Guarded input requires an observed semantic target.");
        var target = uiService.GetTargetIdentity(elementId)
            ?? throw new UnauthorizedAccessException("Backend cannot establish native target identity; guarded input denied.");
        AuthorizeTarget(policy, guardOptions, target, action, arguments);
    }

    // A matching window title is not proof of application identity. The executable bytes are hashed so
    // that an update or a substituted program loses its authorization. The hash is computed only for a
    // path some rule names: hashing every visible window's program would read hundreds of megabytes
    // for applications the operator never approved (list_windows asks about each of them).
    // Throws UnauthorizedAccessException when the application is unknown or outside the approved rules.
    private static void AuthorizeApplication(UiGuardOptions guardOptions, string application)
    {
        if (!Path.IsPathFullyQualified(application))
        {
            throw new UnauthorizedAccessException("Unknown application.");
        }
        string path = Path.GetFullPath(application);
        var rulesForPath = guardOptions.Rules.Where(rule => SamePath(rule.Application, path)).ToArray();
        if (rulesForPath.Length == 0)
        {
            throw new UnauthorizedAccessException("Application is outside the locally approved observation boundary.");
        }
        string hash = HashExecutable(path);
        if (!rulesForPath.Any(rule => rule.ApplicationSha256 == hash))
        {
            throw new UnauthorizedAccessException("Application is outside the locally approved observation boundary.");
        }
    }

    // Throws UnauthorizedAccessException unless exactly one rule matches.
    private static UiActionRule FindUniqueRule(UiGuardOptions guardOptions, UiTargetIdentity target, Tool action, string executable)
    {
        var candidates = guardOptions.Rules
            .Where(rule => rule.Actions.Contains(action))
            .Where(rule => SamePath(rule.Application, executable))
            .Where(rule => rule.WindowTitle == target.WindowTitle)
            .Where(rule => rule.Role == null || rule.Role == target.Role)
            .Where(rule => rule.Name == null || rule.Name == target.Name)
            .Where(rule => rule.AutoId == null || rule.AutoId == target.AutoId)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new UnauthorizedAccessException(NoUniqueRule);
        }
        string executableHash = HashExecutable(executable);
        var matches = candidates.Where(rule => rule.ApplicationSha256 == executableHash).ToArray();
        if (matches.Length != 1)
        {
            throw new UnauthorizedAccessException(NoUniqueRule);
        }
        return matches[0];
    }

    // Throws UnauthorizedAccessException when the text is missing or outside the rule's text policy.
    private static void AuthorizeText(NodePolicy policy, UiActionRule rule, JsonObject arguments)
    {
        string text = arguments["text"]?.GetValue<string>() ?? throw new UnauthorizedAccessException("Literal text required.");
        if (text.Length > rule.MaxTextLength || rule.TextPolicy == TextInputPolicy.None)
        {
            throw new UnauthorizedAccessException("Text is outside the local field policy.");
        }
        if (rule.TextPolicy == TextInputPolicy.WorkspacePath)
        {
            AuthorizeWorkspacePath(policy, rule, text);
        }
    }

    // File pickers can navigate beyond the application and manage host files.
    // Validate the actual path rather than trusting its visible label.
    // Throws UnauthorizedAccessException unless the text is an existing workspace file of an allowed type.
    private static void AuthorizeWorkspacePath(NodePolicy policy, UiActionRule rule, string text)
    {
        string file = policy.ResolvePath(text);
        bool approvedFile = Path.IsPathFullyQualified(text)
            && File.Exists(file)
            && rule.Extensions.Length > 0
            && rule.Extensions.Contains(Path.GetExtension(file).ToLowerInvariant());
        if (!approvedFile)
        {
            throw new UnauthorizedAccessException("Only an existing approved workspace file type may enter this picker.");
        }
    }

    // A process that cannot be inspected is not an approved process, so every failure to find its
    // executable is a denial rather than an unrelated error.
    // Throws UnauthorizedAccessException when the executable cannot be established.
    private static string ExecutablePathOfProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName ?? throw new UnauthorizedAccessException("Unknown process executable.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException)
        {
            throw new UnauthorizedAccessException("The process executable cannot be established.", exception);
        }
    }

    // Throws UnauthorizedAccessException when the executable cannot be read.
    private static string HashExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("The approved executable cannot be read.", exception);
        }
    }

    private static bool SamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), right, comparison);
    }

    private static Dictionary<Tool, GuardClassification> BuildClassifications()
    {
        var table = new Dictionary<Tool, GuardClassification>();
        Add(table, GuardClassification.BlockedInGuardedMode,
            Tool.OperateApplication, Tool.Hotkey, Tool.MouseClick, Tool.MouseMove, Tool.DragAndDrop, Tool.PasteClipboard, Tool.SetClipboard,
            Tool.RunTerminalCommand, Tool.LaunchProcess, Tool.CloseWindow, Tool.SetWindowGeometry);
        Add(table, GuardClassification.WholeDesktopReadBlocked, Tool.TakeScreenshot, Tool.GetClipboard);
        Add(table, GuardClassification.ApplicationBoundRead,
            Tool.InspectApplication, Tool.InspectSection, Tool.GetUiTree, Tool.GetUiDelta, Tool.FindElements, Tool.GetChapters,
            Tool.FocusWindow, Tool.WaitElement, Tool.VerifyElement, Tool.GetWindowGeometry);
        Add(table, GuardClassification.FilteredApplicationList, Tool.ListWindows);
        Add(table, GuardClassification.SemanticTarget, Tool.ClickElement, Tool.SilentClick, Tool.TypeText);
        Add(table, GuardClassification.ScopeOnly,
            Tool.GetMachineCapabilities, Tool.ListAvailableMcps, Tool.GetActionStatus, Tool.EndSession, Tool.CollectInventory,
            Tool.ListMacros, Tool.GetMacro, Tool.GetMacroTrace, Tool.SaveMacro, Tool.RunMacro,
            Tool.FsListDirectory, Tool.FsReadFile, Tool.FsWriteFile, Tool.FsSearchFiles, Tool.FsGetMetadata,
            Tool.ListNodes, Tool.EvaluateTrace, Tool.BackupPlan, Tool.BackupVerify,
            Tool.BackupCreate, Tool.BackupRestore, Tool.ApplicationMcpRead, Tool.ApplicationMcpCall);
        return table;
    }

    private static void Add(Dictionary<Tool, GuardClassification> table, GuardClassification classification, params Tool[] actions)
    {
        foreach (Tool action in actions)
        {
            table.Add(action, classification);
        }
    }
}
