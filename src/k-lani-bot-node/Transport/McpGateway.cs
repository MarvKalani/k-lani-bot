using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Advisory;
using KLaniBot.Startup;
using KLaniBot.Transport.CompactUi;

namespace KLaniBot.Transport;

// Use case: an AI application starts the node with --mcp as its stdio MCP server and works with every machine
// in the operator's node registry through it. The gateway holds no permission of its own: it translates MCP
// into calls to the nodes, and each node decides with its own policy, consent and guard.
//
// The AI can only pick a node by its registry name, never by URL, so it cannot point the gateway at a machine
// the operator did not register. tools/list offers only the tools some node reports as active, so the AI is
// not shown tools that every node would refuse. A mutating call without a request_id gets a fresh UUIDv7,
// because the node requires one; a client that wants to query the same operation later supplies its own.
// Cancelling a request stops waiting for it, not the action already running on the node.
public sealed class McpGateway : IDisposable
{
    private readonly Dictionary<string, NodeTransportClient> _clients = new();
    private readonly JsonObject _nodes;
    private readonly string? _default;
    private readonly IAdvisoryEvaluator? _advisor;
    private readonly bool _fullText;
    private readonly GatewayCartridgeLibrary? _cartridgeLibrary;
    private readonly CompactGatewayOptions? _profile;
    // Throws ArgumentException for an empty or unversioned registry, an insecure node entry or an unknown default node.
    public McpGateway(string registryPath, string? defaultNode, IAdvisoryEvaluator? advisor = null, bool fullText = false,
        string? cartridgeManifest = null, CompactGatewayOptions? profile = null)
    {
        _profile = profile;
        var registry = JsonNode.Parse(File.ReadAllText(registryPath))!.AsObject();
        if (registry["version"]?.GetValue<int>() != 1 || registry["nodes"] is not JsonObject nodes || nodes.Count == 0)
            throw new ArgumentException("A nonempty versioned node registry is required.");
        _nodes = nodes;
        _advisor = advisor;
        _fullText = fullText;
        foreach (var (name, entry) in nodes)
        {
            if (!Uri.TryCreate(entry?["url"]?.ToString(), UriKind.Absolute, out var uri)
                || uri.Scheme != "https"
                || uri.UserInfo != ""
                || uri.Query != ""
                || uri.Fragment != ""
                || entry?["root_sha256"]?.ToString().Length != 64
                || entry["root_der_base64"] == null)
                throw new ArgumentException("Nodes require HTTPS, a pinned CA, and no inline credentials or query string.");
            _clients[name] = new NodeTransportClient(name, Path.GetFullPath(registryPath), entry["token_env"]?.ToString());
        }
        _default = defaultNode ?? (_clients.Count == 1 ? _clients.Keys.Single() : null);
        if (_default != null && !_clients.ContainsKey(_default))
            throw new ArgumentException("Default node is not registered.");
        if (_profile?.Profile == CompactGatewayProfile.Compact && (_default == null || _profile.Roots == null))
        {
            throw new ArgumentException("Compact UI requires a registered default node and validated cartridge roots.");
        }
        if (cartridgeManifest != null)
        {
            _cartridgeLibrary = new GatewayCartridgeLibrary(cartridgeManifest);
        }
    }
    public Task RunAsync(CancellationToken cancellation)
    {
        return RunAsync(Console.OpenStandardInput(), Console.Out, cancellation);
    }

    public async Task RunAsync(Stream standardInput, TextWriter output, CancellationToken cancellation)
    {
        using var session = new GatewaySession(this, output, cancellation);
        await session.ServeAsync(new JsonLineReader(standardInput));
    }

    // A frame that is not a JSON-RPC request object is answered and skipped, never fatal for the loop.
    // JSON "null" parses to no node, and duplicate keys only fail when a property is first read.
    private static JsonObject? ParseFrame(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject request)
            {
                return null;
            }
            if (request["jsonrpc"]?.ToString() != "2.0" || request["method"] == null)
            {
                return null;
            }
            return request;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
    }

    // One stdio conversation with an MCP client: the negotiated protocol, the requests in flight and the output that
    // their answers share. Requests run concurrently, so every answer goes through one lock.
    private sealed class GatewaySession : IDisposable
    {
        private const string LegacyProtocol = "2024-11-05";
        private const string LatestProtocol = "2025-11-25";
        private static readonly string[] SupportedProtocols = [LatestProtocol, "2025-06-18", LegacyProtocol];
        private const int ParseError = -32700;
        private const int InvalidRequest = -32600;
        private const int MethodNotFound = -32601;
        private const int InvalidParams = -32602;
        private const int InternalError = -32603;
        private const int ServerError = -32000;

        // At most 16 requests are accepted; more is answered with "queue full" at once. Tool calls run one at a time.
        private const int AcceptedRequests = 16;

        // How long shutdown waits for the nodes to release this controller's desktop session.
        private static readonly TimeSpan SessionCleanupDeadline = TimeSpan.FromSeconds(5);

        private readonly McpGateway _gateway;
        private readonly TextWriter _output;
        private readonly CancellationToken _cancellation;
        private readonly SemaphoreSlim _outputLock = new(1);
        private readonly Dictionary<string, CancellationTokenSource> _pending = new();
        private readonly List<Task> _jobs = new();
        private readonly SemaphoreSlim _slots = new(AcceptedRequests);
        private Task _previousToolCallTurn = Task.CompletedTask;
        private readonly CartridgeMcpSession? _cartridgeSession;
        private readonly CompactGatewaySession? _compactSession;
        private bool _initialized;
        private string? _protocol;

        public GatewaySession(McpGateway gateway, TextWriter output, CancellationToken cancellation)
        {
            _gateway = gateway;
            _output = output;
            _cancellation = cancellation;
            if (gateway._cartridgeLibrary != null)
            {
                _cartridgeSession = new CartridgeMcpSession(gateway._cartridgeLibrary, gateway._clients,
                    gateway._default, RunCartridgeAsync, gateway._advisor != null, () => _protocol ?? LatestProtocol);
            }
            if (gateway._profile?.Profile == CompactGatewayProfile.Compact)
            {
                var client = new CompactRegisteredNodeClient(gateway._clients[gateway._default!]);
                _compactSession = new(client, gateway._default!, gateway._profile.Roots!, () => _protocol ?? LatestProtocol,
                    _cartridgeSession == null ? null : _cartridgeSession.CallAsync, gateway._profile.Configuration);
            }
        }

        public async Task ServeAsync(JsonLineReader input)
        {
            try
            {
                while (await input.ReadAsync(_cancellation) is { } line)
                {
                    JsonObject? request = ParseFrame(line);
                    if (request == null)
                    {
                        await SendAsync(Error(null, ParseError, "Invalid JSON-RPC frame."));
                        continue;
                    }
                    await DispatchFrameAsync(request);
                }
                await Task.WhenAll(_jobs);
            }
            finally
            {
                await EndNodeSessionsAsync();
            }
        }

        // Without this a restarted controller is refused the desktop until the old lease expires. Releasing is a
        // courtesy: an unreachable node expires the lease itself, so a failure here must not delay or fail shutdown.
        private async Task EndNodeSessionsAsync()
        {
            using var deadline = new CancellationTokenSource(SessionCleanupDeadline);
            foreach (NodeTransportClient client in _gateway._clients.Values)
            {
                try
                {
                    await client.EndDesktopSessionAsync(deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    // The client was still busy with a request that outlived the cleanup deadline.
                }
            }
        }

        public void Dispose()
        {
            _cartridgeSession?.Dispose();
            _compactSession?.Dispose();
            _slots.Dispose();
        }

        private async Task DispatchFrameAsync(JsonObject request)
        {
            var id = request["id"];
            string method = request["method"]!.ToString();
            if (method == "initialize" && id != null)
            {
                await InitializeAsync(request, id);
            }
            else if (method == "notifications/initialized")
            {
                _initialized = _protocol != null;
            }
            else if (method == "notifications/cancelled")
            {
                CancelPending(request["params"]?["requestId"]?.ToJsonString() ?? "");
            }
            else if (id != null)
            {
                await AcceptRequestAsync(request, id);
            }
        }

        private async Task InitializeAsync(JsonObject request, JsonNode id)
        {
            if (_protocol != null)
            {
                await SendAsync(Error(id, InvalidRequest, "Already initialized."));
                return;
            }

            string offered = request["params"]?["protocolVersion"]?.ToString() ?? "";
            string? clientName = request["params"]?["clientInfo"]?["name"]?.ToString();
            foreach (NodeTransportClient client in _gateway._clients.Values)
            {
                client.SetPresentation(clientName, Environment.GetEnvironmentVariable("K_LANI_BOT_TASK"));
            }
            _protocol = SupportedProtocols.Contains(offered) ? offered : LatestProtocol;
            var initializeResult = new JsonObject
            {
                ["protocolVersion"] = _protocol,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                ["serverInfo"] = new JsonObject { ["name"] = "k-lani-bot", ["version"] = ProductIdentity.DisplayVersion },
                ["instructions"] = ServerInstructions()
            };
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = initializeResult });
        }

        private string ServerInstructions()
        {
            if (_compactSession != null)
            {
                return CompactGatewayTools.Instructions;
            }
            if (_cartridgeSession != null)
            {
                return GatewayOrientation.Identity + GatewayOrientation.Session
                    + "Build missing task procedures from verified screenshot/UI exploration and use them yourself through MCP. "
                    + "Identify the application/build; test changed builds before adapting or deriving their cartridge. "
                    + "Write parameterized recipes into the host cartridge library; the gateway runs their steps on the authorized node. "
                    + "Aim for one business-operation call. Browse Catalog for paged summaries; call cartridge_select with arguments.key. "
                    + "Then call cartridge_info, browse only the task topic and request individual function schemas. "
                    + "Info/Coverage shows implementation and acceptance gaps. Cartridge changes do not change the three MCP tools.";
            }

            return GatewayOrientation.Identity + GatewayOrientation.Start + GatewayOrientation.Session
                + "Build missing task procedures from verified screenshot/UI exploration and use them yourself through MCP. "
                + "Identify the application/build; test changed builds before adapting or deriving their cartridge. "
                + "Where macro-author privileges are available, save_macro stores a parameterized procedure and run_macro executes it. "
                + "Otherwise author a host cartridge through the available file tools; do not assume arbitrary code can be uploaded. "
                + "Keep deterministic steps and verification local; return the business result and separate authoring cost from reuse.";
        }

        private void CancelPending(string key)
        {
            lock (_pending)
            {
                if (_pending.TryGetValue(key, out var token))
                {
                    token.Cancel();
                }
            }
        }

        private async Task AcceptRequestAsync(JsonObject request, JsonNode id)
        {
            if (!_initialized)
            {
                await SendAsync(Error(id, ServerError, "Initialize first."));
                return;
            }
            if (!await _slots.WaitAsync(0, _cancellation))
            {
                await SendAsync(Error(id, ServerError, "Gateway queue full."));
                return;
            }

            var token = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
            string key = id.ToJsonString();
            if (!TryAddPending(key, token))
            {
                _slots.Release();
                token.Dispose();
                await SendAsync(Error(id, InvalidRequest, "Request identifier is already pending."));
                return;
            }
            Task previousTurn = ReserveToolCallTurn(request, out Action finishTurn);
            Task job = Task.Run(() => RunRequestAsync(request, key, token, previousTurn, finishTurn), CancellationToken.None);
            _jobs.RemoveAll(task => task.IsCompleted);
            _jobs.Add(job);
        }

        // Tool calls operate one desktop, so they must not interleave. The turn is taken here, on the single reader loop,
        // so it follows arrival order (a semaphore would not); each call waits for the turn before it, not for a lock.
        // Other methods get a finished turn and stay responsive while a tool call runs.
        private Task ReserveToolCallTurn(JsonObject request, out Action finishTurn)
        {
            if (request["method"]?.ToString() != "tools/call")
            {
                finishTurn = () => { };
                return Task.CompletedTask;
            }

            Task previousTurn = _previousToolCallTurn;
            var ownTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _previousToolCallTurn = ownTurn.Task;
            finishTurn = () => ownTurn.TrySetResult();
            return previousTurn;
        }

        private bool TryAddPending(string key, CancellationTokenSource token)
        {
            lock (_pending)
            {
                return _pending.TryAdd(key, token);
            }
        }

        private async Task RunRequestAsync(
            JsonObject request,
            string key,
            CancellationTokenSource token,
            Task previousTurn,
            Action finishTurn)
        {
            try
            {
                await HandleAsync(request, token.Token, previousTurn);
            }
            finally
            {
                // A call cancelled while queued must hand the turn on only after the call before it ended.
                await previousTurn;
                finishTurn();
                lock (_pending)
                {
                    _pending.Remove(key);
                }
                token.Dispose();
                _slots.Release();
            }
        }

        private async Task HandleAsync(JsonObject request, CancellationToken token, Task previousTurn)
        {
            var id = request["id"]!.DeepClone();
            try
            {
                await previousTurn.WaitAsync(token);
                JsonObject? result = await AnswerAsync(request, token);
                JsonObject frame = result == null
                    ? Error(id, MethodNotFound, "Method not found.")
                    : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
                await SendAsync(frame);
            }
            catch (ArgumentException ex)
            {
                await SendAsync(Error(id, InvalidParams, ex.Message));
            }
            catch (OperationCanceledException)
            {
                await SendAsync(Error(id, ServerError, "Request cancelled. Running OS mutations are not rolled back."));
            }
            catch (Exception exception)
            {
                // The boundary of one request: whatever failed, the gateway must answer it and keep serving the others.
                await SendAsync(Failure(id, exception));
            }
        }

        // Null for a method the gateway does not offer.
        private async Task<JsonObject?> AnswerAsync(JsonObject request, CancellationToken token)
        {
            string method = request["method"]!.ToString();
            var parameters = request["params"] as JsonObject ?? new();

            return method switch
            {
                "tools/list" => await ListToolsAsync(token),
                "tools/call" => await CallToolAsync(parameters, token),
                "ping" => new JsonObject(),
                _ => null
            };
        }

        private async Task<JsonObject> ListToolsAsync(CancellationToken token)
        {
            if (_compactSession != null)
            {
                return new JsonObject { ["tools"] = CompactGatewayTools.Descriptors() };
            }
            if (_cartridgeSession != null)
            {
                return new JsonObject { ["tools"] = CartridgeGatewayTools.Descriptors(_gateway._default == null) };
            }
            HashSet<Tool> allowed = await ToolsSomeNodeOffersAsync(token);

            var definitions = new JsonArray();
            var offered = ToolContract.Tools.OfType<JsonObject>()
                .Where(entry => allowed.Contains(ToolNames.OfContractEntry(entry)));
            foreach (var descriptor in offered)
            {
                var definition = new JsonObject();
                foreach (var key in new[] { "name", "description", "inputSchema", "annotations" })
                {
                    definition[key] = descriptor[key]!.DeepClone();
                }
                if (_gateway._default == null && ToolNames.OfContractEntry(definition) is not (Tool.ListNodes or Tool.EvaluateTrace))
                {
                    definition["inputSchema"]!["required"]!.AsArray().Add("node");
                }
                definitions.Add(definition);
            }
            return new JsonObject { ["tools"] = definitions };
        }

        private async Task<HashSet<Tool>> ToolsSomeNodeOffersAsync(CancellationToken token)
        {
            var allowed = new HashSet<Tool> { Tool.ListNodes, Tool.GetMachineCapabilities };
            if (_gateway._advisor != null)
            {
                allowed.Add(Tool.EvaluateTrace);
            }

            var capabilityCalls = _gateway._clients.Values.Select(client => client.CallAsync(Tool.GetMachineCapabilities, new(), token));
            var capabilities = await Task.WhenAll(capabilityCalls);
            foreach (var capability in capabilities)
            {
                bool reported = capability["success"]?.GetValue<bool>() == true;
                if (!reported || capability["capabilities"]?["activeTools"] is not JsonArray tools)
                {
                    continue;
                }
                // A node of a newer version may report tools this gateway does not know; those stay hidden.
                foreach (JsonNode? reportedTool in tools)
                {
                    if (ToolNames.TryParse(reportedTool?.ToString(), out Tool known))
                    {
                        allowed.Add(known);
                    }
                }
            }
            return allowed;
        }

        // Throws ArgumentException for an unknown tool or invalid arguments.
        private async Task<JsonObject> CallToolAsync(JsonObject parameters, CancellationToken token)
        {
            string name = parameters["name"]?.ToString() ?? "";
            if (_compactSession != null)
            {
                var result = await _compactSession.CallAsync(name, parameters["arguments"] as JsonObject ?? new(), token);
                return ToolResult(result);
            }
            if (_cartridgeSession != null && CartridgeMcpSession.Handles(name))
            {
                JsonObject routed = await _cartridgeSession.CallAsync(name, parameters["arguments"] as JsonObject ?? new(), token);
                return ToolResult(routed);
            }
            var descriptor = ToolContract.Get(name);
            Tool tool = ToolNames.Parse(name);
            var args = parameters["arguments"] as JsonObject ?? new();
            ToolContract.Validate(descriptor["inputSchema"]!.AsObject(), args);

            JsonObject response;
            if (tool == Tool.EvaluateTrace)
            {
                response = await EvaluateTraceAsync(args, token);
            }
            else if (tool == Tool.ListNodes)
            {
                response = ListNodes();
            }
            else
            {
                response = await CallNodeAsync(name, descriptor, args, token);
            }
            return ToolResult(response);
        }

        // Throws ArgumentException for invalid parameters or an unregistered node, and UnauthorizedAccessException for a different app.
        private async Task<JsonObject> RunCartridgeAsync(GatewayCartridges cartridges, GatewayRecipe recipe,
            JsonObject arguments, CancellationToken token)
        {
            ToolContract.Validate(recipe.Descriptor(_gateway._default == null)["inputSchema"]!.AsObject(), arguments);
            string? node = arguments["node"]?.ToString() ?? _gateway._default;
            if (node == null || !_gateway._clients.TryGetValue(node, out NodeTransportClient? client))
            {
                throw new ArgumentException("Select an administratively registered node.");
            }
            if (!recipe.IsProgramStart)
            {
                JsonObject windows = await client.CallAsync(Tool.ListWindows, new(), token);
                recipe.RequireApplication(windows, arguments["pid"]!.GetValue<int>());
            }
            var values = (JsonObject)arguments.DeepClone();
            values.Remove("node");

            return await Task.Run(() => cartridges.Run(recipe, values, frame =>
            {
                token.ThrowIfCancellationRequested();
                string action = frame["action"]!.ToString();
                JsonObject stepArguments = frame["args"]!.AsObject();

                return action == ToolNames.Of(Tool.EvaluateTrace)
                    ? EvaluateTraceAsync(stepArguments, token).GetAwaiter().GetResult()
                    : client.CallAsync(action, stepArguments, token).GetAwaiter().GetResult();
            }), token);
        }

        // Throws ArgumentException when no advisory provider is configured.
        private async Task<JsonObject> EvaluateTraceAsync(JsonObject args, CancellationToken token)
        {
            if (_gateway._advisor == null)
            {
                throw new ArgumentException("No advisory provider is configured.");
            }

            var evaluation = await _gateway._advisor.EvaluateAsync(args["state"]!, args["questions"]!.AsObject(), token);
            return evaluation.ToJson();
        }

        private JsonObject ListNodes()
        {
            var nodeEntries = _gateway._nodes.Select(n => (JsonNode)new JsonObject
            {
                ["name"] = n.Key,
                ["url"] = n.Value!["url"]!.DeepClone()
            });
            return new JsonObject { ["success"] = true, ["nodes"] = new JsonArray(nodeEntries.ToArray()) };
        }

        // Throws ArgumentException when the call names no registered node and there is no default.
        private async Task<JsonObject> CallNodeAsync(string name, JsonObject descriptor, JsonObject args, CancellationToken token)
        {
            string? node = args["node"]?.ToString() ?? _gateway._default;
            if (node == null || !_gateway._clients.TryGetValue(node, out var client))
            {
                throw new ArgumentException("Select an administratively registered node; URLs are not accepted.");
            }

            args = (JsonObject)args.DeepClone();
            args.Remove("node");
            if (descriptor["mutates"]!.GetValue<bool>())
            {
                args["request_id"] ??= Guid.CreateVersion7().ToString();
            }
            return await client.CallAsync(name, args, token);
        }

        // The image travels as MCP image content, not inside the structured result. A failure carries the full result as
        // text, because MCP clients show the model only the text of an error result and would hide why the action failed.
        private JsonObject ToolResult(JsonObject response)
        {
            var publicResult = (JsonObject)response.DeepClone();
            publicResult.Remove("image");
            bool succeeded = response["success"]?.GetValue<bool>() == true;
            // Protocol 2024-11-05 has no structuredContent, so the full result has to travel as text.
            bool fullText = !succeeded || _protocol == LegacyProtocol || _gateway._fullText;
            string text = fullText ? publicResult.ToJsonString() : "Action succeeded; structuredContent contains the result.";
            var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
            if (succeeded && response["image"] is { } image)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = image.ToString(),
                    ["mimeType"] = response["mime_type"]!.ToString()
                });
            }

            var result = new JsonObject { ["content"] = content, ["isError"] = !succeeded };
            if (_protocol != LegacyProtocol)
            {
                result["structuredContent"] = publicResult;
            }
            return result;
        }

        private async Task SendAsync(JsonObject frame)
        {
            await _outputLock.WaitAsync(_cancellation);
            try
            {
                await _output.WriteLineAsync(frame.ToJsonString());
                await _output.FlushAsync();
            }
            finally
            {
                _outputLock.Release();
            }
        }

        private static JsonObject Error(JsonNode? id, int code, string message)
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
            };
        }

        // Only the type goes to the client: the message can carry paths or content from the machine.
        // The cleaned message goes to standard error, which stays with the operator.
        private static JsonObject Failure(JsonNode? id, Exception exception)
        {
            Console.Error.WriteLine("Gateway failure: " + Diagnostics.Describe(exception));
            JsonObject frame = Error(id, InternalError, "Gateway failure; no automatic action replay.");
            frame["error"]!["data"] = new JsonObject { ["diagnostic"] = exception.GetType().Name };
            return frame;
        }
    }
}
