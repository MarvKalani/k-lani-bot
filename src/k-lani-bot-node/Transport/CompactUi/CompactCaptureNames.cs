using System.Diagnostics;

namespace KLaniBot.Transport.CompactUi;

// Capture keeps the reference protocol's exact finite route, action and failure names at the boundary.
public static class CompactCaptureNames
{
    public static string Error(CompactCaptureError error)
    {
        return error switch
        {
            CompactCaptureError.EvidenceMissingOrUnknown => "The evidence token is missing or unknown; "
                + "pass the evidence value from the latest successful ui_act reply.",
            CompactCaptureError.InvalidNameOrTopic => "Invalid function name or topic. The name must match "
                + "[a-z][a-z0-9._-]{0,79}; the topic must be Category/topic with Category Administration, Use, Info "
                + "or Programming, for example Use/calculator.",
            CompactCaptureError.InvalidDescription => "The description must be 1 to 200 characters without control characters.",
            CompactCaptureError.UnknownArgument => "Unknown learning argument.",
            CompactCaptureError.InvalidParameters => "Name every write input exactly once. Literal training values are never persisted.",
            CompactCaptureError.UnsupportedSequence => "Standalone key sequences require an explicit focus recipe.",
            CompactCaptureError.InvalidOutputs => "Supply 1–8 observed outputs.",
            CompactCaptureError.InvalidOutput => "Invalid or duplicate output.",
            CompactCaptureError.InvalidPrefix => "Use a bounded literal result prefix.",
            CompactCaptureError.OwningWindowMissing => "Learning requires an observed owning window identity.",
            CompactCaptureError.UnknownReference => "Unknown reference for this application. Observe it first.",
            CompactCaptureError.ForeignReference => "Capture references must belong to the demonstrated application.",
            CompactCaptureError.InvalidSelector => "Generated selectors require an owning window and contain no IDs or coordinates.",
            CompactCaptureError.OutputAbsent => "Output is absent; completion is unverified.",
            CompactCaptureError.OutputAmbiguous => "Output is ambiguous; completion is unverified.",
            CompactCaptureError.TargetTypeChanged => "Target type changed for the recorded Automation ID.",
            CompactCaptureError.WindowAmbiguous => "Window identity is ambiguous.",
            CompactCaptureError.PrefixMismatch => "Result prefix does not match the observed output.",
            CompactCaptureError.OutputUnchanged => "The output did not change; dispatched input alone cannot validate a function.",
            CompactCaptureError.WrittenValueMismatch => "The observed value does not match the written input.",
            CompactCaptureError.InvalidRead => "Invalid observed read capture.",
            CompactCaptureError.EmptyRead => "A nonempty observed source is required.",
            CompactCaptureError.InvalidDefinition => "Invalid generated function definition.",
            CompactCaptureError.PostconditionFailed => "Observed output did not satisfy the learned postcondition.",
            CompactCaptureError.OutputBoundExceeded => "Output exceeds the verified bounded text contract.",
            CompactCaptureError.GoalOutputRequired => "A goal-specific output check is still required; "
                + "the semantic target is available as material.",
            CompactCaptureError.NavigationHeadingAmbiguous => "No unambiguous matching page heading verifies this navigation.",
            CompactCaptureError.CapturedScopeChanged => "Captured function scope changed; revalidate its existing definition before reuse.",
            _ => throw new UnreachableException()
        };
    }

    public static string Action(CompactRecipeAction action)
    {
        return action switch
        {
            CompactRecipeAction.Click => "click",
            CompactRecipeAction.Write => "write",
            CompactRecipeAction.Select => "select",
            CompactRecipeAction.Read => "read",
            CompactRecipeAction.Key => "key",
            _ => throw new UnreachableException()
        };
    }

    public static string Route(CompactRecipeRoute route)
    {
        return route switch
        {
            CompactRecipeRoute.Semantic => "semantic",
            CompactRecipeRoute.NativeText => "native-text",
            CompactRecipeRoute.NativeChoice => "native-choice",
            CompactRecipeRoute.NativeRead => "native-read",
            CompactRecipeRoute.FocusKeyboard => "semantic-focus+keyboard (native editable interface unavailable)",
            _ => throw new UnreachableException()
        };
    }

    public static string Field(CompactOutputField field)
    {
        return field switch
        {
            CompactOutputField.Name => "name",
            CompactOutputField.Value => "value",
            _ => throw new UnreachableException()
        };
    }

    public static string Check(CompactOutputCheckKind check)
    {
        return check switch
        {
            CompactOutputCheckKind.Changed => "changed",
            CompactOutputCheckKind.EqualsParameter => "equals_parameter",
            CompactOutputCheckKind.Exists => "exists",
            CompactOutputCheckKind.NotEmpty => "not_empty",
            _ => throw new UnreachableException()
        };
    }

    public static string Topic(CompactCartridgeTopic topic)
    {
        string category = topic.Category switch
        {
            CompactTopicCategory.Administration => "Administration",
            CompactTopicCategory.Use => "Use",
            CompactTopicCategory.Info => "Info",
            CompactTopicCategory.Programming => "Programming",
            _ => throw new UnreachableException()
        };

        return category + "/" + topic.Branch;
    }
}
