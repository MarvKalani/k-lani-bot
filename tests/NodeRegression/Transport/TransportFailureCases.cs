using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Every transport failure used to read "Transport unavailable", whatever the cause. Each case here
// produces one cause for real (a TLS server, a silent port, a closed port, a bad credential file)
// and checks that the frame names it while the outcome keeps its old meaning. The same fixture node shows
// that a controller releases its desktop session when its input ends, so a restarted one is not locked out.
internal static class TransportFailureCases
{
    private const string CredentialMarker = "MARKER-credential-value-must-never-appear-0123456789";

    public static async Task<int> RunAsync(string temporaryRoot)
    {
        var checks = new Checks("Transport failure");
        string root = Path.Combine(temporaryRoot, "transport-failures");
        Directory.CreateDirectory(root);
        using X509Certificate2 pinned = CreateAuthority("pinned fixture CA");
        using X509Certificate2 stranger = CreateAuthority("unrelated fixture CA");
        using X509Certificate2 trustedServer = CreateServerCertificate(pinned, null);
        using X509Certificate2 strangerServer = CreateServerCertificate(stranger, null);
        using X509Certificate2 misnamedServer = CreateServerCertificate(pinned, "other.invalid");
        var fixture = new FixtureCase(root, pinned);
        await using var server = await FixtureServer.StartAsync(trustedServer);
        TextWriter originalError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);
        try
        {
            await CheckCredentialFailuresAsync(checks, fixture, server, root);
            await CheckNodeRefusalsAsync(checks, fixture, server);
            await CheckCertificateFailuresAsync(checks, fixture, server, strangerServer, misnamedServer);
            server.Certificate = trustedServer;
            await CheckControllerReleasesItsSessionAsync(checks, fixture, server);
            await CheckUnreachableNodesAsync(checks, fixture);
            await CheckToolCallsRunOneAtATimeAsync(checks, fixture, server);
        }
        finally
        {
            Console.SetError(originalError);
        }

        string diagnostics = capturedError.ToString();
        checks.That(diagnostics.Contains("Transport failure (timeout)", StringComparison.Ordinal), "the cause goes to standard error");
        checks.That(!diagnostics.Contains("MARKER", StringComparison.Ordinal), "the credential is not written to standard error");
        return checks.Passed;
    }

    private static async Task CheckCredentialFailuresAsync(Checks checks, FixtureCase fixture, FixtureServer server, string root)
    {
        JsonObject answered = await fixture.CallAsync(server.Port);
        checks.That(answered["success"]?.GetValue<bool>() == true && answered["failure"] == null, "a working node still answers");

        JsonObject missing = await fixture.CallAsync(server.Port, credentialFile: Path.Combine(root, "absent"));
        checks.That(IsFailure(missing, "credential_unavailable", "not_dispatched"), "missing credential file");

        string shortCredential = Path.Combine(root, "short-credential");
        File.WriteAllText(shortCredential, "too-short");
        JsonObject malformed = await fixture.CallAsync(server.Port, credentialFile: shortCredential);
        checks.That(IsFailure(malformed, "credential_invalid", "not_dispatched"), "credential of the wrong form");
    }

    private static async Task CheckNodeRefusalsAsync(Checks checks, FixtureCase fixture, FixtureServer server)
    {
        server.Behaviour = FixtureBehaviour.Unauthorized;
        JsonObject refused = await fixture.CallAsync(server.Port);
        checks.That(IsFailure(refused, "credential_invalid", "not_dispatched"), "credential refused by the node");
        checks.That(!refused.ToJsonString().Contains("MARKER", StringComparison.Ordinal), "the credential is not echoed in the frame");

        server.Behaviour = FixtureBehaviour.Forbidden;
        JsonObject forbidden = await fixture.CallAsync(server.Port);
        checks.That(IsFailure(forbidden, "node_rejected", "not_dispatched"), "other refusal by the node");

        server.Behaviour = FixtureBehaviour.Oversized;
        JsonObject oversized = await fixture.CallAsync(server.Port);
        checks.That(IsFailure(oversized, "response_too_large", "unknown"), "oversized result after dispatch");

        server.Behaviour = FixtureBehaviour.Unhealthy;
        JsonObject unhealthy = await fixture.CallAsync(server.Port);
        checks.That(IsFailure(unhealthy, "protocol_unavailable", "not_dispatched"), "health check without H2");
    }

    private static async Task CheckCertificateFailuresAsync(
        Checks checks,
        FixtureCase fixture,
        FixtureServer server,
        X509Certificate2 strangerServer,
        X509Certificate2 misnamedServer)
    {
        server.Behaviour = FixtureBehaviour.Answer;
        server.Certificate = strangerServer;
        JsonObject foreign = await fixture.CallAsync(server.Port);
        checks.That(IsFailure(foreign, "ca_pin_mismatch", "not_dispatched"), "server certificate from another CA");

        server.Certificate = misnamedServer;
        JsonObject misnamed = await fixture.CallAsync(server.Port);
        checks.That(IsFailure(misnamed, "tls_untrusted", "not_dispatched"), "certificate for another name");
    }

    private static async Task CheckControllerReleasesItsSessionAsync(Checks checks, FixtureCase fixture, FixtureServer server)
    {
        server.Behaviour = FixtureBehaviour.Answer;
        string registry = fixture.RegistryFor(server.Port);

        server.ForgetActions();
        await ServeControllerAsync(registry, callsNode: true);
        string[] expected = ["get_machine_capabilities", "end_session"];
        checks.That(server.Actions().SequenceEqual(expected), "a controller that used its node releases the session at end of input");

        server.ForgetActions();
        await ServeControllerAsync(registry, callsNode: false);
        checks.That(server.Actions().Length == 0, "a controller that never reached its node has no session to end");
    }

    // One stdio conversation: handshake, optionally one node call, then the end of input a closing client produces.
    private static async Task ServeControllerAsync(string registry, bool callsNode)
    {
        var frames = new StringBuilder();
        var handshake = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject { ["protocolVersion"] = "2025-11-25" }
        };
        frames.AppendLine(handshake.ToJsonString());
        frames.AppendLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        if (callsNode)
        {
            var call = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "get_machine_capabilities", ["arguments"] = new JsonObject() }
            };
            frames.AppendLine(call.ToJsonString());
        }
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(frames.ToString()));
        using var gateway = new McpGateway(registry, "fixture");
        await gateway.RunAsync(input, new StringWriter(), CancellationToken.None);
    }

    // A model client sends the tool calls of one turn without waiting; both operate the same desktop, so the second must
    // not start before the first ended, and a ping sent meanwhile is answered at once.
    private static async Task CheckToolCallsRunOneAtATimeAsync(Checks checks, FixtureCase fixture, FixtureServer server)
    {
        server.Behaviour = FixtureBehaviour.Slow;
        server.ForgetActions();
        var frames = new StringBuilder();
        frames.AppendLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""");
        frames.AppendLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        frames.AppendLine(ToolCallFrame(2));
        frames.AppendLine(ToolCallFrame(3));
        frames.AppendLine("""{"jsonrpc":"2.0","id":4,"method":"ping"}""");
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(frames.ToString()));
        var output = new StringWriter();
        using var gateway = new McpGateway(fixture.RegistryFor(server.Port), "fixture");

        await gateway.RunAsync(input, output, CancellationToken.None);

        string[] expectedActions = ["start:get_machine_capabilities", "end:get_machine_capabilities",
            "start:get_machine_capabilities", "end:get_machine_capabilities", "start:end_session", "end:end_session"];
        checks.That(server.Actions().SequenceEqual(expectedActions), "the second tool call starts after the first ended");
        string[] answeredIds = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!["id"]!.ToString())
            .ToArray();
        checks.That(answeredIds.SequenceEqual(["1", "4", "2", "3"]), "ping is answered while a tool call runs; tool calls answer in order");
    }

    private static string ToolCallFrame(int id)
    {
        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "get_machine_capabilities", ["arguments"] = new JsonObject() }
        };
        return call.ToJsonString();
    }

    private static async Task CheckUnreachableNodesAsync(Checks checks, FixtureCase fixture)
    {
        int closedPort = UnusedPort();
        JsonObject closed = await fixture.CallAsync(closedPort);
        checks.That(IsFailure(closed, "connection_failed", "not_dispatched"), "closed port");

        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            int silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;
            JsonObject late = await fixture.CallAsync(silentPort, deadline: TimeSpan.FromSeconds(1));
            checks.That(IsFailure(late, "timeout", "not_dispatched"), "node that never answers");

            using var withdrawn = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            TimeSpan generous = TimeSpan.FromSeconds(20);
            JsonObject cancelled = await fixture.CallAsync(silentPort, deadline: generous, cancellation: withdrawn.Token);
            checks.That(IsFailure(cancelled, "cancelled", "not_dispatched"), "caller withdraws the call");
        }
        finally
        {
            silent.Stop();
        }
    }

    private static bool IsFailure(JsonObject result, string failure, string outcome)
    {
        bool failed = result["success"]?.GetValue<bool>() == false;
        bool named = result["failure"]?.ToString() == failure;
        bool sameOutcome = result["outcome"]?.ToString() == outcome;
        bool transportCode = result["code"]?.ToString() == "transport_error";
        return failed && named && sameOutcome && transportCode;
    }

    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static X509Certificate2 CreateAuthority(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddHours(-1), now.AddDays(1));
    }

    // Without a DNS name the certificate is issued for 127.0.0.1, the address the registry uses.
    private static X509Certificate2 CreateServerCertificate(X509Certificate2 authority, string? dnsName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=fixture node", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var serverAuthentication = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(serverAuthentication, false));
        var names = new SubjectAlternativeNameBuilder();
        if (dnsName == null)
        {
            names.AddIpAddress(IPAddress.Loopback);
        }
        else
        {
            names.AddDnsName(dnsName);
        }
        request.CertificateExtensions.Add(names.Build());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        byte[] serial = RandomNumberGenerator.GetBytes(8);
        using X509Certificate2 issued = request.Create(authority, now.AddMinutes(-30), now.AddHours(12), serial);
        using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);
        // Re-imported from PKCS#12 so that the TLS stack gets a key it can always use.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    // A controller with a node registry that pins the fixture CA, calling one node port with a chosen credential and deadline.
    private sealed class FixtureCase(string root, X509Certificate2 pinned)
    {
        private readonly string _validCredential = WriteCredential(root);

        public async Task<JsonObject> CallAsync(
            int port,
            string? credentialFile = null,
            TimeSpan? deadline = null,
            CancellationToken cancellation = default)
        {
            string registry = WriteRegistry(port, credentialFile ?? _validCredential);
            TimeSpan callDeadline = deadline ?? TimeSpan.FromSeconds(10);
            using var client = new NodeTransportClient("fixture", registry, forceH2: true, callDeadline: callDeadline);
            return await client.CallAsync(Tool.GetMachineCapabilities, new JsonObject(), cancellation);
        }

        // A registry for a gateway under test; the gateway reads the credential file named in it.
        public string RegistryFor(int port)
        {
            return WriteRegistry(port, _validCredential);
        }

        private static string WriteCredential(string root)
        {
            string path = Path.Combine(root, "credential");
            File.WriteAllText(path, CredentialMarker);
            return path;
        }

        private string WriteRegistry(int port, string credentialFile)
        {
            byte[] rootDer = pinned.RawData;
            var entry = new JsonObject
            {
                ["url"] = "https://127.0.0.1:" + port,
                ["root_der_base64"] = Convert.ToBase64String(rootDer),
                ["root_sha256"] = Convert.ToHexStringLower(SHA256.HashData(rootDer)),
                ["token_file"] = credentialFile
            };
            var document = new JsonObject
            {
                ["version"] = 1,
                ["nodes"] = new JsonObject { ["fixture"] = entry }
            };
            string path = Path.Combine(root, "nodes-" + Guid.CreateVersion7() + ".json");
            File.WriteAllText(path, document.ToJsonString());
            return path;
        }
    }

    // How the fixture node answers the next call, so each transport failure can be produced on purpose.
    private enum FixtureBehaviour
    {
        Answer,
        Unauthorized,
        Forbidden,
        Oversized,
        Unhealthy,
        Slow
    }

    // A real Kestrel TLS server standing in for a node; a case switches its behaviour and certificate between calls.
    private sealed class FixtureServer : IAsyncDisposable
    {
        private const int OversizedLength = 5 * 1024 * 1024;
        private static readonly TimeSpan SlowAnswerDelay = TimeSpan.FromMilliseconds(400);
        private readonly List<string> _actions = new();
        private WebApplication? _application;

        private FixtureServer(X509Certificate2 certificate)
        {
            Certificate = certificate;
        }

        public FixtureBehaviour Behaviour
        {
            get;
            set;
        }

        public X509Certificate2 Certificate
        {
            get;
            set;
        }

        public int Port
        {
            get;
            private set;
        }

        public static async Task<FixtureServer> StartAsync(X509Certificate2 certificate)
        {
            var fixture = new FixtureServer(certificate);
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => fixture.Certificate);
            }));
            WebApplication application = builder.Build();
            application.Run(fixture.AnswerAsync);
            await application.StartAsync();
            fixture._application = application;
            IServerAddressesFeature addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            fixture.Port = new Uri(addresses.Addresses.First()).Port;
            return fixture;
        }

        private async Task AnswerAsync(HttpContext context)
        {
            string path = context.Request.Path.Value ?? "";
            if (path == "/health")
            {
                context.Response.StatusCode = Behaviour == FixtureBehaviour.Unhealthy ? 503 : 200;
                return;
            }
            if (path != "/action")
            {
                context.Response.StatusCode = 404;
                return;
            }
            switch (Behaviour)
            {
                case FixtureBehaviour.Unauthorized:
                    context.Response.StatusCode = 401;
                    return;
                case FixtureBehaviour.Forbidden:
                    context.Response.StatusCode = 403;
                    return;
                case FixtureBehaviour.Oversized:
                    await context.Response.Body.WriteAsync(new byte[OversizedLength]);
                    return;
                case FixtureBehaviour.Answer:
                case FixtureBehaviour.Unhealthy:
                    AddAction(await ReadActionAsync(context));
                    await context.Response.WriteAsync("{\"success\":true}");
                    return;
                case FixtureBehaviour.Slow:
                    string action = await ReadActionAsync(context);
                    AddAction("start:" + action);
                    await Task.Delay(SlowAnswerDelay);
                    AddAction("end:" + action);
                    await context.Response.WriteAsync("{\"success\":true}");
                    return;
            }
        }

        public string[] Actions()
        {
            lock (_actions)
            {
                return _actions.ToArray();
            }
        }

        public void ForgetActions()
        {
            lock (_actions)
            {
                _actions.Clear();
            }
        }

        // The action word of every answered call in arrival order, so a case can see what a controller sent and when.
        private static async Task<string> ReadActionAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            string body = await reader.ReadToEndAsync();
            return JsonNode.Parse(body)?["action"]?.ToString() ?? "";
        }

        private void AddAction(string action)
        {
            lock (_actions)
            {
                _actions.Add(action);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_application != null)
            {
                await _application.StopAsync();
                await _application.DisposeAsync();
            }
        }
    }
}
