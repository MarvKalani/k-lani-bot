using System.Text.Json.Nodes;
using KLaniBot.Actions.ApplicationMcp;
using KLaniBot.Guard;

namespace KLaniBot.Actions.Handlers;

// Use case: an agent uses a program's own MCP server through the node instead of its screen, for example FilmCraft's commands
// to cut and export video. app_mcp_read lists the program's tools, loads one schema or runs a tool the operator marked as
// read-only; app_mcp_call runs any tool and counts as a change, so it passes the backup requirement. The program's own
// read-only hints are not trusted.
internal sealed class ApplicationMcpActionHandlers(HandlerServices services)
{
    public void Register(Action<Tool, ActionHandler> add)
    {
        add(Tool.ApplicationMcpRead, Read);
        add(Tool.ApplicationMcpCall, Call);
    }

    // Throws ArgumentException for an unknown program, an unknown tool or a tool the operator did not mark as read-only.
    // A file question is answered without starting the program.
    private object Read(ActionCall call)
    {
        string programId = call.Arguments["program"]!.ToString();
        string? schema = call.Arguments["schema"]?.ToString();
        string? tool = call.Arguments["tool"]?.ToString();
        string? file = call.Arguments["file"]?.ToString();

        if (file != null)
            return FileFacts(programId, file);
        return Run(call, programId, program =>
        {
            if (schema != null)
                return ApplicationMcpAnswers.Schema(program, schema);
            if (tool == null)
                return ApplicationMcpAnswers.Listing(program);
            if (!program.Settings.IsReadTool(tool))
                throw new ArgumentException("'" + tool + "' is not a read-only tool on this node; use app_mcp_call.");
            return Invoke(program, tool, call, changes: false);
        });
    }

    // Throws ArgumentException for a program the policy does not list; UnauthorizedAccessException for a path outside the workspace.
    private JsonObject FileFacts(string programId, string file)
    {
        if (services.Policy.FindApplicationMcp(programId) == null)
            throw new ArgumentException("This node offers no program '" + programId + "'.");
        return ApplicationMcpFiles.Facts(services.Policy, programId, file);
    }

    // Throws ArgumentException for an unknown program or a tool the program does not list.
    private object Call(ActionCall call)
    {
        string programId = call.Arguments["program"]!.ToString();
        string tool = call.Arguments["tool"]!.ToString();

        return Run(call, programId, program => Invoke(program, tool, call, changes: true));
    }

    // Throws ArgumentException for an unknown program. A failing program answers with its code; one that stopped or missed
    // its deadline is forgotten, so the next call starts it fresh instead of reusing half a state.
    private JsonObject Run(ActionCall call, string programId, Func<RunningProgram, JsonObject> use)
    {
        try
        {
            return use(services.ApplicationMcps.Open(call.Caller, programId));
        }
        catch (ApplicationMcpException failure)
        {
            if (failure.Failure is not ApplicationMcpFailure.ToolError)
                services.ApplicationMcps.Forget(call.Caller, programId);
            return ApplicationMcpAnswers.Failed(failure);
        }
    }

    // Throws ArgumentException when the program lists no such tool; ApplicationMcpException when the call fails. A change is
    // marked only once the tool is known, so a refused name stays "not dispatched".
    private static JsonObject Invoke(RunningProgram program, string tool, ActionCall call, bool changes)
    {
        if (program.FindTool(tool) == null)
            throw new ArgumentException("The program lists no tool '" + tool + "'.");
        if (changes)
            call.MarkBackendMayHaveMutated();
        var parameters = new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = call.Arguments["arguments"]?.DeepClone() ?? new JsonObject()
        };
        TimeSpan timeout = TimeSpan.FromSeconds(program.Settings.CallTimeoutSeconds);

        JsonObject result = program.Connection.Request("tools/call", parameters, timeout);
        return ApplicationMcpAnswers.ToolResult(program, tool, result, Window(call.Arguments));
    }

    private static AnswerWindow Window(JsonObject arguments)
    {
        int offset = arguments["offset"]?.GetValue<int>() ?? 0;
        int characters = arguments["max_chars"]?.GetValue<int>() ?? ApplicationMcpAnswers.DefaultCharacters;
        bool images = arguments["images"]?.GetValue<bool>() ?? false;
        return new AnswerWindow(offset, characters, images);
    }
}
