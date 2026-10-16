using System.Text;

namespace KLaniBot.Transport.CompactUi;

// A compact gateway retains bounded volatile result evidence for its own node/session, outside every cartridge file.
public sealed class CompactResultStore(TimeProvider? timeProvider = null)
{
    public const int MaximumEntries = 32;
    public const int MaximumBytes = 4 * 1024 * 1024;
    public const int MaximumEntryBytes = 1024 * 1024;
    public const int DefaultPageSize = 2000;
    public const int MaximumPageSize = 8000;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    // One private entry carries its immutable sections, UTF-8 budget and original insertion expiry.
    private sealed record Evidence(CompactResultAuthority Authority, Dictionary<CompactResultSection, string> Sections,
        int Bytes, DateTimeOffset Expires);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<CompactEvidenceReference, Evidence> _entries = new();
    private readonly LinkedList<CompactEvidenceReference> _order = new();
    private readonly Dictionary<CompactEvidenceReference, CompactResultAuthority> _expired = new();
    private readonly Queue<CompactEvidenceReference> _expiryOrder = new();

    public int Bytes
    {
        get; private set;
    }
    public int Count => _entries.Count;

    public bool TryPut(CompactResultAuthority authority, IReadOnlyDictionary<string, string> outputs, string debug,
        out CompactResultAttempt<CompactEvidenceReference> attempt)
    {
        if (authority == null || outputs == null || debug == null || outputs.Any(output => output.Value == null))
        {
            attempt = new(null, CompactResultError.InvalidEvidenceArguments);
            return false;
        }
        var sections = new Dictionary<CompactResultSection, string> { [new(CompactResultSectionKind.Debug)] = debug };
        foreach (KeyValuePair<string, string> output in outputs)
        {
            sections[new(CompactResultSectionKind.Output, output.Key)] = output.Value;
        }
        long size = sections.Values.Sum(text => (long)Encoding.UTF8.GetByteCount(text));
        if (size > MaximumEntryBytes)
        {
            attempt = new(null, CompactResultError.EvidenceTooLarge);
            return false;
        }
        Expire();
        while (_entries.Count >= MaximumEntries || Bytes + size > MaximumBytes)
        {
            Remove(_order.First!.Value);
        }
        var reference = new CompactEvidenceReference(Guid.NewGuid());
        _entries.Add(reference, new(authority, sections, (int)size, _time.GetUtcNow() + Lifetime));
        _order.AddLast(reference);
        Bytes += (int)size;
        attempt = new(reference, null);

        return true;
    }

    public bool TryRead(CompactResultAuthority authority, CompactResultPageQuery query, out CompactResultAttempt<CompactResultPage> attempt)
    {
        if (authority == null || query == null || query.Section == null
            || query.Offset < 0 || query.Limit < 1 || query.Limit > MaximumPageSize)
        {
            attempt = new(null, CompactResultError.InvalidPageArguments);
            return false;
        }
        Expire();
        CompactResultError? error = FindError(authority, query, out string? text);
        if (error != null)
        {
            attempt = new(null, error);
            return false;
        }
        int length = CompactUnicode.Count(text!);
        if (query.Offset > length)
        {
            attempt = new(null, CompactResultError.OffsetOutOfRange);
            return false;
        }
        int next = (int)Math.Min(length, (long)query.Offset + query.Limit);
        var page = new CompactResultPage(CompactUnicode.Slice(text!, query.Offset, next - query.Offset),
            query.Offset, next, next == length, length);
        attempt = new(page, null);

        return true;
    }

    private CompactResultError? FindError(CompactResultAuthority authority, CompactResultPageQuery query, out string? text)
    {
        text = null;
        if (query.Reference == null || !_entries.TryGetValue(query.Reference, out Evidence? entry) || entry.Authority != authority)
        {
            return query.Reference != null && _expired.GetValueOrDefault(query.Reference) == authority
                ? CompactResultError.EvidenceExpired : CompactResultError.EvidenceNotFound;
        }
        return entry.Sections.TryGetValue(query.Section, out text) ? null : CompactResultError.InvalidSection;
    }

    private void Expire()
    {
        foreach (KeyValuePair<CompactEvidenceReference, Evidence> entry in _entries.ToArray())
        {
            if (entry.Value.Expires > _time.GetUtcNow())
            {
                continue;
            }
            _expired[entry.Key] = entry.Value.Authority;
            _expiryOrder.Enqueue(entry.Key);
            Remove(entry.Key);
        }
        while (_expired.Count > MaximumEntries)
        {
            _expired.Remove(_expiryOrder.Dequeue());
        }
    }

    private void Remove(CompactEvidenceReference reference)
    {
        Bytes -= _entries[reference].Bytes;
        _entries.Remove(reference);
        _order.Remove(reference);
    }

    public void Clear()
    {
        _entries.Clear();
        _order.Clear();
        _expired.Clear();
        _expiryOrder.Clear();
        Bytes = 0;
    }
}
