using System.Text.Json.Nodes;

// A stand-in for a program's own MCP server, started by ApplicationMcpCases as a child of the regression: it keeps a list in
// memory like FilmCraft keeps its project, lists its tools over two pages, sends a notification before each answer, and has
// tools that return a long text, an image, crash, take too long or ask the node a question of their own.
internal static class FakeApplicationMcp
{
    public const string ServeArgument = "--fake-app-mcp";
    public const string VersionArgument = "--fake-app-mcp-version";
    public const string Version = "fake-app 1.2.3";
    public const int LongTextCharacters = 10000;
    private static readonly string[] FirstPage = ["state_read", "state_add", "long_text"];
    private static readonly string[] SecondPage = ["picture", "crash", "slow", "ask"];
    private static readonly List<string> Items = [];

    public static int Serve()
    {
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
            "tools/list" => parameters["cursor"]?.ToString() == "second" ? Page(SecondPage, null) : Page(FirstPage, "second"),
            "tools/call" => Call(parameters["name"]!.ToString(), parameters["arguments"] as JsonObject ?? new JsonObject()),
            _ => new JsonObject()
        };
    }

    private static JsonObject Page(string[] names, string? next)
    {
        var tools = new JsonArray();
        foreach (string name in names)
        {
            var text = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } };
            var schema = new JsonObject { ["type"] = "object", ["properties"] = text };
            tools.Add(new JsonObject { ["name"] = name, ["description"] = "Fake tool " + name, ["inputSchema"] = schema });
        }
        var page = new JsonObject { ["tools"] = tools };
        if (next != null)
            page["nextCursor"] = next;
        return page;
    }

    private static JsonObject? Call(string tool, JsonObject arguments)
    {
        switch (tool)
        {
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
