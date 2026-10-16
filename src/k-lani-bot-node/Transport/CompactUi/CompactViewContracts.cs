using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// The view store separates every verified application/filter scope, or a selected discovery topic, by an opaque host key.
public sealed record CompactViewKey(string Value);

// A completed view can be used as a delta baseline; its opaque identifier grants no application input authority.
public sealed record CompactViewReference(Guid Value);

// A cursor authorizes only the next page of its original scope, frame, stamp and limit.
public sealed record CompactViewCursor(Guid Value);

// View callers distinguish a complete observation from changes against a completed baseline.
public enum CompactViewKind
{
    Full,
    Delta
}

// A full observation explains why no usable completed baseline was selected.
public enum CompactBaselineReason
{
    Reset,
    ContextMismatch,
    UnknownOrIncompleteView,
    NoCompletedBaseline
}

// Expected paging failures stop discovery without granting stale references or repeating any input.
public enum CompactViewError
{
    InvalidPageArguments,
    StaleCursor,
    ScopeTooLarge
}

// Protocol adapters preserve absent fields separately from a present but unknown cursor or baseline reference.
// A full page lists offscreen rows only when IncludeOffscreen is set; a delta reports offscreen changes either way.
public sealed record CompactViewOptions(int? Limit = null, CompactViewReference? SinceView = null,
    CompactViewCursor? Cursor = null, bool Full = false, bool IncludeOffscreen = true);

// Projection supplies an immutable record snapshot; attributes remain untrusted application data at the protocol boundary.
public sealed record CompactViewRow(string Reference, JsonObject Attributes);

// A visibility delta is separate from a semantic content change and never implies that offscreen content is absent.
public sealed record CompactVisibilityChange(string Reference, bool Offscreen);

// Typed view pages retain all changed, removed and visibility items while paging large observations without omissions.
// OffscreenOmitted counts the offscreen rows that a full page left out, so the reply can say how to include them.
public sealed record CompactViewPage(CompactViewReference ViewReference, Guid ContextReference, CompactViewKind Kind,
    CompactViewCursor? NextCursor, int Total, bool Truncated, CompactBaselineReason? BaselineReason,
    IReadOnlyList<CompactViewRow> Elements, IReadOnlyList<CompactViewRow> Changed,
    IReadOnlyList<string> Removed, IReadOnlyList<CompactVisibilityChange> Visibility, int OffscreenOmitted = 0);

// Callers receive a typed expected failure or one view page; ordinary invalid input never requires an exception.
public sealed record CompactViewAttempt(CompactViewPage? Value, CompactViewError? Error);

// MCP serializers translate finite view states in one place to preserve the JS reference vocabulary.
public static class CompactViewNames
{
    // The same words as the JavaScript gateway, so both twins tell the model how to reach the omitted closed menus and hidden elements.
    public const string OffscreenHint = "Pass contains or offscreen:true to include closed menus and hidden elements.";

    // Points the model at the window state view, the route that ui_info always names.
    public const string StateHint = "Read a window as a typed class with ui_observe {pid, window, as: \"state\"}, "
        + "then change it with ui_apply.";
    public const string ApplyHint = "Change it with ui_apply {pid, shape, steps: [{set: path, value}, {call: path}]}.";
    public const string StateCursorHint = "Use a cursor from the more map of the latest state reply.";

    // Named refusals of ui_apply; the wording is the same in the JavaScript twin once it exists.
    public const string ApplyStepsHint =
        "Each step is {set: path, value: text}, {call: path} or {wait: path, until, value?, timeout_ms?}.";
    public const string WaitNotMetHint = "The window did not reach the awaited state; read the state again.";
    public const string ApplyDialogHint = "A dialog opened; change it with ui_apply on its window.";
    public const string ApplyShapeHint = "Unnamed fields are named by position; pass the shape of the state read you used.";
    public const string ApplySetHint = "set accepts text, number and password properties; choice and other members cannot be set yet.";
    public const string ApplyCallHint = "call accepts methods only.";

    public static string Kind(CompactViewKind kind)
    {
        return kind switch
        {
            CompactViewKind.Full => "full",
            CompactViewKind.Delta => "delta",
            _ => throw new UnreachableException()
        };
    }

    public static string Reason(CompactBaselineReason reason)
    {
        return reason switch
        {
            CompactBaselineReason.Reset => "reset",
            CompactBaselineReason.ContextMismatch => "context_mismatch",
            CompactBaselineReason.UnknownOrIncompleteView => "unknown_or_incomplete_view",
            CompactBaselineReason.NoCompletedBaseline => "no_completed_baseline",
            _ => throw new UnreachableException()
        };
    }

    public static string Error(CompactViewError error)
    {
        return error switch
        {
            CompactViewError.InvalidPageArguments => "Invalid view page arguments.",
            CompactViewError.StaleCursor => "stale_cursor",
            CompactViewError.ScopeTooLarge => "view_scope_too_large",
            _ => throw new UnreachableException()
        };
    }
}
