using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Shared compatibility cases use labelled synthetic fingerprints.
// They establish contract parity, never authentic node or application evidence.
internal static class CompatibilityParityCases
{
    public static JsonArray Run(string file)
    {
        JsonObject corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var output = new JsonArray();
        foreach (JsonNode? node in corpus["cases"]!.AsArray())
        {
            JsonObject fixture = node!.AsObject();
            output.Add(new JsonObject { ["name"] = fixture["name"]!.DeepClone(), ["reply"] = RunCase(fixture) });
        }

        return output;
    }

    private static JsonObject RunCase(JsonObject fixture)
    {
        string kind = fixture["operation"]!.GetValue<string>();
        if (kind == "hash")
        {
            return new()
            {
                ["hash"] = CompactContractHash.Of(fixture["value"]).Hex
            };
        }
        if (kind == "schemas")
        {
            CompactContractHash.TrySchemas(fixture["tools"], out var schemas);
            return schemas.Value == null ? Error(schemas.Error!.Value)
                : new()
                {
                    ["schemas"] = CompactCompatibilityWire.Schemas(schemas.Value)
                };
        }
        if (kind == "validate")
        {
            CompactCompatibilityCodec.TryRecorded(fixture["recorded"], out var parsed);
            return parsed.Value == null ? Error(parsed.Error!.Value) : new()
            {
                ["valid"] = true
            };
        }
        CompactFunctionDraft draft = Draft(fixture["entry"]!.AsObject());
        CompactObservedCompatibility current = Observed(fixture["current"]!.AsObject());
        if (kind == "capture")
        {
            CompactCompatibility.TryCapture(draft, current, out var captured);
            return captured.Value == null ? Error(captured.Error!.Value) : CompactCompatibilityWire.Contract(captured.Value);
        }

        return Assess(fixture, draft, current);
    }

    private static JsonObject Assess(JsonObject fixture, CompactFunctionDraft draft, CompactObservedCompatibility current)
    {
        CompactRecordedCompatibility recorded;
        if (fixture["recorded"] != null)
        {
            if (!CompactCompatibilityCodec.TryRecorded(fixture["recorded"], out var loaded))
            {
                return Error(loaded.Error!.Value);
            }
            recorded = loaded.Value!;
        }
        else
        {
            CompactCompatibility.TryCapture(draft, Observed(fixture["baseline"]!.AsObject()), out var captured);
            recorded = new(CompactCompatibilityFormat.Contracts2, Contracts: captured.Value!);
        }
        if (fixture["change_entry"] is JsonObject change)
        {
            JsonObject altered = fixture["entry"]!.DeepClone().AsObject();
            if (change["name"] != null)
            {
                altered["name"] = change["name"]!.DeepClone();
            }
            if (change["selector_name"] != null)
            {
                altered["steps"]![0]!["selector"]!["name"] = change["selector_name"]!.DeepClone();
            }
            draft = Draft(altered);
        }

        return CompactCompatibilityNames.Verdict(CompactCompatibility.Assess(draft, recorded, current));
    }

    public static CompactFunctionDraft Draft(JsonObject entry)
    {
        CompactCaptureCodec.TryTopic(entry["topic"]!.GetValue<string>(), out var topic);
        CompactRecipeStep[] steps = entry["steps"]!.AsArray().Select(node => new CompactRecipeStep(
            Enum.Parse<CompactRecipeAction>(node!["action"]!.GetValue<string>(), true), Selector(node["selector"]!.AsObject()),
            Route(node["route"]!.GetValue<string>()), node["parameter"]?.GetValue<string>())).ToArray();
        CompactRecipeOutput[] outputs = entry["outputs"]!.AsArray().Select(node => Output(node!.AsObject())).ToArray();

        return new(entry["name"]!.GetValue<string>(), topic!, steps, outputs, CompactCaptureValidation.DemonstratedMutation);
    }

    private static CompactRecipeOutput Output(JsonObject node)
    {
        CompactCaptureCodec.TryField(node["field"]!.GetValue<string>(), out var field);
        CompactOutputCheckKind kind = node["check"]!["kind"]!.GetValue<string>() switch
        {
            "changed" => CompactOutputCheckKind.Changed,
            "equals_parameter" => CompactOutputCheckKind.EqualsParameter,
            "exists" => CompactOutputCheckKind.Exists,
            "not_empty" => CompactOutputCheckKind.NotEmpty,
            _ => throw new InvalidOperationException("Unexpected synthetic check.")
        };

        return new(node["name"]!.GetValue<string>(), Selector(node["selector"]!.AsObject()), field, kind,
            node["check"]!["parameter"]?.GetValue<string>());
    }

    private static CompactStoredSelector Selector(JsonObject node)
    {
        CompactSemanticTarget target = CaptureParityCases.Target(node);

        return new(target.Role, node["name"]?.GetValue<string>(), target.AutoId, target.WindowKind!.Value, target.ContainerPath,
            node["namePrefix"]?.GetValue<string>());
    }

    private static CompactRecipeRoute Route(string route)
    {
        return route switch
        {
            "native-text" => CompactRecipeRoute.NativeText,
            "native-read" => CompactRecipeRoute.NativeRead,
            "native-choice" => CompactRecipeRoute.NativeChoice,
            "semantic-focus+keyboard (native editable interface unavailable)" => CompactRecipeRoute.FocusKeyboard,
            _ => CompactRecipeRoute.Semantic
        };
    }

    public static CompactObservedCompatibility Observed(JsonObject current)
    {
        string? wire = current["wire_version"]?.GetValue<string>();
        CompactCompatibilityCodec.TrySchemas(current["action_schemas"], out var actions);
        CompactContractHash.TryDigest(current["native_implementation"]?.GetValue<string>(), out var native);
        CompactContractHash.TryDigest(current["gateway_implementation"]?.GetValue<string>(), out var gateway);
        CompactProgramIdentity? application = current["application"] is JsonObject app
            ? new(app["key"]?.GetValue<string>() ?? "", app["version"]?.GetValue<string>() ?? "") : null;

        return new(current["node_version"]?.GetValue<string>(), wire == null ? null : CompactCompatibilityWire.Wire(wire),
            current["platform"]?.GetValue<string>(), application, actions, native, gateway);
    }

    private static JsonObject Error(CompactCompatibilityError error)
    {
        return new()
        {
            ["error"] = CompactCompatibilityNames.Error(error)
        };
    }
}
