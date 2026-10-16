using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KLaniBot.Guard;
using KLaniBot.Platforms.Mobile;

namespace KLaniBot.Actions.Handlers;

// Use case: an AI starts a program. The operator decides which programs are allowed; the AI only chooses
// among them and supplies arguments.
internal sealed class ProcessActionHandlers
{
    private readonly HandlerServices _services;

    public ProcessActionHandlers(HandlerServices services)
    {
        _services = services;
    }

    public void Register(Action<Tool, ActionHandler> add)
    {
        add(Tool.LaunchProcess, LaunchProcess);
    }

    private object LaunchProcess(ActionCall call)
    {
        string requested = call.Arguments["path"]?.ToString() ?? "";
        string path = ResolveAllowedProgram(call.Caller, requested);
        // The arguments stay a list, so no layer can re-split, re-quote or hand them to a shell.
        IReadOnlyList<string> launchArguments = ReadLaunchArguments(call.Arguments);
        _services.Policy.AuthorizeArguments(call.Caller, path, launchArguments);
        // Everything above refuses without effect; from here on the program may have started.
        call.MarkBackendMayHaveMutated();
        var (ok, message) = _services.Ui.LaunchApp(path, launchArguments);
        return new
        {
            success = ok,
            message
        };
    }

    // On Android the allow-list holds package names, elsewhere paths of programs.
    // Throws UnauthorizedAccessException when the program or package is not in the principal's allow-list.
    private string ResolveAllowedProgram(RequestContext caller, string requested)
    {
        if (_services.Ui is not AdbUiService)
        {
            return _services.Policy.ResolveApplication(caller, requested);
        }
        bool listed = _services.Policy.Get(caller.PrincipalId).Applications.Contains(requested);
        if (!listed || !Regex.IsMatch(requested, "^[a-zA-Z][a-zA-Z0-9_.]+$"))
        {
            throw new UnauthorizedAccessException("Package is not allowlisted.");
        }
        return requested;
    }

    private static string[] ReadLaunchArguments(JsonObject args)
    {
        var values = args["arguments"] as JsonArray ?? [];
        var arguments = new List<string>();
        foreach (var value in values)
        {
            arguments.Add(value!.GetValue<string>());
        }
        return arguments.ToArray();
    }
}
