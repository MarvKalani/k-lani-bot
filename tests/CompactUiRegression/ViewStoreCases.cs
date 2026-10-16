using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// View checks prove retained baselines, complete pages and bounded lifecycle, without creating any desktop surface.
internal static class ViewStoreCases
{
    public static void Run()
    {
        CompletePagesAndCursorAuthority();
        BudgetsAndLifetime();
        RetainedBaselinesAndVisibility();
    }

    private static void CompletePagesAndCursorAuthority()
    {
        var store = new CompactViewStore();
        CompactViewContext context = store.Context(new("a"));
        IReadOnlyList<CompactViewRow> rows = Rows(250);
        Require(store.TryPage(context, rows, "first", new(), out var first), "first page");
        CompactViewPage page = first.Value!;
        CompactViewReference reference = page.ViewReference;
        var collected = new List<CompactViewRow>(page.Elements);
        while (page.NextCursor != null)
        {
            Require(store.TryPage(context, rows, "first", new(Cursor: page.NextCursor), out var next), "next page");
            page = next.Value!;
            Require(page.ViewReference == reference, "same observation frame");
            collected.AddRange(page.Elements);
        }
        Require(collected.Count == 250 && collected.Select(row => row.Reference).Distinct().Count() == 250, "complete unique pages");
        Require(store.TryPage(context, rows, "first", new(SinceView: reference), out var delta), "completed baseline");
        Require(delta.Value!.Kind == CompactViewKind.Delta && delta.Value.Changed.Count == 0, "unchanged delta");
        Require(store.TryPage(context, rows, "first", new(Full: true), out var pending), "reset");
        var cursor = new CompactViewOptions(Cursor: pending.Value!.NextCursor);
        Require(!store.TryPage(store.Context(new("b")), rows, "first", cursor, out var foreign)
            && foreign.Error == CompactViewError.StaleCursor, "foreign cursor");
        Require(!store.TryPage(context, rows, "changed", cursor, out var changed)
            && changed.Error == CompactViewError.StaleCursor, "changed stamp");
    }

    private static void BudgetsAndLifetime()
    {
        var clock = new MutableClock();
        var store = new CompactViewStore(clock);
        CompactViewContext first = store.Context(new("first"));
        Require(store.TryPage(first, Rows(201), "a", new(), out var pending), "pending view");
        Require(store.TryPage(first, Rows(201), "a", new(Full: true), out _), "reset pending view");
        Require(!store.TryPage(first, Rows(201), "a", new(Cursor: pending.Value!.NextCursor), out _), "reset drops cursor");
        for (int index = 0; index < 31; index++)
        {
            store.Context(new(index.ToString()));
        }
        store.Context(new("first"));
        store.Context(new("new"));
        Require(store.Context(new("first")) == first, "LRU keeps touched context");
        Require(store.ContextCount == CompactViewStore.MaximumContexts, "context count cap");
        Require(store.TryPage(store.Context(new("big")), Rows(6000), "b", new(Limit: 200), out _), "first large scope");
        Require(store.TryPage(store.Context(new("other")), Rows(6000), "c", new(Limit: 200), out _), "second large scope");
        Require(!store.TryPage(store.Context(new("oversize")), Rows(10001), "d", new(), out var oversized)
            && oversized.Error == CompactViewError.ScopeTooLarge, "control budget");
        clock.Advance(CompactViewStore.Lifetime);
        store.Expire();
        Require(store.ContextCount == 0 && store.CursorCount == 0, "idle expiry");
    }

    private static void RetainedBaselinesAndVisibility()
    {
        var store = new CompactViewStore();
        CompactViewContext context = store.Context(new("a"));
        CompactViewRow[] before = [new("e1", new JsonObject { ["ref"] = "e1", ["name"] = "A", ["offscreen"] = false })];
        Require(store.TryPage(context, before, "a", new(), out var first), "baseline");
        JsonObject attributes = before[0].Attributes.DeepClone().AsObject();
        attributes["offscreen"] = true;
        CompactViewRow[] after = [new("e1", attributes)];
        Require(store.TryPage(context, after, "b", new(SinceView: first.Value!.ViewReference), out var visibility), "visibility delta");
        Require(visibility.Value!.Changed.Count == 0 && visibility.Value.Visibility.Single().Offscreen, "offscreen is separate");
        Require(store.TryPage(context, after, "c", new(), out _), "third completed view");
        Require(store.TryPage(context, after, "d", new(SinceView: first.Value.ViewReference), out var evicted), "old baseline lookup");
        Require(evicted.Value!.Kind == CompactViewKind.Full
            && evicted.Value.BaselineReason == CompactBaselineReason.UnknownOrIncompleteView, "two-baseline cap");
        store.Clear();
        Require(store.ContextCount == 0 && store.CursorCount == 0, "disconnect clear");
    }

    private static IReadOnlyList<CompactViewRow> Rows(int count)
    {
        return Enumerable.Range(0, count).Select(index => new CompactViewRow("e" + index,
            new JsonObject { ["ref"] = "e" + index, ["name"] = "Field " + index })).ToArray();
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Compact view regression failed: " + name);
        }
    }

    // The deterministic idle clock advances without changing record content or extending cursor authority.
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
        }
    }
}
