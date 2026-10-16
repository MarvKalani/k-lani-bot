using KLaniBot.Transport.CompactUi;

// The parity regression exercises Unicode, node/session authority, exact expiry and bounded result retention.
internal static class ResultStoreCases
{
    public static void Run()
    {
        UnicodePages();
        AuthorityAndExpiry();
        BudgetsAndInvalidPages();
    }

    private static void UnicodePages()
    {
        var authority = new CompactResultAuthority("node", Guid.NewGuid());
        var store = new CompactResultStore();
        string text = string.Concat(Enumerable.Repeat("Größe😀\n", 1600));
        Require(store.TryPut(authority, new Dictionary<string, string> { ["document"] = text }, "{}", out var stored), "put");
        var reconstructed = new System.Text.StringBuilder();
        int offset = 0;
        CompactResultSection section = CompactResultSection.Parse("outputs/document");
        bool complete;
        do
        {
            Require(store.TryRead(authority, new(stored.Value, section, offset, 2000), out var read), "page");
            CompactResultPage page = read.Value!;
            reconstructed.Append(page.Text);
            Require(CompactUnicode.Count(page.Text) == Math.Min(2000, page.TotalCharacters - offset), "codepoint page");
            offset = page.NextOffset;
            complete = page.Complete;
        }
        while (!complete);
        Require(reconstructed.ToString() == text, "exact Unicode reconstruction");
        Require(store.TryRead(authority, new(stored.Value, section, offset, 2000), out var final), "end page");
        Require(final.Value!.Text == "" && final.Value.Complete, "empty complete final page");
    }

    private static void AuthorityAndExpiry()
    {
        var clock = new MutableClock();
        var store = new CompactResultStore(clock);
        var owner = new CompactResultAuthority("node", Guid.NewGuid());
        Require(store.TryPut(owner, new Dictionary<string, string> { ["value"] = "private" }, "{}", out var stored), "put");
        var query = new CompactResultPageQuery(stored.Value, CompactResultSection.Parse("outputs/value"), 0, 2000);
        Require(!store.TryRead(owner with
        {
            Node = "foreign"
        }, query, out var foreign)
            && foreign.Error == CompactResultError.EvidenceNotFound, "node authority");
        Require(!store.TryRead(owner with
        {
            Session = Guid.NewGuid()
        }, query, out _), "session authority");
        clock.Advance(CompactResultStore.Lifetime - TimeSpan.FromMilliseconds(1));
        Require(store.TryRead(owner, query, out _), "before TTL");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Require(!store.TryRead(owner, query, out var expired) && expired.Error == CompactResultError.EvidenceExpired, "exact TTL");
        Require(store.Bytes == 0, "expired bytes removed");
        store.Clear();
        Require(!store.TryRead(owner, query, out var cleared) && cleared.Error == CompactResultError.EvidenceNotFound, "disconnect clear");
    }

    private static void BudgetsAndInvalidPages()
    {
        var owner = new CompactResultAuthority("node", Guid.NewGuid());
        var store = new CompactResultStore();
        Require(store.TryPut(owner, new Dictionary<string, string>(), "first", out var first), "put");
        for (int index = 0; index < CompactResultStore.MaximumEntries; index++)
        {
            Require(store.TryPut(owner, new Dictionary<string, string>(), index.ToString(), out _), "entry budget");
        }
        Require(store.Count == CompactResultStore.MaximumEntries, "entry cap");
        Require(!store.TryRead(owner, new(first.Value, CompactResultSection.Parse("debug"), 0, 1), out _), "FIFO eviction");
        store.Clear();
        string full = new('ä', CompactResultStore.MaximumEntryBytes / 2);
        for (int index = 0; index < 5; index++)
        {
            Require(store.TryPut(owner, new Dictionary<string, string>(), full, out _), "byte budget");
        }
        Require(store.Bytes == CompactResultStore.MaximumBytes && store.Count == 4, "UTF-8 total cap");
        Require(!store.TryPut(owner, new Dictionary<string, string>(), full + "x", out var oversized)
            && oversized.Error == CompactResultError.EvidenceTooLarge, "entry byte cap");
        Require(!store.TryRead(owner, new(first.Value, CompactResultSection.Parse("debug"), -1, 1), out var invalid)
            && invalid.Error == CompactResultError.InvalidPageArguments, "negative offset");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Compact regression failed: " + name);
        }
    }

    // Deterministic tests advance the same clock across reads to prove that reads do not extend evidence lifetime.
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
