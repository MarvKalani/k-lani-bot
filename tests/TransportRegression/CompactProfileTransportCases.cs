using System.Diagnostics;
using System.Text.Json.Nodes;

// A headless controller process uses the native compact MCP profile while the existing isolated HTTPS worker owns desktop visibility.
internal static class CompactProfileTransportCases
{
    public static async Task<int> RunAsync(string binary, string registry, string temporary)
    {
        string library = Path.Combine(temporary, "compact-library");
        Directory.CreateDirectory(library);
        string evidence = Path.Combine(temporary, "unused-acceptance");
        string configuration = Configuration(temporary, evidence);
        var start = Start(binary, registry, library, configuration);
        using Process child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> errors = child.StandardError.ReadToEndAsync();
        foreach (var frame in Frames())
        {
            await child.StandardInput.WriteLineAsync(frame.ToJsonString());
        }
        child.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await child.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            child.Kill(true);
            throw;
        }
        Require(child.ExitCode == 0, await errors);

        Require(!Directory.Exists(evidence), "Compact startup must not create acceptance evidence or signing keys.");

        return Verify(await output) + 1;
    }

    private static JsonObject[] Frames()
    {
        return new JsonObject[]
        {
            Frame(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "synthetic-regression", ["version"] = "1" } }),
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
            Frame(2, "tools/list", new JsonObject()),
            Frame(3, "tools/call", new JsonObject { ["name"] = "ui_info", ["arguments"] = new JsonObject() }),
            Frame(4, "tools/call", new JsonObject { ["name"] = "cartridge_browse", ["arguments"] = new JsonObject
            {
                ["function"] = "Info.compatibility"
            } }),
            Frame(5, "tools/call", new JsonObject { ["name"] = "cartridge_call", ["arguments"] = new JsonObject
            {
                ["function"] = "Programming.revalidate",
                ["arguments"] = new JsonObject { ["function"] = "ui.synthetic", ["expected_revision"] = 1,
                    ["evidence_ref"] = Guid.NewGuid().ToString(), ["approved"] = true }
            } })
        };
    }

    private static int Verify(string output)
    {
        var replies = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject())
            .ToDictionary(frame => frame["id"]!.GetValue<int>());
        Require(replies[1]["result"]?["protocolVersion"]?.ToString() == "2025-11-25", "Compact protocol negotiation failed.");
        string instructions = replies[1]["result"]?["instructions"]?.ToString() ?? "";
        Require(instructions.StartsWith("k-lani-bot is remote computer use", StringComparison.Ordinal)
            && instructions.Contains("Keep one MCP connection for a whole task", StringComparison.Ordinal)
            && instructions.Contains("Author missing task procedures", StringComparison.Ordinal)
            && instructions.Contains("Call ui_info first;", StringComparison.Ordinal),
            "Compact initialize names what the server is, the connection rule, the author role and first discovery call.");
        var tools = replies[2]["result"]!["tools"]!.AsArray().OfType<JsonObject>().ToArray();
        Require(tools.Length == 8 && tools.Any(tool => tool["name"]!.ToString() == "ui_act")
            && tools.Any(tool => tool["name"]!.ToString() == "ui_input")
            && tools.Any(tool => tool["name"]!.ToString() == "ui_apply")
            && !tools.Any(tool => tool["name"]!.ToString() == "type_text"), "Raw native tools leaked into compact discovery.");
        Require(replies[4]["result"]?["structuredContent"]?["function"]?["name"]?.ToString() == "Info.compatibility",
            "Native compatibility descriptor missing at the actual MCP boundary.");
        Require(replies[5]["error"]?["code"]?.GetValue<int>() == -32602,
            "MCP caller approval must be rejected as invalid revalidation arguments.");
        var info = replies[3]["result"]?["structuredContent"];
        if (info?["success"]?.ToString() == "false" && info["error"]?.ToString().Contains("'wmctrl'", StringComparison.Ordinal) == true
            && !File.Exists("/usr/bin/wmctrl"))
        {
            Console.WriteLine("SKIP compact Info execution: the isolated host worker requires wmctrl. Headless startup/discovery passed.");
            return 5;
        }
        Require(info?["success"]?.ToString() == "true", "Headless compact Info failed: "
            + (info?["error"]?.ToString() ?? replies[3]["error"]?["message"]?.ToString()));

        return 6;
    }

    private static string Configuration(string temporary, string evidence)
    {
        string directory = Path.Combine(temporary, "compact-config");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "ui.json");
        var configuration = new JsonObject
        {
            ["version"] = 1,
            ["compatibility"] = new JsonObject
            {
                ["evidenceRoot"] = evidence,
                ["authority"] = new JsonObject { ["node"] = "isolated-fixture", ["principal"] = "operator", ["connection"] = "test-client" }
            }
        };
        File.WriteAllText(file, configuration.ToJsonString());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return file;
    }

    private static ProcessStartInfo Start(string binary, string registry, string library, string configuration)
    {
        bool managed = binary.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(managed ? "dotnet" : binary)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (managed)
        {
            start.ArgumentList.Add(binary);
        }
        foreach (string argument in new[] { "--mcp", "--ui-profile", "compact", "--nodes", registry, "--default-node", "fixture",
            "--cartridges", library, "--ui-config", configuration })
        {
            start.ArgumentList.Add(argument);
        }
        foreach (string variable in new[] { "DISPLAY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "XDG_RUNTIME_DIR", "GDK_BACKEND" })
        {
            start.Environment.Remove(variable);
        }

        return start;
    }

    private static JsonObject Frame(int identifier, string method, JsonObject parameters)
    {
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = identifier, ["method"] = method, ["params"] = parameters };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
