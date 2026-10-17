using System.Text.Json.Serialization;
using KLaniBot.Actions;
using KLaniBot.Guard;

namespace KLaniBot.Platforms;

// One top-level window as a backend lists it, with the identity of its process for the guard.
public record WindowInfo(
    int ProcessId,
    string ProcessName,
    string AppVersion,
    string Title,
    [property: JsonIgnore] IntPtr Handle
)
{
    public long WindowHandle => Handle.ToInt64();
    public string? ExecutablePath
    {
        get; init;
    }
    public DateTimeOffset? ProcessStartedAtUtc
    {
        get; init;
    }
    public int? SessionId
    {
        get; init;
    }
    public bool? IsCloaked
    {
        get; init;
    }
    public bool IsForeground
    {
        get; init;
    }
    // A dialog blocks its main window, so a request that names only the process can select it; every backend that cannot tell
    // reports a main window.
    public WindowKind Kind
    {
        get; init;
    } = WindowKind.Main;
}

// One element of an observed window as every backend reports it to the dispatcher and the agent: its id for later actions, role, name,
// value, state and children.
public class VisualTreeNode
{
    public int Id
    {
        get; set;
    }
    public string Role { get; set; } = "";
    public string Name { get; set; } = "";
    public string AutoId { get; set; } = "";
    public string? Shortcut
    {
        get; set;
    }
    public bool IsEnabled
    {
        get; set;
    }
    public string? Value
    {
        get; set;
    }
    public bool IsOffscreen
    {
        get; set;
    }
    public object Bounds { get; set; } = new();
    public List<VisualTreeNode> Children { get; set; } = new();
}

// The Windows session and desktops the actuator runs in; input is refused when the input desktop is not the worker's.
public sealed record DesktopSessionInfo(
    int SessionId,
    uint? ConsoleSessionId,
    string ConnectionState,
    string? WorkerDesktop,
    string? InputDesktop,
    bool InputAvailable)
{
    // Throws InvalidOperationException when the session has no active input desktop.
    public void RequireInputAvailable()
    {
        if (!InputAvailable)
        {
            throw new InvalidOperationException("The node has no active input desktop. Windows session "
                + SessionId + ": " + ConnectionState + ". That RDP or user session must be connected and unlocked.");
        }
    }
}

// What a backend operation reports: whether it took effect, the sentence the AI reads about it, and for some
// operations what it found.
public sealed record UiOutcome(bool Success, string Message);

// Whether a screenshot was taken, why not, and the PNG.
public sealed record ScreenshotOutcome(bool Success, string Message, byte[]? Png);

// Whether the clipboard could be read, why not, and its text.
public sealed record ClipboardOutcome(bool Success, string Message, string? Text);

// Whether a click was performed, why not, and where it went.
public sealed record ClickOutcome(bool Success, string Message, object? ClickInfo);

// Whether a window was moved or resized, why not, and its geometry afterwards.
public sealed record GeometryOutcome(bool Success, string Message, object? Geometry);

// Waiting for an element is checking it for the state "exists", so both report the element the same way.
public sealed record ElementCheckOutcome(bool Success, string Message, object? Details);

// Use case: before the dispatcher routes a recorded decision, it asks whether a modal dialog stands in the way of the target.
// Absent means the modal rule of the input path lets the element through whatever decision it carries, so the decision is not
// routed. A backend that cannot rule out a dialog reports Unknown, which keeps the routing of the decision as it was.
public enum ModalDialogDetection
{
    Unknown,
    Absent,
    Present
}

// The boundary between the platform-neutral dispatcher and one desktop or device backend. Features says which
// tool features work right now (it can change, for example when macOS permissions are granted), and
// SupportedTools, when set, narrows the contract further; the dispatcher refuses everything outside both and
// get_machine_capabilities reports only what remains.
//
// The members with default bodies are the ones the guard, background mode and modal handling rely on. Their
// defaults refuse or return null, so a backend that does not implement them can never be used for guarded input,
// background input or a target whose identity is unknown; it fails closed instead of falling back to the
// shared keyboard or mouse.
public interface IUiService
{
    UiFormCapabilities FormCapabilities => UiFormCapabilities.Unsupported;
    UiFieldMetadata ReadFieldMetadata(int id) => UiFieldMetadata.Unsupported;
    UiOutcome SelectChoice(UiChoiceCommand command) => new(false, "Native choice selection is unsupported on this backend.");
    UiOutcome OpenChoices(int id) => new(false, "Native choice opening is unsupported on this backend.");
    ApplicationObjectResult InspectApplication(ApplicationObjectQuery query)
    {
        return new(false, "This backend has no native application object model.");
    }
    ApplicationObjectResult OperateApplication(ApplicationObjectCommand command)
    {
        return new(false, "This backend has no native application object model.");
    }
    DesktopSessionInfo? GetDesktopSession() => null;
    // Throws NotSupportedException unless a backend proves it can prepare input safely.
    void PrepareElementInteraction(int id, WindowInteractionMode mode, ModalDialogMode dialogs, bool decisionRecorded)
    {
        throw new NotSupportedException("This backend has no verified window interaction preparation.");
    }
    UiOutcome InvokeBackgroundElement(int id)
    {
        return new(false, "This backend has no verified background invocation.");
    }
    UiTargetIdentity? GetTargetIdentity(int id) => null;
    // The element the activity panel names for a step; a backend whose guard identity is withheld can still name it here.
    UiTargetIdentity? GetDisplayedTarget(int id) => GetTargetIdentity(id);
    // The names of the texts and controls in the window of an observed element, read through that element without a new
    // observation, so element numbers stay valid. Field contents are not part of it. Null where unsupported or unreadable.
    string? ReadWindowText(int id) => null;
    // The title of a modal window of this process that shows right now, read without a new observation so element numbers
    // stay valid. Null when none shows or the backend cannot tell; the wait for a postcondition then simply runs out.
    string? ShowingModalWindowTitle(int processId) => null;
    // Whether a modal dialog stands in the way of the element, judged as PrepareElementInteraction judges it. The default says
    // Unknown, so a backend that does not detect dialogs keeps the routing of a decision as it was.
    ModalDialogDetection DetectModalDialog(int id) => ModalDialogDetection.Unknown;
    UiOutcome SetGuardedText(int id, string text)
        => new(false, "This backend has no established semantic text setter for guarded input.");
    T GuardedInput<T>(Action check, Func<IUiService, T> operation)
    {
        check();
        return operation(this);
    }
    string Platform
    {
        get;
    }
    IReadOnlySet<Tool>? SupportedTools => null;
    IReadOnlySet<ToolFeature> Features
    {
        get;
    }
    UiOutcome CloseWindow(int? pid, string? title);
    UiOutcome MouseMove(int x, int y);
    UiOutcome DragAndDrop(int fromX, int fromY, int toX, int toY);
    UiOutcome KeyChord(string[] keys);
    ScreenshotOutcome Screenshot();
    UiOutcome TypeText(int id, string text, bool clear, bool enter, int autoWaitMs);

    List<WindowInfo> ListWindows();
    bool FocusWindow(WindowSelectionRequest request);
    object? GetWindowGeometry(int? pid, string? titleQuery);
    // Null where the backend cannot prove that its window rectangle and its screenshot pixels share one scale.
    // Throws ArgumentException for an unknown or ambiguous window.
    WindowScreenBounds? ReadWindowBounds(WindowSelectionRequest request) => null;
    GeometryOutcome SetWindowGeometry(
        int? pid,
        string? titleQuery,
        string? state,
        int? width,
        int? height,
        int? x,
        int? y);
    string GetChapters(int? pid, string? titleQuery);
    string InspectSection(string chapterId, int maxDepth = 3);
    string GetUiTree(WindowSelectionRequest request, int maxDepth = 4);
    // Linux, macOS and iOS already write the common rows from GetUiTree, so they keep this default. Windows overrides it.
    string GetCommonUiTree(WindowSelectionRequest request, int maxDepth = 4) => GetUiTree(request, maxDepth);
    UiOutcome ClickElement(int id, int autoWaitMs = 3000);
    // Moves keyboard focus without activating the element, so that real key events can follow.
    UiOutcome FocusElement(int id) => new(false, "Element focus is not supported on this platform.");
    UiOutcome SilentClick(int id);
    ClickOutcome MouseClick(int id, string button = "left", int? x = null, int? y = null);
    UiOutcome SetText(int id, string text, int autoWaitMs = 3000);
    UiNativeTextSupport ProbeNativeTextSupport(int id)
    {
        return UiNativeTextSupport.Unknown;
    }
    UiOutcome LaunchApp(string path, IReadOnlyList<string> arguments);
    object? GetVisualTree(int? pid, string? titleQuery, int maxDepth = 4);
    ElementCheckOutcome WaitForElement(
        string? name,
        string? autoId = null,
        string? role = null,
        int? pid = null,
        string? title = null,
        string state = "exists",
        int timeoutMs = 5000);
    ClipboardOutcome GetClipboard();
    UiOutcome SetClipboard(string text);
    UiOutcome PasteClipboard(int? targetId = null);
    ElementCheckOutcome VerifyElement(
        int? id,
        string? name,
        string? autoId,
        string? role,
        int? pid,
        string? windowTitle,
        string? expectedState,
        string? expectedText,
        int timeoutMs = 5000);
}
