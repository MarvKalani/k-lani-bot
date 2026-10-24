using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Cartridges;
using KLaniBot.Startup;
using KLaniBot.Transport;
using KLaniBot.Updates;
using KLaniBot.Visibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KLaniBot.Fleet;

// Use case: an operator runs one master per site. Nodes pair with it once and then dial out to it over a
// WebSocket (ReverseNodeConnection), so a controller can reach a node behind NAT or a firewall through the
// master without the node opening a port. The master also hands out Windows node updates and runs the
// operator's console commands (approve, deny, revoke, invite, publish, service workflows).
//
// Each node gets two independent credentials: one the node uses to dial in, and one a controller uses to
// forward actions to that node. Only their hashes are stored. The master forwards; the node still applies
// its own policy, consent and guard to every action, so the master can never grant more than the node allows.
// A pairing is approved only on the master's console or through an invitation the operator created there,
// never by an HTTP request alone.
public sealed class PairingMaster
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PairingRequest> _requests = new();
    private readonly Dictionary<string, DateTimeOffset> _addresses = new();
    private readonly Dictionary<string, ReverseRelay> _relays = new();
    private readonly string _stateDirectory;
    private readonly string _origin;
    private readonly string _root;
    private readonly PairingOptions _options;
    private readonly JsonObject _devices;
    private readonly NodeUpdatePublisher _updates;
    private readonly FleetInvitationStore _enrollment = new();
    private MasterServiceWorkflows? _services;
    private readonly Dictionary<string, string> _versions = new();
    private readonly HashSet<string> _updateCapable = new();
    private readonly HashSet<string> _backupRequirementCapable = new(StringComparer.Ordinal);
    private readonly HashSet<string> _vitalsCapable = new(StringComparer.Ordinal);
    private readonly MasterNodeVitals _nodeVitals;
    private readonly Dictionary<string, DateTimeOffset> _updateOffers = new();
    private readonly MasterBackupRequirements _backupRequirements;
    private readonly ControllerAccess _access;

    private PairingMaster(
        string stateDirectory,
        string origin,
        string root,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        _stateDirectory = stateDirectory;
        _nodeVitals = new MasterNodeVitals(stateDirectory);
        _updates = new NodeUpdatePublisher(stateDirectory, certificate);
        _origin = origin;
        _root = root;
        string optionsPath = Path.Combine(stateDirectory, "pairing-options.json");
        _options = File.Exists(optionsPath)
            ? JsonSerializer.Deserialize<PairingOptions>(File.ReadAllText(optionsPath))!
            : new PairingOptions();
        _options.Validate();
        string devicesPath = Path.Combine(stateDirectory, "paired-devices.json");
        _devices = File.Exists(devicesPath) ? JsonNode.Parse(File.ReadAllText(devicesPath))!.AsObject() : new JsonObject();
        _backupRequirements = new MasterBackupRequirements(stateDirectory);
        _access = new ControllerAccess(stateDirectory);
    }

    private const int MaximumPort = 65535;
    private const long MaximumRequestBodyBytes = 4 * 1024 * 1024;
    private const int ConnectionsBeyondNodes = 128;
    private static readonly TimeSpan RequestHeadersTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WebSocketKeepAlive = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CertificateRotationInterval = TimeSpan.FromDays(7);
    private const int MaximumOpenRequests = 8;
    private const int MaximumRememberedAddresses = 256;
    private static readonly TimeSpan AddressCooldown = TimeSpan.FromMinutes(5);
    private const int MaximumAuthorizationLength = 300;
    private const int MaximumEnrollmentAuthorizationLength = 200;
    private const string BearerPrefix = "Bearer ";
    private const string CommandHelp = "Commands: approve TICKET | deny TICKET | revoke NODE | digits 0..6 | pending | "
        + "backup-requirement NODE[,NODE]|all required|off | backup-requirement-status | "
        + "controller-status | client-pause INSTANCE | client-resume INSTANCE | bot on|off | vitals-history NODE [HOURS]";

    // One master owns registry updates and approval state; a second process must not race them.
    private static readonly string[] StateFileNames =
    [
        "master.lock", "node-ca.pem", "node-ca.key", "paired-devices.json", "pairing-options.json", "nodes.json",
        ControllerAccess.StateFileName
    ];

    // Throws ArgumentException when bind or port is not concrete.
    public static async Task<int> RunAsync(string directory, string bind, int port, CancellationToken cancellation)
    {
        IPAddress address = IPAddress.Parse(bind);
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || port is < 1 or > MaximumPort)
        {
            throw new ArgumentException("Select a concrete master IP and port.");
        }
        directory = Path.GetFullPath(directory);
        PairingClient.CreatePrivateDirectory(directory);
        foreach (string name in StateFileNames)
        {
            CartridgeManifest.RejectLinks(Path.Combine(directory, name));
        }

        using var owner = new FileStream(
            Path.Combine(directory, "master.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var certificates = new ServerCertificates(directory, address);
        string host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
        string origin = $"https://{host}:{port}";
        var master = new PairingMaster(directory, origin, Convert.ToBase64String(certificates.Root.RawData), certificates.Root);
        master.RebuildRegistry();
        await using WebApplication app = BuildApplication(address, port, certificates, master._options.MaxNodes);
        master.MapEndpoints(app);
        await app.StartAsync(cancellation);
        await master.ServeAsync(app, directory, certificates, address, cancellation);
        return 0;
    }

    private static WebApplication BuildApplication(IPAddress address, int port, ServerCertificates certificates, int maximumNodes)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(address, port, listener =>
            {
                listener.Protocols = QuicListener.IsSupported ? HttpProtocols.Http1AndHttp2AndHttp3 : HttpProtocols.Http1AndHttp2;
                listener.UseHttps(https => https.ServerCertificateSelector = (_, _) => certificates.Current);
            });
            options.Limits.MaxRequestBodySize = MaximumRequestBodyBytes;
            options.Limits.MaxConcurrentConnections = 2 * maximumNodes + ConnectionsBeyondNodes;
            options.Limits.RequestHeadersTimeout = RequestHeadersTimeout;
        });

        WebApplication app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = WebSocketKeepAlive,
            KeepAliveTimeout = WebSocketKeepAlive
        });
        app.Use(RefuseBrowsersAndAnswerFailuresAsync);
        return app;
    }

    // Browsers add an Origin header; nothing here serves them. A request the handlers refuse ends as 400 or 403.
    private static async Task RefuseBrowsersAndAnswerFailuresAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Headers.Origin.Count != 0)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        try
        {
            await next(context);
        }
        catch (Exception exception) when (
            exception is ArgumentException or JsonException or InvalidDataException
                or FormatException or CryptographicException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapPost("/pair/start", (Delegate)StartAsync);
        app.MapPost("/pair/reveal", (Delegate)RevealAsync);
        app.MapPost("/pair/complete", (Delegate)CompleteAsync);
        app.MapPost("/pair/reject", (Delegate)RejectAsync);
        app.MapPost("/pair/enroll", (Delegate)EnrollAsync);
        app.Map("/connect/{node}", ConnectAsync);
        app.MapGet("/" + ProtocolIdentity.UpdateManifestRoute, (HttpContext context) => UpdateManifest(context));
        app.MapPost("/" + ProtocolIdentity.UpdateStatusRoute, (Delegate)ReceiveUpdateStatusAsync);
        app.MapGet(
            "/" + ProtocolIdentity.UpdateFileRoutePrefix + "{version}",
            (HttpContext context, string version) => UpdateFile(context, version));
        app.MapGet("/node/{node}/health", (string node) => Health(node));
        app.MapGet("/node/{node}/status", (HttpContext context, string node) => NodeStatus(context, node));
        app.MapPost("/node/{node}/action", (HttpContext context, string node) => ForwardAsync(context, node, false));
        app.MapPost("/node/{node}/session", (HttpContext context, string node) => ForwardAsync(context, node, true));
    }

    // Runs the console, discovery, rotation, administration and update loops until the application stops. A loop that
    // dies stops the application, so a broken master never looks healthy.
    private async Task ServeAsync(
        WebApplication app,
        string directory,
        ServerCertificates certificates,
        IPAddress address,
        CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, app.Lifetime.ApplicationStopping);
        _services = new MasterServiceWorkflows(directory, lifetime.Token);
        _ = Task.Run(() => ReadCommands(lifetime.Token), lifetime.Token);
        Task discovery = LanDiscovery.AdvertiseAsync(_origin, lifetime.Token);
        Task rotation = RotateAsync(certificates, address, lifetime.Token);
        Task administration = MasterAdministration.ServeAsync(directory, ExecuteCommand, lifetime.Token);
        Task distribution = DistributeUpdatesAsync(lifetime.Token);
        // Not in the stop set: a dead vitals loop only leaves vitals stale, it does not make a broken master look healthy.
        Task vitals = CollectVitalsAsync(lifetime.Token);
        _ = StopOnBackgroundFailureAsync(app, [rotation, administration, distribution], lifetime.Token);
        Console.WriteLine($"k-lani-bot Master {ProductIdentity.DisplayVersion}: {_origin}");
        Console.WriteLine(CommandHelp);
        Console.WriteLine("MCP registry: " + Path.Combine(directory, "nodes.json"));

        try
        {
            await app.WaitForShutdownAsync(cancellation);
        }
        finally
        {
            lifetime.Cancel();
            DisposeRelays();
            await discovery;
            await rotation;
            await administration;
            await distribution;
            await vitals;
        }
    }

    private void DisposeRelays()
    {
        lock (_gate)
        {
            foreach (var relay in _relays.Values)
            {
                relay.Dispose();
            }
        }
    }

    private static async Task StopOnBackgroundFailureAsync(WebApplication app, Task[] tasks, CancellationToken cancellation)
    {
        await Task.WhenAny(tasks);
        if (!cancellation.IsCancellationRequested)
        {
            // A dead approval/update loop must not leave an apparently healthy management service running.
            app.Lifetime.StopApplication();
        }
    }

    private static async Task RotateAsync(ServerCertificates certificates, IPAddress address, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(CertificateRotationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation))
            {
                certificates.Rotate(address);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task<IResult> StartAsync(HttpContext context)
    {
        JsonObject message = await ReadMessageAsync(context);
        if (message.Count != 2)
        {
            return Results.BadRequest();
        }
        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (string ticket in _requests.Where(entry => now >= entry.Value.ExpiresAt).Select(entry => entry.Key).ToArray())
            {
                _requests.Remove(ticket);
            }
            foreach (string ip in _addresses.Where(entry => now >= entry.Value).Select(entry => entry.Key).ToArray())
            {
                _addresses.Remove(ip);
            }
            string remote = context.Connection.RemoteIpAddress!.ToString();
            if (_requests.Count >= MaximumOpenRequests || _addresses.Count >= MaximumRememberedAddresses || _addresses.ContainsKey(remote))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
            var request = new PairingRequest(message["node"]!.ToString(), message["commitment"]!.ToString(), _root, _options, now);
            if (_devices.Any(device => device.Key.Equals(request.NodeName, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Conflict();
            }
            _requests.Add(request.Ticket, request);
            _addresses.Add(remote, now.Add(AddressCooldown));
            return Results.Json(new JsonObject
            {
                ["ticket"] = request.Ticket,
                ["commitment"] = PairingProtocol.Commitment(request.MasterNonce, _root),
                ["root"] = _root,
                ["digits"] = request.Options.CodeDigits,
                ["expires_at"] = request.ExpiresAt.ToString("O")
            });
        }
    }

    private async Task<IResult> RevealAsync(HttpContext context)
    {
        JsonObject message = await ReadMessageAsync(context);
        if (message.Count != 3)
        {
            return Results.BadRequest();
        }
        lock (_gate)
        {
            PairingRequest request = FindRequest(message);
            request.Reveal(message["nonce"]!.ToString(), message["public_key"]!.ToString(), DateTimeOffset.UtcNow);
            Console.WriteLine($"Pairing: {request.NodeName} | ticket {request.Ticket}");
            if (!Console.IsInputRedirected)
            {
                Console.WriteLine("Code: " + PairingProtocol.Code(request.Transcript, request.Options.CodeDigits));
            }
            Console.WriteLine("Confirm this exact request with: approve " + request.Ticket);
            return Results.Json(new JsonObject { ["nonce"] = request.MasterNonce });
        }
    }

    private async Task<IResult> CompleteAsync(HttpContext context)
    {
        JsonObject message = await ReadMessageAsync(context);
        if (message.Count != 2)
        {
            return Results.BadRequest();
        }
        lock (_gate)
        {
            PairingRequest request = FindRequest(message);
            if (DateTimeOffset.UtcNow >= request.ExpiresAt || request.State is PairingState.Rejected or PairingState.Consumed)
            {
                return Results.StatusCode(403);
            }
            if (request.State != PairingState.Approved)
            {
                return Results.StatusCode(409);
            }
            request.Complete(message["signature"]!.ToString(), DateTimeOffset.UtcNow);
            bool nameTaken = _devices.Any(device => device.Key.Equals(request.NodeName, StringComparison.OrdinalIgnoreCase));
            if (nameTaken || _devices.Count >= _options.MaxNodes)
            {
                return Results.Conflict();
            }
            string credential = RegisterDevice(request.NodeName, request.PublicKey);
            Console.WriteLine("Paired: " + request.NodeName);
            return Results.Json(new JsonObject { ["credential"] = credential });
        }
    }

    private async Task<IResult> EnrollAsync(HttpContext context)
    {
        JsonObject message = await ReadMessageAsync(context);
        string authorization = context.Request.Headers.Authorization.ToString();
        if (message.Count != 3 || !authorization.StartsWith(BearerPrefix, StringComparison.Ordinal)
            || authorization.Length > MaximumEnrollmentAuthorizationLength)
        {
            return Results.BadRequest();
        }
        lock (_gate)
        {
            string node = message["node"]!.ToString();
            if (_devices.Any(device => device.Key.Equals(node, StringComparison.OrdinalIgnoreCase)) || _devices.Count >= _options.MaxNodes)
            {
                return Results.Conflict();
            }
            _enrollment.Consume(
                authorization[BearerPrefix.Length..],
                node,
                message["public_key"]!.ToString(),
                message["signature"]!.ToString(),
                DateTimeOffset.UtcNow);
            string credential = RegisterDevice(node, message["public_key"]!.ToString());
            Console.WriteLine("Managed enrollment: " + node);
            return Results.Json(new JsonObject { ["credential"] = credential });
        }
    }

    private string RegisterDevice(string node, string publicKey)
    {
        string credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string controllerCredential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string controllerFile = Path.Combine(_stateDirectory, node + ".credential");
        PairingProtocol.WritePrivate(controllerFile, controllerCredential, true);
        _devices[node] = new JsonObject
        {
            ["public_key"] = publicKey,
            ["node_token_hash"] = CredentialHash(credential),
            ["controller_token_hash"] = CredentialHash(controllerCredential),
            ["token_file"] = controllerFile
        };
        PersistDevices();
        RebuildRegistry();
        return credential;
    }

    private async Task<IResult> RejectAsync(HttpContext context)
    {
        JsonObject message = await ReadMessageAsync(context);
        if (message.Count != 2)
        {
            return Results.BadRequest();
        }
        lock (_gate)
        {
            PairingRequest request = FindRequest(message);
            if (!PairingProtocol.VerifySignature(request.PublicKey, request.Transcript, message["signature"]!.ToString()))
            {
                return Results.StatusCode(403);
            }
            request.Reject();
            return Results.NoContent();
        }
    }

    // Throws UnauthorizedAccessException when no pairing has that ticket.
    private PairingRequest FindRequest(JsonObject message)
    {
        return _requests.TryGetValue(message["ticket"]!.ToString(), out PairingRequest? request)
            ? request : throw new UnauthorizedAccessException("Unknown pairing.");
    }

    // Throws InvalidDataException when the body is not a JSON object.
    private static async Task<JsonObject> ReadMessageAsync(HttpContext context)
    {
        if (!context.Request.HasJsonContentType())
        {
            throw new InvalidDataException("JSON required.");
        }
        return await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken: context.RequestAborted)
            ?? throw new InvalidDataException("Object required.");
    }

    private async Task ConnectAsync(HttpContext context)
    {
        string node = context.Request.RouteValues["node"]!.ToString()!;
        if (!context.WebSockets.IsWebSocketRequest || !Authenticate(context, node, "node_token_hash"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var relay = new ReverseRelay(socket);
        if (!TryRegisterRelay(node, relay, context.Request.Headers))
        {
            socket.Abort();
            return;
        }

        Console.WriteLine("Connected: " + node);
        _ = PollVitalsIfCapableAsync(node, relay, context.RequestAborted);
        try
        {
            await relay.ReadAsync(context.RequestAborted);
        }
        catch (Exception exception) when (
            exception is System.Net.WebSockets.WebSocketException or IOException or OperationCanceledException
                or JsonException or InvalidDataException)
        {
            Console.WriteLine("Disconnected: " + node);
        }
        finally
        {
            lock (_gate)
            {
                _relays.Remove(node);
                _updateCapable.Remove(node);
                _backupRequirementCapable.Remove(node);
                _vitalsCapable.Remove(node);
                _nodeVitals.Disconnected(node, relay.LastContact);
            }
        }
    }

    // A node that is revoked or already connected gets no second relay.
    private bool TryRegisterRelay(string node, ReverseRelay relay, IHeaderDictionary headers)
    {
        lock (_gate)
        {
            if (!_devices.ContainsKey(node) || _relays.ContainsKey(node))
            {
                return false;
            }
            _relays.Add(node, relay);
            if (headers[ProtocolIdentity.BackupRequirementHeader].ToString() == "1")
            {
                _backupRequirementCapable.Add(node);
                SendBackupRequirement(node, relay);
            }
            _versions[node] = headers[ProtocolIdentity.VersionHeader].ToString();
            if (headers[ProtocolIdentity.VitalsHeader].ToString() == "1")
            {
                _vitalsCapable.Add(node);
            }
            if (headers[ProtocolIdentity.UpdatesHeader].ToString() == "1")
            {
                _updateCapable.Add(node);
            }
            return true;
        }
    }

    // Authenticated like a forwarded call, but answered from the master's cache: it reaches no node, holds no desktop and
    // does not make the caller a client of the node, so list_nodes can ask about every node at once.
    private IResult NodeStatus(HttpContext context, string node)
    {
        if (!Authenticate(context, node, "controller_token_hash"))
        {
            return Results.Unauthorized();
        }
        lock (_gate)
        {
            JsonObject status = ControllerStatus.NodeJson(NodeRow(node, NodeVitalsSettings.Read(_stateDirectory), DateTimeOffset.UtcNow));
            status["success"] = true;
            return Results.Json(status);
        }
    }

    private IResult Health(string node)
    {
        lock (_gate)
        {
            return _devices.ContainsKey(node)
                ? Results.Json(new
                {
                    status = "ok",
                    connected = _relays.ContainsKey(node),
                    version = ProductIdentity.DisplayVersion
                })
                : Results.NotFound();
        }
    }

    private async Task<IResult> ForwardAsync(HttpContext context, string node, bool presence)
    {
        if (!Authenticate(context, node, "controller_token_hash"))
        {
            return Results.Unauthorized();
        }
        string session = context.Request.Headers[ProtocolIdentity.SessionHeader].ToString();
        if (!Guid.TryParse(session, out Guid identifier) || identifier.Version != 7)
        {
            return Results.BadRequest();
        }
        JsonObject body = await ReadMessageAsync(context);
        if (presence && (body.Count != 1 || body["event"]?.ToString() is not ("heartbeat" or "close")))
        {
            return Results.BadRequest();
        }
        ForwardedCall call = DescribeForwardedCall(context.Request.Headers, session, node, presence, body);
        IResult? controllerAnswer = AdmitForwardedCall(call);
        if (controllerAnswer != null)
        {
            return controllerAnswer;
        }
        ReverseRelay? relay;
        lock (_gate)
        {
            _relays.TryGetValue(node, out relay);
        }
        if (relay == null)
        {
            return OfflineReply(node, presence, body);
        }
        JsonObject result = await relay.CallAsync(RelayMessage(call, presence, body), context.RequestAborted);

        return Results.Json(AttachUpdateReport(result, node, presence, body));
    }

    // The node shows this master as the session's master; client and purpose stay the controller's claim.
    private JsonObject RelayMessage(ForwardedCall call, bool presence, JsonObject body)
    {
        return new JsonObject
        {
            ["kind"] = presence ? "presence" : "action",
            ["session"] = call.Session,
            ["body"] = body,
            ["presentation"] = SessionPresentation.Create(call.Presentation.Client, call.Presentation.Purpose,
                Environment.MachineName + " · " + _origin).ToJson()
        };
    }

    // Throws ArgumentException for an oversized or invalid presentation header.
    private static ForwardedCall DescribeForwardedCall(IHeaderDictionary headers, string session, string node, bool presence,
        JsonObject body)
    {
        SessionPresentation presentation = SessionPresentation.Decode(headers[SessionPresentation.Header].ToString());
        string instance = ControllerAccess.InstanceOf(headers[ProtocolIdentity.InstanceHeader].ToString(), session);
        string action = presence ? "" : body["action"]?.ToString() ?? "";

        return new ForwardedCall(instance, session, node, presentation, ForwardedRequestOf(presence, body), action);
    }

    private static ForwardedRequest ForwardedRequestOf(bool presence, JsonObject body)
    {
        if (!presence)
        {
            return ForwardedRequest.Action;
        }
        return body["event"]!.ToString() == "heartbeat" ? ForwardedRequest.Heartbeat : ForwardedRequest.Close;
    }

    // Every forwarded call updates the controller tray's client rows. Returns the master's own answer for a call of a paused
    // client or a switched-off bot, and null when the call goes on to the node.
    private IResult? AdmitForwardedCall(ForwardedCall call)
    {
        AccessDecision decision;
        JsonObject? refusal;
        lock (_gate)
        {
            _access.Record(call, DateTimeOffset.UtcNow);
            decision = _access.Decide(call);
            refusal = decision == AccessDecision.Refuse ? _access.Refusal() : null;
        }

        return decision switch
        {
            AccessDecision.Forward => null,
            AccessDecision.Refuse => Results.Json(refusal),
            AccessDecision.Absorb => Results.Json(new JsonObject { ["success"] = true, ["paused"] = true }),
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private IResult OfflineReply(string node, bool presence, JsonObject body)
    {
        return !presence && body["action"]?.ToString() == "get_machine_capabilities"
            ? Results.Json(OfflineManagement(node))
            : Results.Json(ActionResults.Failure(ActionOutcome.NotDispatched, "Node is offline."));
    }

    private JsonObject AttachUpdateReport(JsonObject result, string node, bool presence, JsonObject body)
    {
        if (!presence && body["action"]?.ToString() == "get_machine_capabilities")
        {
            result["update_report"] = ReadUpdateReport(node);
        }

        return result;
    }

    private JsonObject OfflineManagement(string node)
    {
        return new JsonObject
        {
            ["success"] = true,
            ["connected"] = false,
            ["update_report"] = ReadUpdateReport(node),
            ["capabilities"] = new JsonObject
            {
                ["nodeId"] = node,
                ["version"] = _versions.GetValueOrDefault(node, "unknown"),
                ["platform"] = "management-cache",
                ["features"] = new JsonArray(),
                ["activeTools"] = new JsonArray("get_machine_capabilities")
            }
        };
    }

    private bool Authenticate(HttpContext context, string node, string field)
    {
        string authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(BearerPrefix, StringComparison.Ordinal) || authorization.Length > MaximumAuthorizationLength)
        {
            return false;
        }
        lock (_gate)
        {
            string? expected = _devices[node]?[field]?.ToString();
            return expected != null && CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected), Convert.FromHexString(CredentialHash(authorization[BearerPrefix.Length..])));
        }
    }

    private static string CredentialHash(string credential)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
    }

    private void ReadCommands(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested && Console.ReadLine() is { } line)
        {
            try
            {
                Console.WriteLine(ExecuteCommand(line).ToJsonString());
            }
            catch (Exception exception) when (
                exception is ArgumentException or KeyNotFoundException or UnauthorizedAccessException
                    or IOException or InvalidOperationException)
            {
                Console.WriteLine("Command rejected: " + exception.Message);
            }
        }
    }

    // Throws InvalidOperationException when the node is not connected.
    private ReverseRelay RequireOnlineRelay(string node)
    {
        return _relays.TryGetValue(node, out ReverseRelay? connected)
            ? connected
            : throw new InvalidOperationException("Node is offline.");
    }

    // Throws ArgumentException for an unknown or invalid command and InvalidOperationException when its node is offline.
    private JsonObject ExecuteCommand(string line)
    {
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        lock (_gate)
        {
            return ExecuteCommandLocked(words);
        }
    }

    // Throws ArgumentException for an unknown command.
    private JsonObject ExecuteCommandLocked(string[] words)
    {
        string command = words.Length == 0 ? "" : words[0];
        if (words.Length == 2 && command == "approve")
            return Approve(words[1]);
        if (words.Length == 2 && command == "deny")
            return Deny(words[1]);
        if (words.Length == 2 && command == "digits" && int.TryParse(words[1], out int digits))
            return SetCodeDigits(digits);
        if (words.Length == 2 && command == "revoke")
            return Revoke(words[1]);
        if (words.Length == 3 && command == "service-inventory" && words[2] is "on" or "off")
            return RequestInventoryPermission(words[1], words[2] == "on");
        if (words.Length == 3 && command == "service-configure")
            return _services!.Configure(words[1], words[2]);
        if (words.Length == 2 && command == "service-scan")
            return _services!.StartScan(words[1], RequireOnlineRelay(words[1]));
        if (words.Length == 3 && command == "service-evidence")
            return _services!.StartScreenshot(words[1], words[2], RequireOnlineRelay(words[1]));
        if (words.Length == 2 && command == "service-status")
            return _services!.Status(words[1]);
        if (words.Length >= 3 && command is "service-review" or "service-message")
            return StartReview(words);
        if (words.Length is 2 or 3 && command == "invite")
            return Invite(words);
        if (words.Length >= 3 && command == "publish")
            return Publish(words);
        if (words.Length == 1 && command == "pending")
            return Pending();
        if (words.Length == 3 && command == "backup-requirement")
            return SetBackupRequirement(words[1], words[2]);
        if (words.Length == 1 && command == "backup-requirement-status")
            return _backupRequirements.Status();
        return ExecuteControllerCommand(words) ?? throw new ArgumentException(CommandHelp);
    }

    // The controller tray's commands; null for any other command. Throws ArgumentException for an unknown client instance
    // or a switch word other than on or off.
    private JsonObject? ExecuteControllerCommand(string[] words)
    {
        string command = words.Length == 0 ? "" : words[0];
        if (words.Length is 2 or 3 && command == "vitals-history")
            return VitalsHistory(words);
        if (words.Length == 1 && command == "controller-status")
            return _access.Status(ControllerNodes(), DateTimeOffset.UtcNow).ToJson();
        if (words.Length == 2 && command == "client-pause")
            return PauseClient(words[1]);
        if (words.Length == 2 && command == "client-resume")
        {
            _access.Resume(words[1]);
            return Succeeded();
        }
        if (words.Length == 2 && command == "bot")
        {
            _access.SwitchBot(words[1]);
            Console.WriteLine("Bot switched " + words[1] + " for MCP clients.");
            return Succeeded();
        }
        return null;
    }

    private List<ControllerNodeRow> ControllerNodes()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NodeVitalsSettings limits = NodeVitalsSettings.Read(_stateDirectory);
        return _devices.Select(device => device.Key).Order(StringComparer.Ordinal).Select(node => NodeRow(node, limits, now)).ToList();
    }

    // Throws ArgumentException for an unregistered node or hours outside 1 to 8784 (a leap year).
    private JsonObject VitalsHistory(string[] words)
    {
        if (!_devices.ContainsKey(words[1]))
        {
            throw new ArgumentException("No registered node has this name.");
        }
        int hours = 24;
        if (words.Length == 3 && (!int.TryParse(words[2], System.Globalization.CultureInfo.InvariantCulture, out hours)
            || hours is < 1 or > 8784))
        {
            throw new ArgumentException("Hours must be a whole number from 1 to 8784.");
        }
        return _nodeVitals.HistorySummary(words[1], TimeSpan.FromHours(hours));
    }

    private ControllerNodeRow NodeRow(string node, NodeVitalsSettings limits, DateTimeOffset now)
    {
        return _nodeVitals.Row(node, _versions.GetValueOrDefault(node, ""), _relays.GetValueOrDefault(node), limits, now);
    }

    private async Task PollVitalsIfCapableAsync(string node, ReverseRelay relay, CancellationToken cancellation)
    {
        bool capable;
        lock (_gate)
        {
            capable = _vitalsCapable.Contains(node);
        }
        if (capable)
        {
            await _nodeVitals.PollOnConnectAsync(node, relay, cancellation);
        }
    }

    // A node that is busy when its turn comes is skipped until the next round; its last answer stays shown meanwhile. The
    // interval is read each round, so a changed vitals.json applies from the next poll.
    private async Task CollectVitalsAsync(CancellationToken cancellation)
    {
        try
        {
            while (true)
            {
                NodeVitalsSettings settings = NodeVitalsSettings.Read(_stateDirectory);
                await Task.Delay(settings.PollInterval, cancellation);
                List<KeyValuePair<string, ReverseRelay>> polled;
                lock (_gate)
                {
                    polled = _relays.Where(relay => _vitalsCapable.Contains(relay.Key)).ToList();
                }
                await Task.WhenAll(polled.Select(relay => _nodeVitals.PollAsync(relay.Key, relay.Value, settings, cancellation)));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    // The node no longer shows the paused client as connected; the answer does not matter, a node that misses it expires
    // the session on its own.
    private JsonObject PauseClient(string instance)
    {
        foreach (NodeSession nodeSession in _access.Pause(instance))
        {
            if (_relays.TryGetValue(nodeSession.Node, out ReverseRelay? relay))
            {
                var close = new JsonObject
                {
                    ["kind"] = "presence",
                    ["session"] = nodeSession.Session,
                    ["body"] = new JsonObject { ["event"] = "close" }
                };
                _ = relay.CallAsync(close, CancellationToken.None);
            }
        }
        Console.WriteLine("MCP client paused: " + instance);
        return Succeeded();
    }

    private static JsonObject Succeeded()
    {
        return new JsonObject { ["success"] = true };
    }

    // Throws KeyNotFoundException for an unknown ticket.
    private JsonObject Approve(string ticket)
    {
        _requests[ticket].Approve(DateTimeOffset.UtcNow);
        Console.WriteLine("Approved: " + _requests[ticket].NodeName);
        return Succeeded();
    }

    // Throws KeyNotFoundException for an unknown ticket.
    private JsonObject Deny(string ticket)
    {
        _requests[ticket].Reject();
        return Succeeded();
    }

    private JsonObject SetCodeDigits(int digits)
    {
        new PairingOptions { CodeDigits = digits }.Validate();
        _options.CodeDigits = digits;
        PairingProtocol.WritePrivate(Path.Combine(_stateDirectory, "pairing-options.json"), JsonSerializer.Serialize(_options), true);
        Console.WriteLine("Code digits for new pairings: " + digits);
        return Succeeded();
    }

    private JsonObject Revoke(string node)
    {
        _devices.Remove(node);
        _backupRequirements.Forget(node);
        if (_relays.TryGetValue(node, out ReverseRelay? relay))
        {
            relay.Dispose();
        }
        PersistDevices();
        RebuildRegistry();
        Console.WriteLine("Revoked: " + node);
        return Succeeded();
    }

    // The node asks its own person; the answer arrives later, so the command only reports that it was requested.
    private JsonObject RequestInventoryPermission(string node, bool enabled)
    {
        ReverseRelay relay = RequireOnlineRelay(node);
        var permissionRequest = new JsonObject { ["kind"] = "inventory_permission", ["enabled"] = enabled };
        _ = relay.CallAsync(permissionRequest, CancellationToken.None);
        return new JsonObject { ["success"] = true, ["state"] = "requested" };
    }

    // Throws ArgumentException for an unknown node or word. Connected nodes that understand the order get it now, the others
    // when they connect with a version that does.
    private JsonObject SetBackupRequirement(string target, string word)
    {
        var paired = _devices.Select(device => device.Key).ToArray();
        IReadOnlyList<string> nodes = _backupRequirements.Request(target, word, paired, DateTimeOffset.UtcNow);
        var sent = new JsonArray();
        var pending = new JsonArray();

        foreach (string node in nodes)
        {
            bool reachable = _relays.TryGetValue(node, out ReverseRelay? relay) && _backupRequirementCapable.Contains(node);
            if (reachable)
            {
                SendBackupRequirement(node, relay!);
            }
            (reachable ? sent : pending).Add(node);
        }
        // Pending covers offline nodes and nodes whose version cannot receive the order yet; both get it when they connect.
        return new JsonObject { ["success"] = true, ["requirement"] = word, ["sent"] = sent, ["pending"] = pending };
    }

    // The answer arrives later; only a confirmed change of the same order counts as applied.
    private void SendBackupRequirement(string node, ReverseRelay relay)
    {
        JsonObject? message = _backupRequirements.PendingMessage(node);
        if (message == null)
        {
            return;
        }
        string requestedAt = message["requested_at"]!.ToString();
        _ = Task.Run(async () =>
        {
            JsonObject reply = await relay.CallAsync(message, CancellationToken.None);
            if (reply["success"]?.GetValue<bool>() == true)
            {
                lock (_gate)
                {
                    _backupRequirements.MarkApplied(node, requestedAt);
                }
                Console.WriteLine("Backup requirement applied: " + node);
            }
        });
    }

    private JsonObject StartReview(string[] words)
    {
        string node = words[1];
        ReverseRelay relay = RequireOnlineRelay(node);
        string? message = words[0] == "service-message" ? string.Join(' ', words.Skip(3)) : null;
        return _services!.StartReview(node, words[2], relay, message);
    }

    // Throws ArgumentException for a node that is already registered or an unknown consent mode.
    private JsonObject Invite(string[] words)
    {
        if (_devices.Any(device => device.Key.Equals(words[1], StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Node is already registered; revoke explicitly before re-enrollment.");
        }
        string consentModeText = words.Length == 3 ? words[2] : "ask";
        if (!ConsentModeNames.TryParse(consentModeText, out ConsentMode consentMode))
        {
            throw new ArgumentException("Invite requires a valid node name and ask/unattended mode.");
        }

        JsonObject invitation = _enrollment.Invite(words[1], _origin, _root, consentMode, DateTimeOffset.UtcNow);
        return new JsonObject { ["success"] = true, ["invitation"] = invitation };
    }

    private JsonObject Publish(string[] words)
    {
        string source = string.Join(' ', words.Skip(2));
        NodeUpdateManifest manifest = _updates.Publish(words[1], source);
        return new JsonObject { ["success"] = true, ["published"] = manifest.Version, ["sha256"] = manifest.Sha256 };
    }

    private JsonObject Pending()
    {
        var pending = new JsonArray();
        var awaitingApproval = _requests.Values.Where(request =>
            request.State == PairingState.AwaitingApproval && DateTimeOffset.UtcNow < request.ExpiresAt);
        foreach (var request in awaitingApproval)
        {
            pending.Add(new JsonObject
            {
                ["node"] = request.NodeName,
                ["ticket"] = request.Ticket,
                ["code"] = PairingProtocol.Code(request.Transcript, request.Options.CodeDigits),
                ["expires_at"] = request.ExpiresAt.ToString("O")
            });
        }
        return new JsonObject { ["success"] = true, ["pending"] = pending };
    }

    private IResult UpdateManifest(HttpContext context)
    {
        string node = context.Request.Headers[ProtocolIdentity.NodeHeader].ToString();
        if (!Authenticate(context, node, "node_token_hash"))
        {
            return Results.Unauthorized();
        }
        NodeUpdateManifest? manifest = _updates.Latest();
        return manifest == null ? Results.NotFound() : Results.Json(manifest);
    }

    private IResult UpdateFile(HttpContext context, string version)
    {
        string node = context.Request.Headers[ProtocolIdentity.NodeHeader].ToString();
        if (!Authenticate(context, node, "node_token_hash"))
        {
            return Results.Unauthorized();
        }
        string path = _updates.FileFor(version);
        return File.Exists(path) ? Results.File(path, "application/octet-stream", enableRangeProcessing: false) : Results.NotFound();
    }

    private async Task<IResult> ReceiveUpdateStatusAsync(HttpContext context)
    {
        string node = context.Request.Headers[ProtocolIdentity.NodeHeader].ToString();
        if (!Authenticate(context, node, "node_token_hash"))
        {
            return Results.Unauthorized();
        }
        JsonObject report = await ReadMessageAsync(context);
        if (report.ToJsonString().Length > 16 * 1024
            || !NodeUpdateManifest.IsProductVersion(report["supervisor_version"]?.ToString() ?? ""))
        {
            return Results.BadRequest();
        }
        string path = UpdateReportPath(node);
        PairingClient.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
        CartridgeManifest.RejectLinks(path);
        PairingProtocol.WritePrivate(path, report.ToJsonString(), true);

        return Results.Json(new
        {
            success = true
        });
    }

    private string UpdateReportPath(string node)
    {
        string name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(node)));

        return Path.Combine(_stateDirectory, "update-status", name + ".json");
    }

    private JsonObject? ReadUpdateReport(string node)
    {
        string path = UpdateReportPath(node);
        try
        {
            CartridgeManifest.RejectLinks(path);
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException)
        {
            return new JsonObject { ["unavailable"] = true, ["diagnostic"] = Diagnostics.Describe(failure) };
        }
    }

    private async Task DistributeUpdatesAsync(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation))
            {
                NodeUpdateManifest? manifest = _updates.Latest();
                if (manifest == null || manifest.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    continue;
                }
                var recipients = new List<ReverseRelay>();
                lock (_gate)
                {
                    foreach (string node in _updateCapable)
                    {
                        string offer = node + ":" + manifest.Sha256;
                        if (recipients.Count >= _options.MaxParallelUpdates)
                        {
                            break;
                        }
                        if (!_relays.TryGetValue(node, out ReverseRelay? relay)
                            || !System.Version.TryParse(_versions[node], out Version? current)
                            || current >= System.Version.Parse(manifest.Version)
                            || (_updateOffers.TryGetValue(offer, out DateTimeOffset previous)
                                && DateTimeOffset.UtcNow < previous.AddMinutes(5)))
                        {
                            continue;
                        }
                        _updateOffers[offer] = DateTimeOffset.UtcNow;
                        recipients.Add(relay);
                    }
                }
                await Task.WhenAll(recipients.Select(relay => relay.CallAsync(new JsonObject { ["kind"] = "update" }, cancellation)));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private void PersistDevices()
    {
        WriteAtomic("paired-devices.json", _devices);
    }

    private void RebuildRegistry()
    {
        var nodes = new JsonObject();
        foreach (var device in _devices)
        {
            nodes[device.Key] = new JsonObject
            {
                ["url"] = _origin + "/node/" + device.Key + "/",
                ["root_der_base64"] = _root,
                ["root_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(_root))),
                ["token_file"] = device.Value!["token_file"]!.ToString()
            };
        }
        WriteAtomic("nodes.json", new JsonObject { ["version"] = 1, ["nodes"] = nodes });
    }

    private void WriteAtomic(string name, JsonObject document)
    {
        string destination = Path.Combine(_stateDirectory, name);
        string temporary = destination + ".new";
        PairingProtocol.WritePrivate(temporary, document.ToJsonString(), true);
        CartridgeManifest.RejectLinks(destination);
        File.Move(temporary, destination, true);
    }
}
