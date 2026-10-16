using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace KLaniBot.Actions;

// Why a request can be refused before anything reaches the operating system.
public sealed class OperationWindowException(ActionErrorCode code, string message) : ArgumentException(message)
{
    public ActionErrorCode Code { get; } = code;
}

// What the node answered to one operation, and the hash of the request it answered, so that a retry with different
// content under the same id can be told apart from a genuine retry.
public sealed record RecordedOperation(string Hash, JsonObject Result);

// Use case: an agent sends a mutating request, the answer is lost, and the agent asks again. The node
// must answer the retry from the record instead of clicking or typing a second time. That is only
// possible for operations it still remembers, so this class decides how long and how many it keeps.
//
// Why a time window and not a fixed number of entries: a UUIDv7 carries its creation time in the first
// 48 bits. A request id older than the window is refused, so it does not have to be remembered to be
// safe. This keeps memory bounded and lets the node run indefinitely. The earlier fixed cap of 2048
// operations ended all mutations permanently on a node with persistent state, and every click, text
// entry and macro step counts as one operation.
//
// Evidence is unaffected: the audit journal keeps every entry. Only the in-memory view of recent
// operations is bounded.
public sealed class OperationLedger
{
    // An operation key and when its request id was created, queued in creation order so expired entries leave from the front.
    private sealed record PendingExpiry(string Key, long CreatedMilliseconds);

    public const int MaximumEntries = 200_000;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(30);
    // Clocks of controller and node differ a little. Anything further ahead is refused, because a far
    // future id would otherwise be remembered (and block its own replay window) for a very long time.
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, RecordedOperation> _entries = new();
    private readonly Queue<PendingExpiry> _insertionOrder = new();
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;

    // Throws ArgumentOutOfRangeException for a window outside 1 hour to 30 days.
    public OperationLedger(TimeSpan window, TimeProvider? clock = null)
    {
        if (window < MinimumWindow || window > MaximumWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "The operation window must be between 1 hour and 30 days.");
        }
        _window = window;
        _clock = clock ?? TimeProvider.System;
    }

    public int Count => _entries.Count;

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out RecordedOperation entry)
    {
        return _entries.TryGetValue(key, out entry);
    }

    // Checked before the node records or performs anything for a new mutating request.
    // Throws OperationWindowException when the id is from the future, older than the window, or the ledger is full.
    public void RequireAdmissible(string requestId)
    {
        RemoveExpired();
        long created = ReadCreationMilliseconds(requestId);
        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (created > now + (long)FutureTolerance.TotalMilliseconds)
        {
            throw new OperationWindowException(
                ActionErrorCode.RequestIdFromFuture,
                "The request_id lies in the future. Check the clock of the controller and of this node.");
        }
        if (created < now - (long)_window.TotalMilliseconds)
        {
            throw new OperationWindowException(
                ActionErrorCode.RequestIdExpired,
                "The request_id is older than the retention window. Use a new request_id; earlier outcomes are in the audit journal.");
        }
        if (_entries.Count >= MaximumEntries)
        {
            throw new OperationWindowException(
                ActionErrorCode.OperationLedgerFull,
                "Too many operations inside the retention window; no operation was started.");
        }
    }

    // First insertion of a key remembers it for the window; a later call for the same key only updates the result.
    public void Remember(string key, string hash, JsonObject result)
    {
        if (!_entries.ContainsKey(key))
        {
            _insertionOrder.Enqueue(new PendingExpiry(key, ReadCreationMillisecondsOrNow(key)));
        }
        _entries[key] = new RecordedOperation(hash, result);
    }

    // Used when the journal is replayed at startup: operations outside the window are not loaded at all.
    public void Restore(string key, string hash, JsonObject result)
    {
        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        long created = ReadCreationMillisecondsOrNow(key);
        if (created < now - (long)_window.TotalMilliseconds)
        {
            return;
        }
        Remember(key, hash, result);
    }

    private void RemoveExpired()
    {
        long threshold = _clock.GetUtcNow().ToUnixTimeMilliseconds() - (long)_window.TotalMilliseconds;
        while (_insertionOrder.TryPeek(out var oldest) && oldest.CreatedMilliseconds < threshold)
        {
            _insertionOrder.Dequeue();
            _entries.Remove(oldest.Key);
        }
    }

    // The key is "principal:request_id"; principal ids may contain a colon, request ids do not.
    private static string RequestIdOf(string key)
    {
        return key[(key.LastIndexOf(':') + 1)..];
    }

    private long ReadCreationMillisecondsOrNow(string key)
    {
        return Uuid7Time.TryReadMilliseconds(RequestIdOf(key), out long milliseconds)
            ? milliseconds
            : _clock.GetUtcNow().ToUnixTimeMilliseconds();
    }

    // Throws OperationWindowException when the id is not a UUIDv7.
    private static long ReadCreationMilliseconds(string requestId)
    {
        if (!Uuid7Time.TryReadMilliseconds(requestId, out long milliseconds))
        {
            throw new OperationWindowException(ActionErrorCode.InvalidRequestId, "Mutations require an RFC 9562 UUIDv7 request_id.");
        }
        return milliseconds;
    }
}

// Reads the creation time that RFC 9562 puts into the first 48 bits of a UUIDv7.
public static class Uuid7Time
{
    public static bool TryReadMilliseconds(string text, out long milliseconds)
    {
        milliseconds = 0;
        if (!Guid.TryParse(text, out Guid guid) || guid.Version != 7)
        {
            return false;
        }
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes, bigEndian: true, out _);
        for (int index = 0; index < 6; index++)
        {
            milliseconds = (milliseconds << 8) | bytes[index];
        }
        return true;
    }
}
