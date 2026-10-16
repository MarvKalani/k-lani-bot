using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Guard;

namespace KLaniBot.Actions;

// One request as the dispatcher reads it. Everything that can be derived from the frame is derived once
// here, so no phase has to parse the same text again or look the same tool up several times.
internal sealed class ActionRequest
{
    private JsonObject? _specification;
    private Tool? _tool;

    private ActionRequest(string action, JsonObject arguments, RequestContext context, string operationId, string hash)
    {
        Action = action;
        Arguments = arguments;
        Context = context;
        OperationId = operationId;
        Hash = hash;
    }

    public string Action
    {
        get;
    }
    public JsonObject Arguments
    {
        get;
    }
    public RequestContext Context
    {
        get;
    }

    // The id the client chose for a mutation. Reads need none and get a generated one for the journal.
    public string OperationId
    {
        get;
    }

    // Binds the id to the principal, so two principals can use the same id without seeing each other's results.
    public string OperationKey => Context.PrincipalId + ":" + OperationId;

    // Detects a retry that reuses an id with different content. Journals written by earlier versions
    // contain hashes made exactly this way, so the formula must not change.
    public string Hash
    {
        get;
    }

    // Looked up on first use and not when the frame is read: an unknown tool must be refused by the
    // phase that checks the tool, after the checks that come before it.
    public JsonObject Specification => _specification ??= ToolContract.Get(Action);
    public Tool Tool => _tool ??= ToolNames.Parse(Action);
    public Scope Scope => ToolScope.Of(Specification);
    public ToolFeature Feature => ToolFeatures.Of(Specification);
    public bool Mutates => Specification["mutates"]!.GetValue<bool>();

    public static ActionRequest Read(JsonObject frame, RequestContext context)
    {
        string action = frame["action"]?.ToString() ?? "";
        JsonObject arguments = frame["args"] as JsonObject ?? new JsonObject();
        string operationId = arguments["request_id"]?.ToString() ?? Guid.CreateVersion7().ToString();
        var identity = new JsonObject { ["action"] = action, ["args"] = arguments.DeepClone() };
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToJsonString())));
        return new ActionRequest(action, arguments, context, operationId, hash);
    }
}
