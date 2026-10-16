using System.Diagnostics;

namespace KLaniBot.Transport.CompactUi;

// Boundary errors retain the reference gateway's precise reason and do not imply that input reached the application.
public static class CompactTreeNames
{
    public static string Error(CompactTreeError error, string? detail = null)
    {
        return error switch
        {
            CompactTreeError.UnsupportedFormat => "Unsupported native tree format; no element references were issued.",
            CompactTreeError.RootAmbiguousOrAbsent => "Application root is ambiguous or absent. Observed document names: " + detail,
            CompactTreeError.WindowIdentityChanged => "The observed window identity changed or is ambiguous. No input was sent.",
            CompactTreeError.TargetAbsentOrAmbiguous => "Target identity is absent or ambiguous. No input was sent.",
            CompactTreeError.TargetTypeChanged => "Target type changed for the recorded Automation ID.",
            CompactTreeError.TargetDisabled => "Target is disabled. No input was sent.",
            _ => throw new UnreachableException()
        };
    }

    public static string Error(CompactReferenceError error)
    {
        return error switch
        {
            CompactReferenceError.Unknown => "Unknown reference for this application. Observe it first.",
            CompactReferenceError.Ambiguous => "ambiguous_target: observed identity is ambiguous.",
            CompactReferenceError.UnverifiedWindow => "unverified_window: native tree scope cannot be bound to one observed window.",
            _ => throw new UnreachableException()
        };
    }
}
