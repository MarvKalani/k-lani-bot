namespace KLaniBot.Actions.Handlers;

// Use case: an AI moves text through the system clipboard, for example into a field that cannot be typed
// into. The clipboard is shared with the person at the machine, so it has a scope of its own.
internal sealed class ClipboardActionHandlers
{
    private readonly HandlerServices _services;

    public ClipboardActionHandlers(HandlerServices services)
    {
        _services = services;
    }

    public void Register(Action<Tool, ActionHandler> add)
    {
        add(Tool.GetClipboard, GetClipboard);
        add(Tool.SetClipboard, SetClipboard);
        add(Tool.PasteClipboard, PasteClipboard);
    }

    private object GetClipboard(ActionCall call)
    {
        var (ok, message, text) = _services.Ui.GetClipboard();
        return new
        {
            success = ok,
            message,
            text = text ?? ""
        };
    }

    private object SetClipboard(ActionCall call)
    {
        string text = call.Arguments["text"]?.ToString() ?? "";
        var (ok, message) = _services.Ui.SetClipboard(text);
        return new
        {
            success = ok,
            message
        };
    }

    private object PasteClipboard(ActionCall call)
    {
        int? targetId = call.Arguments["id"]?.GetValue<int?>();
        var (ok, message) = _services.Ui.PasteClipboard(targetId);
        return new
        {
            success = ok,
            message
        };
    }
}
