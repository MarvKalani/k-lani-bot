#if !WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using KLaniBot.Guard;
using KLaniBot.Actions;
namespace KLaniBot.Platforms.Linux;

// The AT-SPI client for LinuxUiService: element ids, the element tree, semantic actions and text. It needs the
// accessibility bus of the controlled session (DBUS_SESSION_BUS_ADDRESS) and reports itself unavailable
// otherwise, so the node offers no semantic features instead of failing on the first call. Every native call
// has a two-second timeout, because one unresponsive application must not hang the desktop thread.
internal sealed partial class AtSpiBackend
{
    private const string Lib = "libatspi.so.0";
    // AtspiStateType ordinals include HAS_TOOLTIP at 13.
    // AT-SPI keeps ENABLED and SENSITIVE as separate flags. GTK3 and Qt set both for an operable control, GTK4 sets
    // only SENSITIVE. SENSITIVE alone decides; a missing ENABLED must not turn a whole GTK4 application "disabled".
    private const int Defunct = 6, Modal = 16, Sensitive = 24, Showing = 25, Visible = 30;
    private readonly Dictionary<int, IntPtr> _elements = new();
    private readonly Dictionary<string, IntPtr> _sections = new();
    private readonly List<HeldActionConnection> _heldActionConnections = new();
    private long _lastReleaseCheckMilliseconds;
    private int _nextId;
    public bool Available
    {
        get;
    }
    public AtSpiBackend()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            return;
        if (!NativeLibrary.TryLoad(Lib, out var library))
            return;
        NativeLibrary.Free(library);
        Available = atspi_init() == 0;
        if (Available)
            atspi_set_timeout(2000, 2000);
    }
    private static bool MatchesCaption(string windowName, string? title, CaptionMatch captionMatch)
    {
        if (captionMatch == CaptionMatch.Exact)
        {
            return windowName == (title ?? "");
        }

        return string.IsNullOrEmpty(title) || windowName.Contains(title, StringComparison.OrdinalIgnoreCase);
    }

    // Throws NotSupportedException without the accessibility bus and InvalidOperationException when its desktop is unavailable.
    public VisualTreeNode Observe(int? pid, string? title, int maxDepth, CaptionMatch captionMatch = CaptionMatch.Contains)
    {
        if (!Available)
            throw new NotSupportedException("AT-SPI is unavailable. Start the accessibility bus in the controlled desktop session.");
        ReleaseSettledActionConnections();
        foreach (var pointer in _elements.Values)
            g_object_unref(pointer);
        _elements.Clear();
        var root = new VisualTreeNode { Name = "Accessibility desktop", Role = "Root" };
        var desktop = atspi_get_desktop(0);
        if (desktop == IntPtr.Zero)
            throw new InvalidOperationException("Accessibility desktop unavailable.");
        try
        {
            foreach (var app in Children(desktop))
            {
                try
                {
                    if (pid.HasValue && Pid(app) != pid)
                        continue;
                    foreach (var window in Children(app))
                    {
                        if (!MatchesCaption(Name(window), title, captionMatch))
                        {
                            g_object_unref(window);
                            continue;
                        }
                        root.Children.Add(Build(window, 0, maxDepth));
                    }
                }
                finally { g_object_unref(app); }
            }
        }
        finally { g_object_unref(desktop); }
        return root;
    }
    public string RegisterSection(int id)
    {
        string key = "K" + Guid.CreateVersion7().ToString("N");
        _sections.Add(key, g_object_ref(Get(id)));
        return key;
    }
    // Throws UnauthorizedAccessException when the target's executable or window is unknown.
    public UiTargetIdentity GetTargetIdentity(int id)
    {
        var node = Current(id);
        var pointer = Get(id);
        int pid = Pid(pointer);
        var ancestor = g_object_ref(pointer);
        string? title = null;
        try
        {
            for (int depth = 0; depth < 64 && ancestor != IntPtr.Zero; depth++)
            {
                IntPtr error = IntPtr.Zero;
                string role = Text(atspi_accessible_get_role_name(ancestor, ref error));
                Check(error);
                if (role is "frame" or "dialog" or "file chooser" or "window")
                {
                    title = Name(ancestor);
                    break;
                }
                error = IntPtr.Zero;
                var parent = atspi_accessible_get_parent(ancestor, ref error);
                Check(error);
                g_object_unref(ancestor);
                ancestor = parent;
            }
        }
        finally
        {
            if (ancestor != IntPtr.Zero)
                g_object_unref(ancestor);
        }
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        string executablePath = process.MainModule?.FileName
            ?? throw new UnauthorizedAccessException("Target executable is unknown.");
        string windowTitle = title ?? throw new UnauthorizedAccessException("Target window is unknown.");
        return new(pid, executablePath, windowTitle, node.Role, node.Name, node.AutoId, !node.IsOffscreen, node.IsEnabled);
    }

    // Throws ArgumentException for an old observation and InvalidOperationException when the provider cannot identify its window.
    public AccessibleScreenBounds? WindowBounds(int id)
    {
        List<IntPtr> chain = AncestorChain(Get(id));

        try
        {
            foreach (IntPtr ancestor in chain)
            {
                IntPtr error = IntPtr.Zero;
                string role = Text(atspi_accessible_get_role_name(ancestor, ref error));
                Check(error);
                if (role is "frame" or "dialog" or "file chooser" or "window")
                {
                    return Extents(ancestor);
                }
            }
        }
        finally
        {
            foreach (IntPtr pointer in chain)
            {
                g_object_unref(pointer);
            }
        }

        return null;
    }
    // GTK blocks the mouse for windows behind a modal dialog, but a semantic action still reaches them.
    // So besides the target's own ancestors, the other windows of the same application are checked.
    // Throws InvalidOperationException when a dialog stands in the way, and ArgumentException for an old observation.
    public void RequireDialogPermission(int id, ModalDialogMode dialogs, bool decisionRecorded)
    {
        ModalFacts facts = ReadModalFacts(id);
        ModalDialogPolicy.Require(facts.TargetIsInsideModal, facts.BehindAnotherModal, dialogs, decisionRecorded);
    }

    // Whether a modal dialog stands in the way of the element, by the same facts RequireDialogPermission decides on.
    // Throws ArgumentException for an old observation and InvalidOperationException when the provider has no state for it.
    public ModalDialogDetection DetectModalDialog(int id)
    {
        ModalFacts facts = ReadModalFacts(id);
        bool dialogInWay = facts.TargetIsInsideModal || facts.BehindAnotherModal;
        return dialogInWay ? ModalDialogDetection.Present : ModalDialogDetection.Absent;
    }

    // Use case: the input path and the routing of a recorded decision both ask what stands in the way of one element.
    // Both read these facts from ReadModalFacts, so they cannot disagree about whether a dialog is open.
    private sealed record ModalFacts(bool TargetIsInsideModal, bool BehindAnotherModal);

    // The chain is released before the caller sees the facts. Throws ArgumentException for an old observation and
    // InvalidOperationException when the provider has no state for an element.
    private ModalFacts ReadModalFacts(int id)
    {
        var chain = AncestorChain(Get(id));
        try
        {
            bool targetIsInsideModal = chain.Any(IsModalAndStillAlive);
            bool behindAnotherModal = !targetIsInsideModal && ApplicationHasShowingModalWindow(chain);
            return new ModalFacts(targetIsInsideModal, behindAnotherModal);
        }
        finally
        {
            foreach (var pointer in chain)
            {
                g_object_unref(pointer);
            }
        }
    }
    private const int MaximumTextElements = 300;
    private const int MaximumTextDepth = 12;

    // The names in the element's window, depth first. The chain ends with window, application and desktop; a shorter
    // chain has no window level, so its top is used.
    // Throws ArgumentException for an element of an older observation and InvalidOperationException when the provider fails.
    public string WindowText(int id)
    {
        List<IntPtr> chain = AncestorChain(Get(id));
        try
        {
            IntPtr window = chain.Count >= 3 ? chain[^3] : chain[^1];
            var names = new List<string>();
            CollectNames(window, 0, names);
            return string.Join("\n", names);
        }
        finally
        {
            foreach (var pointer in chain)
            {
                g_object_unref(pointer);
            }
        }
    }

    private static void CollectNames(IntPtr pointer, int depth, List<string> names)
    {
        if (names.Count >= MaximumTextElements || depth > MaximumTextDepth)
        {
            return;
        }
        string name = Name(pointer);
        if (name.Length > 0)
        {
            names.Add(name);
        }
        foreach (var child in Children(pointer))
        {
            try
            {
                CollectNames(child, depth + 1, names);
            }
            finally
            {
                g_object_unref(child);
            }
        }
    }

    // From the element up to the desktop. The caller owns the references.
    private static List<IntPtr> AncestorChain(IntPtr element)
    {
        var chain = new List<IntPtr>();
        var current = g_object_ref(element);
        for (int depth = 0; depth < 64 && current != IntPtr.Zero; depth++)
        {
            chain.Add(current);
            IntPtr error = IntPtr.Zero;
            var parent = atspi_accessible_get_parent(current, ref error);
            if (error != IntPtr.Zero)
            {
                foreach (var pointer in chain)
                {
                    g_object_unref(pointer);
                }
                Check(error);
            }
            current = parent;
        }
        if (current != IntPtr.Zero)
        {
            g_object_unref(current);
        }
        return chain;
    }
    // Looked up from the desktop down, so the element numbers of the last observation stay valid.
    public string? ShowingModalWindowTitle(int processId)
    {
        var desktop = atspi_get_desktop(0);
        if (desktop == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            foreach (var application in Children(desktop))
            {
                try
                {
                    if (Pid(application) == processId && ShowingModalWindowTitleOf(application) is { } title)
                    {
                        return title;
                    }
                }
                finally { g_object_unref(application); }
            }
            return null;
        }
        finally { g_object_unref(desktop); }
    }

    private static string? ShowingModalWindowTitleOf(IntPtr application)
    {
        foreach (var window in Children(application))
        {
            try
            {
                if (ReadModality(window) == WindowModality.ModalShowing)
                {
                    return Name(window);
                }
            }
            finally { g_object_unref(window); }
        }
        return null;
    }

    // One window that the accessibility tree lists for a process: its name there and whether it is a dialog.
    public sealed record AccessibleWindow(int ProcessId, string Name, bool IsDialog);

    // The windows that each listed process shows to the accessibility tree. An application that does not answer keeps
    // its windows out of the list, so its windows stay main windows.
    public List<AccessibleWindow> ListAccessibleWindows(IReadOnlySet<int> processIds)
    {
        var accessibleWindows = new List<AccessibleWindow>();
        if (!Available)
            return accessibleWindows;
        var desktop = atspi_get_desktop(0);
        if (desktop == IntPtr.Zero)
            return accessibleWindows;
        try
        {
            foreach (var application in Children(desktop))
            {
                try
                {
                    AddApplicationWindows(application, processIds, accessibleWindows);
                }
                catch (InvalidOperationException)
                {
                    // The application did not answer in time; its windows are left out of the list.
                }
                finally { g_object_unref(application); }
            }
        }
        finally { g_object_unref(desktop); }
        return accessibleWindows;
    }

    private static void AddApplicationWindows(IntPtr application, IReadOnlySet<int> processIds, List<AccessibleWindow> accessibleWindows)
    {
        int processId = Pid(application);
        if (!processIds.Contains(processId))
            return;
        foreach (var window in Children(application))
        {
            try
            {
                accessibleWindows.Add(new AccessibleWindow(processId, Name(window), IsDialogWindow(window)));
            }
            finally { g_object_unref(window); }
        }
    }

    // The dialog roles are named by AT-SPI, and a modal window counts as a dialog under any role. A state that cannot be read
    // leaves the window a main window rather than guessing.
    private static bool IsDialogWindow(IntPtr window)
    {
        string role = RoleName(window);
        if (role is "dialog" or "alert" or "file chooser")
            return true;
        try
        {
            return ReadModality(window) != WindowModality.NotModal;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // The chain ends with: window, application, desktop. A shorter chain has no application level to search.
    private static bool ApplicationHasShowingModalWindow(List<IntPtr> chain)
    {
        if (chain.Count < 3)
        {
            return false;
        }
        var application = chain[^2];
        foreach (var window in Children(application))
        {
            try
            {
                if (ReadModality(window) == WindowModality.ModalShowing)
                {
                    return true;
                }
            }
            finally
            {
                g_object_unref(window);
            }
        }
        return false;
    }
    private static bool IsModalAndStillAlive(IntPtr element)
    {
        return ReadModality(element) != WindowModality.NotModal;
    }

    // Whether a window is modal, and whether that modal window is currently showing.
    private enum WindowModality
    {
        NotModal,
        ModalHidden,
        ModalShowing
    }

    // Throws InvalidOperationException when the provider has no state for the element: the dialog check then refuses
    // instead of guessing that no dialog is open.
    private static WindowModality ReadModality(IntPtr element)
    {
        atspi_accessible_clear_cache(element);
        var states = atspi_accessible_get_state_set(element);
        if (states == IntPtr.Zero)
        {
            throw new InvalidOperationException("Accessibility state unavailable.");
        }

        try
        {
            if (atspi_state_set_contains(states, Modal) == 0)
            {
                return WindowModality.NotModal;
            }
            return atspi_state_set_contains(states, Showing) != 0 ? WindowModality.ModalShowing : WindowModality.ModalHidden;
        }
        finally
        {
            g_object_unref(states);
        }
    }
    public void ClearSections()
    {
        foreach (var pointer in _sections.Values)
            g_object_unref(pointer);
        _sections.Clear();
    }
    // Throws ArgumentException for a chapter of an older observation.
    public VisualTreeNode InspectSection(string key, int maxDepth)
    {
        if (!_sections.TryGetValue(key, out var pointer))
            throw new ArgumentException("Stale chapter.");
        var owned = g_object_ref(pointer);
        foreach (var cached in _elements.Values)
            g_object_unref(cached);
        _elements.Clear();
        // Chapter references have their own strong references; element IDs are
        // rebuilt and checked against the new dispatcher snapshot.
        return Build(owned, 0, maxDepth);
    }
    // Throws InvalidOperationException when the tree exceeds 2000 nodes.
    private VisualTreeNode Build(IntPtr pointer, int depth, int maxDepth)
    {
        if (_elements.Count >= 2000)
        {
            g_object_unref(pointer);
            throw new InvalidOperationException("Accessibility tree exceeds 2000 nodes. Narrow the window selector.");
        }
        int id = ++_nextId;
        _elements[id] = pointer;
        var node = Current(id);
        if (depth < maxDepth)
            foreach (var child in Children(pointer))
                node.Children.Add(Build(child, depth + 1, maxDepth));
        return node;
    }
    // Throws InvalidOperationException for more than 2000 children and when the provider does not answer: libatspi then
    // returns -1 without an error, which would read as an element without children.
    private static IEnumerable<IntPtr> Children(IntPtr pointer)
    {
        IntPtr error = IntPtr.Zero;
        int count = atspi_accessible_get_child_count(pointer, ref error);
        Check(error);
        if (count < 0)
            throw new InvalidOperationException(UnansweredChildCountMessage(pointer));
        if (count > 2000)
            throw new InvalidOperationException("Too many accessibility children; narrow the selector.");
        for (int i = 0; i < count; i++)
        {
            error = IntPtr.Zero;
            var child = atspi_accessible_get_child_at_index(pointer, i, ref error);
            Check(error);
            if (child != IntPtr.Zero)
                yield return child;
        }
    }
    // Throws ArgumentException for an id of an older observation.
    private IntPtr Get(int id)
    {
        return _elements.TryGetValue(id, out var pointer)
            ? pointer
            : throw new ArgumentException("Stale accessibility reference.");
    }
    // Empty when the element has no text interface.
    private static string TextValue(IntPtr pointer)
    {
        var text = atspi_accessible_get_text_iface(pointer);
        if (text == IntPtr.Zero)
            return "";

        try
        {
            IntPtr error = IntPtr.Zero;
            string value = Text(atspi_text_get_text(text, 0, -1, ref error));
            Check(error);
            return value;
        }
        finally { g_object_unref(text); }
    }

    // The raw AT-SPI role name; Role() maps it for the tree, so the password check reads this one.
    private static string RoleName(IntPtr pointer)
    {
        IntPtr error = IntPtr.Zero;
        string role = Text(atspi_accessible_get_role_name(pointer, ref error));
        Check(error);
        return role;
    }

    // A password field keeps an empty value; its text interface is never called, so its text is not read.
    private static string ValueOf(IntPtr pointer, string role)
    {
        if (LinuxPasswordRole.IsPasswordRole(role))
        {
            return "";
        }

        return TextValue(pointer);
    }

    // Screen extents; zero when the element has no component interface or reports none.
    private static AccessibleScreenBounds Extents(IntPtr pointer)
    {
        var bounds = new AccessibleScreenBounds(0, 0, 0, 0);
        var component = atspi_accessible_get_component_iface(pointer);
        if (component == IntPtr.Zero)
            return bounds;

        try
        {
            IntPtr error = IntPtr.Zero;
            var rect = atspi_component_get_extents(component, 0, ref error);
            Check(error);
            if (rect != IntPtr.Zero)
            {
                var b = Marshal.PtrToStructure<Bounds>(rect);
                bounds = new(b.X, b.Y, b.Width, b.Height);
                g_free(rect);
            }
        }
        finally { g_object_unref(component); }
        return bounds;
    }

    // Throws InvalidOperationException when the element has no state or is defunct.
    public VisualTreeNode Current(int id)
    {
        var pointer = Get(id);
        atspi_accessible_clear_cache(pointer);
        var states = atspi_accessible_get_state_set(pointer);
        if (states == IntPtr.Zero)
            throw new InvalidOperationException("State set unavailable.");
        try
        {
            if (atspi_state_set_contains(states, Defunct) != 0)
                throw new InvalidOperationException("Accessibility element is defunct.");
            string role = RoleName(pointer);
            string value = ValueOf(pointer, role);
            IntPtr error = IntPtr.Zero;
            string autoId = Text(atspi_accessible_get_accessible_id(pointer, ref error));
            Check(error);
            object bounds = Extents(pointer);
            return new VisualTreeNode
            {
                Id = id,
                Name = Name(pointer),
                Role = Role(role),
                AutoId = autoId,
                Value = value,
                IsEnabled = atspi_state_set_contains(states, Sensitive) != 0,
                IsOffscreen = atspi_state_set_contains(states, Showing) == 0 || atspi_state_set_contains(states, Visible) == 0,
                Bounds = bounds
            };
        }
        finally { g_object_unref(states); }
    }
    // Throws ArgumentException for an old ID, InvalidOperationException when disabled or refused, NotSupportedException when
    // the target has no unambiguous semantic activation or focus action.
    public void Invoke(int id)
    {
        ReleaseSettledActionConnections();
        VisualTreeNode element = Current(id);
        if (!element.IsEnabled)
            throw new InvalidOperationException("Target disabled.");
        if (element.Role == "Edit")
        {
            FocusElement(id);
            return;
        }
        var action = atspi_accessible_get_action_iface(Get(id));
        if (action == IntPtr.Zero)
            throw new NotSupportedException("Target has no semantic action.");
        try
        {
            IntPtr error = IntPtr.Zero;
            int count = atspi_action_get_n_actions(action, ref error);
            Check(error);
            int selected = SelectPrimaryAction(action, count);
            if (DoAction(Get(id), action, selected) == NativeActionResult.Refused)
                throw new InvalidOperationException("Accessibility provider did not perform the action.");
        }
        finally { g_object_unref(action); }
    }

    // Throws ArgumentException for an old ID, InvalidOperationException when disabled or refused, NotSupportedException without focus.
    public void Focus(int id)
    {
        ReleaseSettledActionConnections();
        VisualTreeNode element = Current(id);
        if (!element.IsEnabled)
        {
            throw new InvalidOperationException("Target disabled.");
        }

        FocusElement(id);
    }

    // Throws ArgumentException for an old ID, NotSupportedException without focus, InvalidOperationException when focus is refused.
    private void FocusElement(int id)
    {
        IntPtr component = atspi_accessible_get_component_iface(Get(id));
        if (component == IntPtr.Zero)
        {
            throw new NotSupportedException("Target has no semantic focus interface.");
        }

        try
        {
            IntPtr error = IntPtr.Zero;
            int accepted = atspi_component_grab_focus(component, ref error);
            Check(error);
            if (accepted == 0)
            {
                throw new InvalidOperationException("Accessibility provider did not focus the target.");
            }
        }
        finally
        {
            g_object_unref(component);
        }
    }

    // Throws NotSupportedException for absent or ambiguous actions and InvalidOperationException for provider failures.
    private static int SelectPrimaryAction(IntPtr action, int count)
    {
        const int MaximumActions = 32;
        if (count == 1)
        {
            return 0;
        }
        if (count is < 1 or > MaximumActions)
        {
            throw new NotSupportedException("Target has no bounded semantic action list.");
        }
        var matches = new List<int>();
        for (int index = 0; index < count; index++)
        {
            IntPtr error = IntPtr.Zero;
            string name = Text(atspi_action_get_action_name(action, index, ref error));
            Check(error);
            if (ParseActionPurpose(name) == AccessibleActionPurpose.Activate)
            {
                matches.Add(index);
            }
        }
        if (matches.Count != 1)
        {
            throw new NotSupportedException("Target has no unambiguous semantic activation or focus action.");
        }

        return matches[0];
    }

    private static AccessibleActionPurpose ParseActionPurpose(string name)
    {
        return name switch
        {
            "click" or "press" or "activate" => AccessibleActionPurpose.Activate,
            _ => AccessibleActionPurpose.Other
        };
    }

    // AT-SPI 2.0 action names are converted at the native boundary before selecting the requested operation.
    private enum AccessibleActionPurpose
    {
        Other,
        Activate
    }
    // Throws InvalidOperationException when the target is disabled, is a password field or is refused by the provider,
    // and NotSupportedException when the target has no editable text. A password field is refused before any write.
    public void SetText(int id, string text)
    {
        if (!Current(id).IsEnabled)
            throw new InvalidOperationException("Target disabled.");
        LinuxPasswordRole.ThrowIfPassword(RoleName(Get(id)));
        var editable = atspi_accessible_get_editable_text_iface(Get(id));
        if (editable == IntPtr.Zero)
            throw new NotSupportedException("Target has no editable text interface.");
        try
        {
            IntPtr error = IntPtr.Zero;
            bool written = atspi_editable_text_set_text_contents(editable, text, ref error) != 0;
            Check(error);
            if (!written)
                throw new InvalidOperationException("Accessibility provider did not accept the text.");
        }
        finally { g_object_unref(editable); }
    }
    public int[] Match(string? name, string? autoId, string? role)
    {
        return _elements.Keys.Where(id =>
        {
            var node = Current(id);
            return (name == null || node.Name == name)
                && (autoId == null || node.AutoId == autoId)
                && (role == null || node.Role.Equals(role, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
    }
    private static string Role(string role) => role switch
    {
        "push button" => "Button",
        "text" => "Edit",
        "entry" => "Edit",
        "frame" => "Window",
        "dialog" => "Window",
        "check box" => "CheckBox",
        "menu item" => "MenuItem",
        _ => role
    };
    private static string Name(IntPtr pointer)
    {
        IntPtr error = IntPtr.Zero;
        var value = Text(atspi_accessible_get_name(pointer, ref error));
        Check(error);
        return value;
    }
    private static int Pid(IntPtr pointer)
    {
        IntPtr error = IntPtr.Zero;
        var value = atspi_accessible_get_process_id(pointer, ref error);
        Check(error);
        return (int)value;
    }
    // Names the failed layer: the provider of the element's application, with its process id when the bus can tell it.
    private static string UnansweredChildCountMessage(IntPtr pointer)
    {
        IntPtr error = IntPtr.Zero;
        uint processId = atspi_accessible_get_process_id(pointer, ref error);
        bool known = error == IntPtr.Zero && processId != uint.MaxValue;
        if (error != IntPtr.Zero)
            g_error_free(error);
        string application = known ? "process " + processId : "an application";

        return $"The accessibility provider of {application} did not answer within the timeout. "
            + "It may be busy in a modal loop; observe again once it responds.";
    }
    private static string Text(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
            return "";
        try
        {
            return Marshal.PtrToStringUTF8(pointer) ?? "";
        }
        finally { g_free(pointer); }
    }
    // Throws InvalidOperationException when the provider reported an error.
    private static void Check(IntPtr error)
    {
        if (error == IntPtr.Zero)
            return;
        g_error_free(error);
        throw new InvalidOperationException("Accessibility provider rejected the request.");
    }
    // Element extents as AT-SPI returns them.
    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds
    {
        public int X, Y, Width, Height;
    }
    [DllImport(Lib)] private static extern int atspi_init();
    [DllImport(Lib)] private static extern void atspi_set_timeout(int timeout, int startupTimeout);
    [DllImport(Lib)] private static extern IntPtr atspi_get_desktop(int index);
    [DllImport(Lib)] private static extern void atspi_accessible_clear_cache(IntPtr obj);
    [DllImport(Lib)] private static extern int atspi_accessible_get_child_count(IntPtr obj, ref IntPtr error);
    [DllImport(Lib)] private static extern IntPtr atspi_accessible_get_child_at_index(IntPtr obj, int index, ref IntPtr error);
    [DllImport(Lib)] private static extern IntPtr atspi_accessible_get_parent(IntPtr obj, ref IntPtr error);
    [DllImport(Lib)] private static extern uint atspi_accessible_get_process_id(IntPtr obj, ref IntPtr error);
    [DllImport(Lib)] private static extern IntPtr atspi_accessible_get_name(IntPtr obj, ref IntPtr error);
    [DllImport(Lib)] private static extern IntPtr atspi_accessible_get_accessible_id(IntPtr obj, ref IntPtr error);
    [DllImport(Lib)] private static extern IntPtr atspi_accessible_get_role_name(IntPtr obj, ref IntPtr error);
    [DllImport(Lib)] private static extern IntPtr atspi_accessible_get_state_set(IntPtr obj);
    [DllImport(Lib)] private static extern int atspi_state_set_contains(IntPtr obj, int state);
    [DllImport(Lib)] private static extern IntPtr atspi_accessible_get_text_iface(IntPtr obj);
    [DllImport(Lib)] private static extern IntPtr atspi_text_get_text(IntPtr obj, int start, int end, ref IntPtr error);
    [DllImport(Lib)] private static extern IntPtr atspi_accessible_get_action_iface(IntPtr obj);
    [DllImport(Lib)] private static extern int atspi_action_get_n_actions(IntPtr obj, ref IntPtr error);
    [DllImport(Lib)] private static extern IntPtr atspi_action_get_action_name(IntPtr obj, int index, ref IntPtr error);
    [DllImport(Lib)] private static extern int atspi_component_grab_focus(IntPtr obj, ref IntPtr error);
    [DllImport(Lib)] private static extern int atspi_action_do_action(IntPtr obj, int index, ref IntPtr error);
    [DllImport(Lib)] private static extern IntPtr atspi_accessible_get_editable_text_iface(IntPtr obj);
    [DllImport(Lib)]
    private static extern int atspi_editable_text_set_text_contents(
        IntPtr obj,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        ref IntPtr error);
    [DllImport(Lib)] private static extern IntPtr atspi_accessible_get_component_iface(IntPtr obj);
    [DllImport(Lib)] private static extern IntPtr atspi_component_get_extents(IntPtr obj, int coordinates, ref IntPtr error);
    [DllImport("libgobject-2.0.so.0")] private static extern void g_object_unref(IntPtr obj);
    [DllImport("libgobject-2.0.so.0")] private static extern IntPtr g_object_ref(IntPtr obj);
    [DllImport("libglib-2.0.so.0")] private static extern void g_free(IntPtr data);
    [DllImport("libglib-2.0.so.0")] private static extern void g_error_free(IntPtr error);
}
#endif
