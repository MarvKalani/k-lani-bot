using System.Diagnostics;

namespace KLaniBot.Transport.CompactUi;

// Discovery messages preserve the reference contract while keeping unknown causes and non-executable status explicit.
public static class CompactDiscoveryNames
{
    public const string ActionStatus = "Suggested from observed role; not verified or executable.";
    public const string OffscreenMeaning = "Provider offscreen flag; does not prove conditional visibility.";
    public const string Coverage = "Observed states only; unvisited controls and transitions remain unknown.";
    public const string TransitionCause = "unknown; successive observations do not establish causality";
    public const string StoredStatus = "Discovery material only; no executable function or fresh target verification.";
    public static IReadOnlyList<string> Unknown
    {
        get;
    } = Array.AsReadOnly(new[]
    {
        "required", "read_only", "constraints", "selection_options", "trigger_conditions", "invoke_effect"
    });

    public static string Error(CompactDiscoveryError error, string? detail = null)
    {
        return error switch
        {
            CompactDiscoveryError.InvalidArguments => detail
                ?? "Select a bounded form name, observed PID/window, and optional document root/content limit.",
            CompactDiscoveryError.InvalidLookup => "Invalid form lookup.",
            CompactDiscoveryError.WindowAbsentOrAmbiguous => "Native window scope is absent or ambiguous; "
                + "specify an observed tree window. " + detail,
            CompactDiscoveryError.RootAbsentOrAmbiguous => CompactTreeNames.Error(CompactTreeError.RootAmbiguousOrAbsent, detail),
            CompactDiscoveryError.ScopeAbsent => "The selected window/document is absent.",
            CompactDiscoveryError.ControlBoundExceeded => "Form scope exceeds 200 controls; narrow its document root.",
            CompactDiscoveryError.StateBoundExceeded => "Observed state limit reached; narrow the form scope.",
            CompactDiscoveryError.UnknownState => "Unknown observed form/state. Scan the selected form first.",
            CompactDiscoveryError.InvalidDocument => "Observed form identity or format does not match.",
            CompactDiscoveryError.DocumentBoundExceeded => "Observed discovery document exceeds its storage bound.",
            CompactDiscoveryError.InvalidPath => "Use an absolute private discovery directory without symbolic links.",
            CompactDiscoveryError.StorageUnavailable => detail ?? "Private discovery storage is unavailable.",
            CompactDiscoveryError.StorageChanged => "Observed form storage changed; reload before recording another state.",
            _ => throw new UnreachableException()
        };
    }
}
