using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using KLaniBot.Fleet;

// End to end, as the operator uses it: two real --mcp gateways (named like two AI clients) reach the paired node through the
// real master; controller-status lists both, pausing one refuses only its tool calls with a clear message, resume and the bot
// switch work at once, and the controller tray process reports what it shows.
internal static partial class Program
{
    private const string ClaudeName = "claude-code-e2e";
    private const string CodexName = "codex-e2e";
    private const string ClaudeTask = "Rechnungen prüfen";

    private static async Task TestControllerAccessAsync(string binary, string masterState)
    {
        await TestNodeVitalsAsync(binary, masterState);
        string registry = Path.Combine(masterState, "nodes.json");
        using GatewayProcess claude = await GatewayProcess.StartAsync(binary, registry, ClaudeName, ClaudeTask);
        using GatewayProcess codex = await GatewayProcess.StartAsync(binary, registry, CodexName, null);
        Check(Succeeded(await claude.WriteAsync("claude-1.txt")) && Succeeded(await codex.WriteAsync("codex-1.txt")),
            "Both MCP clients work through the master before any pause.");

        JsonObject status = await MasterAdministration.CallAsync(masterState, "controller-status", CancellationToken.None);
        Check(ControllerStatus.TryRead(status, out ControllerStatus listed), "The master answers controller-status.");
        ControllerClientRow claudeRow = listed.Clients.Single(client => client.Client == ClaudeName);
        ControllerClientRow codexRow = listed.Clients.Single(client => client.Client == CodexName);
        Check(claudeRow.Purpose == ClaudeTask && claudeRow.Nodes.SequenceEqual(["managed"]) && codexRow.Purpose == "",
            "Each gateway is one client row with its clientInfo name, K_LANI_BOT_TASK and node.");
        Check(listed.Nodes.Any(node => node.Name == "managed" && node.Connected), "The status lists the connected node.");

        await PauseOneClientAsync(binary, masterState, claude, codex, claudeRow.Instance);
        await SwitchBotOffAsync(masterState, claude, codex);
        await claude.CloseAsync();
        await codex.CloseAsync();
    }

    private static async Task PauseOneClientAsync(string binary, string masterState, GatewayProcess claude, GatewayProcess codex,
        string claudeInstance)
    {
        await AdministerAsync(masterState, "client-pause " + claudeInstance);
        JsonObject refused = await claude.WriteAsync("claude-paused.txt");
        Check(IsPaused(refused) && ToolText(refused).Contains("paused this MCP client", StringComparison.Ordinal),
            "A paused client's tool call fails with access_paused and says the operator paused it.");
        string workspace = Path.Combine(Path.GetDirectoryName(masterState)!, "managed-workspace");
        Check(File.Exists(Path.Combine(workspace, "claude-1.txt")) && !File.Exists(Path.Combine(workspace, "claude-paused.txt")),
            "Nothing of the refused call reached the node.");
        Check(Succeeded(await codex.WriteAsync("codex-2.txt")), "The other client keeps working while one is paused.");
        Check(Succeeded(await claude.CallToolAsync("get_machine_capabilities", new JsonObject())),
            "A paused client still reads capabilities, so its tool list stays complete.");
        await ExpectTrayLineAsync(binary, masterState, "1 paused");

        await AdministerAsync(masterState, "client-resume " + claudeInstance);
        Check(Succeeded(await claude.WriteAsync("claude-2.txt")), "A resumed client works again at once.");
    }

    private static async Task SwitchBotOffAsync(string masterState, GatewayProcess claude, GatewayProcess codex)
    {
        await AdministerAsync(masterState, "bot off");
        JsonObject claudeRefused = await claude.WriteAsync("claude-off.txt");
        Check(IsPaused(claudeRefused) && IsPaused(await codex.WriteAsync("codex-off.txt"))
            && ToolText(claudeRefused).Contains("switched k-lani-bot off", StringComparison.Ordinal),
            "A switched-off bot refuses every client and says why.");
        await AdministerAsync(masterState, "bot on");
        Check(Succeeded(await codex.WriteAsync("codex-3.txt")), "Switching the bot on admits clients again.");
    }

    private static async Task AdministerAsync(string masterState, string command)
    {
        JsonObject answer = await MasterAdministration.CallAsync(masterState, command, CancellationToken.None);
        Check(answer["success"]?.GetValue<bool>() == true, "The master accepts " + command.Split(' ')[0] + ": " + answer.ToJsonString());
    }

    // The tray runs in this private display, asks the master and logs what its menu shows.
    private static async Task ExpectTrayLineAsync(string binary, string masterState, string expected)
    {
        using Process tray = Start(binary, ["--controller-tray", "--state", masterState]);
        Task<string> errors = tray.StandardError.ReadToEndAsync();
        string? shown = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (shown == null && await tray.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                shown = line.StartsWith("Controller tray: bot on", StringComparison.Ordinal) && line.Contains(expected) ? line : null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            tray.Kill(true);
            await tray.WaitForExitAsync();
        }
        Check(shown != null, "The controller tray shows the paused client: " + (shown ?? await errors));
    }

    private static bool Succeeded(JsonObject toolResult)
    {
        return toolResult["isError"]?.GetValue<bool>() == false;
    }

    private static bool IsPaused(JsonObject toolResult)
    {
        return toolResult["isError"]?.GetValue<bool>() == true && ToolText(toolResult).Contains("access_paused", StringComparison.Ordinal);
    }

    private static string ToolText(JsonObject toolResult)
    {
        return toolResult["content"]?[0]?["text"]?.ToString() ?? "";
    }

    // One --mcp gateway process as an MCP client starts it: stdio JSON-RPC, clientInfo name and K_LANI_BOT_TASK.
    private sealed class GatewayProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Channel<JsonObject> _answers = Channel.CreateUnbounded<JsonObject>();
        private readonly Task _reader;
        private readonly Task<string> _errors;
        private int _nextRequest;

        private GatewayProcess(Process process)
        {
            _process = process;
            _errors = process.StandardError.ReadToEndAsync();
            _reader = Task.Run(ReadAnswersAsync);
        }

        public static async Task<GatewayProcess> StartAsync(string binary, string registry, string clientName, string? task)
        {
            var environment = new Dictionary<string, string?> { ["K_LANI_BOT_TASK"] = task };
            var gateway = new GatewayProcess(Start(binary, ["--mcp", "--nodes", registry, "--default-node", "managed"], environment));
            var parameters = new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = clientName, ["version"] = "1" }
            };
            await gateway.RequestAsync("initialize", parameters);
            await gateway.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            return gateway;
        }

        private async Task ReadAnswersAsync()
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (JsonNode.Parse(line) is JsonObject answer && answer["id"] != null)
                {
                    await _answers.Writer.WriteAsync(answer);
                }
            }
            _answers.Writer.TryComplete();
        }

        public Task<JsonObject> WriteAsync(string path)
        {
            return CallToolAsync("fs_write_file", new JsonObject { ["path"] = path, ["content"] = "controller access" });
        }

        public async Task<JsonObject> CallToolAsync(string name, JsonObject arguments)
        {
            JsonObject answer = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments });
            return answer["result"] as JsonObject ?? throw new Exception("Gateway answered without a result: " + answer.ToJsonString());
        }

        // Requests are sent one at a time, so the next answer is this request's.
        private async Task<JsonObject> RequestAsync(string method, JsonObject parameters)
        {
            int id = ++_nextRequest;
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                return await _answers.Reader.ReadAsync(deadline.Token);
            }
            catch (Exception failure) when (failure is OperationCanceledException or ChannelClosedException)
            {
                throw new Exception("Gateway gave no answer to " + method + ": " + await _errors);
            }
        }

        private async Task SendAsync(JsonObject frame)
        {
            await _process.StandardInput.WriteLineAsync(frame.ToJsonString());
            await _process.StandardInput.FlushAsync();
        }

        // Closing standard input is how an MCP client ends the gateway; it releases its desktop session on the way out.
        public async Task CloseAsync()
        {
            _process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _process.WaitForExitAsync(deadline.Token);
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                _process.WaitForExit();
            }
            _reader.GetAwaiter().GetResult();
            _errors.GetAwaiter().GetResult();
            _process.Dispose();
        }
    }
}
