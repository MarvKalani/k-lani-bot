namespace KLaniBot.Startup;

// Ctrl+C in a terminal, or the stop signal of a service manager, ends a long-running command cleanly
// instead of killing the process in the middle of a write. The handler is removed again on dispose,
// so a late signal cannot reach a token source that no longer exists.
public sealed class ConsoleCancellation : IDisposable
{
    private readonly CancellationTokenSource _source = new();

    private ConsoleCancellation()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    public CancellationToken Token => _source.Token;

    public static ConsoleCancellation Create()
    {
        return new ConsoleCancellation();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs signal)
    {
        signal.Cancel = true;
        _source.Cancel();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        _source.Dispose();
    }
}
