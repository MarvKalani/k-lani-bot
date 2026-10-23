using System.Text.Json.Nodes;
using KLaniBot.Advisory;
using KLaniBot.Startup;
using KLaniBot.Transport;

namespace KLaniBot.Fleet;

// Commands of the controller side and of the node's network-facing supervisor.
internal static class FleetCommands
{
    // The advisor is optional everywhere: without the option no provider is contacted.
    public static IAdvisoryEvaluator? OpenAdvisor(NodeArguments arguments)
    {
        string? configuration = arguments.Option(AdvisoryProviders.OptionName);
        if (configuration == null)
        {
            return null;
        }
        return AdvisoryProviders.Create(configuration);
    }

    // An operator on the master lists pending pairing requests, approves them or issues a deployment
    // invitation. An invitation contains a secret and is therefore written to a private file, not printed.
    public static async Task<int> MasterAdminAsync(NodeArguments arguments)
    {
        JsonObject result = await MasterAdministration.CallAsync(
            arguments.RequireOption("--state"),
            arguments.Option("--command") ?? "pending",
            CancellationToken.None);
        bool writesInvitation = arguments.Option("--output") != null && result["invitation"] is JsonObject;
        if (writesInvitation)
        {
            string outputPath = Path.GetFullPath(arguments.Option("--output")!);
            PairingClient.CreatePrivateDirectory(Path.GetDirectoryName(outputPath)!);
            PairingProtocol.WritePrivate(outputPath, result["invitation"]!.ToJsonString());
            Console.WriteLine("Deployment invitation written to the private output file.");
        }
        else
        {
            Console.WriteLine(result.ToJsonString());
        }
        bool succeeded = result["success"]?.GetValue<bool>() == true;
        return succeeded ? 0 : 1;
    }

    // The operator's login item starts this beside the master: a tray that shows nodes and MCP clients and pauses them. It
    // runs on the calling thread, which the native tray needs as its UI thread.
    public static int ControllerTray(NodeArguments arguments)
    {
        string state = arguments.RequireOption("--state");
        using var cancellation = ConsoleCancellation.Create();
        return KLaniBot.ControllerTray.ControllerTrayService.Run(state, cancellation.Token);
    }

    // The master's service runs this permanently on its LAN address; nodes connect to it to pair.
    public static async Task<int> PairingMasterAsync(NodeArguments arguments)
    {
        string state = arguments.RequireOption("--state");
        string bind = arguments.RequireOption("--bind");
        int port = arguments.GetPort("--port", 5444);
        using var cancellation = ConsoleCancellation.Create();
        return await PairingMaster.RunAsync(state, bind, port, cancellation.Token);
    }

    // A paired node's login item or service starts this; the node dials out to its master.
    public static async Task<int> ConnectAsync(NodeArguments arguments)
    {
        string configuration = arguments.RequireOption("--config");
        using var cancellation = ConsoleCancellation.Create();
        return await ReverseNodeConnection.RunAsync(configuration, cancellation.Token);
    }

    // A node's login item or service starts this; the node accepts QUIC connections from controllers
    // and starts the desktop worker on demand.
    public static async Task<int> ServeAsync(NodeArguments arguments)
    {
        string configuration = arguments.RequireOption("--config");
        using var cancellation = ConsoleCancellation.Create();
        return await NodeServer.RunAsync(configuration, cancellation.Token);
    }

    // Scripts and tests send single actions to one node: one JSON request per stdin line, one answer
    // per stdout line.
    public static async Task<int> CallAsync(NodeArguments arguments)
    {
        using var client = new NodeTransportClient(
            arguments.RequireOption("--node"),
            arguments.RequireOption("--nodes"),
            forceH2: arguments.Has("--h2"));
        client.SetPresentation(arguments.Option("--client-name"), arguments.Option("--task"));
        var reader = new JsonLineReader(Console.OpenStandardInput());
        while (await reader.ReadAsync() is { } line)
        {
            JsonObject request = JsonNode.Parse(line)!.AsObject();
            string action = request["action"]!.ToString();
            JsonObject actionArguments = request["args"]!.AsObject();
            JsonObject answer = await client.CallAsync(action, actionArguments, CancellationToken.None);
            Console.WriteLine(answer.ToJsonString());
        }
        return 0;
    }

    // An MCP client (an AI application) starts this as its stdio server; it reaches the nodes of the registry.
    public static async Task<int> McpAsync(NodeArguments arguments)
    {
        var profile = KLaniBot.Transport.CompactUi.CompactGatewayOptionCodec.Read(arguments, NodeCommand.Mcp);
        using IAdvisoryEvaluator? advisor = OpenAdvisor(arguments);
        using var gateway = new McpGateway(
            arguments.RequireOption("--nodes"),
            arguments.Option("--default-node"),
            advisor,
            arguments.Has("--mcp-full-text"),
            arguments.Option("--cartridges"),
            profile);
        await gateway.RunAsync(CancellationToken.None);
        return 0;
    }
}
