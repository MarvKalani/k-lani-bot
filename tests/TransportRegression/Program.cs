using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

// These runs start a node whose worker opens real windows. Refuse unless the display is private.
KLaniBot.PrivateGuiSession.Require();
string binary = Path.GetFullPath(args[0]);
string temporary = Path.Combine(Path.GetTempPath(), "k-lani-bot-net-test-" + Guid.CreateVersion7());
Directory.CreateDirectory(temporary);
int passed = 0;
Process? server = null;
ProcessStartInfo NodeStart(IEnumerable<string> arguments)
{
    bool frameworkDependent = binary.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    var start = new ProcessStartInfo(frameworkDependent ? "dotnet" : binary)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (string argument in frameworkDependent ? arguments : arguments.Skip(1))
    {
        start.ArgumentList.Add(argument);
    }
    return start;
}
async Task<NodeRun> Run(string[] arguments, string? input = null)
{
    var start = NodeStart(arguments);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (input != null)
        await process.StandardInput.WriteAsync(input);
    process.StandardInput.Close();
    using var deadline = new CancellationTokenSource(20000);
    try
    {
        await process.WaitForExitAsync(deadline.Token);
    }
    catch { process.Kill(true); throw; }
    return new NodeRun(process.ExitCode, await output, await error);
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
string Frames(params JsonObject[] requests) => string.Join("\n", requests.Select(r => r.ToJsonString())) + "\n";
JsonObject Request(string action, JsonObject? arguments = null) => new() { ["action"] = action, ["args"] = arguments ?? new() };
try
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    string state = Path.Combine(temporary, "state"), workspace = Path.Combine(temporary, "workspace");
    var initialized = await Run(
        [
            binary, "--init", "--backup-requirement", "off", "--workspace", workspace, "--state", state, "--node", "fixture",
            "--port", port.ToString(),
            "--scopes", "observe,files.read,files.write", "--consent-mode", "unattended"
        ]);
    Check(initialized.Exit == 0, initialized.Error);
    if (OperatingSystem.IsLinux())
    {
        var invisible = NodeStart([binary, "--serve", "--config", Path.Combine(state, "node.json")]);
        invisible.Environment.Remove("DISPLAY");
        invisible.Environment.Remove("WAYLAND_DISPLAY");
        invisible.Environment.Remove("DBUS_SESSION_BUS_ADDRESS");
        using var hidden = Process.Start(invisible)!;
        var hiddenError = hidden.StandardError.ReadToEndAsync();
        var hiddenOutput = hidden.StandardOutput.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await hidden.WaitForExitAsync(deadline.Token);
        }
        catch { hidden.Kill(true); throw; }
        Check(hidden.ExitCode != 0, "Server runs without a visible desktop worker.");
        Check((await hiddenError).Contains("Visible worker", StringComparison.Ordinal), "Missing indicator was silently ignored.");
        await hiddenOutput;
    }
    var serverInfo = NodeStart([binary, "--serve", "--config", Path.Combine(state, "node.json")]);
    server = Process.Start(serverInfo)!;
    var serverErrors = server.StandardError.ReadToEndAsync();
    var serverOutput = server.StandardOutput.ReadToEndAsync();
    for (int attempt = 0; attempt < 100; attempt++)
    {
        if (server.HasExited)
            throw new Exception(await serverErrors);
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, port);
            break;
        }
        catch (SocketException) { await Task.Delay(50); }
    }
    string registry = Path.Combine(state, "nodes.json");
    async Task<JsonObject[]> Call(JsonObject[] requests, bool h2 = false, string? nodes = null)
    {
        var command = new List<string> { binary, "--call", "--nodes", nodes ?? registry, "--node", "fixture" };
        if (h2)
            command.Add("--h2");
        var result = await Run(command.ToArray(), Frames(requests));
        Check(result.Exit == 0, result.Error);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    }
    foreach (bool h2 in new[] { false, true })
    {
        var responses = await Call(
            [
                Request("get_machine_capabilities"),
                Request(
                    "run_terminal_command",
                    new() { ["command"] = "echo forbidden", ["request_id"] = Guid.CreateVersion7().ToString() })
            ],
            h2);
        Check(responses[0]["success"]!.GetValue<bool>(), "Capability discovery failed.");
        Check(Guid.Parse(responses[0]["receipt"]!["operation_id"]!.ToString()).Version == 7, "Request ID is not UUIDv7.");
        Check(!responses[1]["success"]!.GetValue<bool>()
            && responses[1]["outcome"]!.ToString() == "not_dispatched", "Shell capability not denied.");
        Check(!h2 || responses[0]["transport"]!.ToString() == "h2", "Forced H2 failed.");
    }
    passed += await CapabilitySchemaTransportCases.RunAsync(requests => Call(requests, h2: true));
    passed += await CompactProfileTransportCases.RunAsync(binary, registry, temporary);
    string operation = Guid.CreateVersion7().ToString();
    JsonObject Write() => Request("fs_write_file", new()
    {
        ["path"] = "once.txt",
        ["content"] = "literal",
        ["request_id"] = operation
    });
    var writes = await Call([Write(), Write(), Request("get_action_status", new() { ["request_id"] = operation })]);
    Check(writes[0]["success"]!.GetValue<bool>() && writes[1]["replayed"]!.GetValue<bool>(), "Mutation executed twice.");
    Check(File.ReadAllText(Path.Combine(workspace, "once.txt")) == "literal", "Artifact mismatch.");
    var recovered = await Call([Request("get_action_status", new() { ["request_id"] = operation })]);
    Check(recovered[0]["outcome"]!.ToString() == "completed", "Operation cannot be queried in a new client session.");
    // The supervisor keeps its own ledger and must refuse ids outside the retention window before the actuator is reached.
    string Uuid7At(DateTimeOffset time)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        long milliseconds = time.ToUnixTimeMilliseconds();
        for (int index = 5; index >= 0; index--)
        {
            bytes[index] = (byte)(milliseconds & 0xFF);
            milliseconds >>= 8;
        }
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }
    var refused = await Call([
        Request(
            "fs_write_file",
            new() { ["path"] = "expired.txt", ["content"] = "x", ["request_id"] = Uuid7At(DateTimeOffset.UtcNow.AddHours(-25)) }),
        Request(
            "fs_write_file",
            new() { ["path"] = "future.txt", ["content"] = "x", ["request_id"] = Uuid7At(DateTimeOffset.UtcNow.AddHours(1)) })]);
    Check(
        refused[0]["code"]?.ToString() == "request_id_expired" && refused[0]["outcome"]!.ToString() == "not_dispatched",
        "Expired request_id not refused by the supervisor.");
    Check(
        refused[1]["code"]?.ToString() == "request_id_from_future" && refused[1]["outcome"]!.ToString() == "not_dispatched",
        "Future request_id not refused by the supervisor.");
    Check(
        !File.Exists(Path.Combine(workspace, "expired.txt")) && !File.Exists(Path.Combine(workspace, "future.txt")),
        "A refused request_id still changed the workspace.");
    var badRegistry = JsonNode.Parse(File.ReadAllText(registry))!;
    badRegistry["nodes"]!["fixture"]!["root_sha256"] = new string('0', 64);
    string bad = Path.Combine(temporary, "bad.json");
    File.WriteAllText(bad, badRegistry.ToJsonString());
    Check(
        (await Run([binary, "--call", "--nodes", bad, "--node", "fixture"], Frames(Request("get_machine_capabilities")))).Exit != 0,
        "Incorrect CA pin accepted.");
    badRegistry = JsonNode.Parse(File.ReadAllText(registry))!;
    string badCredential = Path.Combine(temporary, "bad-token");
    File.WriteAllText(badCredential, new string('x', 40));
    badRegistry["nodes"]!["fixture"]!["token_file"] = badCredential;
    File.WriteAllText(bad, badRegistry.ToJsonString());
    var denied = await Call([Request("get_machine_capabilities")], nodes: bad);
    Check(!denied[0]["success"]!.GetValue<bool>(), "Bad credential accepted.");
    var handshake = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = "initialize",
        ["params"] = new JsonObject { ["protocolVersion"] = "2025-11-25" }
    };
    var notification = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };
    var list = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list", ["params"] = new JsonObject() };
    var mcp = await Run([binary, "--mcp", "--nodes", registry], Frames(handshake, notification, list));
    Check(mcp.Exit == 0, mcp.Error);
    var tools = mcp.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonNode.Parse(line)!)
        .Single(frame => frame["id"]?.ToString() == "2")["result"]!["tools"]!.AsArray();
    Check(tools.Count > 0 && tools.All(t => t!["inputSchema"] != null && t["parameters"] == null), "MCP schema contract mismatch.");
    Check(
        tools.All(t => t!["name"]!.ToString() is not ("evaluate_trace" or "register_node")),
        "Disabled classifier or arbitrary registration exposed.");
    Console.WriteLine($"{passed} native transport/MCP regression checks passed.");
}
finally
{
    if (server != null)
    {
        if (!server.HasExited)
        {
            using var signal = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-INT", server.Id.ToString() } });
            if (!server.WaitForExit(5000))
                server.Kill(true);
        }
        server.Dispose();
    }
    Directory.Delete(temporary, true);
}

// What one node process run left behind.
record NodeRun(int Exit, string Output, string Error);
