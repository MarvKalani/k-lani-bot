using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// What a function stored for another program version needs before it may serve the current version. Only an executed, verified
// app_mcp_save run on the current version makes it callable; a copy with a new version string never does.
public enum CompactApplicationMcpDriftStatus
{
    TestRunNeeded,
    RepairNeeded,
    Unchecked
}

// Why the first step of a stored function does not fit the current program, or why the program could not be asked.
public enum CompactApplicationMcpDriftReason
{
    ToolAbsent,
    ArgumentUnknown,
    ArgumentRequired,
    ProgramUnavailable
}

// One function of another version against the current program; Step is the 1-based number of the first step that does not fit.
public sealed record CompactApplicationMcpDriftFunction(string Name, CompactApplicationMcpDriftStatus Status, int? Step, string? Tool,
    CompactApplicationMcpDriftReason? Reason);

// The functions of one other version of a program, assessed against the version the node runs now.
public sealed record CompactApplicationMcpDriftReport(string Program, string Source, string? Current,
    IReadOnlyList<CompactApplicationMcpDriftFunction> Functions);

// Use case: ui_info finds stored app MCP functions only for other versions of a program the node runs, for example after a
// FilmCraft update. Without running anything, this probe checks each stored step against the program's current tool list and
// tool schemas, so the agent learns which functions need only a test run and which step it has to rework.
public sealed class CompactApplicationMcpDrift(ICompactNodeClient client)
{
    private readonly Dictionary<string, JsonObject?> _schemas = new(StringComparer.Ordinal);

    public async Task<CompactApplicationMcpDriftReport> AssessAsync(CompactApplicationMcpDocument source, CompactApplicationMcpProbe probe,
        IReadOnlySet<string> currentNames, CancellationToken cancellation)
    {
        var functions = new List<CompactApplicationMcpDriftFunction>();
        foreach (CompactApplicationMcpFunction function in source.Functions.Where(function => !currentNames.Contains(function.Name)))
        {
            functions.Add(await AssessFunctionAsync(source.Program, function, probe, cancellation));
        }

        return new(source.Program, source.Version, probe.Version, functions);
    }

    private async Task<CompactApplicationMcpDriftFunction> AssessFunctionAsync(string program, CompactApplicationMcpFunction function,
        CompactApplicationMcpProbe probe, CancellationToken cancellation)
    {
        if (probe.Error != null || !CompactApplicationMcpFunctionCodec.KnownVersion(probe.Version))
        {
            CompactApplicationMcpDriftReason unavailable = CompactApplicationMcpDriftReason.ProgramUnavailable;
            return new(function.Name, CompactApplicationMcpDriftStatus.Unchecked, null, null, unavailable);
        }
        for (int index = 0; index < function.Steps.Count; index++)
        {
            CompactApplicationMcpStep step = function.Steps[index];
            CompactApplicationMcpDriftReason? reason = probe.Tools.Contains(step.Tool)
                ? ArgumentReason(step.Arguments, await SchemaAsync(program, probe.Version!, step.Tool, cancellation))
                : CompactApplicationMcpDriftReason.ToolAbsent;
            if (reason != null)
            {
                return new(function.Name, CompactApplicationMcpDriftStatus.RepairNeeded, index + 1, step.Tool, reason);
            }
        }

        return new(function.Name, CompactApplicationMcpDriftStatus.TestRunNeeded, null, null, null);
    }

    // A schema that closes its properties refuses unknown argument names; a required property must be present in the stored step.
    private static CompactApplicationMcpDriftReason? ArgumentReason(JsonObject arguments, JsonObject? schema)
    {
        if (schema == null)
        {
            return null;
        }
        var properties = schema["properties"] as JsonObject;
        bool closed = schema["additionalProperties"] is JsonValue flag && flag.TryGetValue(out bool open) && !open;
        if (closed && arguments.Any(argument => properties?.ContainsKey(argument.Key) != true))
        {
            return CompactApplicationMcpDriftReason.ArgumentUnknown;
        }
        var required = (schema["required"] as JsonArray ?? []).Select(name => name?.ToString()).OfType<string>();

        return required.Any(name => !arguments.ContainsKey(name)) ? CompactApplicationMcpDriftReason.ArgumentRequired : null;
    }

    // One schema read per tool and version for the gateway session; a schema that cannot be read leaves the arguments unchecked.
    private async Task<JsonObject?> SchemaAsync(string program, string version, string tool, CancellationToken cancellation)
    {
        string key = program + "\n" + version + "\n" + tool;
        if (_schemas.TryGetValue(key, out JsonObject? cached))
        {
            return cached;
        }
        var arguments = new JsonObject { ["program"] = program, ["schema"] = tool };
        JsonObject answer = await client.CallAsync(Tool.ApplicationMcpRead, arguments, cancellation);
        JsonObject? schema = answer["complete"]?.GetValue<bool>() == true ? answer["inputSchema"] as JsonObject : null;

        _schemas[key] = schema;
        return schema;
    }
}
