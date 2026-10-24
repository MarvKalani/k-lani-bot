using System.Net.WebSockets;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Fleet;

namespace KLaniBot.Transport;

// The master's end of one node's WebSocket. One request is in flight at a time and the reply must carry its id,
// so a late answer can never be taken for the answer to the next request. If a request was sent and its reply
// is lost, the whole connection is dropped instead of reused, and the caller gets "unknown".
public sealed class ReverseRelay : IDisposable
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _serial = new(1);
    private readonly CancellationTokenSource _closed = new();
    private TaskCompletionSource<JsonObject>? _reply;
    private string? _requestId;
    private int _queued;
    private long _lastContactTicks = DateTimeOffset.UtcNow.UtcTicks;

    public ReverseRelay(WebSocket socket)
    {
        _socket = socket;
    }

    // Throws InvalidDataException for an answer nobody waits for.
    public async Task ReadAsync(CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _closed.Token);
        try
        {
            while (await PairingProtocol.ReceiveAsync(_socket, lifetime.Token) is { } message)
            {
                if (message.Count != 2 || message["id"]?.ToString() != _requestId || message["result"] is not JsonObject result)
                {
                    throw new InvalidDataException("Unexpected relay response.");
                }
                Interlocked.Exchange(ref _lastContactTicks, DateTimeOffset.UtcNow.UtcTicks);
                _reply?.TrySetResult(result);
            }
        }
        finally
        {
            _closed.Cancel();
            _reply?.TrySetException(new IOException("Node disconnected."));
        }
    }

    // When the node connected or last answered a call.
    public DateTimeOffset LastContact => new(Interlocked.Read(ref _lastContactTicks), TimeSpan.Zero);

    public async Task<JsonObject> CallAsync(JsonObject message, CancellationToken cancellation, TimeSpan? timeout = null)
    {
        if (Interlocked.Increment(ref _queued) > 16)
        {
            Interlocked.Decrement(ref _queued);
            return ActionResults.Failure(ActionOutcome.NotDispatched, "Node queue full.");
        }
        return await CallQueuedAsync(message, cancellation, timeout);
    }

    // For background polls: sends only when no call is running or waiting, so a poll never delays an operator's action.
    // Returns null when the relay was busy. An action that arrives meanwhile waits behind this one short call.
    public async Task<JsonObject?> CallIfIdleAsync(JsonObject message, CancellationToken cancellation, TimeSpan timeout)
    {
        if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0)
        {
            return null;
        }
        return await CallQueuedAsync(message, cancellation, timeout);
    }

    // The caller has already counted itself in _queued; this releases that count.
    private async Task<JsonObject> CallQueuedAsync(JsonObject message, CancellationToken cancellation, TimeSpan? timeout)
    {
        bool entered = false;
        bool dispatched = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _closed.Token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(65));
        try
        {
            await _serial.WaitAsync(deadline.Token);
            entered = true;
            deadline.Token.ThrowIfCancellationRequested();
            _requestId = Guid.CreateVersion7().ToString();
            _reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            message["id"] = _requestId;
            dispatched = true;
            await PairingProtocol.SendAsync(_socket, message, deadline.Token);
            return await _reply.Task.WaitAsync(deadline.Token);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or WebSocketException)
        {
            return LostReply(dispatched);
        }
        finally
        {
            if (entered)
            {
                _reply = null;
                _requestId = null;
                _serial.Release();
            }
            Interlocked.Decrement(ref _queued);
        }
    }

    // A lost reply may follow a completed OS action. Close this stream; never replay it.
    private JsonObject LostReply(bool dispatched)
    {
        if (dispatched)
        {
            _closed.Cancel();
            _socket.Abort();
        }

        ActionOutcome outcome = dispatched ? ActionOutcome.Unknown : ActionOutcome.NotDispatched;
        return ActionResults.Failure(outcome, "Relay unavailable; verify the result before another operation.");
    }

    public void Dispose()
    {
        _closed.Cancel();
        _socket.Abort();
        _reply?.TrySetException(new IOException("Relay closed."));
        // Waiting calls may still be unwinding; do not dispose their synchronization objects.
    }
}
