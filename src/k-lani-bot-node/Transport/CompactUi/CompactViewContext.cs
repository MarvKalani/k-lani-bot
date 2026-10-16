namespace KLaniBot.Transport.CompactUi;

// A host-owned context retains completed baselines for one view scope; it does not retain native target authority.
public sealed class CompactViewContext(CompactViewKey key)
{
    public CompactViewKey Key { get; } = key;
    public Guid Reference { get; } = Guid.NewGuid();
    public bool ForceFull
    {
        get; set;
    }
    internal DateTimeOffset Touched
    {
        get; set;
    }
    internal IReadOnlyList<CompactViewRow> Records { get; set; } = [];
    internal Dictionary<CompactViewReference, CompactViewBaseline> Baselines { get; } = new();
    internal Queue<CompactViewReference> BaselineOrder { get; } = new();
    internal CompactViewBaseline? Baseline
    {
        get; set;
    }
    internal CompactViewFrame? Frame
    {
        get; set;
    }
}

// Only a completed frame becomes a baseline; partial observations never authorize a delta.
internal sealed record CompactViewBaseline(CompactViewReference Reference, IReadOnlyList<CompactViewRow> Records);

// A frame preserves an ordered set of semantic changes while its pages are delivered under one stamp.
internal sealed class CompactViewFrame(CompactViewReference reference, string stamp, IReadOnlyList<CompactViewItem> items,
    CompactViewKind kind, CompactBaselineReason? reason, int offscreenOmitted)
{
    public CompactViewReference Reference { get; } = reference;
    public string Stamp { get; } = stamp;
    public IReadOnlyList<CompactViewItem> Items { get; } = items;
    public CompactViewKind Kind { get; } = kind;
    public CompactBaselineReason? Reason { get; } = reason;
    public int OffscreenOmitted { get; } = offscreenOmitted;
    public int Offset
    {
        get; set;
    }
}

// A page keeps element, content-change, removal and visibility-change items in the reference ordering.
internal enum CompactViewItemKind
{
    Element,
    Changed,
    Removed,
    Visibility
}

// The frame uses a named variant record rather than string tags or tuple-shaped change data.
internal sealed record CompactViewItem(CompactViewItemKind Kind, CompactViewRow? Row, string Reference, bool? Offscreen = null);

// Cursor metadata binds an opaque token to one pending frame rather than to an application input target.
internal sealed record CompactViewCursorState(CompactViewKey Key, CompactViewReference Reference, int Offset, int Limit);
