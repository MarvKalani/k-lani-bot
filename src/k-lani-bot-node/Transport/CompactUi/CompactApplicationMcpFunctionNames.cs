using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Use case: ui_info and cartridge_call report stored app MCP functions in these words. A row names only functions verified on the
// version the node runs; a run reply says verified only when every step answered and every result check held.
public static class CompactApplicationMcpFunctionNames
{
    public const string FunctionsField = "app_mcp_functions";
    public const string DriftField = "app_mcp_other_versions";
    public const int FunctionLimit = CompactFunctionSummary.Limit;

    public const string FunctionsHint = "app_mcp_functions lists stored functions of a program's own MCP server for the version the "
        + "node runs: cartridge_call {function, arguments: {program, parameters: {name: value}}} runs one; a reply with verified true "
        + "is final.";

    public const string ListingField = "stored_functions";

    public const string ListingHint = "stored_functions were verified on this version and do a whole task in one call: "
        + "cartridge_call {function, arguments: {program, parameters: {name: value}}}; a reply with verified true is final. "
        + "Otherwise load one tool's schema with app_mcp_read {program, schema}; read tools run through app_mcp_read, all others "
        + "through app_mcp_call.";

    public const string DriftHint = "app_mcp_other_versions lists stored functions of another version of the program; none is "
        + "callable here: test_run_needed means its tools and arguments still fit, so verify it with cartridge_call {function: "
        + "\"app_mcp_save\", arguments: {program, name, from_version, test: {name: value}}}; repair_needed names the first step "
        + "to rework.";

    public static string Failure(CompactApplicationMcpFailure failure)
    {
        return failure switch
        {
            CompactApplicationMcpFailure.InvalidDefinition => "invalid_definition",
            CompactApplicationMcpFailure.InvalidParameters => "invalid_parameters",
            CompactApplicationMcpFailure.ProgramUnavailable => "program_unavailable",
            CompactApplicationMcpFailure.UnknownVersion => "version_unknown",
            CompactApplicationMcpFailure.VersionChanged => "version_changed",
            CompactApplicationMcpFailure.ToolAbsent => "tool_absent",
            CompactApplicationMcpFailure.StepValueAbsent => "step_value_absent",
            CompactApplicationMcpFailure.StepFailed => "step_failed",
            CompactApplicationMcpFailure.ExpectationFailed => "expectation_failed",
            CompactApplicationMcpFailure.CheckFailed => "check_failed",
            CompactApplicationMcpFailure.StorageUnavailable => "storage_unavailable",
            CompactApplicationMcpFailure.UnknownFunction => "unknown_function",
            _ => throw new UnreachableException()
        };
    }

    public static string Status(CompactApplicationMcpDriftStatus status)
    {
        return status switch
        {
            CompactApplicationMcpDriftStatus.TestRunNeeded => "test_run_needed",
            CompactApplicationMcpDriftStatus.RepairNeeded => "repair_needed",
            CompactApplicationMcpDriftStatus.Unchecked => "unchecked",
            _ => throw new UnreachableException()
        };
    }

    public static string Reason(CompactApplicationMcpDriftReason reason)
    {
        return reason switch
        {
            CompactApplicationMcpDriftReason.ToolAbsent => "tool_absent",
            CompactApplicationMcpDriftReason.ArgumentUnknown => "argument_unknown",
            CompactApplicationMcpDriftReason.ArgumentRequired => "argument_required",
            CompactApplicationMcpDriftReason.ProgramUnavailable => "program_unavailable",
            _ => throw new UnreachableException()
        };
    }

    // The reply of a run: on success the checks with what they read; on failure also the step, the code and whether the program
    // may have changed, so the agent never reruns a half-done function blindly.
    public static JsonObject Reply(string function, CompactApplicationMcpRun run)
    {
        var reply = new JsonObject
        {
            ["success"] = run.Verified,
            ["verified"] = run.Verified,
            ["function"] = function,
            ["program"] = run.Program,
            ["version"] = run.Version
        };
        if (!run.Verified)
        {
            AddFailure(reply, run);
        }
        if (run.Checks.Count > 0)
        {
            reply["checks"] = new JsonArray(run.Checks.Select(check => (JsonNode?)CheckEntry(check)).ToArray());
        }

        return reply;
    }

    public static JsonObject Refusal(CompactApplicationMcpFailure failure, string? detail)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["verified"] = false,
            ["code"] = Failure(failure),
            ["error"] = detail,
            ["outcome"] = "not_dispatched"
        };
    }

    // One listed function: its name, description and parameters with their types, enough to call it without a browse.
    public static JsonObject Entry(CompactApplicationMcpFunction function)
    {
        var entry = new JsonObject { ["name"] = function.Name };
        if (function.Description != null)
        {
            entry["description"] = function.Description;
        }
        entry["parameters"] = new JsonObject(function.Parameters.Select(parameter => new KeyValuePair<string, JsonNode?>(parameter.Name,
            CompactApplicationMcpFunctionCodec.TypeName(parameter.Type))));

        return entry;
    }

    // A browsed function: its description, the program tools of its steps and the schema of its cartridge_call arguments.
    public static JsonObject Descriptor(CompactApplicationMcpDocument document, CompactApplicationMcpFunction function)
    {
        var properties = new JsonObject(function.Parameters.Select(parameter => new KeyValuePair<string, JsonNode?>(parameter.Name,
            ParameterSchema(parameter))));
        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(function.Parameters.Select(parameter => (JsonNode?)parameter.Name).ToArray()),
            ["additionalProperties"] = false
        };
        var descriptor = Entry(function);
        descriptor["program"] = document.Program;
        descriptor["version"] = document.Version;
        descriptor["steps"] = new JsonArray(function.Steps.Select(step => (JsonNode?)step.Tool).ToArray());
        descriptor["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["program"] = new JsonObject { ["const"] = document.Program }, ["parameters"] = parameters },
            ["required"] = new JsonArray("program", "parameters")
        };

        return descriptor;
    }

    private static JsonObject ParameterSchema(CompactApplicationMcpParameter parameter)
    {
        var schema = new JsonObject { ["type"] = CompactApplicationMcpFunctionCodec.TypeName(parameter.Type) };
        if (parameter.Description != null)
        {
            schema["description"] = parameter.Description;
        }

        return schema;
    }

    public static JsonObject DriftRow(CompactApplicationMcpDriftReport report)
    {
        var row = new JsonObject { ["program"] = report.Program, ["version"] = report.Source, ["current"] = report.Current };
        foreach (CompactApplicationMcpDriftStatus status in Enum.GetValues<CompactApplicationMcpDriftStatus>())
        {
            var functions = report.Functions.Where(function => function.Status == status).ToArray();
            if (functions.Length == 0)
            {
                continue;
            }
            row[Status(status)] = new JsonArray(functions.Take(FunctionLimit).Select(DriftEntry).ToArray());
            if (functions.Length > FunctionLimit)
            {
                row["more_" + Status(status)] = functions.Length - FunctionLimit;
            }
        }

        return row;
    }

    private static void AddFailure(JsonObject reply, CompactApplicationMcpRun run)
    {
        reply["code"] = Failure(run.Failure!.Value);
        reply["error"] = run.Detail;
        if (run.FailedStep != null)
        {
            reply["step"] = run.FailedStep;
        }
        reply["outcome"] = run.Dispatched == 0 ? "not_dispatched" : "may_have_changed";
        if (run.LastText != null)
        {
            reply["text"] = run.LastText;
        }
    }

    private static JsonObject CheckEntry(CompactApplicationMcpCheckResult result)
    {
        var entry = new JsonObject
        {
            ["kind"] = CompactApplicationMcpFunctionCodec.CheckName(result.Check.Kind),
            ["path"] = result.Path,
            ["passed"] = result.Passed,
            ["bytes"] = result.After.Bytes
        };
        if (result.After.GifFrames != null)
        {
            entry["gif_frames"] = result.After.GifFrames;
        }

        return entry;
    }

    private static JsonNode? DriftEntry(CompactApplicationMcpDriftFunction function)
    {
        if (function.Reason == null)
        {
            return JsonValue.Create(function.Name);
        }
        var entry = new JsonObject { ["name"] = function.Name, ["reason"] = Reason(function.Reason.Value) };
        if (function.Step != null)
        {
            entry["step"] = function.Step;
            entry["tool"] = function.Tool;
        }

        return entry;
    }
}
