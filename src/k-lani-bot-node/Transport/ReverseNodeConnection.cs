using System.Net.WebSockets;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Cartridges;
using KLaniBot.Common;
using KLaniBot.Fleet;
using KLaniBot.Guard;
using KLaniBot.Inventory;
using KLaniBot.Localization;
using KLaniBot.Platforms.Windows;
using KLaniBot.Startup;
using KLaniBot.Visibility;
using KLaniBot.Updates;

namespace KLaniBot.Transport;

// Use case: a paired node started with --connect dials out to its master and keeps that WebSocket open, so
// controllers reach it through the master without the node accepting connections. The master is trusted only
// through the CA saved at pairing. Every action that arrives this way runs as the node's local "operator"
// principal, so that principal's scopes, guard and consent decide, whatever the controller claims to be; the
// controller's name and purpose are only shown to the person at the machine.
//
// After a lost connection the node reconnects but never replays: the sessions of the old connection are
// closed, and an action whose answer was lost stays "unknown" for the controller. Besides actions, the master
// may switch inventory sharing, start a locally confirmed service check, or offer an update, which the node
// refuses when automatic updates are disabled in paired-master.json.
public static class ReverseNodeConnection
{
    private const string OperatorPrincipal = "operator";
    private const int MaximumControllerSessions = 16;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SessionCloseTimeout = TimeSpan.FromSeconds(5);

    // What every connection attempt needs; read once at start.
    private sealed record ReverseLink(Uri Endpoint, JsonObject Paired, NodePolicy Policy, string ConfigPath, WorkerSupervisor Worker);

    // A request from a controller, checked for its shape.
    private sealed record ControllerRequest(Guid RequestId, string Session, string? Kind, JsonObject Body, JsonObject? Presentation);

    public static async Task<int> RunAsync(string configPath, CancellationToken cancellation)
    {
        CartridgeManifest.RejectLinks(configPath);
        NodeConfiguration configuration = NodeConfiguration.Load(configPath);
        NodePolicy policy = NodePolicy.Load(configuration.PolicyPath);
        UiTexts.Use(policy.UiLanguage);
        JsonObject paired = ReadPairing(policy.StateRoot);
        Uri endpoint = ConnectEndpoint(paired, policy.NodeId);
        using var worker = CreateWorker(configuration, policy);
        await EstablishWorkerAsync(worker, cancellation);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task monitor = MonitorWorkerAsync(worker, lifetime);
        string? updatePlan;
        try
        {
            updatePlan = await ReconnectUntilStoppedAsync(new ReverseLink(endpoint, paired, policy, configPath, worker), lifetime);
        }
        finally
        {
            lifetime.Cancel();
            await monitor;
        }

        return cancellation.IsCancellationRequested || updatePlan != null ? 0 : worker.ExitCode;
    }

    private static WorkerSupervisor CreateWorker(NodeConfiguration configuration, NodePolicy policy)
    {
        try
        {
            return new WorkerSupervisor(configuration, policy);
        }
        catch (Exception failure) when (failure is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            string diagnostic = Diagnostics.Describe(failure);
            var channel = new UnavailableActuatorChannel(diagnostic);

            return new WorkerSupervisor(policy, configuration.PolicyPath, configuration.ActionTimeout,
                TimeSpan.FromSeconds(5), channel);
        }
    }

    private static async Task EstablishWorkerAsync(WorkerSupervisor worker, CancellationToken cancellation)
    {
        try
        {
            await worker.RequireVisibleWorkerAsync(cancellation);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or OperationCanceledException
            or System.Text.Json.JsonException)
        {
            cancellation.ThrowIfCancellationRequested();
            worker.MarkUnavailable();
            Console.Error.WriteLine("Desktop bootstrap failed; management remains available. " + Diagnostics.Describe(failure));
        }
    }

    // Throws InvalidDataException when the pairing file is unversioned.
    private static JsonObject ReadPairing(string stateRoot)
    {
        string pairedPath = Path.Combine(stateRoot, "paired-master.json");
        CartridgeManifest.RejectLinks(pairedPath);
        JsonObject paired = JsonNode.Parse(File.ReadAllText(pairedPath))!.AsObject();

        if (paired["version"]?.GetValue<int>() != 1)
        {
            throw new InvalidDataException("Versioned pairing required.");
        }
        return paired;
    }

    // Throws InvalidDataException when the saved master address is not a plain HTTPS origin.
    private static Uri ConnectEndpoint(JsonObject paired, string nodeId)
    {
        Uri master = new(paired["master"]!.ToString());
        bool plainOrigin = master.Scheme == "https" && master.UserInfo == "" && master.Query == "" && master.Fragment == ""
            && master.AbsolutePath == "/";
        if (!plainOrigin)
        {
            throw new InvalidDataException("Invalid saved master address.");
        }

        return new UriBuilder(master) { Scheme = "wss", Path = "/connect/" + Uri.EscapeDataString(nodeId) }.Uri;
    }

    private static async Task MonitorWorkerAsync(WorkerSupervisor worker, CancellationTokenSource lifetime)
    {
        try
        {
            await worker.WaitForExitAsync(lifetime.Token);
            worker.MarkUnavailable();
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Returns the staged update plan when the master's update was accepted, otherwise null once the lifetime ends.
    private static async Task<string?> ReconnectUntilStoppedAsync(ReverseLink link, CancellationTokenSource lifetime)
    {
        string? updatePlan = null;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using ClientWebSocket socket = CreateSocket(link.Paired);
                var sessions = new HashSet<string>();
                try
                {
                    updatePlan = await ServeConnectionAsync(socket, sessions, link, lifetime.Token);
                    if (updatePlan != null)
                    {
                        WindowsNodeUpdater.StartApply(updatePlan, link.Policy.StateRoot);
                        lifetime.Cancel();
                        break;
                    }
                }
                catch (Exception exception) when (IsLostConnection(exception))
                {
                    if (lifetime.IsCancellationRequested)
                    {
                        break;
                    }
                    Console.Error.WriteLine("Master connection unavailable; reconnecting without replaying actions.");
                }
                finally
                {
                    await CloseSessionsAsync(link.Worker, sessions);
                }
                await Task.Delay(ReconnectDelay, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        return updatePlan;
    }

    private static bool IsLostConnection(Exception exception)
    {
        return exception is WebSocketException or IOException or OperationCanceledException
            or System.Text.Json.JsonException or InvalidDataException;
    }

    private static ClientWebSocket CreateSocket(JsonObject paired)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + paired["credential"]!.ToString());
        socket.Options.SetRequestHeader("X-Request-ID", Guid.CreateVersion7().ToString());
        socket.Options.SetRequestHeader(ProtocolIdentity.VersionHeader, ProductIdentity.DisplayVersion);
        bool acceptsUpdates = OperatingSystem.IsWindows() && paired["auto_update"]?.GetValue<bool>() != false;
        socket.Options.SetRequestHeader(ProtocolIdentity.UpdatesHeader, acceptsUpdates ? "1" : "0");
        socket.Options.SetRequestHeader(ProtocolIdentity.BackupRequirementHeader, "1");
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            PairingProtocol.ValidateCertificate(certificate, errors, paired["root"]!.ToString());
        socket.Options.KeepAliveInterval = KeepAliveInterval;
        socket.Options.KeepAliveTimeout = KeepAliveTimeout;
        return socket;
    }

    private static async Task<string?> ServeConnectionAsync(
        ClientWebSocket socket,
        HashSet<string> sessions,
        ReverseLink link,
        CancellationToken lifetime)
    {
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        connecting.CancelAfter(ConnectTimeout);
        await socket.ConnectAsync(link.Endpoint, connecting.Token);

        WriteReadiness(link.Policy.StateRoot, link.Worker.WorkerAvailable);
        await NodeUpdateReporting.SendAsync(link.Policy);
        _ = WindowsNodeUpdater.CleanupStagingAsync(link.Policy.StateRoot, lifetime);
        return await RunSessionAsync(socket, sessions, link, lifetime);
    }

    // Tells the local setup that the node reached its master; replaced atomically so a reader never sees half a file.
    private static void WriteReadiness(string stateRoot, bool workerReady)
    {
        string readiness = Path.Combine(stateRoot, "node-ready.json");
        var content = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["version"] = ProductIdentity.DisplayVersion,
            ["master_connected"] = true,
            ["worker_ready"] = workerReady
        };
        PairingProtocol.WritePrivate(readiness + ".new", content.ToJsonString(), true);
        CartridgeManifest.RejectLinks(readiness);
        File.Move(readiness + ".new", readiness, true);
    }

    private static async Task CloseSessionsAsync(WorkerSupervisor worker, HashSet<string> sessions)
    {
        foreach (string session in sessions)
        {
            using var deadline = new CancellationTokenSource(SessionCloseTimeout);
            await worker.NotifySessionAsync(new RequestContext(OperatorPrincipal, session), false, deadline.Token);
        }
    }

    // Returns the staged update plan when the master's update was accepted, otherwise null when the master closes.
    private static async Task<string?> RunSessionAsync(
        ClientWebSocket socket,
        HashSet<string> sessions,
        ReverseLink link,
        CancellationToken cancellation)
    {
        while (await PairingProtocol.ReceiveAsync(socket, cancellation) is { } message)
        {
            if (IsInventoryPermission(message, out Guid permissionId))
            {
                await AnswerInventoryPermissionAsync(socket, permissionId, message, link.Worker, cancellation);
                continue;
            }
            if (IsBackupRequirement(message, out Guid requirementId, out BackupRequirement requirement))
            {
                await AnswerBackupRequirementAsync(socket, requirementId, requirement, message, link.Worker, cancellation);
                continue;
            }
            if (IsServiceCheck(message, out Guid serviceId, out JsonObject serviceRequest))
            {
                await AnswerServiceCheckAsync(socket, serviceId, serviceRequest, link.Policy.StateRoot, cancellation);
                continue;
            }
            if (IsUpdate(message, out Guid updateId))
            {
                string? plan = await AnswerUpdateAsync(socket, updateId, link, cancellation);
                if (plan != null)
                {
                    return plan;
                }
                continue;
            }
            await AnswerControllerRequestAsync(socket, message, sessions, link, cancellation);
        }
        return null;
    }

    private static bool HasVersion7Id(JsonObject message, out Guid id)
    {
        return Guid.TryParse(message["id"]?.ToString(), out id) && id.Version == 7;
    }

    private static bool IsInventoryPermission(JsonObject message, out Guid id)
    {
        id = Guid.Empty;
        return message.Count == 3 && message["kind"]?.ToString() == "inventory_permission" && message["enabled"] != null
            && HasVersion7Id(message, out id);
    }

    private static bool IsBackupRequirement(JsonObject message, out Guid id, out BackupRequirement requirement)
    {
        id = Guid.Empty;
        string? word = message["requirement"]?.ToString();
        requirement = word == "off" ? BackupRequirement.Off : BackupRequirement.Required;
        return message.Count == 4 && message["kind"]?.ToString() == "backup_requirement" && word is "required" or "off"
            && message["requested_at"] != null && HasVersion7Id(message, out id);
    }

    private static bool IsServiceCheck(JsonObject message, out Guid id, out JsonObject serviceRequest)
    {
        id = Guid.Empty;
        serviceRequest = message["body"] as JsonObject ?? new JsonObject();
        return message.Count == 3 && message["kind"]?.ToString() == "service_check" && message["body"] is JsonObject
            && HasVersion7Id(message, out id);
    }

    private static bool IsUpdate(JsonObject message, out Guid id)
    {
        id = Guid.Empty;
        return message.Count == 2 && message["kind"]?.ToString() == "update" && HasVersion7Id(message, out id);
    }

    // Throws IOException when the worker does not confirm the change, because the permission channel may have lost step.
    private static async Task AnswerInventoryPermissionAsync(
        ClientWebSocket socket,
        Guid permissionId,
        JsonObject message,
        WorkerSupervisor worker,
        CancellationToken cancellation)
    {
        JsonObject permission;
        try
        {
            permission = await worker.SetInventoryPermissionAsync(message["enabled"]!.GetValue<bool>(), cancellation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            throw new IOException("Inventory permission channel lost synchronization; reconnect safely.", exception);
        }

        var permissionReply = new JsonObject { ["id"] = permissionId.ToString(), ["result"] = permission };
        await PairingProtocol.SendAsync(socket, permissionReply, cancellation);
    }

    // Throws IOException when the worker does not confirm the change, because the control channel may have lost step.
    private static async Task AnswerBackupRequirementAsync(
        ClientWebSocket socket,
        Guid requirementId,
        BackupRequirement requirement,
        JsonObject message,
        WorkerSupervisor worker,
        CancellationToken cancellation)
    {
        JsonObject result;
        try
        {
            result = await worker.SetBackupRequirementAsync(requirement, message["requested_at"]!.ToString(), cancellation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            throw new IOException("Backup requirement channel lost synchronization; reconnect safely.", exception);
        }

        var reply = new JsonObject { ["id"] = requirementId.ToString(), ["result"] = result };
        await PairingProtocol.SendAsync(socket, reply, cancellation);
    }

    private static async Task AnswerServiceCheckAsync(
        ClientWebSocket socket,
        Guid serviceId,
        JsonObject serviceRequest,
        string stateRoot,
        CancellationToken cancellation)
    {
        JsonObject response;
        try
        {
            // This serialized loop is the only action ingress. Keep it occupied
            // until the local person finishes, so MCP cannot click its own approval.
            response = await ServiceCheck.RunAsync(serviceRequest, stateRoot, cancellation);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or ArgumentException or OperationCanceledException)
        {
            response = ActionResults.Failure(ActionOutcome.NotDispatched, "Local review unavailable or expired.");
        }

        var serviceReply = new JsonObject { ["id"] = serviceId.ToString(), ["result"] = response };
        await PairingProtocol.SendAsync(socket, serviceReply, cancellation);
    }

    // Returns the staged plan, or null when the update was refused; the master hears the outcome either way.
    private static async Task<string?> AnswerUpdateAsync(
        ClientWebSocket socket,
        Guid updateId,
        ReverseLink link,
        CancellationToken cancellation)
    {
        string? plan = null;
        JsonObject updateResult;
        try
        {
            plan = await StageUpdateAsync(link, cancellation);
            updateResult = new JsonObject { ["success"] = plan != null, ["outcome"] = "staged" };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException
                or HttpRequestException or OperationCanceledException or InvalidOperationException
                or ArgumentException or FormatException
                or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
        {
            updateResult = ActionResults.Failure(ActionOutcome.NotDispatched, "Update rejected: " + exception.GetType().Name);
        }

        var updateReply = new JsonObject { ["id"] = updateId.ToString(), ["result"] = updateResult };
        await PairingProtocol.SendAsync(socket, updateReply, cancellation);
        return plan;
    }

    // Throws UnauthorizedAccessException when automatic updates are disabled locally.
    private static async Task<string?> StageUpdateAsync(ReverseLink link, CancellationToken cancellation)
    {
        if (link.Paired["auto_update"]?.GetValue<bool>() == false)
        {
            throw new UnauthorizedAccessException("Automatic updates are disabled locally.");
        }

        return await WindowsNodeUpdater.StageAsync(link.Paired, link.Policy, link.ConfigPath, cancellation);
    }

    private static async Task AnswerControllerRequestAsync(
        ClientWebSocket socket,
        JsonObject message,
        HashSet<string> sessions,
        ReverseLink link,
        CancellationToken cancellation)
    {
        ControllerRequest request = ReadControllerRequest(message);
        AdmitSession(sessions, request.Session);

        var context = new RequestContext(OperatorPrincipal, request.Session);
        if (link.Worker.WorkerAvailable)
        {
            try
            {
                await link.Worker.SetSessionPresentationAsync(context, Presentation(request, link), cancellation);
            }
            catch (IOException)
            {
                // A failed presentation channel stops desktop input, but authenticated management is still useful.
                link.Worker.MarkUnavailable();
            }
        }
        JsonObject result = await DispatchAsync(request, context, sessions, link.Worker, cancellation);
        var reply = new JsonObject { ["id"] = request.RequestId.ToString(), ["result"] = result };
        await PairingProtocol.SendAsync(socket, reply, cancellation);
    }

    // Throws InvalidDataException for a request without UUIDv7 id and session or without a body.
    private static ControllerRequest ReadControllerRequest(JsonObject message)
    {
        bool validShape = (message.Count is 4 or 5) && (message.Count == 4 || message["presentation"] is JsonObject);
        if (!validShape
            || !HasVersion7Id(message, out Guid requestId)
            || !Guid.TryParse(message["session"]?.ToString(), out Guid sessionId) || sessionId.Version != 7
            || message["body"] is not JsonObject body)
        {
            throw new InvalidDataException("Invalid reverse request.");
        }

        return new ControllerRequest(requestId, sessionId.ToString(), message["kind"]?.ToString(), body,
            message["presentation"] as JsonObject);
    }

    // Throws InvalidDataException when a new session would exceed the limit.
    private static void AdmitSession(HashSet<string> sessions, string session)
    {
        if (!sessions.Contains(session) && sessions.Count >= MaximumControllerSessions)
        {
            throw new InvalidDataException("Too many controller sessions.");
        }

        sessions.Add(session);
    }

    // The controller's name and purpose are its claim; the master address and the granted scopes come from this node.
    private static SessionPresentation Presentation(ControllerRequest request, ReverseLink link)
    {
        SessionPresentation claimed = request.Presentation is { } presented
            ? SessionPresentation.FromJson(presented)
            : SessionPresentation.Create(null, null);
        string masterDisplay = claimed.Master.Length == 0 ? link.Paired["master"]!.ToString() : claimed.Master;
        string scopes = string.Join(", ", link.Policy.Get(OperatorPrincipal).Scopes.Select(WireNames<Scope>.Of));

        return SessionPresentation.Create(claimed.Client, claimed.Purpose, masterDisplay, link.Policy.NodeId, scopes);
    }

    // Throws InvalidDataException for a kind that is neither an action nor a presence event.
    private static async Task<JsonObject> DispatchAsync(
        ControllerRequest request,
        RequestContext context,
        HashSet<string> sessions,
        WorkerSupervisor worker,
        CancellationToken cancellation)
    {
        if (request.Kind == "action")
        {
            return await worker.ExecuteAsync(request.Body, context, cancellation);
        }
        bool presenceEvent = request.Kind == "presence" && request.Body.Count == 1
            && request.Body["event"]?.ToString() is "heartbeat" or "close";
        if (!presenceEvent)
        {
            throw new InvalidDataException("Unknown reverse request kind.");
        }

        bool connected = request.Body["event"]!.ToString() == "heartbeat";
        if (!worker.WorkerAvailable)
        {
            if (!connected)
            {
                sessions.Remove(request.Session);
            }
            return new JsonObject { ["success"] = true, ["management_only"] = true, ["worker_available"] = false };
        }
        JsonObject result = await worker.NotifySessionAsync(context, connected, cancellation);
        if (!connected)
        {
            sessions.Remove(request.Session);
        }
        return result;
    }
}
