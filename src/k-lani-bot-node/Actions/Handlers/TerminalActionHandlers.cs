using KLaniBot.Platforms.Mobile;

namespace KLaniBot.Actions.Handlers;

// Use case: an AI runs a command line. This is the broadest capability the node has, so it needs its own
// scope; on an Android backend the command goes to the device's shell and nowhere else.
internal sealed class TerminalActionHandlers
{
    private readonly HandlerServices _services;

    public TerminalActionHandlers(HandlerServices services)
    {
        _services = services;
    }

    public void Register(Action<Tool, ActionHandler> add)
    {
        add(Tool.RunTerminalCommand, RunCommand);
    }

    // Throws NotSupportedException when an Android shell is asked for outside the Android backend.
    private object RunCommand(ActionCall call)
    {
        string? command = call.Arguments["command"]?.ToString();
        string shell = call.Arguments["shell"]?.ToString() ?? "auto";
        int timeout = call.Arguments["timeout_ms"]?.GetValue<int?>() ?? 15000;
        if (_services.Ui is AdbUiService adb)
        {
            if (call.Arguments["shell"] != null && shell != "android")
            {
                throw new NotSupportedException("ADB commands require the Android shell target.");
            }
            return adb.RunTerminal(command ?? "", timeout);
        }
        return _services.Terminal.RunCommand(command, shell, timeout);
    }
}
