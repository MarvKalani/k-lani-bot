using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Common;
using KLaniBot.Guard;

namespace KLaniBot.Transport;

// Use case: the supervisor talks to exactly one actuator, the process that owns the desktop (or the
// Docker container that does). The protocol is one JSON line out, one JSON line back, with no frame
// numbers. This interface is the whole surface the supervisor needs, so its protocol rules can be
// tested with a scripted channel instead of a real process.
public interface IActuatorChannel : IDisposable
{
    Task WriteLineAsync(string line, CancellationToken cancellation);

    // Null means the actuator closed its output, that is, it exited.
    Task<string?> ReadLineAsync(CancellationToken cancellation);

    Task WaitForExitAsync(CancellationToken cancellation);

    bool HasExited
    {
        get;
    }

    int ExitCode
    {
        get;
    }

    // Ends the actuator for good, including its container if it runs in one.
    void Terminate();

    // Extra facts for the failure log (for example the container state), or null.
    string? DescribeFailure();
}

// The supervisor talks to an actuator started as a process (or a container) over its standard input and output.
public sealed class ProcessActuatorChannel : IActuatorChannel
{
    private readonly Process _process;
    private readonly JsonLineReader _reader;
    private readonly ManagedContainer? _container;

    // A container this node started by name, and the docker executable that started it.
    private sealed record ManagedContainer(string Name, string DockerExecutable);

    private ProcessActuatorChannel(Process process, ManagedContainer? container)
    {
        _process = process;
        _reader = new JsonLineReader(process.StandardOutput.BaseStream);
        _container = container;
    }

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.HasExited ? _process.ExitCode : 1;

    // Throws ArgumentException for an empty command and InvalidOperationException when the process does not start.
    public static ProcessActuatorChannel Start(IReadOnlyList<string> command, NodePolicy policy)
    {
        if (command.Count == 0)
        {
            throw new ArgumentException("Actuator command required.");
        }
        var start = new ProcessStartInfo
        {
            FileName = command[0],
            WorkingDirectory = policy.WorkspaceRoot,
            UseShellExecute = false,
            CreateNoWindow = OperatingSystem.IsWindows(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        foreach (string argument in command.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }
        var platform = OperatingSystem.IsWindows() ? WorkerPlatform.Windows : WorkerPlatform.Unix;
        WorkerEnvironment.Configure(start, policy.StateRoot, platform);
        ManagedContainer? container = FindManagedContainer(command);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Actuator failed to start.");
        return new ProcessActuatorChannel(process, container);
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellation)
    {
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellation);
        await _process.StandardInput.FlushAsync(cancellation);
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellation)
    {
        return _reader.ReadAsync(cancellation);
    }

    public Task WaitForExitAsync(CancellationToken cancellation)
    {
        return _process.WaitForExitAsync(cancellation);
    }

    public void Terminate()
    {
        if (_container != null)
        {
            ProcessRunner.Run(_container.DockerExecutable, ["rm", "-f", _container.Name], 10000);
        }
        try
        {
            _process.Kill(true);
            _process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // The process had already exited; there is nothing left to end.
        }
    }

    public string? DescribeFailure()
    {
        if (_container == null)
        {
            return null;
        }
        var state = ProcessRunner.Run(_container.DockerExecutable, ["inspect", "--format", "{{json .State}}", _container.Name], 2000);
        return state.Success ? "Actuator container state: " + state.Output.Trim() : null;
    }

    public void Dispose()
    {
        _process.Dispose();
    }

    // Only a container this node started by name is removed on termination; an unrelated process is never touched.
    private static ManagedContainer? FindManagedContainer(IReadOnlyList<string> command)
    {
        int nameIndex = command.ToList().IndexOf("--name");
        bool runsDocker = Path.GetFileNameWithoutExtension(command[0]) == "docker";
        if (runsDocker && nameIndex >= 0 && nameIndex + 1 < command.Count)
        {
            return new ManagedContainer(command[nameIndex + 1], command[0]);
        }
        return null;
    }
}
