using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Complete native values are compared before clipping, including every final written field even when its declared output check is
// only Exists.
public static class CompactRecipeValues
{
    public static IReadOnlySet<CompactWindowKind> RetiredInputWindows(CompactFunctionDraft function, int verifiedBeforeStep)
    {
        var outputs = function.Outputs.Select(output => output.Selector.WindowKind).ToHashSet();
        var mutations = function.Steps.Select((step, index) => new CompactIndexedStep(index, step))
            .Where(item => item.Step.Action != CompactRecipeAction.Read).GroupBy(item => item.Step.Selector.WindowKind);

        return mutations.Where(group => !outputs.Contains(group.Key) && group.All(item => item.Index <= verifiedBeforeStep
            && (item.Step.Action != CompactRecipeAction.Write || item.Index < verifiedBeforeStep)))
            .Select(group => group.Key).ToHashSet();
    }

    // Throws ArgumentException when runtime inputs do not match the approved generated-function schema.
    public static void RequireParameters(CompactFunctionDraft function, int process, IReadOnlyDictionary<string, string> parameters)
    {
        var arguments = new JsonObject
        {
            ["pid"] = process,
            ["parameters"] = new JsonObject(parameters.Select(entry =>
            new KeyValuePair<string, JsonNode?>(entry.Key, JsonValue.Create(entry.Value))))
        };

        ToolContract.Validate(CompactCaptureWire.InputSchema(function), arguments);
    }

    // Throws CompactNodeException for ambiguous or wrong-type before-values; absent goal outputs are retained as absent.
    public static IReadOnlyDictionary<string, CompactOptionalOutput> Before(CompactFunctionDraft function,
        CompactFunctionObservation observation)
    {
        var result = new Dictionary<string, CompactOptionalOutput>(StringComparer.Ordinal);
        foreach (var output in function.Outputs)
        {
            if (!CompactCapturedOutputs.TryRead(observation.NodesOf(output.Selector.WindowKind), output.Selector, output.Field,
                out var value))
            {
                throw new CompactNodeException(CompactCaptureNames.Error(value.Error!.Value));
            }
            result.Add(output.Name, value.Value!);
        }

        return result;
    }

    // Throws CompactNodeException when any complete output fails its recorded postcondition against the persisted before-value.
    public static IReadOnlyDictionary<string, string> Verify(CompactFunctionPreflight prepared,
        IReadOnlyDictionary<string, string> parameters, CompactFunctionObservation current)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CompactRecipeOutput output in prepared.Function.Draft.Outputs)
        {
            if (!current.Windows.ContainsKey(output.Selector.WindowKind)
                || !CompactCapturedOutputs.TryRequired(current.NodesOf(output.Selector.WindowKind), output.Selector, output.Field,
                    out var value))
            {
                throw new CompactNodeException("Workflow output is absent or unverified.");
            }
            if (!CompactOutputVerifier.TryCurrent(output, parameters, prepared.Outputs[output.Name], value.Value!, false, out var verified))
            {
                throw new CompactNodeException(CompactCaptureNames.Error(verified.Error!.Value));
            }
            result.Add(output.Name, verified.Value!);
        }

        return result;
    }

    // Throws CompactNodeException when a field's last requested replacement is not exactly present, regardless of output
    // checks or ledger success.
    public static void RequireFinalWrites(CompactFunctionDraft function, IReadOnlyDictionary<string, string> parameters,
        CompactFunctionObservation observation, int verifiedBeforeStep)
    {
        var finalWrites = function.Steps.Select((step, index) => new CompactIndexedStep(index, step))
            .Where(item => item.Step.Action == CompactRecipeAction.Write)
            .GroupBy(item => CompactJsonText.Serialize(CompactDiscoveryWire.Selector(item.Step.Selector))).Select(group => group.Last());
        foreach (var item in finalWrites)
        {
            IReadOnlyList<CompactTreeNode> nodes = observation.NodesOf(item.Step.Selector.WindowKind);
            if (!CompactCapturedOutputs.TryRead(nodes, item.Step.Selector, CompactOutputField.Value, out var observed))
            {
                throw new CompactNodeException(CompactCaptureNames.Error(observed.Error!.Value));
            }
            if (!observed.Value!.Exists && item.Index < verifiedBeforeStep)
            {
                continue;
            }
            if (!CompactWritePostcondition.TryCheck(nodes, item.Step.Selector, parameters[item.Step.Parameter!], out var verified))
            {
                throw new CompactNodeException(CompactCaptureNames.Error(verified.Error!.Value));
            }
        }
    }
}
