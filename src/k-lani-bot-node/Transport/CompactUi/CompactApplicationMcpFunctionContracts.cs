using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// The value types a stored app MCP function accepts; a whole-string "{{name}}" in a step becomes a value of this type.
public enum CompactApplicationMcpParameterType
{
    String,
    Integer,
    Number,
    Boolean
}

// One named input of a stored app MCP function; the caller supplies it on every run, the function stores no training value.
public sealed record CompactApplicationMcpParameter(string Name, CompactApplicationMcpParameterType Type, string? Description);

// A value the program's answer to one step must hold, at an RFC 6901 JSON pointer into the answer text read as JSON.
public sealed record CompactApplicationMcpExpectation(string Pointer, JsonNode? Value);

// One stored step: a tool of the program's own MCP server and its argument template, run through app_mcp_call.
// The template names parameters as "{{name}}" and earlier answers as {"$step": number, "pointer": "/path"}.
public sealed record CompactApplicationMcpStep(string Tool, JsonObject Arguments,
    IReadOnlyList<CompactApplicationMcpExpectation> Expectations);

// What a local result check proves after the last step, read on the node without moving the file.
public enum CompactApplicationMcpCheckKind
{
    FileWritten,
    GifFrames
}

// One result check; its path and frame bounds may name parameters, so a fresh output path is checked on every run.
public sealed record CompactApplicationMcpCheck(CompactApplicationMcpCheckKind Kind, string Path, JsonNode? Minimum, JsonNode? Maximum);

// A stored function of one program version. Only a run on that version whose checks all held stores it; VerifiedAt says when.
public sealed record CompactApplicationMcpFunction(string Name, string? Description,
    IReadOnlyList<CompactApplicationMcpParameter> Parameters, IReadOnlyList<CompactApplicationMcpStep> Steps,
    IReadOnlyList<CompactApplicationMcpCheck> Checks, string VerifiedAt);

// The one active document of a program version; the version is what the program's versionCommand printed on the node.
public sealed record CompactApplicationMcpDocument(string Program, string Version, long Revision,
    IReadOnlyList<CompactApplicationMcpFunction> Functions);

// Why a stored function did not run to a verified end. Refusals before the first step leave the program unchanged.
public enum CompactApplicationMcpFailure
{
    InvalidDefinition,
    InvalidParameters,
    ProgramUnavailable,
    UnknownVersion,
    VersionChanged,
    ToolAbsent,
    StepValueAbsent,
    StepFailed,
    ExpectationFailed,
    CheckFailed,
    StorageUnavailable,
    UnknownFunction
}

// Typed failures of definition, storage and runs, with an optional plain detail for the agent.
public sealed record CompactApplicationMcpAttempt<T>(T? Value, CompactApplicationMcpFailure? Failure = null, string? Detail = null);

// One file the checks read, as the node reported it before or after the steps.
public sealed record CompactApplicationMcpFileFacts(string Path, bool Exists, long Bytes, string? Modified, int? GifFrames);

// The outcome of one check, with what the node read after the last step.
public sealed record CompactApplicationMcpCheckResult(CompactApplicationMcpCheck Check, string Path, bool Passed,
    CompactApplicationMcpFileFacts After);

// What a run did. Dispatched counts the steps sent to the program, so a failure says whether anything may have changed.
public sealed record CompactApplicationMcpRun(string Program, string? Version, CompactApplicationMcpFailure? Failure, string? Detail,
    int? FailedStep, int Dispatched, IReadOnlyList<CompactApplicationMcpCheckResult> Checks, string? LastText)
{
    public bool Verified => Failure == null;
}
