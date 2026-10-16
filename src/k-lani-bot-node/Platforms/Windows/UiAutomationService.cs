using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows.Automation;
using KLaniBot.Actions;
using KLaniBot.Guard;

namespace KLaniBot.Platforms.Windows;

// The Windows backend through UI Automation, with Win32 for windows, input, clipboard and screenshots. It works
// only while the node's session has an input desktop: a disconnected RDP session or a locked screen is reported
// and refused (DesktopSessionInfo) instead of sending input nowhere.
//
// Foreground input activates the top-level window of the target and waits until Windows really made it the foreground
// window, or stops. Activation sends no keys or clicks; a refused activation names the process holding the foreground.
// Background clicks use the Invoke, Toggle and SelectionItem patterns only; silent_click may also send
// BM_CLICK to a native button. Guarded text needs a writable ValuePattern, with no keyboard or clipboard
// fallback. Modal state comes from the WindowPattern: a target inside a modal window and a target blocked by
// another modal window are both detected. A cloaked window (another virtual desktop) is never chosen.
public partial class UiAutomationService : IUiService
{
    private const string NoVisibilityWithoutInputDesktop =
        "UI Automation data is no proof of visibility: the Windows session has no active input desktop.";

    // Throws ArgumentException for a stale target, UnauthorizedAccessException when its window is unknown and InvalidOperationException
    // when a dialog blocks it or it cannot be brought to the front.
    public void PrepareElementInteraction(int id, WindowInteractionMode mode, ModalDialogMode dialogs, bool decisionRecorded)
    {
        if (!_elementCache.TryGetValue(id, out var element))
        {
            throw new ArgumentException("Stale target. Observe again.");
        }
        var window = element;
        for (int depth = 0; depth < 64 && window != null && window.Current.ControlType != ControlType.Window; depth++)
        {
            window = TreeWalker.ControlViewWalker.GetParent(window);
        }
        if (window == null || window.Current.ControlType != ControlType.Window
            || window.Current.ProcessId != element.Current.ProcessId)
        {
            throw new UnauthorizedAccessException("Target window identity is unknown.");
        }
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        if (handle == IntPtr.Zero || !NativeMethods.IsWindowEnabled(handle))
        {
            throw new InvalidOperationException("The target window is blocked. Review the dialog and observe the target again.");
        }
        if (window.TryGetCurrentPattern(WindowPattern.Pattern, out object? pattern) && pattern is WindowPattern windowPattern)
        {
            bool blockedByAnotherModal = windowPattern.Current.WindowInteractionState == WindowInteractionState.BlockedByModalWindow;
            ModalDialogPolicy.Require(windowPattern.Current.IsModal, blockedByAnotherModal, dialogs, decisionRecorded);
        }
        if (mode == WindowInteractionMode.Foreground)
        {
            RequireForeground(handle);
        }
    }
    // How long the foreground is awaited after an activation call, and how often it is checked in the meantime.
    private const int ForegroundWaitMilliseconds = 500;
    private const int ForegroundPollMilliseconds = 10;

    // Throws InvalidOperationException when the window cannot be brought to the front; no input is sent in that case.
    private static void RequireForeground(IntPtr handle)
    {
        ForegroundActivationStep outcome = BringWindowForward(handle);
        if (outcome == ForegroundActivationStep.RefuseCloaked)
        {
            throw new InvalidOperationException(
                "The target window is hidden on this desktop or its visibility is unknown. No input was sent.");
        }

        if (outcome != ForegroundActivationStep.AlreadyForeground)
        {
            throw new InvalidOperationException(ForegroundHolderMessage());
        }
    }

    // Runs the steps the plan orders until the window is in front, or until the plan refuses it or gives up.
    private static ForegroundActivationStep BringWindowForward(IntPtr handle)
    {
        IntPtr window = ResolveActivationWindow(handle);
        ForegroundActivationFacts facts = ReadActivationFacts(window);
        ForegroundActivationStep step = ForegroundActivationPlan.NextStep(facts);
        while (step is ForegroundActivationStep.RestoreAndActivate or ForegroundActivationStep.AttachAndActivate)
        {
            facts = ExecuteActivationStep(window, step, facts);
            step = ForegroundActivationPlan.NextStep(facts);
        }

        return step;
    }

    // The handle of a modern app can be an inner window while the foreground window is its top-level frame.
    private static IntPtr ResolveActivationWindow(IntPtr handle)
    {
        IntPtr root = NativeMethods.GetAncestor(handle, NativeMethods.GA_ROOT);
        return root == IntPtr.Zero ? handle : root;
    }

    private static ForegroundActivationFacts ReadActivationFacts(IntPtr window)
    {
        WindowCloakState cloakState = ReadCloakState(window);
        bool isForeground = NativeMethods.GetForegroundWindow() == window;
        bool isMinimized = NativeMethods.IsIconic(window);

        return new ForegroundActivationFacts(
            cloakState,
            isForeground,
            isMinimized,
            ForegroundAttemptOutcome.NotAttempted,
            ForegroundAttemptOutcome.NotAttempted);
    }

    private static WindowCloakState ReadCloakState(IntPtr window)
    {
        bool? cloaked = IsWindowCloaked(window);
        if (cloaked == null)
        {
            return WindowCloakState.Unknown;
        }

        return cloaked.Value ? WindowCloakState.Cloaked : WindowCloakState.NotCloaked;
    }

    private static ForegroundActivationFacts ExecuteActivationStep(
        IntPtr window,
        ForegroundActivationStep step,
        ForegroundActivationFacts facts)
    {
        if (step == ForegroundActivationStep.RestoreAndActivate)
        {
            return RestoreAndActivate(window, facts);
        }

        return AttachAndActivate(window, facts);
    }

    private static ForegroundActivationFacts RestoreAndActivate(IntPtr window, ForegroundActivationFacts facts)
    {
        if (ForegroundActivationPlan.ShouldRestoreBeforeActivation(facts))
        {
            NativeMethods.ShowWindow(window, NativeMethods.SW_RESTORE);
        }

        bool activated = NativeMethods.SetForegroundWindow(window) && WaitForForeground(window);
        ForegroundAttemptOutcome outcome = activated ? ForegroundAttemptOutcome.Succeeded : ForegroundAttemptOutcome.Failed;

        return facts with
        {
            IsForeground = activated,
            RestoreAttempt = outcome
        };
    }

    // Sharing the input queue of the foreground thread lets Windows accept the request. The queues are shared only for the
    // activation calls, because a shared queue also shares keyboard state with the other application.
    private static ForegroundActivationFacts AttachAndActivate(IntPtr window, ForegroundActivationFacts facts)
    {
        uint currentThread = NativeMethods.GetCurrentThreadId();
        uint foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        bool attached = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        bool accepted;
        try
        {
            NativeMethods.BringWindowToTop(window);
            accepted = NativeMethods.SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        bool activated = accepted && WaitForForeground(window);
        ForegroundAttemptOutcome outcome = activated ? ForegroundAttemptOutcome.Succeeded : ForegroundAttemptOutcome.Failed;

        return facts with
        {
            IsForeground = activated,
            AttachAttempt = outcome
        };
    }

    // Activation is asynchronous across input queues; the return value of SetForegroundWindow alone does not establish
    // that the window actually received the foreground.
    private static bool WaitForForeground(IntPtr window)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ForegroundWaitMilliseconds)
        {
            if (NativeMethods.GetForegroundWindow() == window)
            {
                return true;
            }

            Thread.Sleep(ForegroundPollMilliseconds);
        }

        return false;
    }

    // Names the process that holds the foreground; when its name cannot be read, no name is guessed.
    private static string ForegroundHolderMessage()
    {
        string holder = TryReadForegroundProcessName(out string processName) ? processName : "another application";

        return $"The target window could not be brought to the front; {holder} holds the foreground. No input was sent.";
    }

    // Returns false when no window holds the foreground or its process ended before the name could be read.
    private static bool TryReadForegroundProcessName(out string processName)
    {
        processName = string.Empty;
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out uint processId);
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
    private const int MaximumTextElements = 300;

    // The names in the element's window, in tree order; texts and controls only, never field contents.
    public string? ReadWindowText(int id)
    {
        AutomationElement? element;
        lock (_elementCache)
        {
            _elementCache.TryGetValue(id, out element);
        }
        if (element == null)
        {
            return null;
        }
        try
        {
            var names = new List<string>();
            CollectNames(WindowOf(element), names);
            return string.Join("\n", names);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException)
        {
            return null;
        }
    }

    private static AutomationElement WindowOf(AutomationElement element)
    {
        AutomationElement current = element;
        while (current.Current.ControlType != ControlType.Window)
        {
            AutomationElement? parent = TreeWalker.ControlViewWalker.GetParent(current);
            if (parent == null || parent == AutomationElement.RootElement)
                break;
            current = parent;
        }
        return current;
    }

    private static void CollectNames(AutomationElement window, List<string> names)
    {
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Stack<AutomationElement>();
        pending.Push(window);
        while (pending.Count > 0 && names.Count < MaximumTextElements)
        {
            AutomationElement next = pending.Pop();
            string name = next.Current.Name ?? "";
            if (name.Length > 0)
                names.Add(name);
            var children = new List<AutomationElement>();
            for (var child = walker.GetFirstChild(next); child != null; child = walker.GetNextSibling(child))
                children.Add(child);
            for (int index = children.Count - 1; index >= 0; index--)
                pending.Push(children[index]);
        }
    }

    // Throws ArgumentException for a stale target and UnauthorizedAccessException when its window or executable is unknown.
    public UiTargetIdentity? GetTargetIdentity(int id)
    {
        if (!_elementCache.TryGetValue(id, out var element))
            throw new ArgumentException("Stale target.");
        var current = element.Current;
        var window = element;
        for (int depth = 0; depth < 64 && window != null && window.Current.ControlType != ControlType.Window; depth++)
            window = TreeWalker.ControlViewWalker.GetParent(window);
        if (window == null || window.Current.ControlType != ControlType.Window || window.Current.ProcessId != current.ProcessId)
            throw new UnauthorizedAccessException("Target window identity is unknown.");
        using var process = Process.GetProcessById(current.ProcessId);
        return new(
            current.ProcessId,
            process.MainModule?.FileName ?? throw new UnauthorizedAccessException("Target executable is unknown."),
            window.Current.Name,
            current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal),
            current.Name,
            current.AutomationId,
            !current.IsOffscreen,
            current.IsEnabled);
    }
    public UiOutcome SetGuardedText(int id, string text)
    {
        if (!_elementCache.TryGetValue(id, out var element)
            || !element.Current.IsEnabled
            || element.Current.IsOffscreen
            || !element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern)
            || pattern is not ValuePattern value
            || value.Current.IsReadOnly)
            return new(false, "No actionable UIA ValuePattern; guarded keyboard/clipboard fallback is disabled.");
        value.SetValue(text);
        return new(true, "ValuePattern dispatched; verify the application result.");
    }
    private readonly Dictionary<int, AutomationElement> _elementCache = new();
    private int _nextElementId = 1;

    public List<WindowInfo> ListWindows()
    {
        var result = new List<WindowInfo>();
        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            if (DescribeWindow(hWnd) is { } window)
                result.Add(window);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // Null for a hidden or untitled window and for the desktop's own background shells.
    private static WindowInfo? DescribeWindow(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindowVisible(hWnd))
            return null;
        int length = NativeMethods.GetWindowTextLength(hWnd);
        if (length == 0)
            return null;
        var sb = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        string title = sb.ToString();
        if (string.IsNullOrWhiteSpace(title))
            return null;

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        WindowProcess process = ReadWindowProcess(pid);
        // Filter out internal background shells
        if (process.Name is "dwm" or "ShellExperienceHost")
            return null;
        return new WindowInfo((int)pid, process.Name, process.Version, title, hWnd)
        {
            ExecutablePath = process.ExecutablePath,
            ProcessStartedAtUtc = process.StartedAtUtc,
            SessionId = process.SessionId,
            IsCloaked = IsWindowCloaked(hWnd),
            IsForeground = hWnd == NativeMethods.GetForegroundWindow(),
            Kind = ReadWindowKind(hWnd)
        };
    }

    // What is known of the process behind a window; a field that could not be read stays unknown.
    private sealed record WindowProcess(string Name, string Version, string? ExecutablePath, DateTimeOffset? StartedAtUtc, int? SessionId);

    private static WindowProcess ReadWindowProcess(uint pid)
    {
        string processName = "Unknown";
        string appVersion = "Unknown";
        string? executablePath = null;
        DateTimeOffset? processStartedAtUtc = null;
        int? sessionId = null;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
            try
            {
                processStartedAtUtc = proc.StartTime.ToUniversalTime();
                sessionId = proc.SessionId;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Process lifetime/permissions may change during discovery;
                // missing identity fields remain unknown rather than guessed.
            }
            try
            {
                if (proc.MainModule?.FileName != null)
                {
                    executablePath = proc.MainModule.FileName;
                    var vInfo = FileVersionInfo.GetVersionInfo(executablePath);
                    appVersion = vInfo.ProductVersion ?? vInfo.FileVersion ?? "Unknown";
                }
            }
            catch
            {
                // Elevated or protected processes refuse module access; the window is listed without path and version.
            }
        }
        catch
        {
            // A process can exit between enumeration and inspection; the window is then listed with what is known.
        }
        return new WindowProcess(processName, appVersion, executablePath, processStartedAtUtc, sessionId);
    }

    public bool FocusWindow(WindowSelectionRequest request)
    {
        var windows = ListWindows();
        var match = SelectWindow(windows, request);

        if (match == null)
            return false;

        return BringWindowForward(match.Handle) == ForegroundActivationStep.AlreadyForeground;
    }

    public object? GetWindowGeometry(int? pid, string? titleQuery)
    {
        var windows = ListWindows();
        var match = SelectWindow(windows, pid, titleQuery);
        if (match == null)
            return null;

        NativeMethods.GetWindowRect(match.Handle, out var rect);
        uint dpi = WindowDpi(match.Handle);
        int screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        return new
        {
            pid = match.ProcessId,
            process = match.ProcessName,
            title = match.Title,
            state = WindowState(match.Handle),
            bounds = new
            {
                x = rect.Left,
                y = rect.Top,
                width = rect.Width,
                height = rect.Height
            },
            dpi,
            dpiScale = $"{Math.Round(dpi / 96.0 * 100)}%",
            screenResolution = new
            {
                width = screenW,
                height = screenH
            }
        };
    }

    private static string WindowState(IntPtr handle)
    {
        var wp = new WINDOWPLACEMENT();
        wp.length = Marshal.SizeOf(wp);
        NativeMethods.GetWindowPlacement(handle, ref wp);

        return wp.showCmd switch
        {
            NativeMethods.SW_MAXIMIZE => "Maximized",
            NativeMethods.SW_SHOWMINIMIZED => "Minimized",
            _ => "Normal"
        };
    }

    private const uint DefaultDpi = 96;

    private static uint WindowDpi(IntPtr handle)
    {
        uint dpi = DefaultDpi;
        try
        {
            dpi = NativeMethods.GetDpiForWindow(handle);
        }
        catch
        {
            // GetDpiForWindow is missing before Windows 10 1607; the zero check below falls back to 96 DPI.
        }
        return dpi == 0 ? DefaultDpi : dpi;
    }

    public GeometryOutcome SetWindowGeometry(
        int? pid,
        string? titleQuery,
        string? state,
        int? width,
        int? height,
        int? x,
        int? y)
    {
        var windows = ListWindows();
        var match = SelectWindow(windows, pid, titleQuery);
        if (match == null)
            return new(false, "Target window not found", null);

        if (!string.IsNullOrEmpty(state))
            ShowInState(match.Handle, state);
        if (width.HasValue || height.HasValue || x.HasValue || y.HasValue)
            MoveAndResize(match.Handle, width, height, x, y);
        NativeMethods.SetForegroundWindow(match.Handle);
        Thread.Sleep(100);

        var geom = GetWindowGeometry(match.ProcessId, null);
        return new(true, "Window geometry updated", geom);
    }

    // "Maximized" and "Minimized" as named; any other state word restores the window.
    private static void ShowInState(IntPtr handle, string state)
    {
        if (state.Equals("Maximized", StringComparison.OrdinalIgnoreCase))
            NativeMethods.ShowWindow(handle, NativeMethods.SW_MAXIMIZE);
        else if (state.Equals("Minimized", StringComparison.OrdinalIgnoreCase))
            NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOWMINIMIZED);
        else
            NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
    }

    // A value that is not given keeps the window's current one.
    private static void MoveAndResize(IntPtr handle, int? width, int? height, int? x, int? y)
    {
        NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
        NativeMethods.GetWindowRect(handle, out var curRect);

        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            x ?? curRect.Left,
            y ?? curRect.Top,
            width ?? curRect.Width,
            height ?? curRect.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
    }

    public ClickOutcome MouseClick(int id, string button = "left", int? x = null, int? y = null)
    {
        if (x.HasValue && y.HasValue)
        {
            NativeMethods.SetCursorPos(x.Value, y.Value);
            Thread.Sleep(30);
            string btn = button.ToLowerInvariant();
            PressButton(btn);
            return new(true, $"Mouse click at ({x.Value}, {y.Value}) performed.", new
            {
                clickedAt = new
                {
                    x = x.Value,
                    y = y.Value
                },
                button = btn
            });
        }

        AutomationElement? element;
        lock (_elementCache)
        {
            _elementCache.TryGetValue(id, out element);
        }
        if (element == null)
        {
            return new(false, $"Element id {id} is not in the current UI tree. Call get_ui_tree again.", null);
        }
        try
        {
            return ClickAtElement(id, element, button);
        }
        catch (Exception ex)
        {
            return new(false, $"Mouse click on element [{id}] failed: {ex.Message}", null);
        }
    }

    // The click point must still hit the element after a short pause, so a moving or covered target is refused.
    private ClickOutcome ClickAtElement(int id, AutomationElement element, string button)
    {
        var rect = element.Current.BoundingRectangle;
        bool hasClickable = element.TryGetClickablePoint(out System.Windows.Point pt);
        int clickX = hasClickable ? (int)pt.X : (int)(rect.X + rect.Width / 2);
        int clickY = hasClickable ? (int)pt.Y : (int)(rect.Y + rect.Height / 2);
        bool isOffscreen = element.Current.IsOffscreen;
        if (isOffscreen || !element.Current.IsEnabled || !HitMatches(element, clickX, clickY))
            return new(false, "Target is disabled, offscreen or obscured.", null);
        Thread.Sleep(30);
        if (element.Current.BoundingRectangle != rect || !HitMatches(element, clickX, clickY))
            return new(false, "Target moved before input.", null);

        // Move mouse to target
        NativeMethods.SetCursorPos(clickX, clickY);
        Thread.Sleep(30);
        string btnLower = button.ToLowerInvariant();
        PressButton(btnLower);
        var info = new
        {
            elementId = id,
            role = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
            name = element.Current.Name ?? "",
            clickedAt = new
            {
                x = clickX,
                y = clickY
            },
            isOffscreen,
            button = btnLower
        };
        return new(true, $"Mouse click ({btnLower}) on element [{id}] at ({clickX}, {clickY}) performed.", info);
    }

    // "right" and "double" as named, any other button word is a left click.
    private static void PressButton(string button)
    {
        if (button == "right")
        {
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
            return;
        }

        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        if (button == "double")
        {
            Thread.Sleep(50);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }
    }

    public static AutomationElement? ResolveActionableElement(
        AutomationElement? element,
        out bool resolvedFromChild,
        out string? originalRole)
    {
        resolvedFromChild = false;
        originalRole = null;
        if (element == null)
            return null;

        try
        {
            string role = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
            originalRole = role;
            if (IsActionableRole(role))
            {
                return element;
            }
            if (ActionableAncestor(element) is { } ancestor)
            {
                resolvedFromChild = true;
                return ancestor;
            }
        }
        catch
        {
            // The tree can change while it is walked; a vanished ancestor ends the search with what was found.
        }
        return element;
    }

    // The nearest ancestor with an actionable role or pattern, below the window; null when there is none.
    private static AutomationElement? ActionableAncestor(AutomationElement element)
    {
        var current = element;
        while (current != null)
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(current)
                      ?? TreeWalker.RawViewWalker.GetParent(current);
            if (parent == null || parent == AutomationElement.RootElement)
                break;
            string parentRole = parent.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
            if (IsActionableRole(parentRole))
                return parent;
            if (parent.TryGetCurrentPattern(InvokePattern.Pattern, out _) ||
                parent.TryGetCurrentPattern(TogglePattern.Pattern, out _) ||
                parent.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
                return parent;
            if (parentRole is "Window" or "TitleBar" or "MenuBar")
                break;
            current = parent;
        }
        return null;
    }

    private static bool IsActionableRole(string role)
    {
        return role is "Button" or "SplitButton" or "MenuItem" or "CheckBox" or "RadioButton"
                    or "ComboBox" or "TabItem" or "ListItem" or "Hyperlink" or "TreeItem";
    }

    public object? GetVisualTree(int? pid, string? titleQuery, int maxDepth = 4)
    {
        var windows = ListWindows();
        var match = SelectWindow(windows, pid, titleQuery);

        if (match == null)
            return null;

        AutomationElement rootElement;
        try
        {
            rootElement = AutomationElement.FromHandle(match.Handle);
        }
        catch
        {
            // The window closed after it was listed; the caller reports a missing window.
            return null;
        }

        lock (_elementCache)
        {
            _elementCache.Clear();

        }

        var walker = TreeWalker.ControlViewWalker;
        var rootNode = BuildVisualTreeNode(rootElement, walker, 0, maxDepth);

        return new
        {
            pid = match.ProcessId,
            process = match.ProcessName,
            title = match.Title,
            version = match.AppVersion,
            root = rootNode
        };
    }

    private VisualTreeNode BuildVisualTreeNode(AutomationElement element, TreeWalker walker, int depth, int maxDepth)
    {
        int id;
        lock (_elementCache)
        {
            id = _nextElementId++;
            _elementCache[id] = element;
        }

        ElementProperties properties = ReadProperties(element);
        var node = new VisualTreeNode
        {
            Id = id,
            Role = properties.Role,
            Name = properties.Name,
            AutoId = properties.AutoId,
            Shortcut = properties.Shortcut,
            IsOffscreen = properties.IsOffscreen,
            IsEnabled = element.Current.IsEnabled,
            Bounds = properties.Bounds
        };
        if (depth < maxDepth)
        {
            AddChildren(node, element, walker, depth, maxDepth);
        }
        return node;
    }

    // What the tree shows of one element; a property that could not be read keeps its default.
    private sealed record ElementProperties(string Role, string Name, string AutoId, string? Shortcut, bool IsOffscreen, object Bounds);

    private static ElementProperties ReadProperties(AutomationElement element)
    {
        string role = "Unknown";
        string name = "";
        string autoId = "";
        string? shortcut = null;
        bool isOffscreen = false;
        var boundsObj = (object)new
        {
            x = 0,
            y = 0,
            width = 0,
            height = 0
        };
        try
        {
            role = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
            name = element.Current.Name ?? "";
            autoId = element.Current.AutomationId ?? "";
            shortcut = element.GetCurrentPropertyValue(AutomationElement.AcceleratorKeyProperty) as string;
            isOffscreen = element.Current.IsOffscreen;
            var r = element.Current.BoundingRectangle;
            boundsObj = new
            {
                x = (int)r.X,
                y = (int)r.Y,
                width = (int)r.Width,
                height = (int)r.Height
            };
        }
        catch
        {
            // The element can vanish between reads; it is still listed, with the properties that were read.
        }
        return new ElementProperties(role, name, autoId, shortcut, isOffscreen, boundsObj);
    }

    private void AddChildren(VisualTreeNode node, AutomationElement element, TreeWalker walker, int depth, int maxDepth)
    {
        try
        {
            var child = walker.GetFirstChild(element);
            while (child != null)
            {
                var childNode = BuildVisualTreeNode(child, walker, depth + 1, maxDepth);
                node.Children.Add(childNode);
                child = walker.GetNextSibling(child);
            }
        }
        catch
        {
            // Children can vanish while they are enumerated; the subtree keeps the children read so far.
        }
    }

    private readonly Dictionary<string, AutomationElement> _chapterCache = new(StringComparer.OrdinalIgnoreCase);

    public string GetChapters(int? pid, string? titleQuery)
    {
        var windows = ListWindows();
        var match = SelectWindow(windows, pid, titleQuery);
        if (match == null)
            return "error: Target window not found.";
        AutomationElement rootElement;
        try
        {
            rootElement = AutomationElement.FromHandle(match.Handle);
        }
        catch (Exception ex) { return $"error: Could not load the root element: {ex.Message}"; }

        lock (_chapterCache)
        {
            _chapterCache.Clear();
        }
        var sb = new StringBuilder();
        sb.AppendLine($"window: \"{match.Title}\"");
        sb.AppendLine($"process: \"{match.ProcessName}\" (PID: {match.ProcessId}, Version: {match.AppVersion})");
        sb.AppendLine("chapters (table of contents):");
        AppendChapters(sb, rootElement);
        return sb.ToString();
    }

    // Each top-level child of the window becomes a chapter K1, K2, ... that inspect_section can open.
    private void AppendChapters(StringBuilder sb, AutomationElement rootElement)
    {
        var walker = TreeWalker.ControlViewWalker;
        int chapterIndex = 1;
        try
        {
            var child = walker.GetFirstChild(rootElement);
            while (child != null)
            {
                string role = child.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
                string name = child.Current.Name ?? "";
                string autoId = child.Current.AutomationId ?? "";
                string chapterId = $"K{chapterIndex++}";
                lock (_chapterCache)
                {
                    _chapterCache[chapterId] = child;
                }

                var line = new StringBuilder();
                line.Append($"  - [{chapterId}] {role}");
                if (!string.IsNullOrEmpty(name))
                    line.Append($" \"{name}\"");
                if (!string.IsNullOrEmpty(autoId))
                    line.Append($" (autoId: {autoId})");
                sb.AppendLine(line.ToString());
                child = walker.GetNextSibling(child);
            }
        }
        catch
        {
            // Children can vanish while they are enumerated; the listing keeps the entries read so far.
        }
    }

    public string InspectSection(string chapterId, int maxDepth = 3)
    {
        AutomationElement? sectionElement;
        lock (_chapterCache)
        {
            _chapterCache.TryGetValue(chapterId, out sectionElement);
        }

        if (sectionElement == null)
        {
            return $"error: Chapter '{chapterId}' not found. Call get_chapters first.";
        }

        lock (_elementCache)
        {
            _elementCache.Clear();

        }

        var sb = new StringBuilder();
        string role = sectionElement.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
        string name = sectionElement.Current.Name ?? "";
        sb.AppendLine($"chapter: [{chapterId}] {role} \"{name}\"");
        sb.AppendLine("sections & controls:");

        var walker = TreeWalker.ControlViewWalker;
        TraverseElement(sectionElement, walker, 1, maxDepth, sb);

        return sb.ToString();
    }

    public string GetUiTree(WindowSelectionRequest request, int maxDepth = 4)
    {
        var windows = ListWindows();
        var match = SelectWindow(windows, request);

        if (match == null)
        {
            return "error: Target window not found.";
        }

        AutomationElement rootElement;
        try
        {
            rootElement = AutomationElement.FromHandle(match.Handle);
        }
        catch (Exception ex)
        {
            return $"error: Could not load the root element: {ex.Message}";
        }

        lock (_elementCache)
        {
            _elementCache.Clear();

        }

        var sb = new StringBuilder();
        sb.AppendLine($"window: \"{match.Title}\"");
        sb.AppendLine($"process: \"{match.ProcessName}\" (PID: {match.ProcessId}, Version: {match.AppVersion})");
        sb.AppendLine("elements:");

        var walker = TreeWalker.ControlViewWalker;
        TraverseElement(rootElement, walker, 1, maxDepth, sb);

        return sb.ToString();
    }

    // Indentation levels of the common rows: the synthetic desktop, the target window, then the window's descendants.
    private const int DesktopRowDepth = 0;
    private const int WindowRowDepth = 1;
    private const int WindowChildDepth = 2;

    // The common rows for the compact gateway. Unlike the condensed text, every element is kept and nothing swallows a
    // failed read: an unknown field or a broken walk becomes an error, so the gateway never sees a partial tree as complete.
    public string GetCommonUiTree(WindowSelectionRequest request, int maxDepth = 4)
    {
        var windows = ListWindows();
        var match = SelectWindow(windows, request);
        if (match == null)
        {
            return "error: Target window not found.";
        }

        AutomationElement windowElement;
        try
        {
            windowElement = AutomationElement.FromHandle(match.Handle);
        }
        catch (Exception ex)
        {
            return $"error: Could not load the root element: {ex.Message}";
        }

        lock (_elementCache)
        {
            _elementCache.Clear();
        }

        var rows = new List<CommonUiRowReading>();
        bool walkComplete = TryAddDesktopRow(rows) && TryAddCommonWindow(windowElement, match.Title, maxDepth, rows);

        return CommonUiRowFormat.TryFormat(rows, walkComplete, out string text) ? text : CommonUiRowFormat.IncompleteTreeError;
    }

    // The desktop is the synthetic root, as on the other backends. It is not cached, so no action can target it.
    private bool TryAddDesktopRow(List<CommonUiRowReading> rows)
    {
        try
        {
            CommonUiRowReading desktop = ReadCommonRow(AutomationElement.RootElement, DesktopRowDepth, false);
            rows.Add(desktop with
            {
                Role = "Root"
            });

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The window row carries the title that list_windows reports, because the compact gateway binds the window to that title.
    private bool TryAddCommonWindow(AutomationElement window, string title, int maxDepth, List<CommonUiRowReading> rows)
    {
        rows.Add(ReadCommonRow(window, WindowRowDepth, true) with
        {
            Name = title
        });

        // The window row adds one level above the window content, so the content keeps the condensed depth budget.
        return TryAddCommonChildren(window, WindowChildDepth, maxDepth + WindowRowDepth, rows);
    }

    // Returns false when a child cannot be enumerated or an element lies below maxDepth; the caller then refuses the tree.
    private bool TryAddCommonChildren(AutomationElement parent, int depth, int maxDepth, List<CommonUiRowReading> rows)
    {
        TreeWalker walker = TreeWalker.ControlViewWalker;
        try
        {
            if (depth > maxDepth)
            {
                return walker.GetFirstChild(parent) == null;
            }

            AutomationElement? child = walker.GetFirstChild(parent);
            while (child != null)
            {
                rows.Add(ReadCommonRow(child, depth, true));
                if (!TryAddCommonChildren(child, depth + 1, maxDepth, rows))
                {
                    return false;
                }

                child = walker.GetNextSibling(child);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Properties are read live from the element. When any read throws, every field of the row stays unknown and is refused.
    private CommonUiRowReading ReadCommonRow(AutomationElement element, int depth, bool cacheElement)
    {
        int id = AssignElementId(element, cacheElement);
        try
        {
            AutomationElement.AutomationElementInformation current = element.Current;
            string role = current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal);
            CommonValueReading value = ReadCommonValue(element, current.IsPassword);

            return new CommonUiRowReading(depth, id, role, current.Name, current.AutomationId, value, current.IsEnabled,
                current.IsOffscreen);
        }
        catch (Exception)
        {
            var unknownValue = new CommonValueReading(CommonValueState.Unreadable, null);

            return new CommonUiRowReading(depth, id, null, null, null, unknownValue, null, null);
        }
    }

    private int AssignElementId(AutomationElement element, bool cacheElement)
    {
        lock (_elementCache)
        {
            int id = _nextElementId++;
            if (cacheElement)
            {
                _elementCache[id] = element;
            }

            return id;
        }
    }

    // A password value is withheld on purpose, so it is written as null. A value that cannot be read stays unknown.
    private static CommonValueReading ReadCommonValue(AutomationElement element, bool isPassword)
    {
        if (isPassword)
        {
            return new CommonValueReading(CommonValueState.Withheld, null);
        }

        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern) || pattern is not ValuePattern valuePattern)
            {
                return new CommonValueReading(CommonValueState.Absent, null);
            }

            string? text = valuePattern.Current.Value;

            return text == null
                ? new CommonValueReading(CommonValueState.Unreadable, null)
                : new CommonValueReading(CommonValueState.Read, text);
        }
        catch (Exception)
        {
            return new CommonValueReading(CommonValueState.Unreadable, null);
        }
    }

    private void TraverseElement(AutomationElement element, TreeWalker walker, int depth, int maxDepth, StringBuilder sb)
    {
        if (depth > maxDepth)
            return;

        try
        {
            var child = walker.GetFirstChild(element);
            while (child != null)
            {
                RecordAndFormatElement(child, depth, sb);
                TraverseElement(child, walker, depth + 1, maxDepth, sb);
                child = walker.GetNextSibling(child);
            }
        }
        catch
        {
            // Children can vanish while they are enumerated (UIA reports that as a COM error); the snapshot keeps what was read.
        }
    }

    private void RecordAndFormatElement(AutomationElement element, int depth, StringBuilder sb)
    {
        try
        {
            string role = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
            string name = element.Current.Name ?? "";
            string autoId = element.Current.AutomationId ?? "";

            // Check if element is interactive or has valuable content
            bool isInteractive = role is "Button" or "Edit" or "MenuItem" or "CheckBox" or "RadioButton"
                                    or "ComboBox" or "TabItem" or "ListItem" or "Hyperlink" or "SplitButton";
            if (!isInteractive && string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            int id;
            lock (_elementCache)
            {
                id = _nextElementId++;
                _elementCache[id] = element;
            }
            sb.AppendLine(DescribeElementLine(element, depth, id, role, name, autoId));
        }
        catch
        {
            // The element vanished or refused access while it was described; it is left out of the snapshot.
        }
    }

    // One line of the chapter text: id, role, name, automation id, the shortcut or access key, a value and offscreen.
    private static string DescribeElementLine(AutomationElement element, int depth, int id, string role, string name, string autoId)
    {
        string? shortcut = element.GetCurrentPropertyValue(AutomationElement.AcceleratorKeyProperty) as string;
        string? accessKey = element.GetCurrentPropertyValue(AutomationElement.AccessKeyProperty) as string;
        string? value = null;
        if (!element.Current.IsPassword
            && element.TryGetCurrentPattern(ValuePattern.Pattern, out object? vp)
            && vp is ValuePattern valPat)
        {
            value = valPat.Current.Value;
        }

        string indent = new string(' ', depth * 2);
        var itemDesc = new StringBuilder();
        itemDesc.Append($"{indent}- [{id}] {role}");
        if (!string.IsNullOrEmpty(name))
            itemDesc.Append($" \"{name}\"");
        if (!string.IsNullOrEmpty(autoId))
            itemDesc.Append($" (autoId: {autoId})");
        if (!string.IsNullOrEmpty(shortcut))
            itemDesc.Append($" [shortcut: \"{shortcut}\"]");
        else if (!string.IsNullOrEmpty(accessKey))
            itemDesc.Append($" [accessKey: \"{accessKey}\"]");
        if (!string.IsNullOrEmpty(value))
            itemDesc.Append($" (val: \"{value}\")");
        if (element.Current.IsOffscreen)
            itemDesc.Append(" [offscreen]");
        return itemDesc.ToString();
    }

    public UiOutcome ClickElement(int id, int autoWaitMs = 3000)
    {
        AutomationElement? element;
        lock (_elementCache)
        {
            _elementCache.TryGetValue(id, out element);
        }
        if (element == null)
        {
            return new(false, $"Element id {id} is not in the current UI tree. Call get_ui_tree again.");
        }

        // Playwright Actionability Guard: Auto-Wait until enabled
        var sw = Stopwatch.StartNew();
        if (!WaitUntilEnabled(element, sw, autoWaitMs))
        {
            return new(false, $"Element [{id}] is still not enabled after {sw.ElapsedMilliseconds}ms (IsEnabled == false).");
        }
        ScrollIntoViewQuietly(element);
        try
        {
            return InvokeSemantically(id, element);
        }
        catch (Exception ex)
        {
            return new(false, $"Click on element [{id}] failed: {ex.Message}");
        }
    }

    // A transient UIA error counts as not yet enabled; the deadline decides.
    private static bool WaitUntilEnabled(AutomationElement element, Stopwatch sw, int autoWaitMs)
    {
        bool isEnabled = false;
        while (sw.ElapsedMilliseconds <= autoWaitMs)
        {
            try
            {
                isEnabled = element.Current.IsEnabled;
                if (isEnabled)
                    break;
            }
            catch
            {
                // A transient UIA error counts as not yet enabled; the wait below decides.
            }
            Thread.Sleep(50);
        }
        return isEnabled;
    }

    // Auto-scroll into view if offscreen. A convenience: the pattern calls afterwards report a real failure themselves.
    private static void ScrollIntoViewQuietly(AutomationElement element)
    {
        try
        {
            if (element.Current.IsOffscreen &&
                element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object? sp) && sp is ScrollItemPattern sip)
            {
                sip.ScrollIntoView();
                Thread.Sleep(50);
            }
        }
        catch
        {
            // Scrolling into view is a convenience; the pattern calls below report a real failure themselves.
        }
    }

    // Invoke, then toggle (a checkbox), then select (a list item or tab); a control with none of them is not clicked.
    private static UiOutcome InvokeSemantically(int id, AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object? invPat) && invPat is InvokePattern invoke)
        {
            invoke.Invoke();
            return new(true, $"Element [{id}] clicked via InvokePattern.");
        }
        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object? togPat) && togPat is TogglePattern toggle)
        {
            toggle.Toggle();
            return new(true, $"Element [{id}] toggled via TogglePattern.");
        }
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? selPat) && selPat is SelectionItemPattern select)
        {
            select.Select();
            return new(true, $"Element [{id}] selected.");
        }
        return new(false, "No semantic action available. Use explicit physical input after a fresh observation.");
    }

    public UiOutcome SilentClick(int id)
    {
        return InvokeElement(id, allowNativeButton: true);
    }
    public UiOutcome InvokeBackgroundElement(int id)
    {
        return InvokeElement(id, allowNativeButton: false);
    }
    private UiOutcome InvokeElement(int id, bool allowNativeButton)
    {
        AutomationElement? element;
        lock (_elementCache)
        {
            _elementCache.TryGetValue(id, out element);
        }
        if (element == null)
        {
            return new(false, $"Element id {id} is not in the current UI tree.");
        }

        try
        {
            if (!element.Current.IsEnabled)
                return new(false, "Target disabled.");
            // Try UIA patterns first (silent, no mouse pointer movement)
            if (InvokeWithoutPointer(id, element) is { } invoked)
                return invoked;
            // Fallback: Send Win32 BM_CLICK directly to HWND
            int nativeHwnd = element.Current.NativeWindowHandle;
            if (allowNativeButton && nativeHwnd != 0)
                return ClickNativeButton(id, element, nativeHwnd);
            return new(false, $"Element [{id}] supports no InvokePattern and has no Win32 window handle of its own.");
        }
        catch (Exception ex)
        {
            return new(false, $"Silent click on [{id}] failed: {ex.Message}");
        }
    }

    // Null when the element offers none of the invoke, toggle and selection patterns.
    private static UiOutcome? InvokeWithoutPointer(int id, AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object? invPat) && invPat is InvokePattern invoke)
        {
            invoke.Invoke();
            return new(true, $"Silent click on element [{id}] via InvokePattern (pointer not moved).");
        }
        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object? togPat) && togPat is TogglePattern toggle)
        {
            toggle.Toggle();
            return new(true, $"Silent click on element [{id}] via TogglePattern (pointer not moved).");
        }
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? selPat) && selPat is SelectionItemPattern select)
        {
            select.Select();
            return new(true, $"Silent click on element [{id}] via SelectionItemPattern (pointer not moved).");
        }
        return null;
    }

    private const uint NativeClickTimeoutMilliseconds = 1000;

    private static UiOutcome ClickNativeButton(int id, AutomationElement element, int nativeHwnd)
    {
        if (element.Current.ControlType != ControlType.Button || !element.Current.IsEnabled)
            return new(false, "Not an enabled native button.");

        IntPtr sendResult = NativeMethods.SendMessageTimeout(
            (IntPtr)nativeHwnd, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero, 2, NativeClickTimeoutMilliseconds, out _);
        if (sendResult == IntPtr.Zero)
            return new(false, "Native button invocation timed out.");
        return new(true, $"Silent click on element [{id}] sent as Win32 BM_CLICK message to HWND {nativeHwnd}.");
    }

    public ClipboardOutcome GetClipboard()
    {
        try
        {
            string? text = NativeMethods.GetClipboardText();
            return new(true, "Clipboard read.", text);
        }
        catch (Exception ex)
        {
            return new(false, $"Reading the clipboard failed: {ex.Message}", null);
        }
    }

    public UiOutcome SetClipboard(string text)
    {
        try
        {
            bool ok = NativeMethods.SetClipboardText(text);
            return ok
                ? new(true, $"Clipboard updated ({text.Length} characters).")
                : new(false, "Could not write the text to the Win32 clipboard.");
        }
        catch (Exception ex)
        {
            return new(false, $"Writing the clipboard failed: {ex.Message}");
        }
    }

    public UiOutcome PasteClipboard(int? targetId = null)
    {
        try
        {
            if (targetId.HasValue)
            {
                AutomationElement? element;
                lock (_elementCache)
                {
                    _elementCache.TryGetValue(targetId.Value, out element);
                }

                if (element == null)
                    return new(false, "Stale target. Clipboard was not pasted.");
                if (element != null)
                {
                    element.SetFocus();
                    Thread.Sleep(30);
                }
            }

            WindowsInput.Chord(["ctrl", "v"]);
            return new(true, "Clipboard pasted with Ctrl+V.");
        }
        catch (Exception ex)
        {
            return new(false, $"Pasting the clipboard failed: {ex.Message}");
        }
    }

    public UiOutcome SetText(int id, string text, int autoWaitMs = 3000)
    {
        AutomationElement? element;
        lock (_elementCache)
        {
            _elementCache.TryGetValue(id, out element);
        }
        if (element == null)
        {
            return new(false, $"Element id {id} is not in the current UI tree.");
        }

        // Playwright Actionability Guard
        var sw = Stopwatch.StartNew();
        if (!WaitUntilEnabled(element, sw, autoWaitMs))
        {
            return new(false, $"Element [{id}] is still disabled after {sw.ElapsedMilliseconds}ms.");
        }
        ScrollIntoViewQuietly(element);
        try
        {
            if (!element.Current.IsPassword
                && element.TryGetCurrentPattern(ValuePattern.Pattern, out object? vp)
                && vp is ValuePattern valPat)
            {
                valPat.SetValue(text);
                return new(true, $"Text of element [{id}] set via ValuePattern.");
            }

            // Fallback: Focus and SendKeys
            element.SetFocus();
            WindowsInput.Text(text);
            return new(true, $"Element [{id}] focused and text typed via SendKeys (fallback).");
        }
        catch (Exception ex)
        {
            return new(false, $"Setting text on [{id}] failed: {ex.Message}");
        }
    }

    public UiOutcome LaunchApp(string path, IReadOnlyList<string> arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                // ShellExecute takes a single parameter string; see WindowsCommandLine for why the
                // arguments are joined with the quoting rules of the target's own parser.
                Arguments = WindowsCommandLine.Join(arguments),
                UseShellExecute = true
            };
            var proc = Process.Start(psi);
            return new(true, $"App gestartet (PID: {proc?.Id ?? -1})");
        }
        catch (Exception ex)
        {
            return new(false, $"Starting {path} failed: {ex.Message}");
        }
    }

    public ElementCheckOutcome WaitForElement(
        string? name,
        string? autoId,
        string? role,
        int? pid = null,
        string? windowTitle = null,
        string state = "exists",
        int timeoutMs = 10000)
    {
        if (name == null && autoId == null && role == null)
            return new(false, "A selector is required.", null);
        if (role != null && GetControlTypeByName(role) == null)
            return new(false, "Unsupported role.", null);
        DesktopSessionInfo desktopSession = GetDesktopSession();
        if (string.Equals(state, "visible", StringComparison.OrdinalIgnoreCase) && !desktopSession.InputAvailable)
        {
            return new(false, NoVisibilityWithoutInputDesktop, desktopSession);
        }
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var match = SelectWindow(ListWindows(), pid, windowTitle);
            if (match != null && WaitStep(match, name, autoId, role, state, sw) is { } outcome)
                return outcome;
            Thread.Sleep(50);
        }

        return new(false,
            $"Timed out after {timeoutMs}ms waiting for element "
            + $"(Name: '{name}', AutoId: '{autoId}', Role: '{role}', State: '{state}').",
            null);
    }

    // One look into the window: the answer that ends the wait, or null to look again.
    private ElementCheckOutcome? WaitStep(WindowInfo match, string? name, string? autoId, string? role, string state, Stopwatch sw)
    {
        AutomationElement? rootElement = null;
        try
        {
            rootElement = AutomationElement.FromHandle((IntPtr)match.WindowHandle);
        }
        catch (Exception ex) { return new(false, "Accessibility unavailable: " + ex.Message, null); }
        if (rootElement == null)
            return null;

        var foundAll = rootElement.FindAll(TreeScope.Descendants, SelectorCondition(name, autoId, role));
        if (foundAll.Count > 1)
            return new(false, "Element selector is ambiguous.", null);
        var found = foundAll.Count == 1 ? foundAll[0] : null;
        if (found == null && state == "absent")
            return new(true, "Absence verified.", null);
        if (found == null)
            return null;
        bool? conditionMet = WaitStateMet(found, state.ToLowerInvariant());
        if (conditionMet == null)
            return new(false, "Unsupported wait state.", null);
        return conditionMet.Value ? Found(found, state, sw) : null;
    }

    // Null for a state the wait does not know.
    private static bool? WaitStateMet(AutomationElement found, string stateLower)
    {
        return stateLower switch
        {
            "enabled" => found.Current.IsEnabled,
            "visible" => !found.Current.IsOffscreen,
            "disabled" => !found.Current.IsEnabled,
            "hidden" => found.Current.IsOffscreen,
            "absent" => false,
            "exists" => true,
            _ => null
        };
    }

    // The found element gets an id in the element cache, so the next action can name it.
    private ElementCheckOutcome Found(AutomationElement found, string state, Stopwatch sw)
    {
        int id;
        lock (_elementCache)
        {
            id = ++_nextElementId;
            _elementCache[id] = found;
        }

        var info = new
        {
            id,
            role = found.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
            name = found.Current.Name ?? "",
            autoId = found.Current.AutomationId ?? "",
            isEnabled = found.Current.IsEnabled,
            isOffscreen = found.Current.IsOffscreen,
            elapsedMs = sw.ElapsedMilliseconds
        };
        return new(true, $"Element '{found.Current.Name}' ({state}) found after {sw.ElapsedMilliseconds}ms.", info);
    }

    public ElementCheckOutcome VerifyElement(
        int? id = null,
        string? name = null,
        string? autoId = null,
        string? role = null,
        int? pid = null,
        string? windowTitle = null,
        string? expectedState = "enabled",
        string? expectedText = null,
        int timeoutMs = 3000)
    {
        if (RefuseSelector(id, name, autoId, role) is { } refusal)
            return refusal;
        DesktopSessionInfo desktopSession = GetDesktopSession();
        if (string.Equals(expectedState, "visible", StringComparison.OrdinalIgnoreCase) && !desktopSession.InputAvailable)
            return new(false, NoVisibilityWithoutInputDesktop, desktopSession);
        var sw = Stopwatch.StartNew();
        AutomationElement? target = null;
        var progress = new AssertionProgress(expectedText);

        while (sw.ElapsedMilliseconds <= timeoutMs)
        {
            AssertionTarget found = FindAssertionTarget(id, name, autoId, role, pid, windowTitle);
            if (found.Refusal != null)
                return found.Refusal;
            target = found.Element;
            string stateLower = (expectedState ?? "enabled").ToLowerInvariant();
            if (stateLower == "absent")
            {
                if (target == null)
                    return AbsenceVerified(expectedState, sw);
            }
            else if (target != null && CheckAssertion(target, stateLower, expectedState, progress, desktopSession, sw) is { } outcome)
            {
                return outcome;
            }
            Thread.Sleep(50);
        }

        return new(false, $"Assertion failed (timeout {timeoutMs}ms): state '{expectedState}' was not reached.", new
        {
            verified = false,
            expectedState,
            expectedText = progress.ExpectedText,
            found = target != null,
            actualText = progress.ObservedText,
            elapsedMs = sw.ElapsedMilliseconds
        });
    }

    private static ElementCheckOutcome? RefuseSelector(int? id, string? name, string? autoId, string? role)
    {
        if (id == null && name == null && autoId == null && role == null)
            return new(false, "An assertion selector is required.", null);
        if (role != null && GetControlTypeByName(role) == null)
            return new(false, "Unsupported role.", null);
        return null;
    }

    private const int MaximumVerifiedTextLength = 16384;

    // What polling learned so far: the expected text after line-ending normalization and the last text read.
    private sealed class AssertionProgress(string? expectedText)
    {
        public string? ExpectedText
        {
            get;
            set;
        } = expectedText;

        public string? ObservedText
        {
            get;
            set;
        }
    }

    // The element found for an assertion, or the answer that ends the assertion (ambiguous selector, failed query).
    private sealed record AssertionTarget(AutomationElement? Element, ElementCheckOutcome? Refusal);

    // The result of comparing the element's state, or null for a state the assertion does not know.
    private sealed record StateCheck(bool Matches, string ActualState);

    private AssertionTarget FindAssertionTarget(int? id, string? name, string? autoId, string? role, int? pid, string? windowTitle)
    {
        if (id.HasValue)
        {
            AutomationElement? cached;
            lock (_elementCache)
            {
                _elementCache.TryGetValue(id.Value, out cached);
            }
            return new AssertionTarget(cached, null);
        }

        var match = SelectWindow(ListWindows(), pid, windowTitle);
        if (match == null)
            return new AssertionTarget(null, null);
        try
        {
            var root = AutomationElement.FromHandle((IntPtr)match.WindowHandle);
            if (root == null)
                return new AssertionTarget(null, null);
            var foundAll = root.FindAll(TreeScope.Descendants, SelectorCondition(name, autoId, role));
            if (foundAll.Count > 1)
                return new AssertionTarget(null, new(false, "Assertion selector is ambiguous.", null));
            return new AssertionTarget(foundAll.Count == 1 ? foundAll[0] : null, null);
        }
        catch (Exception ex) { return new AssertionTarget(null, new(false, "Accessibility query failed: " + ex.Message, null)); }
    }

    private static Condition SelectorCondition(string? name, string? autoId, string? role)
    {
        var conditions = new List<Condition>();
        if (!string.IsNullOrWhiteSpace(name))
            conditions.Add(new PropertyCondition(AutomationElement.NameProperty, name));
        if (!string.IsNullOrWhiteSpace(autoId))
            conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, autoId));
        if (!string.IsNullOrWhiteSpace(role))
        {
            var ct = GetControlTypeByName(role);
            if (ct != null)
                conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ct));
        }

        return conditions.Count switch
        {
            0 => Condition.TrueCondition,
            1 => conditions[0],
            _ => new AndCondition(conditions.ToArray())
        };
    }

    private static ElementCheckOutcome AbsenceVerified(string? expectedState, Stopwatch sw)
    {
        return new(true, $"Assertion met: element does not exist (after {sw.ElapsedMilliseconds}ms).", new
        {
            verified = true,
            expectedState,
            elapsedMs = sw.ElapsedMilliseconds
        });
    }

    // The answer that ends the assertion, or null to poll again. The element can vanish while it is polled; then the next
    // poll or the deadline decides.
    private static ElementCheckOutcome? CheckAssertion(
        AutomationElement target,
        string stateLower,
        string? expectedState,
        AssertionProgress progress,
        DesktopSessionInfo desktopSession,
        Stopwatch sw)
    {
        try
        {
            string actualText = target.Current.Name ?? "";
            if (progress.ExpectedText != null)
            {
                TextRead read = ComparableText(target, progress, actualText);
                if (read.Refusal != null)
                    return read.Refusal;
                actualText = read.Text!;
            }
            if (EvaluateState(target, stateLower, actualText, progress.ExpectedText) is not { } state)
                return new(false, "Unsupported assertion state.", null);

            bool stateOk = state.Matches;
            if (progress.ExpectedText != null)
                stateOk &= string.Equals(actualText, progress.ExpectedText, StringComparison.Ordinal);
            return stateOk ? Verified(target, expectedState, state.ActualState, actualText, desktopSession, sw) : null;
        }
        catch
        {
            return null;
        }
    }

    // The text read for a comparison, or the refusal for a password field or an oversized expectation.
    private sealed record TextRead(string? Text, ElementCheckOutcome? Refusal);

    // The element's text with line endings normalized. Both texts are recorded in the progress as soon as they are read.
    private static TextRead ComparableText(AutomationElement target, AssertionProgress progress, string nameText)
    {
        string expectedText = progress.ExpectedText!;
        if (target.Current.IsPassword)
            return new TextRead(null, new(false, "Password fields cannot be used for text verification.", null));
        if (expectedText.Length > MaximumVerifiedTextLength)
            return new TextRead(null, new(false, "Text verification exceeds the supported size.", null));

        string actualText = nameText;
        if (target.TryGetCurrentPattern(ValuePattern.Pattern, out object? valuePattern) && valuePattern is ValuePattern value)
        {
            actualText = value.Current.Value ?? actualText;
        }
        else if (target.TryGetCurrentPattern(TextPattern.Pattern, out object? textPattern) && textPattern is TextPattern document)
        {
            // Document controls such as modern Notepad expose TextPattern,
            // not ValuePattern. Read only enough to compare this assertion.
            actualText = document.DocumentRange.GetText(expectedText.Length + 2);
        }
        actualText = actualText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        progress.ObservedText = actualText;
        progress.ExpectedText = expectedText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        return new TextRead(actualText, null);
    }

    private static StateCheck? EvaluateState(AutomationElement target, string stateLower, string actualText, string? expectedText)
    {
        return stateLower switch
        {
            "exists" => new StateCheck(true, "exists"),
            "enabled" => new StateCheck(target.Current.IsEnabled, target.Current.IsEnabled ? "enabled" : "disabled"),
            "disabled" => new StateCheck(!target.Current.IsEnabled, target.Current.IsEnabled ? "enabled" : "disabled"),
            "visible" => new StateCheck(!target.Current.IsOffscreen, target.Current.IsOffscreen ? "hidden" : "visible"),
            "hidden" => new StateCheck(target.Current.IsOffscreen, target.Current.IsOffscreen ? "hidden" : "visible"),
            "has_text" or "text_equals" => new StateCheck(
                string.Equals(actualText, expectedText, StringComparison.OrdinalIgnoreCase), $"text='{actualText}'"),
            "text_contains" => new StateCheck(
                expectedText != null && actualText.Contains(expectedText, StringComparison.OrdinalIgnoreCase), $"text='{actualText}'"),
            _ => null
        };
    }

    private static ElementCheckOutcome Verified(
        AutomationElement target,
        string? expectedState,
        string actualState,
        string actualText,
        DesktopSessionInfo desktopSession,
        Stopwatch sw)
    {
        string verifiedMessage = $"Assertion met: state '{expectedState}' verified "
            + $"({actualState}, {sw.ElapsedMilliseconds}ms).";
        return new(true, verifiedMessage, new
        {
            verified = true,
            expectedState,
            actualState,
            actualText,
            desktopSession,
            processId = target.Current.ProcessId,
            evidenceSource = "accessibility-provider",
            name = target.Current.Name,
            role = target.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
            elapsedMs = sw.ElapsedMilliseconds
        });
    }

    private static ControlType? GetControlTypeByName(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "button" => ControlType.Button,
            "edit" => ControlType.Edit,
            "document" => ControlType.Document,
            "splitbutton" => ControlType.SplitButton,
            "checkbox" => ControlType.CheckBox,
            "radiobutton" => ControlType.RadioButton,
            "combobox" => ControlType.ComboBox,
            "list" => ControlType.List,
            "listitem" => ControlType.ListItem,
            "menu" => ControlType.Menu,
            "menubar" => ControlType.MenuBar,
            "menuitem" => ControlType.MenuItem,
            "tab" => ControlType.Tab,
            "tabitem" => ControlType.TabItem,
            "tree" => ControlType.Tree,
            "treeitem" => ControlType.TreeItem,
            "window" => ControlType.Window,
            "pane" => ControlType.Pane,
            "text" => ControlType.Text,
            "hyperlink" => ControlType.Hyperlink,
            "custom" => ControlType.Custom,
            "toolbar" => ControlType.ToolBar,
            "statusbar" => ControlType.StatusBar,
            _ => null
        };
    }
}
