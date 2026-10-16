using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using KLaniBot.Guard;

namespace KLaniBot.Actions.ApplicationMcp;

// Use case: the node talks to one program's own MCP server, such as FilmCraft's "filmcraft-cli mcp", over the program's
// standard input and output, one JSON-RPC message per line. A connection lives for one agent session, because such a program
// keeps its open project in memory. The program starts with an empty environment, so node secrets never reach it; requests
// the program sends to the node are refused and its notifications are ignored.
internal sealed class ApplicationMcpConnection : IDisposable
{
    private const string ProtocolVersion = "2025-06-18";
    private const int StandardErrorCharacters = 4000;
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(1);

    private readonly Process _process;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly StringBuilder _standardError = new();
    private int _nextId;

    private ApplicationMcpConnection(Process process)
    {
        _process = process;
    }

    public bool HasExited => _process.HasExited;

    // Throws ApplicationMcpException when the program cannot be started or does not complete the MCP handshake in time.
    public static ApplicationMcpConnection Start(ApplicationMcpPolicy program, string workspace, string home, TimeSpan timeout)
    {
        var process = new Process { StartInfo = StartInfo(program.Command, workspace, home) };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new ApplicationMcpException("The program could not be started.", ApplicationMcpFailure.StartFailed);
        }
        var connection = new ApplicationMcpConnection(process);
        connection.BeginReading();
        try
        {
            connection.Handshake(timeout);
        }
        catch (ApplicationMcpException)
        {
            connection.Dispose();
            throw;
        }
        return connection;
    }

    // Throws ApplicationMcpException when the program answers with an error, stops, or misses the deadline; a program that
    // missed the deadline is stopped, because its next answer could belong to this request.
    public JsonObject Request(string method, JsonObject parameters, TimeSpan timeout)
    {
        int id = ++_nextId;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });

        JsonObject response = AwaitResponse(id, timeout);
        if (response["error"] is JsonObject error)
        {
            string message = error["message"]?.ToString() ?? "The program refused the request.";
            throw new ApplicationMcpException(Shorten(message, 500), ApplicationMcpFailure.ToolError);
        }
        return response["result"] as JsonObject ?? new JsonObject();
    }

    public string RecentStandardError()
    {
        lock (_standardError)
        {
            return _standardError.ToString();
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(ExitGrace))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception)
        {
            // The program already ended; there is nothing left to stop.
        }
        _process.Dispose();
    }

    private static ProcessStartInfo StartInfo(string[] command, string workspace, string home)
    {
        ProcessStartInfo start = Common.ProcessRunner.CreateStartInfo(command[0], command.Skip(1), workspace);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        start.Environment["HOME"] = home;
        start.Environment["LANG"] = "C.UTF-8";
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        return start;
    }

    private void BeginReading()
    {
        _process.ErrorDataReceived += (_, received) => KeepStandardError(received.Data);
        _process.BeginErrorReadLine();
        _ = Task.Run(ReadStandardOutputAsync);
    }

    private async Task ReadStandardOutputAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
                await _lines.Writer.WriteAsync(line);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A closed pipe ends the conversation like an end of file.
        }
        _lines.Writer.TryComplete();
    }

    private void KeepStandardError(string? line)
    {
        if (line == null)
            return;
        lock (_standardError)
        {
            _standardError.AppendLine(line);
            if (_standardError.Length > StandardErrorCharacters)
                _standardError.Remove(0, _standardError.Length - StandardErrorCharacters);
        }
    }

    // Throws ApplicationMcpException when the program does not answer initialize in time or answers it with an error.
    private void Handshake(TimeSpan timeout)
    {
        var parameters = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "k-lani-bot-node", ["version"] = "1" }
        };
        Request("initialize", parameters, timeout);
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
    }

    // Throws ApplicationMcpException when the program stops, sends something that is not JSON, or misses the deadline.
    private JsonObject AwaitResponse(int id, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (true)
        {
            string line = NextLine(deadline.Token);
            JsonObject message = ParseMessage(line);
            if (message["method"] != null && message["id"] != null)
            {
                Send(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = message["id"]!.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "The node accepts no requests from programs." }
                });
                continue;
            }
            if (message["method"] == null && message["id"]?.GetValueKind() == JsonValueKind.Number
                && message["id"]!.GetValue<int>() == id)
                return message;
        }
    }

    // Throws ApplicationMcpException when the program stopped or the deadline passed; the program is stopped on a deadline.
    private string NextLine(CancellationToken deadline)
    {
        try
        {
            return _lines.Reader.ReadAsync(deadline).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw new ApplicationMcpException("The program did not answer in time and was stopped.", ApplicationMcpFailure.TimedOut);
        }
        catch (ChannelClosedException)
        {
            throw new ApplicationMcpException("The program's MCP server stopped.", ApplicationMcpFailure.Stopped);
        }
    }

    // Throws ApplicationMcpException for output that is not a JSON object.
    private static JsonObject ParseMessage(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is JsonObject message)
                return message;
        }
        catch (JsonException)
        {
            // Reported below together with other malformed output.
        }
        throw new ApplicationMcpException("The program wrote something that is not an MCP message.", ApplicationMcpFailure.ProtocolError);
    }

    // Throws ApplicationMcpException when the program no longer reads its input.
    private void Send(JsonObject message)
    {
        try
        {
            _process.StandardInput.WriteLine(message.ToJsonString());
            _process.StandardInput.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new ApplicationMcpException("The program's MCP server stopped.", ApplicationMcpFailure.Stopped);
        }
    }

    private void Stop()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The program already ended.
        }
    }

    private static string Shorten(string text, int length)
    {
        return text.Length <= length ? text : text[..length] + " …";
    }
}

// Why a program's MCP server could not answer; the handlers turn it into the code the agent reads.
internal enum ApplicationMcpFailure
{
    StartFailed,
    Stopped,
    TimedOut,
    ProtocolError,
    ToolError
}

// A program's MCP server failed, stopped or missed its deadline; the message is safe to show to the agent.
internal sealed class ApplicationMcpException(string message, ApplicationMcpFailure failure) : Exception(message)
{
    public ApplicationMcpFailure Failure { get; } = failure;
}
