using System.Net;
using System.Net.Quic;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Common;
using KLaniBot.Guard;
using KLaniBot.Localization;
using KLaniBot.Platforms.MacOS;
using KLaniBot.Startup;
using KLaniBot.Visibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KLaniBot.Transport;

// Use case: a node started with --serve accepts controllers over the network (HTTPS over HTTP/3, or HTTP/2
// where QUIC is missing). This is the network edge only: it authenticates the bearer token, requires the
// "observe" scope that every session needs, and hands the request to the WorkerSupervisor. It never touches
// the desktop itself, and nothing a client sends here can widen what the supervisor and the dispatcher allow.
//
// The API has no browser clients, so a request carrying an Origin header (which browsers add) is refused.
// Limits on body size, connections and header time keep one misbehaving client from exhausting the node.
public static class NodeServer
{
    private const string BearerPrefix = "Bearer ";
    private const long MaximumRequestBodyBytes = 4 * 1024 * 1024;
    private const int MaximumConnections = 64;
    private static readonly TimeSpan RequestHeadersTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PermissionRequestPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SettingsOpenTimeout = TimeSpan.FromSeconds(5);
    private const string ScreenCaptureSettings = "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture";

    // The leaf certificate lives 13 days and is replaced every 7, so a node that runs for months never
    // serves an expired certificate.
    private static readonly TimeSpan CertificateRotationInterval = TimeSpan.FromDays(7);

    public static async Task<int> RunAsync(string configFile, CancellationToken cancellation)
    {
        NodeConfiguration configuration = NodeConfiguration.Load(configFile);
        var policy = NodePolicy.Load(configuration.PolicyPath);
        UiTexts.Use(policy.UiLanguage);
        var address = IPAddress.Parse(configuration.Bind);
        using var certificates = new ServerCertificates(policy.StateRoot, address);
        using var worker = new WorkerSupervisor(configuration, policy);
        await worker.RequireVisibleWorkerAsync(cancellation);

        bool quic = QuicListener.IsSupported;
        await using WebApplication app = BuildApplication(address, configuration.HttpsPort, quic, certificates);
        MapEndpoints(app, policy, worker, quic);
        await app.StartAsync(cancellation);

        return await ServeUntilShutdownAsync(app, policy, worker, certificates, address, cancellation);
    }

    private static WebApplication BuildApplication(IPAddress address, int port, bool quic, ServerCertificates certificates)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(address, port, listener =>
            {
                listener.Protocols = quic ? HttpProtocols.Http2 | HttpProtocols.Http3 : HttpProtocols.Http2;
                listener.UseHttps(https => https.ServerCertificateSelector = (_, _) => certificates.Current);
            });
            options.Limits.MaxRequestBodySize = MaximumRequestBodyBytes;
            options.Limits.MaxConcurrentConnections = MaximumConnections;
            options.Limits.RequestHeadersTimeout = RequestHeadersTimeout;
        });

        return builder.Build();
    }

    private static void MapEndpoints(WebApplication app, NodePolicy policy, WorkerSupervisor worker, bool quic)
    {
        app.MapGet("/health", () => Results.Json(new
        {
            status = "ok",
            version = ProductIdentity.DisplayVersion,
            http3_available = quic
        }));
        // The explicit return type keeps these lambdas from binding as a RequestDelegate, which would discard the result.
        app.MapPost("/session", Task<IResult> (HttpContext context) => HandleSessionAsync(context, policy, worker));
        app.MapPost("/action", Task<IResult> (HttpContext context) => HandleActionAsync(context, policy, worker));
    }

    private static async Task<IResult> HandleSessionAsync(HttpContext context, NodePolicy policy, WorkerSupervisor worker)
    {
        try
        {
            Result<RequestContext, IResult> admission = Admit(context, policy);
            return await admission.Match(caller => NotifySessionAsync(context, caller, policy, worker), Task.FromResult);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return Results.BadRequest();
        }
    }

    private static async Task<IResult> HandleActionAsync(HttpContext context, NodePolicy policy, WorkerSupervisor worker)
    {
        try
        {
            Result<RequestContext, IResult> admission = Admit(context, policy);
            return await admission.Match(caller => ExecuteActionAsync(context, caller, policy, worker), Task.FromResult);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return Results.BadRequest();
        }
    }

    // The checks every request passes before its body is read, in this order: a bearer token, a principal with the
    // observe scope, no browser Origin, a JSON body.
    // Throws UnauthorizedAccessException for an unknown token or a principal without the observe scope.
    private static Result<RequestContext, IResult> Admit(HttpContext context, NodePolicy policy)
    {
        string authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return Result<RequestContext, IResult>.Failure(Results.Unauthorized());
        }

        string sessionId = context.Request.Headers[ProtocolIdentity.SessionHeader].ToString();
        var caller = new RequestContext(policy.Authenticate(authorization[BearerPrefix.Length..]), sessionId);
        policy.Authorize(caller, Scope.Observe);
        if (context.Request.Headers.Origin.Count != 0)
        {
            return Result<RequestContext, IResult>.Failure(Results.StatusCode(StatusCodes.Status403Forbidden));
        }
        if (!context.Request.HasJsonContentType())
        {
            return Result<RequestContext, IResult>.Failure(Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        }
        return Result<RequestContext, IResult>.Success(caller);
    }

    private static async Task<IResult> NotifySessionAsync(
        HttpContext context,
        RequestContext caller,
        NodePolicy policy,
        WorkerSupervisor worker)
    {
        var message = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken: context.RequestAborted);
        if (message?.Count != 1 || message["event"]?.ToString() is not ("heartbeat" or "close"))
        {
            return Results.BadRequest();
        }

        await SetDirectPresentationAsync(context, caller, policy, worker);
        bool isHeartbeat = message["event"]!.ToString() == "heartbeat";
        return Results.Json(await worker.NotifySessionAsync(caller, isHeartbeat, context.RequestAborted));
    }

    private static async Task<IResult> ExecuteActionAsync(
        HttpContext context,
        RequestContext caller,
        NodePolicy policy,
        WorkerSupervisor worker)
    {
        var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken: context.RequestAborted);
        if (request == null)
        {
            return Results.BadRequest();
        }

        await SetDirectPresentationAsync(context, caller, policy, worker);
        return Results.Json(await worker.ExecuteAsync(request, caller, context.RequestAborted));
    }

    private static async Task SetDirectPresentationAsync(
        HttpContext http,
        RequestContext caller,
        NodePolicy policy,
        WorkerSupervisor worker)
    {
        SessionPresentation claimed = SessionPresentation.Decode(http.Request.Headers[SessionPresentation.Header].ToString());
        // A direct client cannot claim to be an authenticated master. The
        // connection address and granted rights come from the local server.
        string address = http.Connection.RemoteIpAddress?.ToString() ?? UiTexts.Get(UiText.AddressUnknown);
        string connection = UiTexts.Format(UiText.DirectConnection, address);
        SessionPresentation display = SessionPresentation.Create(
            claimed.Client,
            claimed.Purpose,
            connection,
            policy.NodeId,
            string.Join(", ", policy.Get(caller.PrincipalId).Scopes.Select(WireNames<Scope>.Of)));
        await worker.SetSessionPresentationAsync(caller, display, http.RequestAborted);
    }

    // Runs until the application stops or the caller cancels. A worker that exits stops the application; its exit code
    // becomes the node's.
    private static async Task<int> ServeUntilShutdownAsync(
        WebApplication app,
        NodePolicy policy,
        WorkerSupervisor worker,
        ServerCertificates certificates,
        IPAddress address,
        CancellationToken cancellation)
    {
        using var visibleLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, app.Lifetime.ApplicationStopping);
        Task<int> monitor = Task.Run(() => StopWhenWorkerExitsAsync(app, worker, visibleLifetime.Token));
        using var onboarding = new PeriodicTimer(PermissionRequestPollInterval);
        Task onboardingTask = Task.Run(() => AnswerPermissionRequestsAsync(onboarding, policy, cancellation), cancellation);
        using var rotation = new PeriodicTimer(CertificateRotationInterval);
        Task rotate = Task.Run(() => RotateCertificatesAsync(rotation, certificates, address, cancellation), cancellation);

        try
        {
            await app.WaitForShutdownAsync(cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Shutdown was requested by the caller; the finally block stops the background loops.
        }
        finally
        {
            visibleLifetime.Cancel();
            rotation.Dispose();
            onboarding.Dispose();
            await AwaitCancelledQuietlyAsync(monitor);
            await AwaitCancelledQuietlyAsync(rotate);
            await AwaitCancelledQuietlyAsync(onboardingTask);
        }
        return monitor.IsCompletedSuccessfully ? monitor.Result : 0;
    }

    private static async Task<int> StopWhenWorkerExitsAsync(WebApplication app, WorkerSupervisor worker, CancellationToken lifetime)
    {
        await worker.WaitForExitAsync(lifetime);

        int exitCode = worker.ExitCode == 0 ? 0 : 1;
        app.Lifetime.StopApplication();
        return exitCode;
    }

    private static async Task RotateCertificatesAsync(
        PeriodicTimer rotation,
        ServerCertificates certificates,
        IPAddress address,
        CancellationToken cancellation)
    {
        while (await rotation.WaitForNextTickAsync(cancellation))
        {
            certificates.Rotate(address);
        }
    }

    // On macOS, the operator's setup command asks the running node for the privacy permissions through a private file,
    // because only the running app has the code identity the permissions are granted to.
    private static async Task AnswerPermissionRequestsAsync(PeriodicTimer onboarding, NodePolicy policy, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string requestPath = Path.Combine(policy.StateRoot, "permissions-request.json");
        string reportPath = Path.Combine(policy.StateRoot, "permissions-report.json");
        while (await onboarding.WaitForNextTickAsync(cancellation))
        {
            if (File.Exists(requestPath))
            {
                AnswerPermissionRequest(requestPath, reportPath);
            }
        }
    }

    // Throws InvalidDataException for a request that is not version 1 with a UUIDv7 request id.
    private static void AnswerPermissionRequest(string requestPath, string reportPath)
    {
        CartridgeManifest.RejectLinks(requestPath);
        var request = JsonNode.Parse(File.ReadAllText(requestPath))!.AsObject();
        Guid identifier = Guid.Empty;
        bool wellFormed = request.Count == 2
            && request["version"]?.GetValue<int>() == 1
            && Guid.TryParse(request["request_id"]?.ToString(), out identifier)
            && identifier.Version == 7;
        if (!wellFormed)
        {
            throw new InvalidDataException("Invalid operator permission request.");
        }

        File.Delete(requestPath);
        // TCC request APIs can wait in a console LaunchAgent while the
        // user decides. Keep the authenticated node responsive and use
        // supported Settings navigation for this service context.
        var result = JsonSerializer.SerializeToNode(MacUiService.Permissions())!.AsObject();
        result["request_id"] = identifier.ToString();
        string pane = result["accessibility"]?.GetValue<bool>() == true ? "Privacy_ScreenCapture" : "Privacy_Accessibility";
        string settingsUrl = "x-apple.systempreferences:com.apple.preference.security?" + pane;
        var opened = ProcessRunner.Run("/usr/bin/open", [settingsUrl], (int)SettingsOpenTimeout.TotalMilliseconds);
        result["settings_opened"] = opened.Success;
        result["target_bundle_id"] = ProtocolIdentity.BundleIdentifier;
        result["screen_capture_settings"] = ScreenCaptureSettings;
        PrivateFile.WriteAllText(reportPath, result.ToJsonString(), replace: true);
    }

    // Cancelling the lifetime is how the loops are told to stop, so their cancellation is the expected end.
    private static async Task AwaitCancelledQuietlyAsync(Task backgroundLoop)
    {
        try
        {
            await backgroundLoop;
        }
        catch (OperationCanceledException)
        {
            // Expected after visibleLifetime.Cancel().
        }
    }
}
