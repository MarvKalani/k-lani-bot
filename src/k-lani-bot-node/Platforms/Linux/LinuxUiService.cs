#if !WINDOWS
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KLaniBot.Guard;
using KLaniBot.Actions;
using KLaniBot.Common;
namespace KLaniBot.Platforms.Linux;

// The Linux backend. Reading and operating elements goes through AT-SPI (AtSpiBackend) and works under X11 and
// Wayland as long as the session's accessibility bus runs. Everything that needs the shared screen, keyboard or
// window manager (window list and control, raw input, clipboard, screenshot) uses X11 tools (wmctrl, xdotool,
// xclip, scrot) and is refused under Wayland: X11 tools cannot reach native Wayland windows, and Wayland input
// would need a portal session the operator approved.
//
// Foreground input raises the target window first and stops if that fails. Background input and guarded text go
// to the element through AT-SPI only. Modal dialogs are detected for the target and for other windows of the
// same application. Pasting types the clipboard text into the target; there is no paste keystroke.
public sealed class LinuxUiService : IUiService
{
    // Throws InvalidOperationException when the target window cannot be brought to the front.
    public void PrepareElementInteraction(int id, WindowInteractionMode mode, ModalDialogMode dialogs, bool decisionRecorded)
    {
        _a11y.RequireDialogPermission(id, dialogs, decisionRecorded);
        if (mode == WindowInteractionMode.BackgroundSemantic)
        {
            return;
        }
        var identity = _a11y.GetTargetIdentity(id);
        if (!TryFocusAccessibleWindow(id, identity))
        {
            throw new InvalidOperationException("The target window could not be brought to the front. No input was sent.");
        }
    }

    // AT-SPI and X11 can report different titles for the same Chrome window. A fallback must still prove the owning window.
    private bool TryFocusAccessibleWindow(int id, UiTargetIdentity identity)
    {
        try
        {
            WindowInfo? target = Select(identity.ProcessId, identity.WindowTitle);
            if (target == null)
            {
                target = MatchAccessibleWindow(id, identity.ProcessId);
            }
            if (target == null)
            {
                return false;
            }

            string handle = target.Handle.ToInt64().ToString();
            ProcessResult activation = Run("xdotool", "windowactivate", "--sync", handle);
            ProcessResult active = Run("xdotool", "getactivewindow");

            return activation.Success && active.Success && active.Output == handle;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    // Throws InvalidOperationException when X11 or AT-SPI cannot report geometry.
    private WindowInfo? MatchAccessibleWindow(int id, int processId)
    {
        WindowInfo[] windows = ListWindows().Where(window => window.ProcessId == processId).ToArray();
        if (windows.Length != 1)
        {
            return null;
        }
        AccessibleScreenBounds? accessible = _a11y.WindowBounds(id);
        if (accessible == null)
        {
            return null;
        }
        var native = GetWindowGeometry(processId, null) as Dictionary<string, string>;
        if (native == null)
        {
            return null;
        }

        bool sameBounds = WindowBoundsMatch(accessible, native);

        return sameBounds ? windows[0] : null;
    }

    private static bool WindowBoundsMatch(AccessibleScreenBounds accessible, Dictionary<string, string> native)
    {
        if (accessible.Width <= 0 || accessible.Height <= 0)
        {
            return false;
        }

        return int.TryParse(native.GetValueOrDefault("X"), out int x) && x == accessible.X
            && int.TryParse(native.GetValueOrDefault("Y"), out int y) && y == accessible.Y
            && int.TryParse(native.GetValueOrDefault("WIDTH"), out int width) && width == accessible.Width
            && int.TryParse(native.GetValueOrDefault("HEIGHT"), out int height) && height == accessible.Height;
    }
    public UiOutcome InvokeBackgroundElement(int id)
    {
        return ClickElement(id);
    }
    public UiTargetIdentity? GetTargetIdentity(int id) => _a11y.GetTargetIdentity(id);
    public string? ReadWindowText(int id)
    {
        try
        {
            return _a11y.WindowText(id);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
    public string? ShowingModalWindowTitle(int processId)
    {
        try
        {
            return _a11y.ShowingModalWindowTitle(processId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // Unknown when the accessibility provider cannot judge the element, so the decision keeps the routing it had before.
    public ModalDialogDetection DetectModalDialog(int id)
    {
        try
        {
            return _a11y.DetectModalDialog(id);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return ModalDialogDetection.Unknown;
        }
    }
    public UiOutcome SetGuardedText(int id, string text) => SetText(id, text);
    public UiFormCapabilities FormCapabilities => _a11y.Available
        ? new(UiFormSupport.Available, UiFormSupport.Available, UiFormSupport.Available) : UiFormCapabilities.Unsupported;
    public UiFieldMetadata ReadFieldMetadata(int id)
    {
        try
        {
            return _a11y.ReadFieldMetadata(id);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return UiFieldMetadata.Unsupported;
        }
    }
    public UiOutcome SelectChoice(UiChoiceCommand command)
    {
        try
        {
            _a11y.SelectChoice(command);
            return new(true, "Native observed option dispatched; completion requires fresh verification.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new(false, exception.Message);
        }
    }
    public UiOutcome OpenChoices(int id)
    {
        try
        {
            _a11y.OpenChoices(id);
            return new(true, "Native choice opening dispatched; no option selected.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new(false, exception.Message);
        }
    }
    private readonly AtSpiBackend _a11y = new();
    private readonly bool _x11 = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
    private readonly Dictionary<string, VisualTreeNode> _chapters = new();
    public string Platform => _x11 ? "linux-x11-atspi" : "linux-wayland-atspi";
    public IReadOnlySet<ToolFeature> Features
    {
        get
        {
            var features = new HashSet<ToolFeature>();
            if (_a11y.Available)
                features.UnionWith(
                [ToolFeature.SemanticInspect, ToolFeature.SemanticInvoke, ToolFeature.BackgroundInvoke, ToolFeature.TextInjection]);
            if (_x11)
                features.UnionWith(
                [ToolFeature.Windows, ToolFeature.WindowControl, ToolFeature.PhysicalInput, ToolFeature.Clipboard, ToolFeature.Screenshot]);
            return features;
        }
    }
    private static ProcessResult Run(string exe, params string[] args)
    {
        ProcessResult result = ProcessRunner.Run(exe, args);
        return result with
        {
            Output = result.Output.TrimEnd()
        };
    }
    private static string XdotoolButtonNumber(string button)
    {
        if (button == "right")
            return "3";
        if (button == "middle")
            return "2";
        return "1";
    }
    // Throws NotSupportedException outside X11, where physical input needs a portal session.
    private void RequireX11()
    {
        if (!_x11)
            throw new NotSupportedException(
                "Physical Wayland input requires an explicitly approved portal session. "
                + "X11 utilities are not used for native Wayland windows.");
    }
    // Throws InvalidOperationException when wmctrl fails.
    public List<WindowInfo> ListWindows()
    {
        RequireX11();
        var result = Run("wmctrl", "-l", "-p", "-G");
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
        var windows = new List<WindowInfo>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (WmctrlLineParser.TryParse(line, out var parsed))
                windows.Add(ReadWindowIdentity(parsed.ProcessId, parsed.Title, new IntPtr(parsed.Handle)));
        }
        return ReadDialogKinds(windows);
    }

    // Marks the windows that the accessibility tree shows as dialogs. A window that no accessible name matches stays a main window.
    private List<WindowInfo> ReadDialogKinds(List<WindowInfo> windows)
    {
        var processIds = windows.Select(window => window.ProcessId).ToHashSet();
        var marked = windows.ToList();
        foreach (var accessibleWindow in ReadAccessibleWindows(processIds).Where(window => window.IsDialog))
        {
            int index = IndexOfAccessibleWindow(marked, accessibleWindow);
            if (index == WindowSelection.NoWindow)
                continue;

            // An untitled dialog takes its accessible name, so that observing it by caption later reaches its subtree.
            string title = marked[index].Title.Length == 0 ? accessibleWindow.Name : marked[index].Title;
            marked[index] = marked[index] with
            {
                Kind = WindowKind.Dialog,
                Title = title
            };
        }

        return marked;
    }

    // A window list must not fail because the accessibility bus cannot tell its dialogs, so an unreadable tree lists none.
    private IReadOnlyList<AtSpiBackend.AccessibleWindow> ReadAccessibleWindows(IReadOnlySet<int> processIds)
    {
        try
        {
            return _a11y.ListAccessibleWindows(processIds);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            return [];
        }
    }

    // A dialog whose accessible name matches no X11 title (the window has none) belongs to the process's single untitled window.
    // A main window never claims it, so a hidden main window cannot take the dialog.
    private static int IndexOfAccessibleWindow(IReadOnlyList<WindowInfo> windows, AtSpiBackend.AccessibleWindow accessibleWindow)
    {
        int index = WindowSelection.IndexOfNamedWindow(windows, accessibleWindow.ProcessId, accessibleWindow.Name);
        if (index != WindowSelection.NoWindow || !accessibleWindow.IsDialog)
            return index;

        return WindowSelection.IndexOfSingleUntitledWindow(windows, accessibleWindow.ProcessId);
    }

    // The caption the accessibility tree gives this window: the one name of its process that the caption rule matches to it.
    private string? AccessibleCaptionOf(WindowInfo window, IReadOnlyList<WindowInfo> windows)
    {
        foreach (var accessibleWindow in ReadAccessibleWindows(new HashSet<int> { window.ProcessId }))
        {
            int index = IndexOfAccessibleWindow(windows, accessibleWindow);
            if (index != WindowSelection.NoWindow && windows[index].WindowHandle == window.WindowHandle)
                return accessibleWindow.Name;
        }

        return null;
    }

    private static WindowInfo ReadWindowIdentity(int processId, string title, IntPtr handle)
    {
        string name = "unknown";
        string? executable = null;
        DateTimeOffset? started = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
            started = process.StartTime.ToUniversalTime();
            executable = process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Inaccessible lifetime data remains unknown; the controller must refuse to assume continuity.
        }

        string version = ReadKnownApplicationVersion(processId, executable);

        return new(processId, name, version, title, handle)
        {
            ProcessStartedAtUtc = started,
            ExecutablePath = executable
        };
    }

    private static string ReadKnownApplicationVersion(int processId, string? executable)
    {
        var versions = new LinuxExecutableVersion();
        var commandLine = ReadCommandLine(processId);

        return versions.TryRead(executable, commandLine, out var build) ? build!.Version : "unknown";
    }

    // The script of an interpreter process is only visible in its command line.
    private static IReadOnlyList<string> ReadCommandLine(int processId)
    {
        try
        {
            var arguments = File.ReadAllText($"/proc/{processId}/cmdline").Split('\0').ToList();
            if (arguments.Count > 0 && arguments[^1].Length == 0)
            {
                arguments.RemoveAt(arguments.Count - 1);
            }

            return arguments;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    // Throws ArgumentException when the request matches several windows or names an unknown handle.
    private WindowInfo? Select(WindowSelectionRequest request)
    {
        if (!WindowSelection.HasCriteria(request))
            return ActiveWindow();
        var result = WindowSelection.Select(ListWindows(), request);

        return WindowSelection.RequireChoice(request, result);
    }

    private WindowInfo? Select(int? pid, string? title)
    {
        return Select(new WindowSelectionRequest(pid, title, null));
    }

    private WindowInfo? ActiveWindow()
    {
        var active = Run("xdotool", "getactivewindow");
        if (active.Success && long.TryParse(active.Output, out long activeHandle))
            return ListWindows().SingleOrDefault(w => w.Handle.ToInt64() == activeHandle);
        return null;
    }

    public bool FocusWindow(WindowSelectionRequest request)
    {
        var w = Select(request);
        if (w == null)
            return false;
        var result = Run("xdotool", "windowactivate", "--sync", w.Handle.ToInt64().ToString());
        var active = Run("xdotool", "getactivewindow");
        return result.Success && active.Output == w.Handle.ToInt64().ToString();
    }
    public UiOutcome CloseWindow(int? pid, string? title)
    {
        var w = Select(pid, title);
        if (w == null)
            return new(false, "Window not found.");
        var r = Run("wmctrl", "-i", "-c", "0x" + w.Handle.ToInt64().ToString("X"));
        return new(r.Success, r.Success ? "Close requested." : r.Error);
    }
    // Throws InvalidOperationException when xdotool fails.
    public object? GetWindowGeometry(int? pid, string? titleQuery)
    {
        var w = Select(pid, titleQuery);
        if (w == null)
            return null;
        var r = Run("xdotool", "getwindowgeometry", "--shell", w.Handle.ToInt64().ToString());
        if (!r.Success)
            throw new InvalidOperationException(r.Error);
        return r.Output.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
    }
    // X11 screenshots and pointer input both use root-window pixels, so the rectangle needs no scaling.
    // Throws ArgumentException for an unknown or ambiguous window and InvalidOperationException when xdotool fails.
    public WindowScreenBounds? ReadWindowBounds(WindowSelectionRequest request)
    {
        RequireX11();
        var window = Select(request);
        if (window == null)
        {
            return null;
        }

        var result = Run("xdotool", "getwindowgeometry", "--shell", window.Handle.ToInt64().ToString());
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error);
        }
        var fields = result.Output.Split('\n').Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1]);

        return ReadBounds(fields);
    }

    private static WindowScreenBounds? ReadBounds(Dictionary<string, string> fields)
    {
        if (!int.TryParse(fields.GetValueOrDefault("X"), out int x) || !int.TryParse(fields.GetValueOrDefault("Y"), out int y)
            || !int.TryParse(fields.GetValueOrDefault("WIDTH"), out int width)
            || !int.TryParse(fields.GetValueOrDefault("HEIGHT"), out int height) || width <= 0 || height <= 0)
        {
            return null;
        }

        return new WindowScreenBounds(x, y, width, height);
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
        var w = Select(pid, titleQuery);
        if (w == null)
            return new(false, "Window not found.", null);
        string id = w.Handle.ToInt64().ToString();
        if (state == "minimized")
        {
            var r = Run("xdotool", "windowminimize", id);
            return new(r.Success, r.Error, GetWindowGeometry(pid, titleQuery));
        }
        if (state != null)
        {
            string maximizeAction = state == "maximized" ? "add" : "remove";
            string windowHandle = "0x" + w.Handle.ToInt64().ToString("X");
            var r = Run("wmctrl", "-i", "-r", windowHandle, "-b", maximizeAction + ",maximized_vert,maximized_horz");
            if (!r.Success)
                return new(false, r.Error, null);
        }
        if (width.HasValue || height.HasValue)
        {
            if (width is < 1 or > 16384 || height is < 1 or > 16384)
                return new(false, "Invalid geometry.", null);
            var r = Run("xdotool", "windowsize", id, width?.ToString() ?? "%", height?.ToString() ?? "%");
            if (!r.Success)
                return new(false, r.Error, null);
        }
        if (x.HasValue || y.HasValue)
        {
            var r = Run("xdotool", "windowmove", id, x?.ToString() ?? "x", y?.ToString() ?? "y");
            if (!r.Success)
                return new(false, r.Error, null);
        }
        return new(true, "Geometry dispatched.", GetWindowGeometry(pid, titleQuery));
    }
    public object? GetVisualTree(int? pid, string? titleQuery, int maxDepth = 4) => _a11y.Observe(pid, titleQuery, maxDepth);
    private static string Format(VisualTreeNode root)
    {
        var output = new StringBuilder();
        void Visit(VisualTreeNode node, int depth)
        {
            string indent = new string(' ', depth * 2);
            string name = JsonSerializer.Serialize(node.Name);
            string autoId = JsonSerializer.Serialize(node.AutoId);
            string value = JsonSerializer.Serialize(node.Value);
            output.AppendLine(
                $"{indent}[{node.Id}] {node.Role} {name} (autoId: {autoId}, val: {value}, "
                + $"enabled: {node.IsEnabled}, offscreen: {node.IsOffscreen})");
            foreach (var child in node.Children)
                Visit(child, depth + 1);
        }
        Visit(root, 0);
        return output.ToString();
    }
    public string GetUiTree(WindowSelectionRequest request, int maxDepth = 4)
    {
        try
        {
            return Format(ObserveRequest(request, maxDepth));
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    // Without a handle the request observes as before. A handle observes only its own window, found through the caption the
    // accessibility tree gives it. Throws ArgumentException for an unknown handle and InvalidOperationException when the bus
    // shows no caption for the window.
    private VisualTreeNode ObserveRequest(WindowSelectionRequest request, int maxDepth)
    {
        if (!request.Handle.HasValue)
            return _a11y.Observe(request.ProcessId, request.TitleQuery, maxDepth);
        var windows = ListWindows();
        var window = WindowSelection.RequireChoice(request, WindowSelection.Select(windows, request))
            ?? throw new UnreachableException();
        string caption = AccessibleCaptionOf(window, windows)
            ?? throw new InvalidOperationException(UiTreeErrors.NoTreeMessage);

        var captionMatch = caption.Length == 0 ? CaptionMatch.Exact : CaptionMatch.Contains;

        return _a11y.Observe(window.ProcessId, caption, maxDepth, captionMatch);
    }
    public string GetChapters(int? pid, string? titleQuery)
    {
        try
        {
            _a11y.ClearSections();
            var root = _a11y.Observe(pid, titleQuery, 2);
            _chapters.Clear();
            foreach (var window in root.Children)
                foreach (var section in window.Children)
                    _chapters[_a11y.RegisterSection(section.Id)] = section;
            return string.Join("\n", _chapters.Select(p => $"[{p.Key}] {p.Value.Role} {JsonSerializer.Serialize(p.Value.Name)}"));
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }
    public string InspectSection(string chapterId, int maxDepth = 3)
    {
        try
        {
            return _chapters.ContainsKey(chapterId) ? Format(_a11y.InspectSection(chapterId, maxDepth)) : "error: Stale chapter.";
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }
    public UiOutcome ClickElement(int id, int autoWaitMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            try
            {
                if (_a11y.Current(id).IsEnabled)
                {
                    _a11y.Invoke(id);
                    return new(true, "Semantic action dispatched.");
                }
            }
            catch (Exception ex) { return new(false, ex.Message); }
            Thread.Sleep(50);
        } while (sw.ElapsedMilliseconds < autoWaitMs);
        return new(false, "Target stayed disabled.");
    }
    public UiOutcome SilentClick(int id) => ClickElement(id);
    public UiOutcome FocusElement(int id)
    {
        try
        {
            _a11y.Focus(id);
            return new(true, "Focused.");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }
    public UiOutcome TypeText(int id, string text, bool clear, bool enter, int autoWaitMs)
    {
        try
        {
            if (enter)
                return new(false, "Semantic text insertion cannot imply Enter. Use an explicit supported input backend.");
            string value = clear ? text : (_a11y.Current(id).Value ?? "") + text;
            _a11y.SetText(id, value);
            return new(true, "Literal text dispatched through accessibility.");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }
    public UiNativeTextSupport ProbeNativeTextSupport(int id)
    {
        return _a11y.ProbeNativeTextSupport(id);
    }
    public UiOutcome SetText(int id, string text, int autoWaitMs = 3000)
        => TypeText(id, text, true, false, autoWaitMs);
    public UiOutcome MouseMove(int x, int y)
    {
        RequireX11();
        var result = Run("xdotool", "mousemove", x.ToString(), y.ToString(), "getmouselocation", "--shell");
        if (!result.Success)
        {
            return new(false, result.Error);
        }
        bool verified = TryReadPointerCoordinate(result.Output, "X=", out int actualX)
            && TryReadPointerCoordinate(result.Output, "Y=", out int actualY) && actualX == x && actualY == y;

        return new(verified, verified ? "Pointer position verified." : "Pointer did not reach the requested position.");
    }

    private static bool TryReadPointerCoordinate(string output, string prefix, out int coordinate)
    {
        coordinate = 0;
        string? line = output.Split('\n').FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));

        return line != null && int.TryParse(line[prefix.Length..], out coordinate);
    }
    public ClickOutcome MouseClick(int id, string button = "left", int? x = null, int? y = null)
    {
        RequireX11();
        if (id > 0)
        {
            var node = _a11y.Current(id);
            if (node.IsOffscreen || !node.IsEnabled)
                return new(false, "Target is not actionable.", null);
            var b = JsonSerializer.SerializeToNode(node.Bounds)!;
            x = b["x"]!.GetValue<int>() + b["width"]!.GetValue<int>() / 2;
            y = b["y"]!.GetValue<int>() + b["height"]!.GetValue<int>() / 2;
            Thread.Sleep(30);
            if (JsonSerializer.Serialize(_a11y.Current(id).Bounds) != JsonSerializer.Serialize(node.Bounds))
                return new(false, "Target moved.", null);
        }
        if (!x.HasValue || !y.HasValue)
            return new(false, "Specify a target or both coordinates.", null);
        var move = MouseMove(x.Value, y.Value);
        if (!move.Success)
            return new(false, move.Message, null);
        var r = button == "double"
            ? Run("xdotool", "click", "--repeat", "2", "--delay", "100", "1")
            : Run("xdotool", "click", XdotoolButtonNumber(button));
        return new(r.Success, r.Success ? "Physical input dispatched." : r.Error, new
        {
            x,
            y,
            button
        });
    }
    public UiOutcome DragAndDrop(int fromX, int fromY, int toX, int toY)
    {
        RequireX11();
        var move = MouseMove(fromX, fromY);
        if (!move.Success)
            return move;
        try
        {
            var down = Run("xdotool", "mousedown", "1");
            if (!down.Success)
                return new(false, down.Error);
            return MouseMove(toX, toY);
        }
        finally { Run("xdotool", "mouseup", "1"); }
    }
    // Throws ArgumentException for a key xdotool does not know.
    public UiOutcome KeyChord(string[] keys)
    {
        RequireX11();
        string Map(string key) => key.ToLowerInvariant() switch
        {
            "ctrl" => "ctrl",
            "alt" => "alt",
            "shift" => "shift",
            "win" => "super",
            "enter" or "return" => "Return",
            "escape" => "Escape",
            "tab" => "Tab",
            "backspace" => "BackSpace",
            "delete" => "Delete",
            "home" => "Home",
            "end" => "End",
            "pageup" or "page_up" => "Page_Up",
            "pagedown" or "page_down" => "Page_Down",
            "space" => "space",
            "up" or "arrowup" => "Up",
            "down" or "arrowdown" => "Down",
            "left" or "arrowleft" => "Left",
            "right" or "arrowright" => "Right",
            _ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) => key.ToLowerInvariant(),
            _ when key.StartsWith("f", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(key[1..], out int f)
                && f is >= 1 and <= 24 => "F" + f,
            _ => throw new ArgumentException("Unsupported key.")
        };
        var mapped = keys.Select(Map).ToArray();
        var r = Run("xdotool", "key", "--clearmodifiers", string.Join("+", mapped));
        return new(r.Success, r.Success ? "Chord dispatched." : r.Error);
    }
    public UiOutcome LaunchApp(string path, IReadOnlyList<string> arguments)
    {
        try
        {
            using var process = ProcessLauncher.StartDetached(ProcessLauncher.CreateStartInfo(path, arguments));
            return new(process != null, "Process started.");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }
    public ElementCheckOutcome WaitForElement(
        string? name,
        string? autoId = null,
        string? role = null,
        int? pid = null,
        string? title = null,
        string state = "exists",
        int timeoutMs = 5000)
        => VerifyElement(null, name, autoId, role, pid, title, state, null, timeoutMs);
    public ElementCheckOutcome VerifyElement(
        int? id,
        string? name,
        string? autoId,
        string? role,
        int? pid,
        string? windowTitle,
        string? expectedState,
        string? expectedText,
        int timeoutMs = 5000)
    {
        string state = expectedState ?? "exists";
        if (!new[] { "exists", "absent", "enabled", "disabled", "visible", "hidden" }.Contains(state))
            return new(false, "Unsupported assertion state.", null);
        if (id == null && name == null && autoId == null && role == null)
            return new(false, "A selector is required.", null);
        var sw = Stopwatch.StartNew();
        do
        {
            try
            {
                int[] matches = MatchingIds(id, name, autoId, role, pid, windowTitle);
                if (matches.Length > 1)
                    return new(false, "Assertion selector is ambiguous.", null);
                if (state == "absent" && matches.Length == 0)
                    return new(true, "Absence verified.", null);
                if (matches.Length == 1 && VerifiedNode(matches[0], state, expectedText) is { } node)
                    return new(true, "State and text verified.", node);
            }
            catch (Exception ex) { return new(false, ex.Message, null); }
            Thread.Sleep(VerifyPollMilliseconds);
        } while (sw.ElapsedMilliseconds < timeoutMs);
        return new(false, "Assertion timed out without a match.", null);
    }

    private const int VerifyPollMilliseconds = 50;
    private const int SelectorObservationDepth = 32;

    private int[] MatchingIds(int? id, string? name, string? autoId, string? role, int? pid, string? windowTitle)
    {
        if (id.HasValue)
            return [id.Value];

        _a11y.Observe(pid, windowTitle, SelectorObservationDepth);
        return _a11y.Match(name, autoId, role);
    }

    // The node when it is in the expected state and shows the expected text, otherwise null.
    private VisualTreeNode? VerifiedNode(int id, string state, string? expectedText)
    {
        var node = _a11y.Current(id);
        bool stateOk = state switch
        {
            "exists" => true,
            "enabled" => node.IsEnabled,
            "disabled" => !node.IsEnabled,
            "visible" => !node.IsOffscreen,
            "hidden" => node.IsOffscreen,
            _ => false
        };
        string? actualText = node.Value == "" ? node.Name : node.Value;
        bool textOk = expectedText == null || string.Equals(actualText, expectedText, StringComparison.Ordinal);

        return stateOk && textOk ? node : null;
    }
    public ClipboardOutcome GetClipboard()
    {
        RequireX11();
        var r = Run("xclip", "-o", "-selection", "clipboard");
        return new(r.Success, r.Error, r.Success ? r.Output : null);
    }
    public UiOutcome SetClipboard(string text)
    {
        RequireX11();
        var startInfo = new ProcessStartInfo { FileName = "xclip", RedirectStandardInput = true, UseShellExecute = false };
        using var process = new Process { StartInfo = startInfo };
        foreach (string arg in new[] { "-i", "-selection", "clipboard" })
            process.StartInfo.ArgumentList.Add(arg);
        try
        {
            process.Start();
            process.StandardInput.Write(text);
            process.StandardInput.Close();
            if (!process.WaitForExit(2000))
            {
                process.Kill(true);
                return new(false, "Clipboard deadline exceeded.");
            }
            return new(process.ExitCode == 0, "Clipboard updated.");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }
    public UiOutcome PasteClipboard(int? targetId = null)
    {
        if (!targetId.HasValue)
            return new(false, "Target required.");
        var text = GetClipboard();
        return text.Success ? TypeText(targetId.Value, text.Text!, true, false, 3000) : new(false, text.Message);
    }
    public ScreenshotOutcome Screenshot()
    {
        RequireX11();
        string file = Path.Combine(Path.GetTempPath(), "k-lani-bot-screen-" + Guid.CreateVersion7() + ".png");
        // scrot selects its encoder from the extension; .tmp is not an image format.
        // The name is reserved as a private file first: /tmp is shared and the capture is the whole desktop.
        PrivateFile.WriteAllBytes(file, []);
        try
        {
            var r = Run("scrot", "--overwrite", file);
            return r.Success ? new(true, "Display captured.", File.ReadAllBytes(file)) : new(false, r.Error, null);
        }
        finally { File.Delete(file); }
    }
}
#endif
