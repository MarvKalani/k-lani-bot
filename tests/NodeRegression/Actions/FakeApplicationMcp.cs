using System.Text.Json.Nodes;

// A stand-in for a program's own MCP server, started by ApplicationMcpCases as a child of the regression: it keeps a list in
// memory like FilmCraft keeps its project, lists its tools over two pages, sends a notification before each answer, and has
// tools that return a long text, an image, crash, take too long or ask the node a question of their own. Like FilmCraft it
// imports a file as an item whose id a later export needs and writes a GIF; its next version drops state_add and adds a
// required argument to export_gif, so stored functions of the first version drift.
internal static class FakeApplicationMcp
{
    public const string ServeArgument = "--fake-app-mcp";
    public const string VersionArgument = "--fake-app-mcp-version";
    public const string NextServeArgument = "--fake-app-mcp-next";
    public const string NextVersionArgument = "--fake-app-mcp-next-version";
    public const string Version = "fake-app 1.2.3";
    public const string NextVersion = "fake-app 2.0.0";
    public const int LongTextCharacters = 10000;
    private static readonly string[] FirstPage = ["state_read", "state_add", "long_text"];
    private static readonly string[] SecondPage = ["picture", "crash", "slow", "ask", "import", "export_gif"];
    private static readonly List<string> Items = [];
    private static readonly List<string> Imported = [];
    private static bool _next;

    public static int Serve(bool next)
    {
        _next = next;
        while (Console.ReadLine() is { } line)
        {
            if (JsonNode.Parse(line) is not JsonObject request || request["id"] == null)
                continue;
            Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/message", ["params"] = new JsonObject() });
            JsonObject? result = Answer(request["method"]!.ToString(), request["params"] as JsonObject ?? new JsonObject());
            if (result != null)
                Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result });
        }
        return 0;
    }

    private static JsonObject? Answer(string method, JsonObject parameters)
    {
        return method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "fake-app", ["version"] = "1.2.3" }
            },
            "tools/list" => parameters["cursor"]?.ToString() == "second" ? Page(SecondPage, null) : Page(FirstNames(), "second"),
            "tools/call" => Call(parameters["name"]!.ToString(), parameters["arguments"] as JsonObject ?? new JsonObject()),
            _ => new JsonObject()
        };
    }

    private static string[] FirstNames()
    {
        return _next ? FirstPage.Where(name => name != "state_add").ToArray() : FirstPage;
    }

    private static JsonObject Page(string[] names, string? next)
    {
        var tools = new JsonArray();
        foreach (string name in names)
        {
            tools.Add(new JsonObject { ["name"] = name, ["description"] = "Fake tool " + name, ["inputSchema"] = Schema(name) });
        }
        var page = new JsonObject { ["tools"] = tools };
        if (next != null)
            page["nextCursor"] = next;
        return page;
    }

    private static JsonObject Schema(string tool)
    {
        if (tool != "export_gif")
        {
            var text = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } };
            return new JsonObject { ["type"] = "object", ["properties"] = text };
        }
        var properties = new JsonObject
        {
            ["item"] = new JsonObject { ["type"] = "integer" },
            ["path"] = new JsonObject { ["type"] = "string" },
            ["frames"] = new JsonObject { ["type"] = "integer" },
            ["format"] = new JsonObject { ["type"] = "string" }
        };
        JsonArray required = _next ? ["item", "path", "format"] : ["item", "path"];
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
        schema["additionalProperties"] = false;
        return schema;
    }

    private static JsonObject? Call(string tool, JsonObject arguments)
    {
        switch (tool)
        {
            case "import":
                return Import(arguments);
            case "export_gif":
                return ExportGif(arguments);
            case "state_add":
                Items.Add(arguments["text"]?.ToString() ?? "");
                return Text("count " + Items.Count);
            case "state_read":
                return Text("items: " + string.Join(",", Items));
            case "long_text":
                return Text(new string('x', LongTextCharacters));
            case "picture":
                return Picture();
            case "crash":
                Environment.Exit(3);
                return null;
            case "slow":
                Thread.Sleep(TimeSpan.FromSeconds(5));
                return Text("late");
            default:
                return Ask();
        }
    }

    // Asks the node for its roots, which the node must refuse, and reports the refusal code as text.
    private static JsonObject Ask()
    {
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "from-program", ["method"] = "roots/list", ["params"] = new JsonObject() });
        string reply = Console.ReadLine() ?? "{}";
        string code = (JsonNode.Parse(reply) as JsonObject)?["error"]?["code"]?.ToString() ?? "none";
        return Text("refused with " + code);
    }

    private static JsonObject Picture()
    {
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = "rendered frame" },
            new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(new byte[64]) }
        };
        return new JsonObject { ["content"] = content, ["serverOnlyField"] = "must not reach the agent" };
    }

    // An empty path is reported in errors, like FilmCraft reports a file it cannot read; each file becomes an item with an id.
    private static JsonObject Import(JsonObject arguments)
    {
        var errors = new JsonArray();
        var items = new JsonArray();
        foreach (string path in (arguments["paths"] as JsonArray ?? []).Select(path => path?.ToString() ?? ""))
        {
            if (path.Length == 0)
            {
                errors.Add("empty path");
                continue;
            }
            Imported.Add(path);
            items.Add(Imported.Count);
        }
        return Text(new JsonObject { ["errors"] = errors, ["items"] = items }.ToJsonString());
    }

    // Writes a GIF with the asked number of 1x1 frames; an unknown item or no frames is the program's own error.
    private static JsonObject ExportGif(JsonObject arguments)
    {
        int item = arguments["item"]?.GetValue<int>() ?? 0;
        int frames = arguments["frames"]?.GetValue<int>() ?? 1;
        if (item < 1 || item > Imported.Count || frames < 1)
        {
            var failed = Text("cannot export item " + item);
            failed["isError"] = true;
            return failed;
        }
        File.WriteAllBytes(arguments["path"]!.ToString(), Gif(frames));
        return Text("{\"exported\":true}");
    }

    public static byte[] Gif(int frames)
    {
        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8.ToArray());
        bytes.AddRange([1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 255, 255, 255]);
        for (int frame = 0; frame < frames; frame++)
        {
            bytes.AddRange([0x21, 0xF9, 4, 0, 10, 0, 0, 0]);
            bytes.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0]);
        }
        bytes.Add(0x3B);
        return bytes.ToArray();
    }

    private static JsonObject Text(string text)
    {
        return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
    }

    private static void Write(JsonObject message)
    {
        Console.WriteLine(message.ToJsonString());
        Console.Out.Flush();
    }
}
