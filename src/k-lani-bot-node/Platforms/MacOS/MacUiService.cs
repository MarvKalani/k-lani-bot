using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using KLaniBot.Actions;
using KLaniBot.Common;
using KLaniBot.Guard;
using Microsoft.Win32.SafeHandles;

namespace KLaniBot.Platforms.MacOS;

// The macOS backend through the Accessibility API (AX) and Quartz. What it can do depends on the privacy
// permissions (TCC) the person granted this process: without Accessibility there are no window or element
// features, without Screen Recording no screenshot. The node never changes TCC itself; it can only open the
// Settings pane and report what is missing. The clipboard is not exposed.
//
// Foreground input raises the target and checks that it really became frontmost. Guarded text is set through
// AXValue on text fields only, with no keyboard fallback. A target inside a modal window or sheet is detected;
// whether a target behind another modal window is blocked is not detected yet, so that case is not refused here.
public sealed class MacUiService : IUiService, IDisposable
{
    // Throws InvalidOperationException when a dialog blocks the target, it is disabled or its window cannot be brought to the front.
    public void PrepareElementInteraction(int id, WindowInteractionMode mode, ModalDialogMode dialogs, bool decisionRecorded)
    {
        var target = Current(id);
        if (!Actionable(target))
        {
            throw new InvalidOperationException(
                "The target window is blocked by a dialog or the element is disabled. Observe the target again.");
        }
        using var sheet = Attr(target.Handle.Ptr, "AXTopLevelUIElement");
        bool modal = ReadBool(target.Window.Ptr, "AXModal")
            || (!sheet.IsInvalid && (ReadBool(sheet.Ptr, "AXModal") || ReadText(sheet.Ptr, "AXRole") == "AXSheet"));
        // Blocking by another modal window of the application is not detected on macOS; see the type header.
        ModalDialogPolicy.Require(modal, false, dialogs, decisionRecorded);
        if (mode == WindowInteractionMode.BackgroundSemantic)
        {
            return;
        }
        using var app = new Ref(AXUIElementCreateApplication(target.Pid));
        int raised = Action(target.Window.Ptr, "AXRaise");
        if (SetBool(app.Ptr, "AXFrontmost", true) != 0 || !IsFrontmost(target.Pid)
            || (raised != 0 && !ReadBool(target.Window.Ptr, "AXMain")))
        {
            throw new InvalidOperationException("The target window could not be brought to the front. No input was sent.");
        }
    }
    public UiOutcome InvokeBackgroundElement(int id)
    {
        return ClickElement(id);
    }
    private const int MaximumTextElements = 300;
    private const int MaximumTextDepth = 12;

    // Titles of controls and the values of static texts in the element's window; text field contents are left out.
    public string? ReadWindowText(int id)
    {
        try
        {
            var names = new List<string>();
            CollectNames(Current(id).Window.Ptr, 0, names);
            return string.Join("\n", names);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void CollectNames(IntPtr element, int depth, List<string> names)
    {
        if (names.Count >= MaximumTextElements || depth > MaximumTextDepth)
            return;
        bool staticText = ReadText(element, "AXRole") == "AXStaticText";
        string name = staticText ? ReadText(element, "AXValue") : ReadText(element, "AXTitle");
        if (name.Length > 0)
            names.Add(name);
        var children = Children(element);
        try
        {
            foreach (var child in children)
                CollectNames(child.Ptr, depth + 1, names);
        }
        finally { foreach (var child in children) child.Dispose(); }
    }

    // Throws UnauthorizedAccessException when the target's executable is unknown.
    public UiTargetIdentity? GetTargetIdentity(int id)
    {
        var target = Current(id);
        var node = Node(target.Handle.Ptr, id);
        using var process = Process.GetProcessById(target.Pid);
        string executable = process.MainModule?.FileName ?? throw new UnauthorizedAccessException("Target executable is unknown.");
        return new(
            target.Pid,
            executable,
            ReadText(target.Window.Ptr, "AXTitle"),
            node.Role,
            node.Name,
            node.AutoId,
            !node.IsOffscreen,
            node.IsEnabled);
    }
    public UiOutcome SetGuardedText(int id, string text)
    {
        var target = Current(id);
        var node = Node(target.Handle.Ptr, id);
        if (!Actionable(target) || node.Role is not ("TextField" or "TextArea"))
            return new(false, "Guarded AX text target is not actionable.");
        using var value = Str(text);
        return Set(target.Handle.Ptr, "AXValue", value.Ptr) == 0
            ? new(true, "AXValue dispatched without keyboard synthesis; verify the application result.")
            : new(false, "AXValue setter is unsupported; no keyboard fallback in guarded mode.");
    }
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AX = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    // A point or size as Core Graphics passes it.
    [StructLayout(LayoutKind.Sequential)]
    private struct Pair
    {
        public double X, Y; public Pair(double x, double y)
        {
            X = x;
            Y = y;
        }
    }
    // A Core Foundation reference released when the handle is disposed.
    private sealed class Ref : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Ref(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle()
        {
            CFRelease(handle);
            return true;
        }
        public IntPtr Ptr => DangerousGetHandle();
    }
    [DllImport(CF)] private static extern void CFRelease(IntPtr value);
    [DllImport(CF)] private static extern IntPtr CFRetain(IntPtr value);
    [DllImport(CF)] private static extern nuint CFGetTypeID(IntPtr value);
    [DllImport(CF)] private static extern nuint CFStringGetTypeID();
    [DllImport(CF)] private static extern nuint CFBooleanGetTypeID();
    [DllImport(CF)] private static extern nuint CFArrayGetTypeID();
    [DllImport(CF)]
    private static extern IntPtr CFStringCreateWithCString(
        IntPtr allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        uint encoding);
    [DllImport(CF)] private static extern nint CFStringGetLength(IntPtr value);
    [DllImport(CF)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, nint size, uint encoding);
    [DllImport(CF)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CF)] private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
    [DllImport(CF)]
    private static extern IntPtr CFDictionaryCreate(
        IntPtr allocator,
        IntPtr[] keys,
        IntPtr[] values,
        nint count,
        IntPtr keyCallbacks,
        IntPtr valueCallbacks);
    [DllImport(CF)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(IntPtr value, int type, out long number);
    [DllImport(CF)][return: MarshalAs(UnmanagedType.I1)] private static extern bool CFBooleanGetValue(IntPtr value);
    [DllImport(CF)][return: MarshalAs(UnmanagedType.I1)] private static extern bool CFEqual(IntPtr a, IntPtr b);
    [DllImport(CF)] private static extern nuint CFHash(IntPtr value);
    private const int ProcessPathBufferBytes = 4096;
    [DllImport("/usr/lib/libproc.dylib")] private static extern int proc_pidpath(int pid, byte[] buffer, uint size);
    [DllImport(AX)][return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrusted();
    [DllImport(AX)][return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);
    [DllImport(AX)] private static extern IntPtr AXUIElementCreateApplication(int pid);
    [DllImport(AX)] private static extern int AXUIElementGetPid(IntPtr element, out int pid);
    [DllImport(AX)] private static extern int AXUIElementSetMessagingTimeout(IntPtr element, float seconds);
    [DllImport(AX)] private static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
    [DllImport(AX)] private static extern int AXUIElementSetAttributeValue(IntPtr element, IntPtr attribute, IntPtr value);
    [DllImport(AX)] private static extern int AXUIElementPerformAction(IntPtr element, IntPtr action);
    [DllImport(AX)] private static extern IntPtr AXValueCreate(int type, ref Pair pair);
    [DllImport(AX)][return: MarshalAs(UnmanagedType.I1)] private static extern bool AXValueGetValue(IntPtr value, int type, out Pair pair);
    [DllImport(CG)] private static extern IntPtr CGWindowListCopyWindowInfo(uint options, uint relativeWindow);
    [DllImport(CG)][return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CG)][return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestScreenCaptureAccess();
    [DllImport(CG)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint type, Pair position, uint button);
    [DllImport(CG)]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort key, [MarshalAs(UnmanagedType.I1)] bool down);
    [DllImport(CG)] private static extern void CGEventSetFlags(IntPtr value, ulong flags);
    [DllImport(CG)] private static extern void CGEventPost(uint tap, IntPtr value);
    [DllImport(CG)]
    private static extern void CGEventKeyboardSetUnicodeString(
        IntPtr value,
        nuint length,
        [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] text);

    // An observed element kept for later actions, with its window and process start, so an action can prove it is still the same element.
    private sealed record Cached(Ref Handle, Ref Window, int Pid, long Started, string Role, string AutoId, string Name);
    private readonly Dictionary<int, Cached> _cache = new();
    private readonly Dictionary<string, int> _chapters = new();
    private int _nextId;
    public string Platform => "macos-ax-quartz";
    public static object Permissions()
        => new
        {
            accessibility = AXIsProcessTrusted(),
            screen_capture = CGPreflightScreenCaptureAccess(),
            runtime_readytorun_disabled = Environment.GetEnvironmentVariable("COMPlus_ReadyToRun") == "0"
                && Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") == "0",
            instruction = "Grant this process Accessibility and Screen Recording in macOS System Settings, then restart it."
        };
    public static object RequestPermissions()
    {
        if (!AXIsProcessTrusted())
        {
            using var key = Str("AXTrustedCheckOptionPrompt");
            IntPtr library = NativeLibrary.Load(CF);
            try
            {
                var value = Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCFBooleanTrue"));
                // The CFType callbacks make the dictionary compare keys by content. Without them AX looks up its own
                // constant by pointer, does not find this key, reads a null value and the process crashes.
                IntPtr keyCallbacks = NativeLibrary.GetExport(library, "kCFTypeDictionaryKeyCallBacks");
                IntPtr valueCallbacks = NativeLibrary.GetExport(library, "kCFTypeDictionaryValueCallBacks");
                using var options = new Ref(CFDictionaryCreate(IntPtr.Zero, [key.Ptr], [value], 1, keyCallbacks, valueCallbacks));
                AXIsProcessTrustedWithOptions(options.Ptr);
            }
            finally { NativeLibrary.Free(library); }
        }
        if (!CGPreflightScreenCaptureAccess())
            CGRequestScreenCaptureAccess();
        return Permissions();
    }
    public IReadOnlySet<ToolFeature> Features
    {
        get
        {
            var features = new HashSet<ToolFeature>();
            if (AXIsProcessTrusted())
                features.UnionWith(
                [
                    ToolFeature.Windows, ToolFeature.WindowControl, ToolFeature.SemanticInspect, ToolFeature.SemanticInvoke,
                    ToolFeature.BackgroundInvoke, ToolFeature.TextInjection, ToolFeature.PhysicalInput
                ]);
            if (CGPreflightScreenCaptureAccess())
                features.Add(ToolFeature.Screenshot);
            return features;
        }
    }
    public IReadOnlySet<Tool>? SupportedTools
    {
        get;
    } = new HashSet<Tool>
    {
        Tool.GetMachineCapabilities, Tool.ListAvailableMcps, Tool.GetActionStatus, Tool.EndSession, Tool.ListWindows,
        Tool.FocusWindow, Tool.CloseWindow, Tool.GetWindowGeometry, Tool.SetWindowGeometry, Tool.GetUiTree, Tool.GetChapters,
        Tool.InspectSection, Tool.FindElements, Tool.GetUiDelta, Tool.ClickElement, Tool.SilentClick, Tool.MouseClick,
        Tool.MouseMove, Tool.DragAndDrop, Tool.Hotkey, Tool.TypeText, Tool.TakeScreenshot, Tool.WaitElement, Tool.VerifyElement,
        Tool.LaunchProcess, Tool.RunTerminalCommand, Tool.FsListDirectory, Tool.FsReadFile, Tool.FsWriteFile, Tool.FsSearchFiles,
        Tool.FsGetMetadata, Tool.ListMacros, Tool.GetMacro, Tool.GetMacroTrace, Tool.SaveMacro, Tool.RunMacro
    };
    private static Ref Str(string text) => new(CFStringCreateWithCString(IntPtr.Zero, text, 0x08000100));
    // Throws InvalidDataException for an oversized or undecodable string.
    private static string Text(IntPtr value)
    {
        if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID())
            return "";
        long length = CFStringGetLength(value);
        if (length > 65536)
            throw new InvalidDataException("AX string exceeds observation limit.");
        var bytes = new byte[checked((int)length * 4 + 1)];
        return CFStringGetCString(value, bytes, bytes.Length, 0x08000100)
            ? Encoding.UTF8.GetString(bytes.AsSpan(0, Array.IndexOf(bytes, (byte)0)))
            : throw new InvalidDataException("AX string encoding failed.");
    }
    // Throws InvalidOperationException when the attribute read is a failure, which refuses the observation. A descriptive
    // attribute that fails generically is absent instead, see MacAttributeRead.
    private static Ref Attr(IntPtr element, string key)
    {
        using var name = Str(key);
        int error = AXUIElementCopyAttributeValue(element, name.Ptr, out var value);
        var outcome = MacAttributeRead.Classify(key, error);
        if (outcome == MacAttributeOutcome.Failed)
            throw new InvalidOperationException($"AX attribute {key} failed ({error}); observation is unavailable.");
        return new Ref(outcome == MacAttributeOutcome.Value ? value : IntPtr.Zero);
    }
    private static string ReadText(IntPtr element, string key)
    {
        using var value = Attr(element, key);
        return Text(value.Ptr);
    }
    private static bool ReadBool(IntPtr element, string key, bool fallback = false)
    {
        bool? value = ReadOptionalBool(element, key);

        return value ?? fallback;
    }
    // Returns null when the attribute is absent or is not a boolean, so the caller chooses the default.
    private static bool? ReadOptionalBool(IntPtr element, string key)
    {
        using var value = Attr(element, key);
        if (value.IsInvalid || CFGetTypeID(value.Ptr) != CFBooleanGetTypeID())
            return null;

        return CFBooleanGetValue(value.Ptr);
    }
    // macOS text areas report no AXEnabled, so an absent attribute counts as enabled. Only an explicit false disables.
    private static bool IsElementEnabled(IntPtr element)
    {
        bool? reportedValue = ReadOptionalBool(element, "AXEnabled");

        return MacAttributeRead.EnabledFrom(reportedValue);
    }
    private static Pair ReadPair(IntPtr element, string key, int type)
    {
        using var value = Attr(element, key);
        return !value.IsInvalid && AXValueGetValue(value.Ptr, type, out var pair) ? pair : default;
    }
    // Throws UnauthorizedAccessException without the Accessibility permission.
    private static void Require()
    {
        if (!AXIsProcessTrusted())
            throw new UnauthorizedAccessException(
                "macOS Accessibility permission is missing. Grant the actuator in System Settings; no input was dispatched.");
    }
    // Throws InvalidDataException for an invalid or oversized list of children.
    private static List<Ref> Children(IntPtr element, string key = "AXChildren")
    {
        using var array = Attr(element, key);
        var children = new List<Ref>();
        if (array.IsInvalid)
            return children;
        if (CFGetTypeID(array.Ptr) != CFArrayGetTypeID())
            throw new InvalidDataException("Invalid AX children response.");
        int count = checked((int)CFArrayGetCount(array.Ptr));
        if (count > 2000)
            throw new InvalidDataException("Too many AX children; narrow the window.");
        for (int i = 0; i < count; i++)
            children.Add(new Ref(CFRetain(CFArrayGetValueAtIndex(array.Ptr, i))));
        if (key == "AXChildren" && ReadText(element, "AXRole") == "AXWindow")
        {
            foreach (var sheet in Children(element, "AXSheets"))
            {
                if (children.Any(child => CFEqual(child.Ptr, sheet.Ptr)))
                    sheet.Dispose();
                else
                    children.Add(sheet);
            }
        }
        return children;
    }
    private static long Started(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.StartTime.ToUniversalTime().Ticks;
    }
    // Throws InvalidOperationException when the window server does not answer.
    private static List<int> WindowPids()
    {
        using var list = new Ref(CGWindowListCopyWindowInfo(1 | 16, 0));
        if (list.IsInvalid)
            throw new InvalidOperationException("Window server unavailable.");
        var pids = new HashSet<int>();
        using var pidKey = Str("kCGWindowOwnerPID");
        using var layerKey = Str("kCGWindowLayer");
        for (nint i = 0; i < CFArrayGetCount(list.Ptr); i++)
        {
            var dictionary = CFArrayGetValueAtIndex(list.Ptr, i);
            var pid = CFDictionaryGetValue(dictionary, pidKey.Ptr);
            var layer = CFDictionaryGetValue(dictionary, layerKey.Ptr);
            if (pid != IntPtr.Zero
                && layer != IntPtr.Zero
                && CFNumberGetValue(layer, 4, out long level)
                && level == 0
                && CFNumberGetValue(pid, 4, out long id))
                pids.Add(checked((int)id));
        }
        return pids.ToList();
    }
    // One accessibility window and the process it belongs to; the caller disposes Window.
    private sealed record AppWindow(int Pid, Ref Window);

    // Rethrows the Exception of a failed enumeration after releasing the handles.
    private static List<AppWindow> Windows(int? pid = null)
    {
        Require();
        var result = new List<AppWindow>();
        try
        {
            foreach (int processId in pid.HasValue ? [pid.Value] : WindowPids())
            {
                using var app = new Ref(AXUIElementCreateApplication(processId));
                AXUIElementSetMessagingTimeout(app.Ptr, 1);
                var windows = Children(app.Ptr, "AXWindows");
                foreach (var window in windows)
                    result.Add(new AppWindow(processId, window));
            }
            return result;
        }
        catch
        {
            // AX references are native handles: release them for every failure type, then report it.
            foreach (var entry in result)
                entry.Window.Dispose();
            throw;
        }
    }
    // Throws ArgumentException when the request matches several windows or names an unknown handle. Every window reference
    // that is not returned is released, whatever fails.
    private static AppWindow? Select(WindowSelectionRequest request)
    {
        var windows = Windows(request.ProcessId);
        try
        {
            return WindowSelection.HasCriteria(request) ? SelectChosen(windows, request) : SelectFrontmostMain(windows);
        }
        finally { foreach (var entry in windows) entry.Window.Dispose(); }
    }

    private static AppWindow? Select(int? pid, string? title)
    {
        return Select(new WindowSelectionRequest(pid, title, null));
    }

    // The window that the shared rule chooses, retained for the caller.
    private static AppWindow? SelectChosen(List<AppWindow> windows, WindowSelectionRequest request)
    {
        var descriptions = windows.Select(Describe).ToList();
        var chosen = WindowSelection.RequireChoice(request, WindowSelection.Select(descriptions, request));
        if (chosen == null)
            return null;
        int index = descriptions.FindIndex(description => description.WindowHandle == chosen.WindowHandle);

        return Retain(windows[index]);
    }

    // Without a process, caption or handle, the frontmost main window is the target. Throws ArgumentException when several are
    // frontmost, because input must not go to a guessed one.
    private static AppWindow? SelectFrontmostMain(List<AppWindow> windows)
    {
        var frontmost = windows.Where(entry => IsFrontmost(entry.Pid) && ReadBool(entry.Window.Ptr, "AXMain")).ToList();
        if (frontmost.Count > 1)
            throw new ArgumentException(WindowSelection.AmbiguityMessage(null, frontmost.Count));

        return frontmost.Count == 0 ? null : Retain(frontmost[0]);
    }

    private static AppWindow Retain(AppWindow entry)
    {
        return new AppWindow(entry.Pid, new Ref(CFRetain(entry.Window.Ptr)));
    }
    private static bool IsFrontmost(int pid)
    {
        using var app = new Ref(AXUIElementCreateApplication(pid));
        return ReadBool(app.Ptr, "AXFrontmost");
    }
    private static int Set(IntPtr element, string key, IntPtr value)
    {
        using var name = Str(key);
        return AXUIElementSetAttributeValue(element, name.Ptr, value);
    }
    private static int SetBool(IntPtr element, string key, bool value)
    {
        // Use Apple's canonical CFBoolean values, exported as data symbols.
        IntPtr library = NativeLibrary.Load(CF);
        try
        {
            var boolean = Marshal.ReadIntPtr(NativeLibrary.GetExport(library, value ? "kCFBooleanTrue" : "kCFBooleanFalse"));
            return Set(element, key, boolean);
        }
        finally { NativeLibrary.Free(library); }
    }
    private static int Action(IntPtr element, string action)
    {
        using var name = Str(action);
        return AXUIElementPerformAction(element, name.Ptr);
    }
    public List<WindowInfo> ListWindows()
    {
        var windows = Windows();
        try
        {
            return windows.Select(Describe).ToList();
        }
        finally
        {
            foreach (var entry in windows)
                entry.Window.Dispose();
        }
    }
    // The handle joins the process id with CFHash of the accessibility window, which agrees with CFEqual, so one window keeps
    // one handle while it exists. Version, path and start come from the process; an unknown value stays unknown.
    private static WindowInfo Describe(AppWindow window)
    {
        using var process = Process.GetProcessById(window.Pid);
        string? path = ExecutablePath(window.Pid);
        long identity = ((long)window.Pid << 32) | (long)((ulong)CFHash(window.Window.Ptr) & 0xFFFFFFFFUL);

        return new WindowInfo(window.Pid, process.ProcessName, MacBundleVersion.Read(path), ReadText(window.Window.Ptr, "AXTitle"),
            new IntPtr(identity))
        {
            ExecutablePath = path,
            ProcessStartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime()),
            Kind = ReadWindowKind(window.Window.Ptr)
        };
    }

    // AX gives a sheet and a modal window the role or attribute of a dialog, and a dialog its subrole. A floating window is a
    // palette or tool window, not a dialog. A missing attribute reads as absent; other AX errors keep throwing.
    private static WindowKind ReadWindowKind(IntPtr window)
    {
        string role = ReadText(window, "AXRole");
        if (role == "AXSheet" || ReadBool(window, "AXModal") || ParentIsSheet(window))
            return WindowKind.Dialog;
        if (role != "AXWindow")
            return WindowKind.Main;
        string subrole = ReadText(window, "AXSubrole");

        return subrole is "AXDialog" or "AXSystemDialog" ? WindowKind.Dialog : WindowKind.Main;
    }

    private static bool ParentIsSheet(IntPtr window)
    {
        using var parent = Attr(window, "AXParent");
        return !parent.IsInvalid && ReadText(parent.Ptr, "AXRole") == "AXSheet";
    }
    private static string? ExecutablePath(int pid)
    {
        var buffer = new byte[ProcessPathBufferBytes];
        int length = proc_pidpath(pid, buffer, (uint)buffer.Length);

        return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
    }
    private VisualTreeNode Node(IntPtr handle, int id)
    {
        var position = ReadPair(handle, "AXPosition", 1);
        var size = ReadPair(handle, "AXSize", 2);
        string role = ReadText(handle, "AXRole"), name = ReadText(handle, "AXTitle");
        if (name == "")
            name = ReadText(handle, "AXDescription");
        string? subrole = MacAccessibilityRoleName.ReadsSubrole(role) ? ReadText(handle, "AXSubrole") : null;
        string value = MacAccessibilityRoleName.HidesValue(role, subrole) ? "" : ReadText(handle, "AXValue");
        if (role == "AXStaticText" && value == "")
            value = name;
        return new VisualTreeNode
        {
            Id = id,
            Role = MacAccessibilityRoleName.From(role, subrole),
            Name = name,
            AutoId = ReadText(handle, "AXIdentifier"),
            Value = value,
            IsEnabled = IsElementEnabled(handle),
            IsOffscreen = size.X <= 0 || size.Y <= 0,
            Bounds = new { x = position.X, y = position.Y, width = size.X, height = size.Y }
        };
    }
    private void Clear()
    {
        foreach (var cached in _cache.Values)
        {
            cached.Handle.Dispose();
            cached.Window.Dispose();
        }
        _cache.Clear();
        _chapters.Clear();
    }
    public void Dispose() => Clear();
    // Throws InvalidDataException beyond 2000 nodes, and rethrows any other Exception after releasing the handles.
    private VisualTreeNode Observe(int? pid, string? title, int depth)
    {
        Require();
        Clear();
        var root = new VisualTreeNode { Role = "Root", Name = "macOS Accessibility" };
        var windows = Windows(pid);
        try
        {
            foreach (var entry in windows)
                if (title == null || ReadText(entry.Window.Ptr, "AXTitle").Contains(title, StringComparison.OrdinalIgnoreCase))
                    root.Children.Add(Visit(entry.Window.Ptr, entry.Window.Ptr, entry.Pid, Started(entry.Pid), 0, depth));
            return root;
        }
        catch
        {
            // A half-built element cache would hand out ids for windows that were never read.
            Clear();
            throw;
        }
        finally { foreach (var entry in windows) entry.Window.Dispose(); }
    }

    private const int MaximumObservedNodes = 2000;

    // Every visited element is cached with its window and process start, so a later action can prove it is the same one.
    // Throws InvalidDataException when the observation exceeds the node limit.
    private VisualTreeNode Visit(IntPtr handle, IntPtr window, int processId, long started, int level, int depth)
    {
        if (_cache.Count >= MaximumObservedNodes)
            throw new InvalidDataException("AX observation exceeds 2000 nodes; narrow the window.");
        int id = checked(++_nextId);
        var node = Node(handle, id);
        _cache[id] = new Cached(
            new Ref(CFRetain(handle)),
            new Ref(CFRetain(window)),
            processId,
            started,
            node.Role,
            node.AutoId,
            node.Name);
        if (level >= depth)
            return node;

        var children = Children(handle);
        try
        {
            foreach (var child in children)
                node.Children.Add(Visit(child.Ptr, window, processId, started, level + 1, depth));
        }
        finally { foreach (var child in children) child.Dispose(); }
        return node;
    }
    // Throws ArgumentException when the target is from an older observation, its process or identity changed, or it left its window.
    private Cached Current(int id)
    {
        Require();
        if (!_cache.TryGetValue(id, out var cached))
            throw new ArgumentException("Stale macOS target.");
        if (AXUIElementGetPid(cached.Handle.Ptr, out int pid) != 0 || pid != cached.Pid || Started(pid) != cached.Started)
            throw new ArgumentException("Target process changed.");
        var node = Node(cached.Handle.Ptr, id);
        if (node.Role != cached.Role
            || node.AutoId != cached.AutoId
            || node.Role is not ("TextField" or "TextArea" or "StaticText") && node.Name != cached.Name)
            throw new ArgumentException("Target identity changed.");
        // A retained AX reference can outlive removal from a window. Check native ancestry.
        using var parent = new Ref(CFRetain(cached.Handle.Ptr));
        IntPtr current = CFRetain(parent.Ptr);
        try
        {
            for (int i = 0; i < 64 && current != IntPtr.Zero; i++)
            {
                if (CFEqual(current, cached.Window.Ptr))
                    return cached;
                using var next = Attr(current, "AXParent");
                CFRelease(current);
                current = next.IsInvalid ? IntPtr.Zero : CFRetain(next.Ptr);
            }
        }
        finally { if (current != IntPtr.Zero) CFRelease(current); }
        throw new ArgumentException("Target detached from the observed window.");
    }
    private static string Format(VisualTreeNode root)
    {
        var text = new StringBuilder();
        void Visit(VisualTreeNode node, int level)
        {
            string indent = new string(' ', level * 2);
            string name = JsonSerializer.Serialize(node.Name);
            string autoId = JsonSerializer.Serialize(node.AutoId);
            string value = JsonSerializer.Serialize(node.Value);
            text.AppendLine(
                $"{indent}[{node.Id}] {node.Role} {name} (autoId: {autoId}, val: {value}, "
                + $"enabled: {node.IsEnabled}, offscreen: {node.IsOffscreen})");
            foreach (var child in node.Children)
                Visit(child, level + 1);
        }
        Visit(root, 0);
        return text.ToString();
    }
    public object? GetVisualTree(int? pid, string? title, int maxDepth = 4) => Observe(pid, title, maxDepth);
    public string GetUiTree(WindowSelectionRequest request, int maxDepth = 4)
    {
        try
        {
            VisualTreeNode tree = request.Handle.HasValue
                ? ObserveHandle(request, maxDepth)
                : Observe(request.ProcessId, request.TitleQuery, maxDepth);
            return Format(tree);
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    // Observes only the window that the native handle names. Throws ArgumentException for an unknown handle, and rethrows any
    // other Exception after clearing the element cache.
    private VisualTreeNode ObserveHandle(WindowSelectionRequest request, int maxDepth)
    {
        Require();
        Clear();
        var selected = Select(request) ?? throw new UnreachableException();
        var root = new VisualTreeNode { Role = "Root", Name = "macOS Accessibility" };
        try
        {
            root.Children.Add(Visit(selected.Window.Ptr, selected.Window.Ptr, selected.Pid, Started(selected.Pid), 0, maxDepth));
            return root;
        }
        catch
        {
            // A half-built element cache would hand out ids for windows that were never read.
            Clear();
            throw;
        }
        finally { selected.Window.Dispose(); }
    }
    public string GetChapters(int? pid, string? title)
    {
        var root = Observe(pid, title, 2);
        foreach (var window in root.Children)
            foreach (var child in window.Children)
                _chapters["K" + child.Id] = child.Id;
        return string.Join("\n", _chapters.Select(p => $"[{p.Key}] {Node(Current(p.Value).Handle.Ptr, p.Value).Role}"));
    }
    // Throws InvalidDataException when the observation limit is reached.
    public string InspectSection(string chapter, int maxDepth = 3)
    {
        if (!_chapters.TryGetValue(chapter, out int id))
            return "error: Stale chapter.";
        var target = Current(id);
        var window = target.Window;
        var started = target.Started;
        VisualTreeNode Visit(IntPtr handle, int level)
        {
            if (_cache.Count >= 2000)
                throw new InvalidDataException("Observation limit exceeded.");
            int newId = checked(++_nextId);
            var node = Node(handle, newId);
            _cache[newId] = new Cached(
                new Ref(CFRetain(handle)),
                new Ref(CFRetain(window.Ptr)),
                target.Pid,
                started,
                node.Role,
                node.AutoId,
                node.Name);
            if (level < maxDepth)
            {
                var children = Children(handle);
                try
                {
                    foreach (var child in children)
                        node.Children.Add(Visit(child.Ptr, level + 1));
                }
                finally { foreach (var child in children) child.Dispose(); }
            }
            return node;
        }
        return Format(Visit(target.Handle.Ptr, 0));
    }
    public bool FocusWindow(WindowSelectionRequest request)
    {
        var selected = Select(request);
        if (selected == null)
            return false;
        using var window = selected.Window;
        using var app = new Ref(AXUIElementCreateApplication(selected.Pid));
        if (SetBool(app.Ptr, "AXFrontmost", true) != 0)
            return false;
        int raised = Action(window.Ptr, "AXRaise");
        return IsFrontmost(selected.Pid) && (raised == 0 || ReadBool(window.Ptr, "AXMain"));
    }
    public UiOutcome CloseWindow(int? pid, string? title)
    {
        var selected = Select(pid, title);
        if (selected == null)
            return new(false, "Window not found.");
        using var window = selected.Window;
        using var close = Attr(window.Ptr, "AXCloseButton");
        return !close.IsInvalid
            && Action(close.Ptr, "AXPress") == 0
                ? new(true, "Close requested; an unsaved dialog may remain.")
                : new(false, "Window does not expose a close action.");
    }
    public object? GetWindowGeometry(int? pid, string? title)
    {
        var selected = Select(pid, title);
        if (selected == null)
            return null;
        using var window = selected.Window;
        return Node(window.Ptr, 0).Bounds;
    }
    public GeometryOutcome SetWindowGeometry(int? pid, string? title, string? state, int? width, int? height, int? x, int? y)
    {
        var selected = Select(pid, title);
        if (selected == null)
            return new(false, "Window not found.", null);
        using var window = selected.Window;
        if (state is "maximized")
            return new(false, "macOS zoom/fullscreen is app-specific; maximization is unsupported.", null);
        if (state != null && SetBool(window.Ptr, "AXMinimized", state == "minimized") != 0)
            return new(false, "Window refused state change.", null);
        var position = ReadPair(window.Ptr, "AXPosition", 1);
        var size = ReadPair(window.Ptr, "AXSize", 2);
        if (x.HasValue || y.HasValue)
        {
            position = new Pair(x ?? position.X, y ?? position.Y);
            using var value = new Ref(AXValueCreate(1, ref position));
            if (Set(window.Ptr, "AXPosition", value.Ptr) != 0)
                return new(false, "Position change refused.", null);
        }
        if (width.HasValue || height.HasValue)
        {
            size = new Pair(width ?? size.X, height ?? size.Y);
            using var value = new Ref(AXValueCreate(2, ref size));
            if (Set(window.Ptr, "AXSize", value.Ptr) != 0)
                return new(false, "Size change refused.", null);
        }
        return new(true, "Geometry dispatched.", Node(window.Ptr, 0).Bounds);
    }
    public UiOutcome ClickElement(int id, int autoWaitMs = 3000)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            var target = Current(id);
            if (Actionable(target))
                return Action(target.Handle.Ptr, "AXPress") == 0
                    ? new(true, "AXPress dispatched.")
                    : new(false, "Target does not support AXPress.");
            Thread.Sleep(50);
        } while (watch.ElapsedMilliseconds < autoWaitMs);
        return new(false, "Target remained disabled or blocked by a modal sheet.");
    }
    private static bool Actionable(Cached target)
    {
        if (!IsElementEnabled(target.Handle.Ptr))
            return false;
        var sheets = Children(target.Window.Ptr, "AXSheets");
        try
        {
            if (sheets.Count == 0)
                return true;
            IntPtr parent = CFRetain(target.Handle.Ptr);
            try
            {
                for (int level = 0; level < 64 && parent != IntPtr.Zero; level++)
                {
                    if (sheets.Any(sheet => CFEqual(sheet.Ptr, parent)))
                        return true;
                    using var next = Attr(parent, "AXParent");
                    CFRelease(parent);
                    parent = next.IsInvalid ? IntPtr.Zero : CFRetain(next.Ptr);
                }
            }
            finally { if (parent != IntPtr.Zero) CFRelease(parent); }
            return false;
        }
        finally { foreach (var sheet in sheets) sheet.Dispose(); }
    }
    public UiOutcome SilentClick(int id) => ClickElement(id);
    public UiOutcome SetText(int id, string text, int autoWaitMs = 3000) => TypeText(id, text, true, false, autoWaitMs);
    // Throws InvalidOperationException when focus moves away during typing or no keyboard event can be made.
    public UiOutcome TypeText(int id, string text, bool clear, bool enter, int autoWaitMs)
    {
        if (text.Any(char.IsControl))
            return new(false, "Literal keyboard input rejects control characters; use explicit hotkey actions.");
        var watch = Stopwatch.StartNew();
        do
        {
            var target = Current(id);
            var node = Node(target.Handle.Ptr, id);
            if (node.Role is not ("TextField" or "TextArea"))
                return new(false, "AX literal input requires a text control.");
            if (Actionable(target))
                return TypeIntoFocusedTarget(target, id, text, clear, enter);
            Thread.Sleep(50);
        } while (watch.ElapsedMilliseconds < autoWaitMs);
        return new(false, "Target remained disabled or blocked by a modal sheet.");
    }

    private const int MaximumChunkCharacters = 20;

    // AXValue can alter an editor's display without notifying its app's data binding, so type_text uses real literal
    // keyboard events, in short chunks, each only while the target still has the focus.
    // Throws InvalidOperationException when focus moves away during typing or no keyboard event can be made.
    private UiOutcome TypeIntoFocusedTarget(Cached target, int id, string text, bool clear, bool enter)
    {
        UiOutcome? notFocused = FocusTextTarget(target, id);
        if (notFocused != null)
            return notFocused;
        if (clear)
        {
            var chord = KeyChord(["cmd", "a"]);
            if (!chord.Success)
                return chord;
        }

        var chunk = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (chunk.Length + rune.Utf16SequenceLength > MaximumChunkCharacters)
                SendChunk(target.Pid, id, chunk);
            chunk.Append(rune.ToString());
        }
        SendChunk(target.Pid, id, chunk);
        if (enter)
            return KeyChord(["enter"]);
        return new(true, "Literal Quartz keyboard input dispatched; verify app state separately.");
    }

    // Null when the application is frontmost and the field has the focus; otherwise the refusal.
    private UiOutcome? FocusTextTarget(Cached target, int id)
    {
        using var app = new Ref(AXUIElementCreateApplication(target.Pid));
        int activated = SetBool(app.Ptr, "AXFrontmost", true);
        int raised = Action(target.Window.Ptr, "AXRaise");
        int focused = SetBool(target.Handle.Ptr, "AXFocused", true);
        if (activated != 0 || raised != 0 && !ReadBool(target.Window.Ptr, "AXMain") || focused != 0)
            return new(false, $"Text target could not be focused (activate={activated}, raise={raised}, focus={focused}).");

        Thread.Sleep(50);
        if (!IsFrontmost(target.Pid) || !ReadBool(Current(id).Handle.Ptr, "AXFocused") || !Actionable(target))
            return new(false, "Focused text target changed; no text was dispatched.");
        return null;
    }

    // Throws InvalidOperationException when the focus moved away or Quartz gives no keyboard event.
    private void SendChunk(int pid, int id, StringBuilder chunk)
    {
        if (chunk.Length == 0)
            return;
        if (!IsFrontmost(pid) || !ReadBool(Current(id).Handle.Ptr, "AXFocused"))
            throw new InvalidOperationException("Focus changed during text input; outcome is unknown.");

        char[] chars = chunk.ToString().ToCharArray();
        foreach (bool down in new[] { true, false })
        {
            using var ev = new Ref(CGEventCreateKeyboardEvent(IntPtr.Zero, 0, down));
            if (ev.IsInvalid)
                throw new InvalidOperationException("Keyboard event unavailable.");
            CGEventSetFlags(ev.Ptr, 0);
            CGEventKeyboardSetUnicodeString(ev.Ptr, (nuint)chars.Length, chars);
            CGEventPost(0, ev.Ptr);
        }
        chunk.Clear();
        Thread.Sleep(10);
    }
    // Throws InvalidOperationException when no mouse event can be made.
    private static void Mouse(uint type, int x, int y, uint button = 0)
    {
        Require();
        using var ev = new Ref(CGEventCreateMouseEvent(IntPtr.Zero, type, new Pair(x, y), button));
        if (ev.IsInvalid)
            throw new InvalidOperationException("Quartz event unavailable.");
        CGEventPost(0, ev.Ptr);
    }
    public UiOutcome MouseMove(int x, int y)
    {
        Mouse(5, x, y);
        return new(true, "Pointer event dispatched.");
    }
    public UiOutcome DragAndDrop(int fromX, int fromY, int toX, int toY)
    {
        Mouse(1, fromX, fromY);
        try
        {
            for (int i = 1; i <= 20; i++)
            {
                Mouse(6, fromX + (toX - fromX) * i / 20, fromY + (toY - fromY) * i / 20);
                Thread.Sleep(10);
            }
        }
        finally { Mouse(2, toX, toY); }
        return new(true, "Drag events dispatched.");
    }
    public ClickOutcome MouseClick(int id, string button = "left", int? x = null, int? y = null)
    {
        if (button is not ("left" or "right" or "middle"))
            return new(false, "Unsupported button.", null);
        if (id > 0)
        {
            var target = Current(id);
            var node = Node(target.Handle.Ptr, id);
            var first = ReadPair(target.Handle.Ptr, "AXPosition", 1);
            Thread.Sleep(50);
            var second = ReadPair(Current(id).Handle.Ptr, "AXPosition", 1);
            var size = ReadPair(target.Handle.Ptr, "AXSize", 2);
            if (!node.IsEnabled || node.IsOffscreen || !IsFrontmost(target.Pid) || first.X != second.X || first.Y != second.Y)
                return new(false, "Target is not enabled, stable and foreground-visible.", null);
            // Hit testing/occlusion cannot be established solely from AX bounds. Semantic press is safer.
            return new(
                false,
                "Physical element click requires occlusion hit-testing, which is not implemented. Use click_element or explicit "
                + "coordinates.",
                null);
        }
        if (!x.HasValue || !y.HasValue)
            return new(false, "Coordinates required.", null);
        uint code = button == "right" ? 1u : button == "middle" ? 2u : 0u;
        Mouse(code == 0 ? 1u : code == 1 ? 3u : 25u, x.Value, y.Value, code);
        Mouse(code == 0 ? 2u : code == 1 ? 4u : 26u, x.Value, y.Value, code);
        return new(true, "Coordinate click dispatched.", new
        {
            x,
            y
        });
    }
    // Throws InvalidOperationException when no keyboard event can be made.
    // Virtual key codes of the keys a chord may name; anything else is refused instead of guessed.
    private static readonly Dictionary<string, ushort> KeyCodes = new()
    {
        ["enter"] = 36,
        ["return"] = 36,
        ["tab"] = 48,
        ["escape"] = 53,
        ["backspace"] = 51,
        ["delete"] = 117,
        ["left"] = 123,
        ["right"] = 124,
        ["down"] = 125,
        ["up"] = 126,
        ["a"] = 0,
        ["c"] = 8,
        ["v"] = 9,
        ["x"] = 7,
        ["z"] = 6,
        ["s"] = 1,
        ["q"] = 12,
        ["w"] = 13
    };
    private const ulong ShiftFlag = 1ul << 17;
    private const ulong ControlFlag = 1ul << 18;
    private const ulong AlternateFlag = 1ul << 19;
    private const ulong CommandFlag = 1ul << 20;

    public UiOutcome KeyChord(string[] keys)
    {
        Require();
        ulong flags = 0;
        ushort? key = null;
        foreach (string raw in keys)
        {
            string item = raw.ToLowerInvariant();
            if (item is "cmd" or "command" or "win")
                flags |= CommandFlag;
            else if (item == "shift")
                flags |= ShiftFlag;
            else if (item == "ctrl")
                flags |= ControlFlag;
            else if (item == "alt")
                flags |= AlternateFlag;
            else if (key == null && KeyCodes.TryGetValue(item, out ushort code))
                key = code;
            else
                return new(false, "Unsupported macOS key/chord.");
        }
        if (key == null)
            return new(false, "Chord requires one supported non-modifier key.");

        PostKey(key.Value, flags);
        return new(true, "Key chord dispatched.");
    }

    // Throws InvalidOperationException when Quartz gives no keyboard event.
    private static void PostKey(ushort key, ulong flags)
    {
        foreach (bool down in new[] { true, false })
        {
            using var ev = new Ref(CGEventCreateKeyboardEvent(IntPtr.Zero, key, down));
            if (ev.IsInvalid)
                throw new InvalidOperationException("Keyboard event unavailable.");
            CGEventSetFlags(ev.Ptr, flags);
            CGEventPost(0, ev.Ptr);
        }
    }
    public UiOutcome LaunchApp(string path, IReadOnlyList<string> arguments)
    {
        if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            // "open -a" would reinterpret arguments, so a bundle is only ever started without any.
            if (arguments.Count > 0)
            {
                return new(false, "Bundle launch arguments are unsupported.");
            }
            var result = ProcessRunner.Run("/usr/bin/open", ["-a", path]);
            return new(result.Success, result.Success ? "App launch requested." : result.Error);
        }
        using var process = ProcessLauncher.StartDetached(ProcessLauncher.CreateStartInfo(path, arguments));
        return new(process != null, "Process launch requested.");
    }
    public ScreenshotOutcome Screenshot()
    {
        if (!CGPreflightScreenCaptureAccess())
            return new(false, "macOS Screen Recording permission is missing.", null);
        string path = Path.Combine(Path.GetTempPath(), "k-lani-bot-" + Guid.CreateVersion7() + ".png");
        try
        {
            var result = ProcessRunner.Run("/usr/sbin/screencapture", ["-x", "-t", "png", path], 10000);
            if (!result.Success || !File.Exists(path))
                return new(false, "macOS screenshot failed: " + result.Error, null);
            var data = File.ReadAllBytes(path);
            return data.Length <= 2 * 1024 * 1024
                && data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                    ? new(true, "macOS display captured.", data)
                    : new(false, "Screenshot exceeded limit or is invalid.", null);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
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
    // Throws ArgumentException for a state this backend cannot check.
    public ElementCheckOutcome VerifyElement(
        int? id,
        string? name,
        string? autoId,
        string? role,
        int? pid,
        string? title,
        string? expectedState,
        string? text,
        int timeoutMs = 5000)
    {
        if (id == null && name == null && autoId == null && role == null)
            return new(false, "A selector is required.", null);
        string state = expectedState ?? "exists";
        var watch = Stopwatch.StartNew();
        do
        {
            VisualTreeNode[] matches = id.HasValue
                ? [Node(Current(id.Value).Handle.Ptr, id.Value)]
                : ObservedMatches(pid, title, name, autoId, role);
            if (matches.Length > 1)
                return new(false, "Ambiguous selector.", null);
            if (matches.Length == 0 && state == "absent" && text == null)
                return new(true, "Absence verified.", null);
            if (matches.Length == 1 && VerifiedOutcome(matches[0], state, text) is { } verified)
                return verified;
            Thread.Sleep(50);
        } while (watch.ElapsedMilliseconds < timeoutMs);
        return new(false, "Assertion did not match.", null);
    }

    private VisualTreeNode[] ObservedMatches(int? pid, string? title, string? name, string? autoId, string? role)
    {
        Observe(pid, title, 12);
        return _cache.Select(p => Node(p.Value.Handle.Ptr, p.Key)).Where(n =>
            (name == null || n.Name == name)
            && (autoId == null || n.AutoId == autoId)
            && (role == null || n.Role.Equals(role, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    // The success answer when the one match is in the expected state and shows the expected text, otherwise null.
    // Throws ArgumentException for a state the contract does not name.
    private static ElementCheckOutcome? VerifiedOutcome(VisualTreeNode node, string state, string? text)
    {
        bool match = state switch
        {
            "exists" => true,
            "enabled" => node.IsEnabled,
            "disabled" => !node.IsEnabled,
            "visible" => !node.IsOffscreen,
            "hidden" => node.IsOffscreen,
            "absent" => false,
            _ => throw new ArgumentException("Unsupported state.")
        };

        return match && (text == null || node.Value == text) ? new(true, "State and exact text verified.", node) : null;
    }
    public ClipboardOutcome GetClipboard() => new(false, "Clipboard is not exposed by this backend.", null);
    public UiOutcome SetClipboard(string text) => new(false, "Clipboard is not exposed by this backend.");
    public UiOutcome PasteClipboard(int? targetId = null) => new(false, "Clipboard is not exposed by this backend.");
}
