using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Visibility;

namespace KLaniBot.Transport;

// Use case: the supervisor (--serve or --connect) starts the worker with --stdio-worker inside the
// desktop session and talks to it over stdin and stdout, one JSON object per line. Besides tool calls
// the supervisor sends three control frames that keep the visible session state in step: the client's
// presentation, the inventory permission and heartbeat/close. Each frame gets exactly one answer line,
// in the order the frames arrived.
public sealed class StdioWorkerLoop
{
    public const int VisibilityProtocolVersion = 1;

    private readonly NodePolicy _policy;
    private readonly string _policyPath;
    private readonly AccessVisibility _visibility;
    private readonly Func<JsonObject, RequestContext, Task<JsonObject>> _executeAction;

    public StdioWorkerLoop(
        NodePolicy policy,
        string policyPath,
        AccessVisibility visibility,
        Func<JsonObject, RequestContext, Task<JsonObject>> executeAction)
    {
        _policy = policy;
        _policyPath = policyPath;
        _visibility = visibility;
        _executeAction = executeAction;
    }

    // A frame longer than the reader's limit ends the loop with InvalidDataException: the rest of that
    // line is still in the pipe, so no later frame boundary could be trusted.
    public async Task RunAsync(Stream input, TextWriter output, CancellationToken cancellation)
    {
        var ready = new JsonObject { ["ready"] = true, ["visibility_protocol"] = VisibilityProtocolVersion };
        await WriteFrameAsync(output, ready);
        var reader = new JsonLineReader(input);
        while (await reader.ReadAsync(cancellation) is { } line)
        {
            JsonObject answer = await AnswerAsync(line);
            await WriteFrameAsync(output, answer);
        }
    }

    private static async Task WriteFrameAsync(TextWriter output, JsonObject frame)
    {
        await output.WriteLineAsync(frame.ToJsonString());
        await output.FlushAsync();
    }

    // The dispatcher answers every failure after a mutation was admitted itself. What arrives here
    // failed before that point (an unreadable frame, a refused control, a request the dispatcher could
    // not even read), so "not dispatched" is the truthful answer.
    private async Task<JsonObject> AnswerAsync(string line)
    {
        try
        {
            JsonObject frame = JsonNode.Parse(line)!.AsObject();
            var context = new RequestContext(frame["principal_id"]!.ToString(), frame["session_id"]!.ToString());
            if (frame["control"] is { } control)
            {
                return ApplyControl(frame, control.ToString(), context);
            }
            return await _executeAction(frame, context);
        }
        catch (Exception exception)
        {
            return ActionResults.Failure(ActionOutcome.NotDispatched, exception.Message);
        }
    }

    // A control frame carries principal_id, session_id, control and at most one value. A field more or
    // less is refused, so a control cannot carry an action along with it.
    // Throws ArgumentException for a malformed control frame.
    private JsonObject ApplyControl(JsonObject frame, string control, RequestContext context)
    {
        switch (control)
        {
            case "session_details":
                ApplySessionDetails(frame, context);
                break;
            case "inventory_permission":
                ApplyInventoryPermission(frame, context);
                break;
            case "backup_requirement":
                ApplyBackupRequirement(frame, context);
                break;
            case "heartbeat":
            case "close":
                ApplyConnectionState(frame, control, context);
                break;
            default:
                throw new ArgumentException("Invalid visibility control frame.");
        }
        return new JsonObject { ["success"] = true };
    }

    // Throws ArgumentException for a malformed presentation frame.
    private void ApplySessionDetails(JsonObject frame, RequestContext context)
    {
        if (frame.Count != 4 || frame["details"] is not JsonObject details)
        {
            throw new ArgumentException("Invalid presentation frame.");
        }
        _policy.Authorize(context, Scope.Observe);
        _visibility.SetSessionPresentation(context, SessionPresentation.FromJson(details));
    }

    // Throws ArgumentException for a malformed inventory permission frame.
    private void ApplyInventoryPermission(JsonObject frame, RequestContext context)
    {
        if (frame.Count != 4 || frame["enabled"] == null)
        {
            throw new ArgumentException("Invalid inventory permission frame.");
        }
        _policy.Authorize(context, Scope.Observe);
        bool enabled = frame["enabled"]!.GetValue<bool>();
        InventoryPermission.Set(_policy, _policyPath, context.PrincipalId, enabled);
    }

    // Throws ArgumentException for a malformed backup requirement frame.
    private void ApplyBackupRequirement(JsonObject frame, RequestContext context)
    {
        string? word = frame["requirement"]?.ToString();
        if (frame.Count != 4 || word is not ("required" or "off"))
        {
            throw new ArgumentException("Invalid backup requirement frame.");
        }
        _policy.Authorize(context, Scope.Observe);

        BackupRequirement requirement = word == "off" ? BackupRequirement.Off : BackupRequirement.Required;
        BackupRequirementSetting.Set(_policy, _policyPath, requirement);
    }

    // Throws ArgumentException for a malformed control frame.
    private void ApplyConnectionState(JsonObject frame, string control, RequestContext context)
    {
        if (frame.Count != 3)
        {
            throw new ArgumentException("Invalid visibility control frame.");
        }
        _policy.Authorize(context, Scope.Observe);
        bool connected = control == "heartbeat";
        _visibility.Touch(context, connected);
    }
}
