using System.Net;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KLaniBot.Transport;

// Optional local diagnostics only. Fleet traffic uses the authenticated .NET HTTPS node server.
public sealed class HttpMcpServer(DesktopActor actor, NodePolicy policy, int port)
{
    private const long MaximumRequestBodyBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan RequestHeadersTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(15);
    private const string BearerPrefix = "Bearer ";
    private const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; "
        + "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    public async Task StartAsync(CancellationToken cancellation)
    {
        await using WebApplication app = BuildApplication();
        app.Use(RefuseForeignRequestsAsync);
        app.MapGet("/health", () => Results.Json(new
        {
            status = "ok",
            transport = "local-diagnostics",
            version = ProductIdentity.DisplayVersion
        }));
        app.MapGet("/inspector", () => Results.Content(InspectorPage.Build(), "text/html; charset=utf-8"));
        // The explicit return type keeps this lambda from binding as a RequestDelegate, which would discard the result.
        app.MapPost("/action", Task<IResult> (HttpContext context) => HandleActionAsync(context));

        await app.StartAsync(cancellation);
        await app.WaitForShutdownAsync(cancellation);
    }

    private WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1);
            options.Limits.MaxRequestBodySize = MaximumRequestBodyBytes;
            options.Limits.RequestHeadersTimeout = RequestHeadersTimeout;
            options.Limits.KeepAliveTimeout = KeepAliveTimeout;
        });

        return builder.Build();
    }

    // A web page in the operator's browser may reach this port too, so only this exact loopback host and its own
    // origin get through.
    private async Task RefuseForeignRequestsAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        var host = context.Request.Host;
        if (host.Port != port || host.Host is not ("127.0.0.1" or "localhost"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        string origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != $"http://{host}")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private async Task<IResult> HandleActionAsync(HttpContext context)
    {
        try
        {
            string authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith(BearerPrefix, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }
            string sessionId = context.Request.Headers[ProtocolIdentity.SessionHeader].ToString();
            var caller = new RequestContext(policy.Authenticate(authorization[BearerPrefix.Length..]), sessionId);
            if (!context.Request.HasJsonContentType())
            {
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }
            var request = await context.Request.ReadFromJsonAsync<JsonObject>(cancellationToken: context.RequestAborted);
            if (request == null)
            {
                return Results.BadRequest();
            }

            var result = await actor.ExecuteAsync(request, caller);
            return Results.Json(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.BadRequest(new
            {
                success = false,
                error = "Malformed JSON."
            });
        }
    }
}
