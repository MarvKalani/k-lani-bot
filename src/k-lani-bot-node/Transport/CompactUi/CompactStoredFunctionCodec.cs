using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// The generated-function loader converts finite action/check values while preserving the approved original parameter schema and wire body.
public static class CompactStoredFunctionCodec
{
    public static bool TryRead(JsonNode? node, out CompactFunctionAttempt<CompactLoadedFunction> attempt)
    {
        attempt = new(null, CompactFunctionError.InvalidDefinition);
        if (node is not JsonObject entry || !CompactDiscoveryDocumentCodec.Only(entry, "name", "topic", "steps", "outputs",
                "compatibility", "validation", "inputSchema", "description") || !Text(entry, "name", out string name)
            || !CompactDiscoveryCodec.ValidName(name) || !CompactCaptureCodec.TryDescription(entry, out string? description)
            || !Text(entry, "topic", out string topic) || !CompactCaptureCodec.TryTopic(topic, out var category)
            || entry["steps"] is not JsonArray steps || steps.Count is < 1 or > CompactCaptureCodec.MaximumSteps
            || entry["outputs"] is not JsonArray outputs || outputs.Count is < 1 or > CompactCaptureCodec.MaximumOutputs)
        {
            return false;
        }
        if (!CompactCompatibilityCodec.TryRecorded(entry["compatibility"], out var compatibility))
        {
            attempt = new(null, CompactFunctionError.InvalidDefinition, CompactCompatibilityNames.Error(compatibility.Error!.Value));
            return false;
        }
        if (!TryParameters(entry["inputSchema"], out var schema))
        {
            attempt = new(null, CompactFunctionError.InvalidParameters);
            return false;
        }
        if (!TrySteps(steps, schema!, out var parsedSteps))
        {
            attempt = new(null, parsedSteps.Error, parsedSteps.Detail);
            return false;
        }
        if (!TryOutputs(outputs, schema!, out var parsedOutputs))
        {
            attempt = new(null, parsedOutputs.Error, parsedOutputs.Detail);
            return false;
        }
        CompactCaptureValidation validation = Text(entry, "validation", out string label)
            && label == CompactCaptureWire.Validation(CompactCaptureValidation.ObservedRead)
                ? CompactCaptureValidation.ObservedRead : CompactCaptureValidation.DemonstratedMutation;
        var draft = new CompactFunctionDraft(name, category!, parsedSteps.Value!, parsedOutputs.Value!, validation,
            entry["inputSchema"]!.DeepClone().AsObject(), description);
        attempt = new(new(draft, compatibility.Value!, entry.DeepClone().AsObject()));

        return true;
    }

    private static bool TryParameters(JsonNode? node, out IReadOnlySet<string>? parameters)
    {
        parameters = null;
        if (node is not JsonObject schema || schema["properties"] is not JsonObject root
            || root["parameters"] is not JsonObject argument || argument["properties"] is not JsonObject properties
            || argument["required"] is not JsonArray required || argument["additionalProperties"] is not JsonValue flag
            || !flag.TryGetValue<bool>(out bool additional) || additional)
        {
            return false;
        }
        var names = properties.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> property in properties)
        {
            if (!CompactDiscoveryCodec.ValidName(property.Key) || property.Value is not JsonObject definition
                || !Text(definition, "type", out string kind) || kind != "string")
            {
                return false;
            }
        }
        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? value in required)
        {
            if (value is not JsonValue name || !name.TryGetValue(out string? text) || text == null
                || !names.Contains(text) || !requiredNames.Add(text))
            {
                return false;
            }
        }
        parameters = names;

        return true;
    }

    private static bool TrySteps(JsonArray steps, IReadOnlySet<string> parameters,
        out CompactFunctionAttempt<IReadOnlyList<CompactRecipeStep>> attempt)
    {
        var parsed = new List<CompactRecipeStep>();
        foreach (JsonNode? node in steps)
        {
            attempt = new(null, CompactFunctionError.InvalidAction);
            if (node is not JsonObject step || !CompactDiscoveryDocumentCodec.Only(step, "action", "selector", "route", "parameter",
                    "enabledBeforeFirstAction")
                || !Text(step, "action", out string action) || !TryAction(action, out var kind)
                || !Text(step, "route", out string route) || !TryRoute(route, out var access))
            {
                return false;
            }
            if (!CompactStoredSelectorCodec.TryRead(step["selector"], false, out var selector))
            {
                attempt = new(null, selector.Error);
                return false;
            }
            string? parameter = Text(step, "parameter", out string name) ? name : null;
            if (kind is CompactRecipeAction.Write or CompactRecipeAction.Select
                && (!CompactDiscoveryCodec.ValidName(parameter) || !parameters.Contains(parameter!)))
            {
                attempt = new(null, CompactFunctionError.InvalidWriteParameter);
                return false;
            }
            if (!TryEnabledBeforeFirstAction(step, kind, parsed, out bool enabled))
            {
                return false;
            }
            parsed.Add(new(kind, selector.Value!, access, parameter, enabled));
        }
        attempt = new(parsed);

        return true;
    }

    // A false flag only makes sense for a mutation that an earlier mutation may enable, so the first mutation and every read refuse it.
    private static bool TryEnabledBeforeFirstAction(JsonObject step, CompactRecipeAction kind, IReadOnlyList<CompactRecipeStep> earlier,
        out bool enabled)
    {
        enabled = true;
        if (step.ContainsKey("enabledBeforeFirstAction")
            && !CompactDiscoveryDocumentCodec.Boolean(step, "enabledBeforeFirstAction", out enabled))
        {
            return false;
        }
        bool followsMutation = earlier.Any(previous => previous.Action != CompactRecipeAction.Read);

        return enabled || (kind != CompactRecipeAction.Read && followsMutation);
    }

    private static bool TryOutputs(JsonArray outputs, IReadOnlySet<string> parameters,
        out CompactFunctionAttempt<IReadOnlyList<CompactRecipeOutput>> attempt)
    {
        var parsed = new List<CompactRecipeOutput>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in outputs)
        {
            if (node is not JsonObject output || !Text(output, "name", out string name) || !names.Add(name))
            {
                attempt = new(null, CompactFunctionError.DuplicateOutput);
                return false;
            }
            if (!TryOutput(output, parameters, out var value))
            {
                attempt = new(null, value.Error);
                return false;
            }
            parsed.Add(value.Value!);
        }
        attempt = new(parsed);

        return true;
    }

    private static bool TryOutput(JsonObject output, IReadOnlySet<string> parameters,
        out CompactFunctionAttempt<CompactRecipeOutput> attempt)
    {
        if (!CompactStoredSelectorCodec.TryRead(output["selector"], true, out var selector))
        {
            attempt = new(null, selector.Error);
            return false;
        }
        attempt = new(null, CompactFunctionError.InvalidOutput);
        if (!CompactDiscoveryDocumentCodec.Only(output, "name", "selector", "field", "check")
            || !Text(output, "name", out string name) || !CompactDiscoveryCodec.ValidName(name)
            || !Text(output, "field", out string field) || !CompactCaptureCodec.TryField(field, out var selected)
            || output["check"] is not JsonObject check || !CompactDiscoveryDocumentCodec.Only(check, "kind", "parameter")
            || !Text(check, "kind", out string condition) || !TryCheck(condition, out var kind))
        {
            return false;
        }
        string? parameter = Text(check, "parameter", out string reference) ? reference : null;
        if (kind == CompactOutputCheckKind.EqualsParameter && (parameter == null || !parameters.Contains(parameter)))
        {
            attempt = new(null, CompactFunctionError.UnknownOutputParameter);
            return false;
        }
        attempt = new(new(name, selector.Value!, selected, kind, parameter));

        return true;
    }

    public static bool TryAction(string value, out CompactRecipeAction action)
    {
        return Enum.TryParse(value, true, out action) && Enum.IsDefined(action) && action != CompactRecipeAction.Key
            && value == CompactCaptureNames.Action(action);
    }

    public static bool TryRoute(string value, out CompactRecipeRoute route)
    {
        foreach (CompactRecipeRoute candidate in Enum.GetValues<CompactRecipeRoute>())
        {
            if (CompactCaptureNames.Route(candidate) == value)
            {
                route = candidate;
                return true;
            }
        }
        route = CompactRecipeRoute.Semantic;

        return false;
    }

    private static bool TryCheck(string value, out CompactOutputCheckKind check)
    {
        foreach (CompactOutputCheckKind candidate in Enum.GetValues<CompactOutputCheckKind>())
        {
            if (CompactCaptureNames.Check(candidate) == value)
            {
                check = candidate;
                return true;
            }
        }
        check = CompactOutputCheckKind.Exists;

        return false;
    }

    private static bool Text(JsonObject document, string name, out string value)
    {
        return CompactDiscoveryDocumentCodec.Text(document, name, out value);
    }
}
