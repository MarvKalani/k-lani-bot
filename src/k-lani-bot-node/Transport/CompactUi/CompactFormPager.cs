namespace KLaniBot.Transport.CompactUi;

// Form cursors bind one use to an exact context/selection/options scope; stale or changed page requests are refused.
public sealed class CompactFormPager
{
    public const int MaximumCursors = 32;
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 100;

    // One pending page retains its form owner and exact binding, independently of application input authority.
    private sealed record Cursor(string Binding, CompactFormReference Form, int Offset, int Limit);

    private readonly Dictionary<CompactFormCursor, Cursor> _cursors = new();
    private readonly LinkedList<CompactFormCursor> _order = new();
    public int Count => _cursors.Count;

    public bool TryPage<T>(string binding, IReadOnlyList<T> items, CompactFormPageOptions options, CompactFormReference form,
        out CompactFormAttempt<CompactFormPage<T>> attempt)
    {
        if (options.Limit is < 1 or > MaximumPageSize)
        {
            attempt = new(null, CompactFormError.InvalidArguments);
            return false;
        }
        int offset = 0;
        int limit = options.Limit ?? DefaultPageSize;
        if (options.Cursor != null)
        {
            if (!_cursors.TryGetValue(options.Cursor, out Cursor? cursor) || cursor.Binding != binding
                || options.Limit != null && options.Limit != cursor.Limit)
            {
                attempt = new(null, CompactFormError.StaleCursor);
                return false;
            }
            offset = cursor.Offset;
            limit = cursor.Limit;
            Remove(options.Cursor);
        }
        T[] page = items.Skip(offset).Take(limit).ToArray();
        bool complete = offset + page.Length >= items.Count;
        CompactFormCursor? next = complete ? null : Add(new(binding, form, offset + page.Length, limit));
        attempt = new(new(page, complete, next), null);

        return true;
    }

    private CompactFormCursor Add(Cursor state)
    {
        while (_cursors.Count >= MaximumCursors)
        {
            Remove(_order.First!.Value);
        }
        var reference = new CompactFormCursor(Guid.NewGuid());
        _cursors.Add(reference, state);
        _order.AddLast(reference);

        return reference;
    }

    public void Drop(CompactFormReference form)
    {
        foreach (KeyValuePair<CompactFormCursor, Cursor> cursor in _cursors.ToArray())
        {
            if (cursor.Value.Form == form)
            {
                Remove(cursor.Key);
            }
        }
    }

    private void Remove(CompactFormCursor reference)
    {
        _cursors.Remove(reference);
        _order.Remove(reference);
    }

    public void Clear()
    {
        _cursors.Clear();
        _order.Clear();
    }
}
