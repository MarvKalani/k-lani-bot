using System.Diagnostics;

namespace KLaniBot.Transport.CompactUi;

// Workflow boundaries preserve the established finite value and error vocabulary across native and reference runtimes.
public static class CompactWorkflowNames
{
    public static string Error(CompactWorkflowError error, string? detail = null)
    {
        return error switch
        {
            CompactWorkflowError.UnsupportedFields => "Workflow contracts reject unsupported fields, paths, "
                + "runtime IDs and executable expressions.",
            CompactWorkflowError.InvalidIdentity => "Invalid workflow identity/revision.",
            CompactWorkflowError.InvalidBounds => "A workflow needs bounded typed inputs, applications, stages and outputs.",
            CompactWorkflowError.InvalidInputName => "Invalid workflow input name.",
            CompactWorkflowError.InvalidValueType => "Invalid workflow value type.",
            CompactWorkflowError.InvalidEnum => "Invalid enum choices.",
            CompactWorkflowError.InvalidLength => "Invalid bounded input length.",
            CompactWorkflowError.InvalidNumericBound => "Invalid numeric bound.",
            CompactWorkflowError.InvalidNumericRange => "Invalid numeric range.",
            CompactWorkflowError.InvalidApplication => "Invalid application contract.",
            CompactWorkflowError.InvalidStage => "Invalid or duplicate workflow stage.",
            CompactWorkflowError.UnknownFunction => "Unknown or unverified workflow function.",
            CompactWorkflowError.InvalidConditions => "Stages require fresh target checks and the verified function postconditions.",
            CompactWorkflowError.InvalidStageBindings => "Stage bindings do not match the function contract.",
            CompactWorkflowError.BindingTypeMismatch => "Binding type does not match its function parameter.",
            CompactWorkflowError.InvalidDecision => "Invalid explicit workflow decision.",
            CompactWorkflowError.InvalidOutputName => "Invalid workflow output name.",
            CompactWorkflowError.UndeclaredInput => "Binding names an undeclared input.",
            CompactWorkflowError.UnknownPriorOutput => "Binding refers forward, forms a cycle, or names an unconfirmed/unknown output.",
            CompactWorkflowError.MissingParameter => "Missing workflow parameter: " + detail,
            CompactWorkflowError.ParameterTypeMismatch => "Workflow parameter does not match its declared type.",
            CompactWorkflowError.InvalidDocument => "Use an opaque document reference, digest and provenance.",
            CompactWorkflowError.UnconfirmedOutput => "A workflow binding requires a confirmed prior output.",
            _ => throw new UnreachableException()
        };
    }

    public static string Kind(CompactWorkflowValueKind kind)
    {
        return kind switch
        {
            CompactWorkflowValueKind.String => "string",
            CompactWorkflowValueKind.Number => "number",
            CompactWorkflowValueKind.Boolean => "boolean",
            CompactWorkflowValueKind.Enum => "enum",
            CompactWorkflowValueKind.Document => "document",
            _ => throw new UnreachableException()
        };
    }

    public static bool TryKind(string? value, out CompactWorkflowValueKind kind)
    {
        foreach (CompactWorkflowValueKind candidate in Enum.GetValues<CompactWorkflowValueKind>())
        {
            if (Kind(candidate) == value)
            {
                kind = candidate;
                return true;
            }
        }
        kind = CompactWorkflowValueKind.String;

        return false;
    }
}
