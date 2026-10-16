using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Native workflow contracts never substitute a missing confirmation, clipped value or arbitrary object for a typed binding.
internal static class WorkflowContractCases
{
    public static void Run()
    {
        var inputs = new Dictionary<string, CompactWorkflowInput>(StringComparer.Ordinal)
        {
            ["text"] = new(CompactWorkflowValueKind.String),
            ["confirm"] = new(CompactWorkflowValueKind.Boolean),
            ["document"] = new(CompactWorkflowValueKind.Document, Required: false)
        };
        var parameters = new JsonObject { ["text"] = "complete input", ["confirm"] = false };
        Require(CompactWorkflowParameters.TryRead(inputs, parameters, false, out var parsed), "false stays explicit boolean");
        Require(parsed.Value!["confirm"].Boolean == false, "no truthiness conversion");
        var bindings = new Dictionary<string, CompactWorkflowBinding>
        {
            ["target"] = new(CompactWorkflowBindingKind.ConfirmedOutput, Stage: "read", Output: "value")
        };
        var unconfirmed = new Dictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>>();
        Require(!CompactWorkflowBindings.TryResolve(bindings, parsed.Value, unconfirmed, out var refused)
            && refused.Error == CompactWorkflowError.UnconfirmedOutput, "unconfirmed source cannot become input");
        string complete = new('x', 16000);
        var confirmed = new Dictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>>
        {
            ["read"] = new Dictionary<string, CompactWorkflowValue> { ["value"] = new(CompactWorkflowValueKind.String, Text: complete) }
        };
        Require(CompactWorkflowBindings.TryResolve(bindings, parsed.Value, confirmed, out var resolved)
            && resolved.Value!["target"].Text == complete, "complete confirmed value retained without display clipping");
        var input = new Dictionary<string, CompactWorkflowBinding> { ["target"] = new(CompactWorkflowBindingKind.Input, Input: "missing") };
        Require(!CompactWorkflowBindings.TryResolve(input, parsed.Value, confirmed, out var missing)
            && missing.Error == CompactWorkflowError.MissingParameter, "missing optional input remains missing");
        var document = new JsonObject { ["path"] = "/private/host", ["sha256"] = new string('a', 64), ["origin"] = "fixture" };
        Require(!CompactWorkflowInputs.TryValue(document, inputs["document"], out var path)
            && path.Error == CompactWorkflowError.UnsupportedFields, "host path cannot be an opaque document reference");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Workflow contract regression failed: " + name);
        }
    }
}
