using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Actions.Handlers;
using KLaniBot.Transport;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This isolated acceptance runner currently requires Linux Docker.");
string binary = Path.GetFullPath(args[0]), repo = Path.GetFullPath(args[1]), mode = args.Length > 2 ? args[2] : "fixture";
string root = Path.Combine(repo, ".artifacts", "orcaslicer", mode + "-run-" + Guid.CreateVersion7());
Directory.CreateDirectory(root);
string state = Path.Combine(root, "state"), workspace = Path.Combine(root, "workspace");
Directory.CreateDirectory(workspace);
File.Copy(Path.Combine(repo, "tests/OrcaSlicer/synthetic-cube.stl.txt"), Path.Combine(workspace, "synthetic-cube.stl"));
string fixtureProgram = Path.Combine(workspace, "k-lani-bot-cartridge-lab");
if (mode == "fixture")
    File.Copy(Path.Combine(repo, ".artifacts/orcaslicer/fixture/k-lani-bot-cartridge-lab"), fixtureProgram);
string launch = Path.Combine(workspace, "k-lani-bot-launch-test-app.sh");
string fixtureLaunchScript = "#!/bin/sh\nexec /workspace/k-lani-bot-cartridge-lab fixture model-editor "
    + "/workspace/fixture-effects.jsonl >>/tmp/k-lani-bot-fixture.log 2>&1\n";
string orcaLaunchScript = "#!/bin/sh\nexport HOME=/tmp/k-lani-bot-orcaslicer-home\nmkdir -p \"$HOME\"\n"
    + "exec /opt/k-lani-bot-tests/orcaslicer-2.4.2/AppRun \"$@\" >>/tmp/k-lani-bot-orca.log 2>&1\n";
File.WriteAllText(launch, mode == "fixture" ? fixtureLaunchScript : orcaLaunchScript);
UnixFileMode readAndExecuteForAll = UnixFileMode.UserRead | UnixFileMode.UserExecute
    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
File.SetUnixFileMode(launch, readAndExecuteForAll | UnixFileMode.UserWrite);
if (mode == "fixture")
    File.SetUnixFileMode(fixtureProgram, readAndExecuteForAll);
var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
socket.Start();
int port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
socket.Stop();
Process? server = null, dockerEvents = null;
Task<string>? errors = null, eventLog = null;
int checks = 0;
try
{
    var init = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in new[]
    {
        binary,
        "--init",
        "--backup-requirement",
        "off",
        "--workspace",
        workspace,
        "--state",
        state,
        "--node",
        "orcaslicer-test",
        "--port",
        port.ToString(),
        "--scopes",
        "observe,ui,process,macro.write",
        "--application",
        "/workspace/k-lani-bot-launch-test-app.sh",
        "--sandbox-image",
        "k-lani-bot-orcaslicer-test:2.4.2",
        "--consent-mode",
        "unattended",
        "--ui-guard",
        "desktop"
    })
        init.ArgumentList.Add(argument);
    if (mode != "fixture")
    {
        init.ArgumentList.Add("--sandbox-resources");
        init.ArgumentList.Add(Path.Combine(repo, "tests/OrcaSlicer/sandbox-resources.json"));
    }
    using (var process = Process.Start(init)!)
    {
        string output = await process.StandardOutput.ReadToEndAsync(), error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new Exception(error + output);
    }
    var command = JsonNode.Parse(File.ReadAllText(Path.Combine(state, "node.json")))!["worker"]!.AsArray()
        .Select(x => x!.ToString())
        .ToArray();
    string container = command[Array.IndexOf(command, "--name") + 1];
    var eventsStart = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in new[] { "events", "--filter", "container=" + container, "--format", "{{json .}}" })
        eventsStart.ArgumentList.Add(argument);
    dockerEvents = Process.Start(eventsStart)!;
    eventLog = dockerEvents.StandardOutput.ReadToEndAsync();
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in new[] { binary, "--serve", "--config", Path.Combine(state, "node.json") })
        start.ArgumentList.Add(argument);
    server = Process.Start(start)!;
    errors = server.StandardError.ReadToEndAsync();
    var stdout = server.StandardOutput.ReadToEndAsync();
    using var client = new NodeTransportClient("orcaslicer-test", Path.Combine(state, "nodes.json"));
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(mode == "fixture" ? 3 : 30));
    async Task<JsonObject> Call(string action, JsonObject arguments)
    {
        if (ToolContract.Get(action)["mutates"]!.GetValue<bool>())
            arguments["request_id"] = Guid.CreateVersion7().ToString();
        var result = await client.CallAsync(action, arguments, timeout.Token);
        return result;
    }
    for (int i = 0; i < 50; i++)
    {
        var c = await Call("get_machine_capabilities", new());
        if (c["success"]?.GetValue<bool>() == true)
            break;
        if (server.HasExited)
            throw new Exception(await errors);
        await Task.Delay(200);
    }
    var launched = await Call("launch_process", new()
    {
        ["path"] = "/workspace/k-lani-bot-launch-test-app.sh"
    });
    if (launched["success"]?.GetValue<bool>() != true)
        throw new Exception(launched.ToJsonString());
    await Task.Delay(mode == "fixture" ? 1500 : 10000);
    var windows = await Call("list_windows", new());
    Console.WriteLine(windows.ToJsonString());
    var tree = await Call("get_ui_tree", new()
    {
        ["max_depth"] = 12,
        ["title"] = mode == "fixture" ? "k-lani-bot · OrcaSlicer" : ""
    });
    File.WriteAllText(Path.Combine(root, "initial-tree.json"), tree.ToJsonString());
    Console.WriteLine(new JsonObject
    {
        ["initial_tree_saved"] = true,
        ["success"] = tree["success"]?.DeepClone(),
        ["total_lines"] = tree["total_lines"]?.DeepClone(),
        ["truncated"] = tree["truncated"]?.DeepClone()
    }.ToJsonString());
    async Task Capture(string name)
    {
        var screenshot = await Call("take_screenshot", new());
        if (screenshot["success"]?.GetValue<bool>() != true)
            throw new Exception(screenshot.ToJsonString());
        File.WriteAllBytes(Path.Combine(root, name + ".png"), Convert.FromBase64String(screenshot["image"]!.ToString()));
    }
    await Capture("initial-screen");
    Console.WriteLine("EVIDENCE " + root);
    if (mode == "real")
    {
        Console.WriteLine("Interactive isolated acceptance: action followed by JSON arguments; screenshot; quit.");
        string? line;
        while ((line = Console.ReadLine()) != null && line != "quit")
        {
            try
            {
                if (line == "screenshot")
                {
                    await Capture("screen-" + DateTime.UtcNow.ToString("HHmmss"));
                    Console.WriteLine("Screenshot saved.");
                    continue;
                }
                int space = line.IndexOf(' ');
                string action = space < 0 ? line : line[..space];
                var input = space < 0 ? new JsonObject() : JsonNode.Parse(line[(space + 1)..])!.AsObject();
                var result = await Call(action, input);
                Console.WriteLine(result.ToJsonString());
                File.AppendAllText(
                    Path.Combine(root, "acceptance.jsonl"),
                    new JsonObject { ["action"] = action, ["arguments"] = input, ["result"] = result.DeepClone() }.ToJsonString() + "\n");
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); }
        }
    }
    else
    {
        var fixtureWindow = windows["windows"]!.AsArray().OfType<JsonObject>().Single(w => w["Title"]!.ToString().Contains("OrcaSlicer"));
        int pid = fixtureWindow["ProcessId"]!.GetValue<int>();
        if (mode == "real-replay")
        {
            var closed = await Call("close_window", new()
            {
                ["pid"] = pid,
                ["title"] = "Setup Wizard"
            });
            if (closed["success"]?.GetValue<bool>() != true)
                throw new Exception(closed.ToJsonString());
            var absent = await Call("wait_element", new()
            {
                ["pid"] = pid,
                ["name"] = "Setup Wizard",
                ["role"] = "Window",
                ["state"] = "absent",
                ["timeout_ms"] = 5000
            });
            if (absent["success"]?.GetValue<bool>() != true)
                throw new Exception(absent.ToJsonString());
        }
        string macroFile = mode == "fixture" ? "import-stl-fixture.json" : "import-stl-linux-en.json";
        string macroPath = Path.Combine(repo, "cartridges/programs/orcaslicer/2.4.2/macros", macroFile);
        var macro = JsonSerializer.Deserialize<MacroDefinition>(File.ReadAllText(macroPath))!;
        var macroJson = JsonSerializer.SerializeToNode(macro)!.AsObject();
        macroJson.Remove("version");
        macroJson.Remove("sha256");
        var saved = await Call("save_macro", macroJson);
        if (saved["success"]?.GetValue<bool>() != true)
            throw new Exception(saved.ToJsonString());
        var parameters = new JsonObject { ["pid"] = pid, ["model_path"] = "/workspace/synthetic-cube.stl" };
        if (mode == "real-replay")
            parameters["expected_project_title"] = "*synthetic-cube - OrcaSlicer";
        var result = await Call("run_macro", new()
        {
            ["name"] = macro.Name,
            ["sha256"] = saved["macro"]!["sha256"]!.ToString(),
            ["params"] = parameters,
            ["trace"] = true
        });
        File.WriteAllText(Path.Combine(root, "macro-result.json"), result.ToJsonString());
        if (result["run_id"] is { } traceId)
        {
            var trace = await Call("get_macro_trace", new()
            {
                ["run_id"] = traceId.ToString(),
                ["offset"] = 0,
                ["limit"] = 10
            });
            File.WriteAllText(Path.Combine(root, "macro-trace.json"), trace.ToJsonString());
        }
        if (result["success"]?.GetValue<bool>() != true || result["outcome"]?.ToString() != "verified")
        {
            try
            {
                await Capture("failed-macro");
            }
            catch (Exception captureFailure) { File.WriteAllText(Path.Combine(root, "failed-capture.txt"), captureFailure.Message); }
            throw new Exception(result.ToJsonString());
        }
        checks++;
        async Task Click(string name)
        {
            var found = await Call("find_elements", new()
            {
                ["pid"] = pid,
                ["name"] = name
            });
            if (found["total_matches"]?.GetValue<int>() != 1)
                throw new Exception(found.ToJsonString());
            var clicked = await Call("click_element", new()
            {
                ["id"] = found["matches"]![0]!["Id"]!.GetValue<int>(),
                ["snapshot_id"] = found["snapshot_id"]!.ToString()
            });
            if (clicked["success"]?.GetValue<bool>() != true)
                throw new Exception(clicked.ToJsonString());
        }
        if (mode == "fixture")
        {
            await Click("Print plate");
            await Click("Delete master data");
            await Click("Import 3MF/STL/STEP/SVG/OBJ/AMF...");
            await Click("Delete file");
            await Click("Rename file");
            await Click("Change properties");
            await Click("Cancel");
            var events = File.ReadAllLines(Path.Combine(workspace, "fixture-effects.jsonl"))
                .Select(line => JsonNode.Parse(line)!.AsObject())
                .ToArray();
            foreach (string operation in new[] { "print", "delete-master-data", "delete-file", "rename-file", "properties" })
            {
                if (!events.Any(e => e["Operation"]!.ToString() == operation && e["Allowed"]!.GetValue<bool>() == false))
                    throw new Exception("Missing denied effect: " + operation);
                checks++;
            }
            if (!events.Any(e => e["Operation"]!.ToString() == "import" && e["Allowed"]!.GetValue<bool>()))
                throw new Exception("Missing synthetic import oracle.");
            checks++;
        }
        else
        {
            var falseTitle = await Call("verify_element", new()
            {
                ["pid"] = pid,
                ["name"] = "*wrong-model - OrcaSlicer",
                ["role"] = "Window",
                ["expected_state"] = "visible",
                ["timeout_ms"] = 100
            });
            if (falseTitle["success"]?.GetValue<bool>() != false)
                throw new Exception("False positive actual model title assertion.");
            checks++;
        }
        await Capture(mode == "fixture" ? "fixture-verified" : "real-import-verified");
        File.WriteAllText(Path.Combine(root, "report.json"), new JsonObject
        {
            ["checks_passed"] = checks,
            ["boundary"] = mode == "fixture" ? "isolated_native_fixture_only" : "isolated_real_orcaslicer_2.4.2_linux_en",
            ["real_application_verified"] = mode == "real-replay",
            ["scope"] = mode == "fixture" ? "synthetic_import_and_effect_denials" : "STL_dialog_import_and_project_title_only",
            ["secured"] = false
        }.ToJsonString());
        Console.WriteLine($"{checks} native cartridge {mode} checks passed.");
    }
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
    if (errors != null)
        File.WriteAllText(Path.Combine(root, "operator.log"), await errors);
    if (dockerEvents != null)
    {
        if (!dockerEvents.HasExited)
            dockerEvents.Kill(true);
        await dockerEvents.WaitForExitAsync();
        if (eventLog != null)
            File.WriteAllText(Path.Combine(root, "container-events.jsonl"), await eventLog);
        dockerEvents.Dispose();
    }
}
