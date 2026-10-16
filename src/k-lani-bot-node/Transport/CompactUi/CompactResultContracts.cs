using System.Diagnostics;

namespace KLaniBot.Transport.CompactUi;

// Compact clients choose how much verified result data is returned inline; legacy preserves the original reply.
public enum CompactResultMode
{
    Legacy,
    Summary,
    Values,
    Debug
}

// A result-detail request fails explicitly without changing application state or replaying input.
public enum CompactResultError
{
    InvalidEvidenceArguments,
    InvalidPageArguments,
    EvidenceTooLarge,
    EvidenceNotFound,
    EvidenceExpired,
    InvalidSection,
    OffsetOutOfRange
}

// Private evidence belongs to one authorized node and one gateway conversation.
public sealed record CompactResultAuthority(string Node, Guid Session);

// The result store issues opaque identifiers; no identifier is a host path or application target.
public sealed record CompactEvidenceReference(Guid Value);

// Protocol adapters parse result paging before consulting the private store; offsets count Unicode codepoints.
public sealed record CompactResultPageQuery(CompactEvidenceReference? Reference, CompactResultSection Section, int Offset, int Limit);

// A detail reference names debug or one declared output; an unknown boundary value remains explicitly invalid.
public enum CompactResultSectionKind
{
    Debug,
    Output,
    Unknown
}

// Store callers use typed section kinds; arbitrary output names remain application data rather than executable paths.
public sealed record CompactResultSection(CompactResultSectionKind Kind, string? Name = null)
{
    private const string OutputPrefix = "outputs/";

    public static CompactResultSection Parse(string value)
    {
        if (value == "debug")
        {
            return new(CompactResultSectionKind.Debug);
        }
        if (value.StartsWith(OutputPrefix, StringComparison.Ordinal))
        {
            return new(CompactResultSectionKind.Output, value[OutputPrefix.Length..]);
        }

        return new(CompactResultSectionKind.Unknown, value);
    }
}

// A compact client assembles successive exact codepoint pages without splitting a supplementary Unicode character.
public sealed record CompactResultPage(string Text, int Offset, int NextOffset, bool Complete, int TotalCharacters);

// Store callers inspect a named result or a typed error; expected input failures never require exceptions.
public sealed record CompactResultAttempt<T>(T? Value, CompactResultError? Error);

// Protocol responses preserve the established compact result error spelling across runtimes.
public static class CompactResultErrors
{
    public static string ToWireMessage(CompactResultError error)
    {
        return error switch
        {
            CompactResultError.InvalidEvidenceArguments => "Invalid result evidence.",
            CompactResultError.InvalidPageArguments => "Invalid result page arguments.",
            CompactResultError.EvidenceTooLarge => "evidence_too_large",
            CompactResultError.EvidenceNotFound => "evidence_not_found",
            CompactResultError.EvidenceExpired => "evidence_expired",
            CompactResultError.InvalidSection => "Invalid result section.",
            CompactResultError.OffsetOutOfRange => "Result offset is out of range.",
            _ => throw new UnreachableException()
        };
    }
}
