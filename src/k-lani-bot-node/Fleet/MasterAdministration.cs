using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Transport;

namespace KLaniBot.Fleet;

// The operator's console commands reach a running master through a local pipe that only this user and state directory can open; the master
// answers each command with JSON.
public static class MasterAdministration
{
    private static string PipeName(string directory)
    {
        string directoryHash = PairingProtocol.Hash(Path.GetFullPath(directory), Environment.UserName);
        return ProtocolIdentity.MasterAdministrationChannelPrefix + directoryHash[..24];
    }

    // One command per connection, answered in order. The next server instance is created before the current
    // connection is closed: on Linux a named pipe is a Unix socket shared by the instances of one name, and closing
    // the last instance closed the listening socket and reset every client already waiting in its queue
    // ("Connection reset by peer"), which concurrent commands hit regularly.
    public static async Task ServeAsync(string directory, Func<string, JsonObject> execute, CancellationToken cancellation)
    {
        NamedPipeServerStream next = CreateServer(directory);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                NamedPipeServerStream current = next;
                try
                {
                    await current.WaitForConnectionAsync(cancellation);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                next = CreateServer(directory);
                using (current)
                {
                    await AnswerAsync(current, execute, cancellation);
                }
            }
        }
        finally
        {
            next.Dispose();
        }
    }

    private static NamedPipeServerStream CreateServer(string directory)
    {
        return new NamedPipeServerStream(PipeName(directory), PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private static async Task AnswerAsync(Stream pipe, Func<string, JsonObject> execute, CancellationToken cancellation)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            string command = await ReadLineAsync(pipe, deadline.Token);
            JsonObject result;
            try
            {
                result = execute(command);
            }
            catch (Exception exception) when (
                exception is ArgumentException or KeyNotFoundException or UnauthorizedAccessException
                    or IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                result = new JsonObject { ["success"] = false, ["error"] = exception.Message };
            }
            await WriteLineAsync(pipe, result.ToJsonString(), deadline.Token);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or InvalidDataException)
        {
            // A local client can disconnect; this must not stop existing node connections.
        }
    }

    public static async Task<JsonObject> CallAsync(string directory, string command, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        await WriteLineAsync(pipe, command, deadline.Token);
        return JsonNode.Parse(await ReadLineAsync(pipe, deadline.Token))!.AsObject();
    }

    // Throws InvalidDataException for an oversized frame and IOException when the connection closes first.
    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellation)
    {
        using var content = new MemoryStream();
        byte[] character = new byte[1];
        while (await stream.ReadAsync(character, cancellation) != 0)
        {
            if (character[0] == '\n')
            {
                return Encoding.UTF8.GetString(content.ToArray());
            }
            if (content.Length >= 16384)
            {
                throw new InvalidDataException("Administrative frame too large.");
            }
            content.WriteByte(character[0]);
        }
        throw new IOException("Administrative connection closed.");
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellation)
    {
        byte[] content = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(content, cancellation);
        await stream.FlushAsync(cancellation);
    }
}
