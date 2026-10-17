using System.Text.Json.Nodes;
using KLaniBot.Guard;
using KLaniBot.Platforms;

namespace KLaniBot.Actions.Handlers;

// Use case: an AI arranges windows: lists them, brings one forward, moves, resizes or closes it. Under
// the guard, a window list only contains windows of programs the operator allowed.
internal sealed class WindowActionHandlers
{
    private readonly HandlerServices _services;

    public WindowActionHandlers(HandlerServices services)
    {
        _services = services;
    }

    public void Register(Action<Tool, ActionHandler> add)
    {
        add(Tool.CloseWindow, CloseWindow);
        add(Tool.ListWindows, ListWindows);
        add(Tool.FocusWindow, FocusWindow);
        add(Tool.GetWindowGeometry, GetWindowGeometry);
        add(Tool.SetWindowGeometry, SetWindowGeometry);
    }

    private object CloseWindow(ActionCall call)
    {
        var (ok, message) = _services.Ui.CloseWindow(call.Arguments["pid"]?.GetValue<int?>(), call.Arguments["title"]?.ToString());
        return new
        {
            success = ok,
            message
        };
    }

    private object ListWindows(ActionCall call)
    {
        var windows = _services.Ui.ListWindows();
        var guard = _services.Policy.Get(call.Caller.PrincipalId).UiGuard;
        if (guard.Mode == UiGuardMode.Guarded)
        {
            windows = windows.Where(window => UiActionGuard.AllowsApplication(guard, window.ProcessId)).ToList();
        }
        return new
        {
            success = true,
            count = windows.Count,
            windows
        };
    }

    // Reads the window arguments that observation and focus share, so one call names one window the same way.
    internal static WindowSelectionRequest ReadWindowRequest(JsonObject arguments)
    {
        int? pid = arguments["pid"]?.GetValue<int?>();
        string? title = arguments["title"]?.ToString();
        long? handle = arguments["window"]?.GetValue<long?>();

        return new WindowSelectionRequest(pid, title, handle);
    }

    // The German messages are what clients of earlier versions already receive; they stay as they are.
    private object FocusWindow(ActionCall call)
    {
        bool ok = _services.Ui.FocusWindow(ReadWindowRequest(call.Arguments));
        return new
        {
            success = ok,
            message = ok ? "Window focused" : "The target window could not be brought to the foreground"
        };
    }

    private object GetWindowGeometry(ActionCall call)
    {
        if (call.Arguments["window"] != null)
        {
            return ReadWindowBounds(call);
        }
        int? pid = call.Arguments["pid"]?.GetValue<int?>();
        string? title = call.Arguments["title"]?.ToString();
        var geometry = _services.Ui.GetWindowGeometry(pid, title);
        if (geometry == null)
        {
            return new
            {
                success = false,
                message = "Target window not found"
            };
        }
        return new
        {
            success = true,
            geometry
        };
    }

    // A window named by its handle answers with one rectangle shape on every platform, or with unsupported where the backend cannot
    // prove that rectangle and screenshot share one scale. Throws ArgumentException for an unknown or ambiguous handle.
    private object ReadWindowBounds(ActionCall call)
    {
        WindowSelectionRequest request = ReadWindowRequest(call.Arguments);

        WindowScreenBounds? bounds = _services.Ui.ReadWindowBounds(request);

        if (bounds == null)
        {
            return new
            {
                success = false,
                code = ActionErrorCode.Unsupported.ToWireName(),
                error = "This platform cannot report the window's rectangle in screenshot pixels."
            };
        }
        return new
        {
            success = true,
            bounds
        };
    }

    private object SetWindowGeometry(ActionCall call)
    {
        JsonObject arguments = call.Arguments;
        var (ok, message, geometry) = _services.Ui.SetWindowGeometry(
            arguments["pid"]?.GetValue<int?>(),
            arguments["title"]?.ToString(),
            arguments["state"]?.ToString(),
            arguments["width"]?.GetValue<int?>(),
            arguments["height"]?.GetValue<int?>(),
            arguments["x"]?.GetValue<int?>(),
            arguments["y"]?.GetValue<int?>());
        return new
        {
            success = ok,
            message,
            geometry
        };
    }
}
