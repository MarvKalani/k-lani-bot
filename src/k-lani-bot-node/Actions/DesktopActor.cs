using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using KLaniBot.Guard;

namespace KLaniBot.Actions;

// Use case: the HTTP endpoint and the stdio worker receive requests on many threads, but a desktop has one
// keyboard, one mouse and one focus. Every request therefore runs on this single thread, in arrival order,
// and the dispatcher with all its state is only ever touched from here. The queue is short on purpose: a
// client that floods the node gets "busy" at once instead of actions that run long after it stopped waiting.
public sealed class DesktopActor : IDisposable
{
    // A request waiting for the desktop thread, with the caller and the completion its answer goes to.
    private sealed record QueuedRequest(JsonObject Request, RequestContext Context, TaskCompletionSource<JsonObject> Result);

    private readonly BlockingCollection<QueuedRequest> _queue = new(16);
    private readonly Thread _thread;
    public DesktopActor(ActionDispatcher dispatcher)
    {
        _thread = new Thread(() =>
        {
            try
            {
                foreach (var job in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        job.Result.SetResult(dispatcher.Execute(job.Request, job.Context));
                    }
                    catch (Exception ex) { job.Result.SetException(ex); }
                }
            }
            finally { dispatcher.Dispose(); }
        })
        {
            IsBackground = true,
            Name = "k-lani-bot desktop actor"
        };
        if (OperatingSystem.IsWindows())
            _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }
    public Task<JsonObject> ExecuteAsync(JsonObject request, RequestContext context)
    {
        var result = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryAdd(new QueuedRequest(request, context, result)))
            return Task.FromResult(ActionResults.Rejected(ActionErrorCode.Busy, "Desktop queue is full."));
        return result.Task;
    }
    public void Dispose() => _queue.CompleteAdding();
}
