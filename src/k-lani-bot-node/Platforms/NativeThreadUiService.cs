using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Visibility;

namespace KLaniBot.Platforms;

// On Linux the visibility indicator (GTK) and the backend (AT-SPI) share GLib state, and neither is safe to call
// from another thread. When both run in one process, every backend call is marshalled to the indicator's native
// thread by this wrapper; the backend itself stays unaware of threads.
public sealed class NativeThreadUiService(IUiService inner, NativeVisibilityHost host) : IUiService, IDisposable
{
    public UiFormCapabilities FormCapabilities => host.NativeCall(() => inner.FormCapabilities);
    public UiFieldMetadata ReadFieldMetadata(int id) => host.NativeCall(() => inner.ReadFieldMetadata(id));
    public UiOutcome SelectChoice(UiChoiceCommand command) => host.NativeCall(() => inner.SelectChoice(command));
    public UiOutcome OpenChoices(int id) => host.NativeCall(() => inner.OpenChoices(id));
    public ApplicationObjectResult InspectApplication(ApplicationObjectQuery query)
    {
        return host.NativeCall(() => inner.InspectApplication(query));
    }
    public ApplicationObjectResult OperateApplication(ApplicationObjectCommand command)
    {
        return host.NativeCall(() => inner.OperateApplication(command));
    }
    public string Platform => inner.Platform;
    public DesktopSessionInfo? GetDesktopSession() => host.NativeCall(inner.GetDesktopSession);
    public IReadOnlySet<ToolFeature> Features => host.NativeCall(() => inner.Features);
    public IReadOnlySet<Tool>? SupportedTools => inner.SupportedTools;
    public void PrepareElementInteraction(int id, WindowInteractionMode mode, ModalDialogMode dialogs, bool decisionRecorded)
    {
        host.NativeCall(() =>
        {
            inner.PrepareElementInteraction(id, mode, dialogs, decisionRecorded);
            return true;
        });
    }
    public UiOutcome InvokeBackgroundElement(int id)
    {
        return host.NativeCall(() => inner.InvokeBackgroundElement(id));
    }
    public UiTargetIdentity? GetTargetIdentity(int id) => host.NativeCall(() => inner.GetTargetIdentity(id));
    public string? ReadWindowText(int id) => host.NativeCall(() => inner.ReadWindowText(id));
    public string? ShowingModalWindowTitle(int processId) => host.NativeCall(() => inner.ShowingModalWindowTitle(processId));
    public ModalDialogDetection DetectModalDialog(int id) => host.NativeCall(() => inner.DetectModalDialog(id));
    public UiOutcome SetGuardedText(int id, string text) => host.NativeCall(() => inner.SetGuardedText(id, text));
    public T GuardedInput<T>(Action check, Func<IUiService, T> operation) => host.NativeCall(() =>
    {
        check();
        return operation(inner);
    });
    public UiOutcome CloseWindow(int? pid, string? title) => host.NativeCall(() => inner.CloseWindow(pid, title));
    public UiOutcome MouseMove(int x, int y) => host.NativeCall(() => inner.MouseMove(x, y));
    public UiOutcome DragAndDrop(int fromX, int fromY, int toX, int toY)
        => host.NativeCall(() => inner.DragAndDrop(fromX, fromY, toX, toY));
    public UiOutcome KeyChord(string[] keys) => host.NativeCall(() => inner.KeyChord(keys));
    public ScreenshotOutcome Screenshot() => host.NativeCall(() => inner.Screenshot());
    public UiOutcome TypeText(int id, string text, bool clear, bool enter, int autoWaitMs)
        => host.NativeCall(() => inner.TypeText(id, text, clear, enter, autoWaitMs));
    public List<WindowInfo> ListWindows() => host.NativeCall(() => inner.ListWindows());
    public bool FocusWindow(WindowSelectionRequest request) => host.NativeCall(() => inner.FocusWindow(request));
    public object? GetWindowGeometry(int? pid, string? titleQuery) => host.NativeCall(() => inner.GetWindowGeometry(pid, titleQuery));
    public WindowScreenBounds? ReadWindowBounds(WindowSelectionRequest request) => host.NativeCall(() => inner.ReadWindowBounds(request));
    public GeometryOutcome SetWindowGeometry(
        int? pid,
        string? titleQuery,
        string? state,
        int? width,
        int? height,
        int? x,
        int? y)
        => host.NativeCall(() => inner.SetWindowGeometry(pid, titleQuery, state, width, height, x, y));
    public string GetChapters(int? pid, string? titleQuery) => host.NativeCall(() => inner.GetChapters(pid, titleQuery));
    public string InspectSection(string chapterId, int maxDepth = 3) => host.NativeCall(() => inner.InspectSection(chapterId, maxDepth));
    public string GetUiTree(WindowSelectionRequest request, int maxDepth = 4)
        => host.NativeCall(() => inner.GetUiTree(request, maxDepth));
    public string GetCommonUiTree(WindowSelectionRequest request, int maxDepth = 4)
        => host.NativeCall(() => inner.GetCommonUiTree(request, maxDepth));
    public UiOutcome ClickElement(int id, int autoWaitMs = 3000)
        => host.NativeCall(() => inner.ClickElement(id, autoWaitMs));
    public UiOutcome FocusElement(int id)
        => host.NativeCall(() => inner.FocusElement(id));
    public UiOutcome SilentClick(int id) => host.NativeCall(() => inner.SilentClick(id));
    public ClickOutcome MouseClick(int id, string button = "left", int? x = null, int? y = null)
        => host.NativeCall(() => inner.MouseClick(id, button, x, y));
    public UiOutcome SetText(int id, string text, int autoWaitMs = 3000)
        => host.NativeCall(() => inner.SetText(id, text, autoWaitMs));
    public UiNativeTextSupport ProbeNativeTextSupport(int id)
    {
        return host.NativeCall(() => inner.ProbeNativeTextSupport(id));
    }
    public UiOutcome LaunchApp(string path, IReadOnlyList<string> arguments)
    {
        return host.NativeCall(() => inner.LaunchApp(path, arguments));
    }
    public object? GetVisualTree(int? pid, string? titleQuery, int maxDepth = 4)
        => host.NativeCall(() => inner.GetVisualTree(pid, titleQuery, maxDepth));
    public ElementCheckOutcome WaitForElement(
        string? name,
        string? autoId = null,
        string? role = null,
        int? pid = null,
        string? title = null,
        string state = "exists",
        int timeoutMs = 5000)
        => Poll(() => host.NativeCall(() => inner.WaitForElement(name, autoId, role, pid, title, state, 0)), timeoutMs);
    public ClipboardOutcome GetClipboard() => host.NativeCall(() => inner.GetClipboard());
    public UiOutcome SetClipboard(string text) => host.NativeCall(() => inner.SetClipboard(text));
    public UiOutcome PasteClipboard(int? targetId = null) => host.NativeCall(() => inner.PasteClipboard(targetId));
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
        => Poll(
            () => host.NativeCall(() => inner.VerifyElement(id, name, autoId, role, pid, windowTitle, expectedState, expectedText, 0)),
            timeoutMs);
    private static ElementCheckOutcome Poll(
        Func<ElementCheckOutcome> once,
        int timeoutMs)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            var result = once();
            if (result.Success
                || result.Message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)
                || watch.ElapsedMilliseconds >= timeoutMs)
                return result;
            Thread.Sleep(50);
        } while (true);
    }
    public void Dispose()
    {
        host.NativeCall(() =>
        {
            (inner as IDisposable)?.Dispose();
            return true;
        });
    }
}
