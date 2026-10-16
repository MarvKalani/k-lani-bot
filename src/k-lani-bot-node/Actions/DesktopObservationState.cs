namespace KLaniBot.Actions;

// Use case: an AI reads the screen, then clicks on something it saw. The element numbers it was given are
// only meaningful for that reading, because the platform backends reuse them. This class remembers which
// reading is current, which session owns the desktop, and a few recent readings for "what changed since".
//
// A reference is valid only together with the snapshot it came from. A new reading, by anyone, makes
// every older reference stale; a new session owner starts with no snapshot at all.
internal sealed class DesktopObservationState
{
    private const int MaximumRememberedObservations = 32;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, string> _observations = new();
    private readonly Queue<string> _observationOrder = new();
    private string? _leaseOwner;
    private DateTimeOffset _leaseUntil;

    public string Snapshot { get; private set; } = "";
    public string ChapterSnapshot { get; private set; } = "";

    // The process whose chapters were last listed. A guarded principal may only inspect sections of that
    // process, whatever process the client names later.
    public int? ChapterProcess
    {
        get; private set;
    }

    public string IssueSnapshot()
    {
        Snapshot = Guid.NewGuid().ToString();
        return Snapshot;
    }

    public string IssueChapterSnapshot(int? process)
    {
        ChapterProcess = process;
        ChapterSnapshot = Guid.NewGuid().ToString();
        return ChapterSnapshot;
    }

    // The tree text is kept so that a later reading can be compared with it. Only the newest readings are
    // kept, so a long session cannot grow the node's memory without bound.
    public void RememberObservation(string snapshot, string tree)
    {
        _observations[snapshot] = tree;
        _observationOrder.Enqueue(snapshot);
        while (_observationOrder.Count > MaximumRememberedObservations)
        {
            _observations.Remove(_observationOrder.Dequeue());
        }
    }

    public bool TryGetObservation(string snapshot, out string tree)
    {
        bool found = _observations.TryGetValue(snapshot, out string? remembered);
        tree = remembered ?? "";
        return found;
    }

    // One session at a time drives the desktop, so two clients cannot type into each other's windows.
    // A new owner starts without any snapshot, because the old owner's references mean nothing to it.
    // Throws UnauthorizedAccessException while another session holds the lease.
    public void TakeLease(string owner, DateTimeOffset now)
    {
        if (_leaseOwner != null && _leaseOwner != owner && _leaseUntil > now)
        {
            throw new UnauthorizedAccessException("Desktop leased to another session.");
        }
        if (_leaseOwner != owner)
        {
            Snapshot = "";
            ChapterSnapshot = "";
        }
        _leaseOwner = owner;
        _leaseUntil = now + LeaseDuration;
    }

    // A session may end only its own lease. Ending it also forgets the snapshots, which were its own.
    // Throws UnauthorizedAccessException when the requester does not hold the lease.
    public void ReleaseLease(string requester)
    {
        if (_leaseOwner != null && _leaseOwner != requester)
        {
            throw new UnauthorizedAccessException("Cannot end another desktop session.");
        }
        _leaseOwner = null;
        Snapshot = "";
        ChapterSnapshot = "";
    }

    public string SnapshotFor(bool section)
    {
        return section ? ChapterSnapshot : Snapshot;
    }
}
