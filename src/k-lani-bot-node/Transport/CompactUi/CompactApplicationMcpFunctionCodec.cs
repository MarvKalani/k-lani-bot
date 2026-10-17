using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// Use case: app_mcp_save reads a function definition from the agent, and the store reads the same shape back from disk. Both
// pass these bounds and template rules, so a stored function can only name declared parameters, earlier steps and finite checks.
public static class CompactApplicationMcpFunctionCodec
{
    public const string Format = "k-lani-app-mcp-functions/1";
    public const int MaximumParameters = 16;
    public const int MaximumSteps = 12;
    public const int MaximumExpectations = 4;
    public const int MaximumChecks = 8;
    public const int MaximumFunctions = 64;
    public const int MaximumArgumentCharacters = 16000;
    public const int MaximumPathCharacters = 1024;
    private static readonly Regex ToolName = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ProgramId = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly string[] DefinitionFields = ["name", "description", "parameters", "steps", "checks"];

    public static bool ValidProgram(string? program)
    {
        return program != null && ProgramId.IsMatch(program);
    }

    // A known version is a nonempty single line other than "unknown", the word the node reports when it cannot read one.
    public static bool KnownVersion(string? version)
    {
        return !string.IsNullOrWhiteSpace(version) && !version.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            && !version.Any(char.IsControl);
    }

    // Reads the definition fields of an app_mcp_save request; other request fields are the caller's to check.
    public static bool TryDefinition(JsonObject request, string verifiedAt,
        out CompactApplicationMcpAttempt<CompactApplicationMcpFunction> attempt)
    {
        var definition = new JsonObject();
        foreach (string field in DefinitionFields.Where(request.ContainsKey))
        {
            definition[field] = request[field]?.DeepClone();
        }
        definition["verified_at"] = verifiedAt;

        return TryFunction(definition, out attempt);
    }

    public static bool TryFunction(JsonNode? node, out CompactApplicationMcpAttempt<CompactApplicationMcpFunction> attempt)
    {
        attempt = new(null, CompactApplicationMcpFailure.InvalidDefinition, "Use name, description, parameters, steps and checks.");
        if (node is not JsonObject entry || !CompactDiscoveryDocumentCodec.Only(entry, [.. DefinitionFields, "verified_at"])
            || !Text(entry, "name", out string name) || !CompactDiscoveryCodec.ValidName(name)
            || !CompactCaptureCodec.TryDescription(entry, out string? description) || !Text(entry, "verified_at", out string verifiedAt))
        {
            return false;
        }
        if (!TryParameters(entry["parameters"], out var parameters))
        {
            attempt = new(null, CompactApplicationMcpFailure.InvalidDefinition, "parameters: up to 16 of {name, type, description}.");
            return false;
        }
        var declared = parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        if (!TrySteps(entry["steps"], declared, out var steps, out string stepProblem))
        {
            attempt = new(null, CompactApplicationMcpFailure.InvalidDefinition, stepProblem);
            return false;
        }
        if (!TryChecks(entry["checks"], parameters, out var checks) || checks.Count + steps.Sum(step => step.Expectations.Count) == 0)
        {
            attempt = new(null, CompactApplicationMcpFailure.InvalidDefinition,
                "checks: up to 8 of {kind: file_written|gif_frames, path, min, max}; a function needs a check or an expectation.");
            return false;
        }
        attempt = new(new(name, description, parameters, steps, checks, verifiedAt));

        return true;
    }

    public static JsonObject Write(CompactApplicationMcpFunction function)
    {
        var entry = new JsonObject { ["name"] = function.Name };
        if (function.Description != null)
        {
            entry["description"] = function.Description;
        }
        entry["parameters"] = new JsonArray(function.Parameters.Select(parameter => (JsonNode?)WriteParameter(parameter)).ToArray());
        entry["steps"] = new JsonArray(function.Steps.Select(step => (JsonNode?)WriteStep(step)).ToArray());
        entry["checks"] = new JsonArray(function.Checks.Select(check => (JsonNode?)WriteCheck(check)).ToArray());
        entry["verified_at"] = function.VerifiedAt;

        return entry;
    }

    public static bool TryDocument(JsonNode? node, string program, string version,
        out CompactApplicationMcpAttempt<CompactApplicationMcpDocument> attempt)
    {
        attempt = new(null, CompactApplicationMcpFailure.StorageUnavailable, "The stored app MCP document is damaged.");
        if (node is not JsonObject document || !CompactDiscoveryDocumentCodec.Only(document, "format", "program", "version", "revision",
                "functions") || !Text(document, "format", out string format) || format != Format
            || !Text(document, "program", out string storedProgram) || storedProgram != program
            || !Text(document, "version", out string storedVersion) || storedVersion != version
            || document["revision"] is not JsonValue revisionValue || !revisionValue.TryGetValue(out long revision) || revision < 1
            || document["functions"] is not JsonArray entries || entries.Count > MaximumFunctions)
        {
            return false;
        }
        var functions = new List<CompactApplicationMcpFunction>();
        foreach (JsonNode? entry in entries)
        {
            if (!TryFunction(entry, out var function) || functions.Any(known => known.Name == function.Value!.Name))
            {
                return false;
            }
            functions.Add(function.Value!);
        }
        attempt = new(new(program, version, revision, functions));

        return true;
    }

    public static JsonObject WriteDocument(CompactApplicationMcpDocument document)
    {
        return new JsonObject
        {
            ["format"] = Format,
            ["program"] = document.Program,
            ["version"] = document.Version,
            ["revision"] = document.Revision,
            ["functions"] = new JsonArray(document.Functions.Select(function => (JsonNode?)Write(function)).ToArray())
        };
    }

    public static string TypeName(CompactApplicationMcpParameterType type)
    {
        return type switch
        {
            CompactApplicationMcpParameterType.String => "string",
            CompactApplicationMcpParameterType.Integer => "integer",
            CompactApplicationMcpParameterType.Number => "number",
            CompactApplicationMcpParameterType.Boolean => "boolean",
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    public static string CheckName(CompactApplicationMcpCheckKind kind)
    {
        return kind switch
        {
            CompactApplicationMcpCheckKind.FileWritten => "file_written",
            CompactApplicationMcpCheckKind.GifFrames => "gif_frames",
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private static bool TryParameters(JsonNode? node, out IReadOnlyList<CompactApplicationMcpParameter> parameters)
    {
        var parsed = new List<CompactApplicationMcpParameter>();
        parameters = parsed;
        if (node == null)
        {
            return true;
        }
        if (node is not JsonArray entries || entries.Count > MaximumParameters)
        {
            return false;
        }
        foreach (JsonNode? entry in entries)
        {
            if (!TryParameter(entry, out var parameter) || parsed.Any(known => known.Name == parameter!.Name))
            {
                return false;
            }
            parsed.Add(parameter!);
        }

        return true;
    }

    private static bool TryParameter(JsonNode? node, out CompactApplicationMcpParameter? parameter)
    {
        parameter = null;
        if (node is not JsonObject entry || !CompactDiscoveryDocumentCodec.Only(entry, "name", "type", "description")
            || !Text(entry, "name", out string name) || !CompactDiscoveryCodec.ValidName(name)
            || !CompactCaptureCodec.TryDescription(entry, out string? description))
        {
            return false;
        }
        string typeName = Text(entry, "type", out string given) ? given : "string";
        var types = Enum.GetValues<CompactApplicationMcpParameterType>().Where(type => TypeName(type) == typeName).ToArray();
        if (types.Length != 1)
        {
            return false;
        }
        parameter = new(name, types[0], description);

        return true;
    }

    private static bool TrySteps(JsonNode? node, IReadOnlySet<string> parameters, out IReadOnlyList<CompactApplicationMcpStep> steps,
        out string problem)
    {
        var parsed = new List<CompactApplicationMcpStep>();
        steps = parsed;
        problem = "steps: 1 to 12 of {tool, arguments, expect}.";
        if (node is not JsonArray entries || entries.Count is < 1 or > MaximumSteps)
        {
            return false;
        }
        foreach (JsonNode? entry in entries)
        {
            int number = parsed.Count + 1;
            if (!TryStep(entry, parameters, number, out var step))
            {
                problem = "Step " + number + ": a listed tool, an arguments object up to 16000 characters whose \"{{name}}\" "
                    + "names a parameter and whose {\"$step\", \"pointer\"} names an earlier step, and up to 4 expect entries "
                    + "{pointer, equals}.";
                return false;
            }
            parsed.Add(step!);
        }

        return true;
    }

    private static bool TryStep(JsonNode? node, IReadOnlySet<string> parameters, int number, out CompactApplicationMcpStep? step)
    {
        step = null;
        if (node is not JsonObject entry || !CompactDiscoveryDocumentCodec.Only(entry, "tool", "arguments", "expect")
            || !Text(entry, "tool", out string tool) || !ToolName.IsMatch(tool))
        {
            return false;
        }
        JsonObject arguments = entry["arguments"] as JsonObject ?? new JsonObject();
        if (entry["arguments"] is not null and not JsonObject || arguments.ToJsonString().Length > MaximumArgumentCharacters
            || !CompactApplicationMcpTemplate.IsValid(arguments, parameters, number))
        {
            return false;
        }
        if (!TryExpectations(entry["expect"], out var expectations))
        {
            return false;
        }
        step = new(tool, (JsonObject)arguments.DeepClone(), expectations);

        return true;
    }

    private static bool TryExpectations(JsonNode? node, out IReadOnlyList<CompactApplicationMcpExpectation> expectations)
    {
        var parsed = new List<CompactApplicationMcpExpectation>();
        expectations = parsed;
        if (node == null)
        {
            return true;
        }
        if (node is not JsonArray entries || entries.Count > MaximumExpectations)
        {
            return false;
        }
        foreach (JsonNode? entry in entries)
        {
            if (entry is not JsonObject expectation || !CompactDiscoveryDocumentCodec.Only(expectation, "pointer", "equals")
                || !Text(expectation, "pointer", out string pointer) || !CompactApplicationMcpTemplate.ValidPointer(pointer)
                || !expectation.ContainsKey("equals"))
            {
                return false;
            }
            parsed.Add(new(pointer, expectation["equals"]?.DeepClone()));
        }

        return true;
    }

    private static bool TryChecks(JsonNode? node, IReadOnlyList<CompactApplicationMcpParameter> parameters,
        out IReadOnlyList<CompactApplicationMcpCheck> checks)
    {
        var parsed = new List<CompactApplicationMcpCheck>();
        checks = parsed;
        if (node == null)
        {
            return true;
        }
        if (node is not JsonArray entries || entries.Count > MaximumChecks)
        {
            return false;
        }
        foreach (JsonNode? entry in entries)
        {
            if (!TryCheck(entry, parameters, out var check))
            {
                return false;
            }
            parsed.Add(check!);
        }

        return true;
    }

    private static bool TryCheck(JsonNode? node, IReadOnlyList<CompactApplicationMcpParameter> parameters,
        out CompactApplicationMcpCheck? check)
    {
        check = null;
        var names = parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        if (node is not JsonObject entry || !CompactDiscoveryDocumentCodec.Only(entry, "kind", "path", "min", "max")
            || !Text(entry, "kind", out string kindName) || !Text(entry, "path", out string path)
            || path.Length is < 1 or > MaximumPathCharacters || !CompactApplicationMcpTemplate.IsValid(JsonValue.Create(path), names, 1))
        {
            return false;
        }
        var kinds = Enum.GetValues<CompactApplicationMcpCheckKind>().Where(kind => CheckName(kind) == kindName).ToArray();
        if (kinds.Length != 1)
        {
            return false;
        }
        bool bounded = kinds[0] == CompactApplicationMcpCheckKind.GifFrames;
        if (!ValidBound(entry["min"], bounded, parameters) || !ValidBound(entry["max"], bounded, parameters))
        {
            return false;
        }
        check = new(kinds[0], path, entry["min"]?.DeepClone(), entry["max"]?.DeepClone());

        return true;
    }

    // A frame bound is a whole number or the placeholder of an integer parameter; a file check takes no bounds.
    private static bool ValidBound(JsonNode? bound, bool allowed, IReadOnlyList<CompactApplicationMcpParameter> parameters)
    {
        if (bound == null)
        {
            return true;
        }
        if (!allowed || bound is not JsonValue value)
        {
            return false;
        }
        if (value.TryGetValue(out string? text))
        {
            return parameters.Any(parameter => parameter.Type == CompactApplicationMcpParameterType.Integer
                && text == "{{" + parameter.Name + "}}");
        }

        return value.TryGetValue(out int number) && number >= 0;
    }

    private static JsonObject WriteParameter(CompactApplicationMcpParameter parameter)
    {
        var entry = new JsonObject { ["name"] = parameter.Name, ["type"] = TypeName(parameter.Type) };
        if (parameter.Description != null)
        {
            entry["description"] = parameter.Description;
        }

        return entry;
    }

    private static JsonObject WriteStep(CompactApplicationMcpStep step)
    {
        var entry = new JsonObject { ["tool"] = step.Tool, ["arguments"] = step.Arguments.DeepClone() };
        if (step.Expectations.Count > 0)
        {
            entry["expect"] = new JsonArray(step.Expectations.Select(expectation => (JsonNode?)new JsonObject
            {
                ["pointer"] = expectation.Pointer,
                ["equals"] = expectation.Value?.DeepClone()
            }).ToArray());
        }

        return entry;
    }

    private static JsonObject WriteCheck(CompactApplicationMcpCheck check)
    {
        var entry = new JsonObject { ["kind"] = CheckName(check.Kind), ["path"] = check.Path };
        if (check.Minimum != null)
        {
            entry["min"] = check.Minimum.DeepClone();
        }
        if (check.Maximum != null)
        {
            entry["max"] = check.Maximum.DeepClone();
        }

        return entry;
    }

    private static bool Text(JsonObject document, string name, out string value)
    {
        return CompactDiscoveryDocumentCodec.Text(document, name, out value);
    }
}
