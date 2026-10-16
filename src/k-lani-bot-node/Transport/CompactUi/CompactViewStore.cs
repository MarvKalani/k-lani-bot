namespace KLaniBot.Transport.CompactUi;

// A gateway keeps bounded view baselines and one-use cursors per application/filter context, separate from input authority.
public sealed class CompactViewStore(TimeProvider? timeProvider = null)
{
    public const int MaximumContexts = 32;
    public const int MaximumControls = 10000;
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 200;
    public const int CompletedViews = 2;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<CompactViewKey, CompactViewContext> _contexts = new();
    private readonly LinkedList<CompactViewKey> _order = new();
    private readonly Dictionary<CompactViewCursor, CompactViewCursorState> _cursors = new();

    public int ContextCount => _contexts.Count;
    public int CursorCount => _cursors.Count;

    public bool IsCurrent(CompactViewContext context)
    {
        return context != null && _contexts.TryGetValue(context.Key, out CompactViewContext? current) && ReferenceEquals(context, current);
    }

    public void Invalidate(CompactViewKey key)
    {
        Remove(key);
    }

    public void InvalidateCursors(CompactViewKey key)
    {
        DropCursors(key);
    }

    // Throws ArgumentNullException for an absent host scope key; protocol adapters must construct a validated key first.
    public CompactViewContext Context(CompactViewKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Expire();
        if (!_contexts.TryGetValue(key, out CompactViewContext? context))
        {
            while (_contexts.Count >= MaximumContexts)
            {
                Remove(_order.First!.Value);
            }
            context = new(key);
            _contexts.Add(key, context);
        }
        _order.Remove(key);
        _order.AddLast(key);
        context.Touched = _time.GetUtcNow();

        return context;
    }

    public bool TryPage(CompactViewContext context, IReadOnlyList<CompactViewRow> records, string stamp,
        CompactViewOptions options, out CompactViewAttempt attempt)
    {
        if (context == null || records == null || options == null || stamp == null || !ValidOptions(options)
            || records.Any(row => row == null || row.Reference == null || row.Attributes == null))
        {
            attempt = new(null, CompactViewError.InvalidPageArguments);
            return false;
        }
        if (options.Cursor != null)
        {
            return TryContinue(context, stamp, options, out attempt);
        }
        DropCursors(context.Key);
        if (options.Full || context.ForceFull)
        {
            context.Baselines.Clear();
            context.BaselineOrder.Clear();
            context.Baseline = null;
        }
        CompactBaselineReason? reason = BaselineReason(context, options);
        CompactViewBaseline? baseline = reason != null ? null : SelectedBaseline(context, options);
        CompactViewRow[] captured = records.Select(row => new CompactViewRow(row.Reference,
            row.Attributes.DeepClone().AsObject())).ToArray();
        CompactViewKind kind = baseline != null ? CompactViewKind.Delta : CompactViewKind.Full;
        IReadOnlyList<CompactViewItem> items = baseline != null
            ? CompactViewDelta.Compare(baseline.Records, captured, !options.IncludeOffscreen)
            : FullItems(captured, options.IncludeOffscreen);
        int omitted = baseline == null && !options.IncludeOffscreen ? captured.Count(CompactViewDelta.Offscreen) : 0;
        context.Records = captured;
        context.Frame = new(new(Guid.NewGuid()), stamp, items, kind, reason, omitted);
        if (!TryEnforceControls(context))
        {
            attempt = new(null, CompactViewError.ScopeTooLarge);
            return false;
        }
        attempt = new(TakePage(context, options.Limit ?? DefaultPageSize), null);

        return true;
    }

    private static bool ValidOptions(CompactViewOptions options)
    {
        return (options.Limit == null || options.Limit is >= 1 and <= MaximumPageSize)
            && (options.Cursor == null || options.SinceView == null && !options.Full);
    }

    // Every row of a full frame is listed unless offscreen rows are left out; the baseline keeps them either way.
    private static IReadOnlyList<CompactViewItem> FullItems(IReadOnlyList<CompactViewRow> rows, bool includeOffscreen)
    {
        return rows.Where(row => includeOffscreen || !CompactViewDelta.Offscreen(row))
            .Select(row => new CompactViewItem(CompactViewItemKind.Element, row, row.Reference)).ToArray();
    }

    private CompactBaselineReason? BaselineReason(CompactViewContext context, CompactViewOptions options)
    {
        if (options.Full || context.ForceFull)
        {
            return CompactBaselineReason.Reset;
        }
        if (options.SinceView != null && !context.Baselines.ContainsKey(options.SinceView))
        {
            bool foreign = _contexts.Values.Any(other => other.Key != context.Key && other.Baselines.ContainsKey(options.SinceView));
            return foreign ? CompactBaselineReason.ContextMismatch : CompactBaselineReason.UnknownOrIncompleteView;
        }

        return context.Baseline != null ? null : CompactBaselineReason.NoCompletedBaseline;
    }

    private static CompactViewBaseline? SelectedBaseline(CompactViewContext context, CompactViewOptions options)
    {
        return options.SinceView != null ? context.Baselines.GetValueOrDefault(options.SinceView) ?? context.Baseline : context.Baseline;
    }

    private bool TryContinue(CompactViewContext context, string stamp, CompactViewOptions options, out CompactViewAttempt attempt)
    {
        CompactViewFrame? frame = context.Frame;
        if (!_cursors.TryGetValue(options.Cursor!, out CompactViewCursorState? cursor) || cursor.Key != context.Key
            || frame == null || cursor.Reference != frame.Reference || stamp != frame.Stamp
            || options.Limit != null && options.Limit != cursor.Limit)
        {
            if (stamp != frame?.Stamp)
            {
                DropCursors(context.Key);
            }
            attempt = new(null, CompactViewError.StaleCursor);
            return false;
        }
        _cursors.Remove(options.Cursor!);
        frame.Offset = cursor.Offset;
        attempt = new(TakePage(context, cursor.Limit), null);

        return true;
    }

    private CompactViewPage TakePage(CompactViewContext context, int limit)
    {
        CompactViewFrame frame = context.Frame!;
        CompactViewItem[] page = frame.Items.Skip(frame.Offset).Take(limit).ToArray();
        frame.Offset += page.Length;
        bool complete = frame.Offset >= frame.Items.Count;
        CompactViewCursor? next = null;
        if (complete)
        {
            CompleteBaseline(context, frame);
        }
        else
        {
            next = new(Guid.NewGuid());
            _cursors[next] = new(context.Key, frame.Reference, frame.Offset, limit);
        }

        return new(frame.Reference, context.Reference, frame.Kind, next, context.Records.Count, !complete, frame.Reason,
            page.Where(item => item.Kind == CompactViewItemKind.Element).Select(item => item.Row!).ToArray(),
            page.Where(item => item.Kind == CompactViewItemKind.Changed).Select(item => item.Row!).ToArray(),
            page.Where(item => item.Kind == CompactViewItemKind.Removed).Select(item => item.Reference).ToArray(),
            page.Where(item => item.Kind == CompactViewItemKind.Visibility)
                .Select(item => new CompactVisibilityChange(item.Reference, item.Offscreen!.Value)).ToArray(), frame.OffscreenOmitted);
    }

    private static void CompleteBaseline(CompactViewContext context, CompactViewFrame frame)
    {
        context.Baseline = new(frame.Reference, context.Records);
        context.Baselines[frame.Reference] = context.Baseline;
        context.BaselineOrder.Enqueue(frame.Reference);
        while (context.Baselines.Count > CompletedViews)
        {
            context.Baselines.Remove(context.BaselineOrder.Dequeue());
        }
        context.ForceFull = false;
    }

    private bool TryEnforceControls(CompactViewContext current)
    {
        if (current.Records.Count > MaximumControls)
        {
            Remove(current.Key);
            return false;
        }
        while (_contexts.Values.Sum(RetainedControls) > MaximumControls)
        {
            CompactViewKey key = _order.First!.Value;
            Remove(key);
            if (key == current.Key)
            {
                return false;
            }
        }

        return true;
    }

    private static int RetainedControls(CompactViewContext context)
    {
        return context.Records.Concat(context.Baselines.Values.SelectMany(baseline => baseline.Records))
            .Select(row => row.Reference).Distinct(StringComparer.Ordinal).Count();
    }

    public void Expire()
    {
        foreach (CompactViewContext context in _contexts.Values.ToArray())
        {
            if (_time.GetUtcNow() - context.Touched >= Lifetime)
            {
                Remove(context.Key);
            }
        }
    }

    private void DropCursors(CompactViewKey key)
    {
        foreach (KeyValuePair<CompactViewCursor, CompactViewCursorState> cursor in _cursors.ToArray())
        {
            if (cursor.Value.Key == key)
            {
                _cursors.Remove(cursor.Key);
            }
        }
    }

    private void Remove(CompactViewKey key)
    {
        DropCursors(key);
        _contexts.Remove(key);
        _order.Remove(key);
    }

    public void Clear()
    {
        _contexts.Clear();
        _cursors.Clear();
        _order.Clear();
    }
}
