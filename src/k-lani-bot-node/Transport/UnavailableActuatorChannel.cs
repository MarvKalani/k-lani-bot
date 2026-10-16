using System.Text.Json.Nodes;

namespace KLaniBot.Transport;

// A worker executable that cannot start leaves the authenticated supervisor alive. This channel can never accept
// desktop input; it represents the missing worker until an update or restart repairs the executable.
internal sealed class UnavailableActuatorChannel(string diagnostic) : IActuatorChannel
{
    public bool HasExited => true;
    public int ExitCode => 1;

    public Task WriteLineAsync(string line, CancellationToken cancellation)
    {
        return Task.FromException(new IOException("Desktop worker could not start."));
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellation)
    {
        return Task.FromResult<string?>(null);
    }

    public Task WaitForExitAsync(CancellationToken cancellation)
    {
        return Task.CompletedTask;
    }

    public string? DescribeFailure()
    {
        return diagnostic;
    }

    public void Terminate()
    {
    }

    public void Dispose()
    {
    }
}
