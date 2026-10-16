using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Actions;

string binary = Path.GetFullPath(args[0]);
string root = Path.Combine(Path.GetTempPath(), "k-lani-bot-sandbox-test-" + Guid.CreateVersion7());
Directory.CreateDirectory(root);
string state = Path.Combine(root, "state"), workspace = Path.Combine(root, "workspace");
var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
socket.Start();
int port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
socket.Stop();
Process? server = null;
Task<string>? operatorLog = null;
ProcessStartInfo NodeStart(IEnumerable<string> arguments)
{
    bool frameworkDependent = binary.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    var start = new ProcessStartInfo(frameworkDependent ? "dotnet" : binary)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    if (frameworkDependent)
    {
        start.ArgumentList.Add(binary);
    }
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    return start;
}
try
{
    var init = NodeStart(
        [
            "--init", "--backup-requirement", "off", "--workspace", workspace, "--state", state, "--node", "sandbox",
            "--port", port.ToString(),
            "--scopes", "observe,ui,process,files.read,files.write", "--application", "/usr/bin/mousepad",
            "--sandbox-image", "k-lani-bot-actuator:local", "--consent-mode", "unattended", "--ui-guard", "desktop"
        ]);
    using (var process = Process.Start(init)!)
    {
        string output = await process.StandardOutput.ReadToEndAsync(), errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new Exception(errors);
    }
    var start = NodeStart(["--serve", "--config", Path.Combine(state, "node.json")]);
    server = Process.Start(start)!;
    var errorLog = server.StandardError.ReadToEndAsync();
    operatorLog = errorLog;
    var outputLog = server.StandardOutput.ReadToEndAsync();
    using var client = new KLaniBot.Transport.NodeTransportClient("sandbox", Path.Combine(state, "nodes.json"));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    JsonObject? capabilities = null;
    for (int i = 0; i < 30; i++)
    {
        capabilities = await client.CallAsync(Tool.GetMachineCapabilities, new(), timeout.Token);
        if (capabilities["success"]?.GetValue<bool>() == true)
            break;
        if (server.HasExited)
            throw new Exception(await errorLog);
        await Task.Delay(200, timeout.Token);
    }
    if (capabilities?["success"]?.GetValue<bool>() != true)
        throw new Exception("Sandbox failed readiness: " + capabilities?.ToJsonString());
    if (!capabilities["capabilities"]!["features"]!.AsArray().Any(value => value!.ToString() == "semanticInspect"))
        throw new Exception("Real AT-SPI unavailable.");
    JsonObject Mutation(JsonObject arguments)
    {
        arguments["request_id"] = Guid.CreateVersion7().ToString();
        return arguments;
    }
    var launched = await client.CallAsync(Tool.LaunchProcess, Mutation(new()
    {
        ["path"] = "/usr/bin/mousepad"
    }), timeout.Token);
    if (launched["success"]?.GetValue<bool>() != true)
        throw new Exception(launched.ToJsonString());
    JsonObject? elements = null;
    for (int i = 0; i < 30; i++)
    {
        elements = await client.CallAsync(Tool.FindElements, new()
        {
            ["role"] = "Edit"
        }, timeout.Token);
        if (elements["success"]?.GetValue<bool>() == true
            && elements["matches"]!.AsArray().Count == 1
            && elements["matches"]![0]!["IsEnabled"]!.GetValue<bool>())
            break;
        await Task.Delay(200, timeout.Token);
    }
    if (elements?["success"]?.GetValue<bool>() != true
        || elements["matches"]!.AsArray().Count != 1
        || !elements["matches"]![0]!["IsEnabled"]!.GetValue<bool>())
        throw new Exception(elements?.ToJsonString() ?? "Editor unavailable.");
    int id = elements["matches"]![0]!["Id"]!.GetValue<int>();
    string snapshot = elements["snapshot_id"]!.ToString();
    string literal = "000123{ENTER}%^$ Grüße";
    var typed = await client.CallAsync(Tool.TypeText, Mutation(new()
    {
        ["id"] = id,
        ["snapshot_id"] = snapshot,
        ["text"] = literal
    }), timeout.Token);
    if (typed["success"]?.GetValue<bool>() != true)
        throw new Exception(typed.ToJsonString());
    var verified = await client.CallAsync(Tool.VerifyElement, new()
    {
        ["id"] = id,
        ["snapshot_id"] = snapshot,
        ["expected_state"] = "enabled",
        ["expected_text"] = literal,
        ["timeout_ms"] = 1000
    }, timeout.Token);
    if (verified["success"]?.GetValue<bool>() != true)
        throw new Exception(verified.ToJsonString());
    var falsePositive = await client.CallAsync(Tool.VerifyElement, new()
    {
        ["id"] = id,
        ["snapshot_id"] = snapshot,
        ["expected_state"] = "enabled",
        ["expected_text"] = "wrong",
        ["timeout_ms"] = 100
    }, timeout.Token);
    if (falsePositive["success"]?.GetValue<bool>() != false)
        throw new Exception("False positive UI assertion.");
    var nativeWindows = await client.CallAsync(Tool.ListWindows, new(), timeout.Token);
    int editorPid = nativeWindows["windows"]!.AsArray()
        .OfType<JsonObject>()
        .Single(w => w["Title"]!.ToString().Contains("Mousepad", StringComparison.OrdinalIgnoreCase))["ProcessId"]!.GetValue<int>();
    var chapters = await client.CallAsync(Tool.GetChapters, new()
    {
        ["pid"] = editorPid
    }, timeout.Token);
    if (chapters["success"]?.GetValue<bool>() != true)
        throw new Exception(chapters.ToJsonString());
    var chapter = System.Text.RegularExpressions.Regex.Match(chapters["chapters"]!.ToString(), @"\[([^\]]+)\]");
    if (!chapter.Success)
        throw new Exception("No native chapters.");
    var shallow = await client.CallAsync(Tool.InspectSection, new()
    {
        ["chapter_id"] = chapter.Groups[1].Value,
        ["snapshot_id"] = chapters["snapshot_id"]!.ToString(),
        ["max_depth"] = 1
    }, timeout.Token);
    var deep = await client.CallAsync(Tool.InspectSection, new()
    {
        ["chapter_id"] = chapter.Groups[1].Value,
        ["snapshot_id"] = chapters["snapshot_id"]!.ToString(),
        ["max_depth"] = 12
    }, timeout.Token);
    if (deep["success"]?.GetValue<bool>() != true
        || deep["sectionTree"]!.ToString().Length <= shallow["sectionTree"]!.ToString().Length
        || !deep["sectionTree"]!.ToString().Contains("000123"))
        throw new Exception("Chapter depth does not expand freshly into the editor.");
    var captured = await client.CallAsync(Tool.TakeScreenshot, new(), timeout.Token);
    if (captured["success"]?.GetValue<bool>() != true || captured["mime_type"]?.ToString() != "image/png")
        throw new Exception("Real screenshot failed: " + captured["message"]);
    byte[] png = Convert.FromBase64String(captured["image"]!.ToString());
    if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        throw new Exception("Screenshot is not a valid PNG transfer.");
    Directory.CreateDirectory(".artifacts/visibility");
    File.WriteAllBytes(".artifacts/visibility/linux-active.png", png);
    using var compressed = new MemoryStream();
    int offset = 8;
    while (offset + 12 <= png.Length)
    {
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
        if (length < 0 || offset + 12L + length > png.Length)
            throw new Exception("Invalid PNG chunk.");
        if (System.Text.Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT")
            compressed.Write(png, offset + 8, length);
        offset += length + 12;
    }
    if (png[24] != 8 || png[25] is not (2 or 6))
        throw new Exception("Expected true-color screenshot.");
    compressed.Position = 0;
    using var pixels = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Decompress);
    byte[] firstPixel = new byte[4];
    pixels.ReadExactly(firstPixel);
    // The first pixel has no left/upper predictor for any PNG row filter.
    if (firstPixel[1] < 220 || firstPixel[2] > 90 || firstPixel[3] > 90)
        throw new Exception("Activity border was not present before the screenshot.");
    Console.WriteLine(
        "Real isolated X11/AT-SPI editor, literal Unicode, negative assertion, fresh chapter depth and PNG screenshot checks passed.");
}
finally
{
    if (server != null)
    {
        if (!server.HasExited)
        {
            using var signal = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-INT", server.Id.ToString() } });
            if (!server.WaitForExit(10000))
                server.Kill(true);
        }
        server.Dispose();
    }
    if (operatorLog != null)
        File.WriteAllText(".artifacts/sandbox-operator.log", await operatorLog);
    Directory.Delete(root, true);
}
