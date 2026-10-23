using System.Net;
using System.Net.Http.Headers;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Startup;
using KLaniBot.Visibility;

namespace KLaniBot.Transport;

// Use case: a controller (the MCP gateway, --call, the evaluation runner) sends one action to one node from the
// registry. The node is trusted only through the CA pinned in the registry entry; the system trust store,
// proxies, redirects and cookies play no part, so a network in between can neither redirect nor read a call.
//
// HTTP/3 is preferred and probed once per connection; a failed probe falls back to HTTP/2. An action is
// never sent twice: when the transport fails after the request left, the result is "unknown" and the caller
// has to query or verify the operation. Repeating a click because an answer was lost could do it twice.
// Calls are serialised per client, so the answers of one session arrive in the order they were asked.
public sealed class NodeTransportClient : IDisposable
{
    private const int ResultFrameLimit = 4 * 1024 * 1024;
    private const string ServerAuthenticationUsage = "1.3.6.1.5.5.7.3.1";
    private static readonly TimeSpan DefaultCallDeadline = TimeSpan.FromSeconds(65);

    // One MCP client runs one gateway process; its sessions to every node share this value (see ProtocolIdentity.InstanceHeader).
    private static readonly string ProcessInstance = Guid.CreateVersion7().ToString();

    private readonly HttpClient _client;
    private readonly X509Certificate2 _root;
    private readonly Uri _base;
    private readonly string? _credentialFile;
    private readonly string? _credentialEnvironment;
    private readonly SemaphoreSlim _serial = new(1);
    private readonly string _session = Guid.CreateVersion7().ToString();
    private readonly string _instance;
    private Version? _version;
    private readonly bool _forceH2;
    private readonly TimeSpan _callDeadline;
    private readonly CancellationTokenSource _heartbeatStop = new();
    private Task? _heartbeat;
    private volatile bool _connected;
    private SessionPresentation _presentation = SessionPresentation.Create(null, null);

    // Only the certificate callback knows whether the peer was refused for the pin or for the name.
    // Calls are serialised by _serial, so one field per client is enough.
    private TransportFailure? _certificateRejection;

    // Throws ArgumentException for an invalid registry, an unregistered or insecure node or no credential source, and
    // UnauthorizedAccessException when the CA pin does not match.
    public NodeTransportClient(
        string node,
        string registry,
        string? tokenEnvironment = null,
        bool forceH2 = false,
        TimeSpan? callDeadline = null,
        string? instance = null)
    {
        _forceH2 = forceH2;
        _instance = instance ?? ProcessInstance;
        _callDeadline = callDeadline ?? DefaultCallDeadline;
        var document = JsonNode.Parse(File.ReadAllText(registry))!;
        if (document["version"]?.GetValue<int>() != 1)
        {
            throw new ArgumentException("Versioned node registry required.");
        }
        var entry = document["nodes"]?[node] ?? throw new ArgumentException("Node is not administratively registered.");
        _base = new Uri(entry["url"]!.ToString().TrimEnd('/') + "/");
        if (_base.Scheme != "https" || _base.UserInfo != "" || _base.Query != "" || _base.Fragment != "")
        {
            throw new ArgumentException("Node requires HTTPS without inline credentials.");
        }
        byte[] rootDer = Convert.FromBase64String(entry["root_der_base64"]!.ToString());
        string expected = entry["root_sha256"]!.ToString();
        if (Convert.ToHexStringLower(SHA256.HashData(rootDer)) != expected)
        {
            throw new UnauthorizedAccessException("Registry CA pin mismatch.");
        }
        _root = X509CertificateLoader.LoadCertificate(rootDer);
        _credentialFile = entry["token_file"]?.ToString();
        _credentialEnvironment = tokenEnvironment ?? entry["token_env"]?.ToString();
        if (_credentialFile == null && _credentialEnvironment == null)
        {
            throw new ArgumentException("Credential source required.");
        }
        _client = CreatePinnedHttpClient();
    }

    private HttpClient CreatePinnedHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };
        handler.SslOptions.RemoteCertificateValidationCallback = ValidateServerCertificate;

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public void SetPresentation(string? client, string? purpose)
    {
        _presentation = SessionPresentation.Create(client, purpose);
    }

    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? offeredChain, SslPolicyErrors errors)
    {
        SslPolicyErrors unusable = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable;
        if (certificate == null || (errors & unusable) != 0)
        {
            _certificateRejection = TransportFailure.TlsUntrusted;
            return false;
        }
        using var peer = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthenticationUsage));
        if (!chain.Build(peer))
        {
            _certificateRejection = TransportFailure.CaPinMismatch;
            return false;
        }
        return true;
    }

    // Throws TransportFailureException when no authenticated HTTP/2 or HTTP/3 transport is available.
    private async Task<Version> SelectProtocol(CancellationToken cancellation)
    {
        if (_version != null)
        {
            return _version;
        }
        if (!_forceH2 && QuicConnection.IsSupported && await HealthAnswersAsync(HttpVersion.Version30, cancellation))
        {
            _version = HttpVersion.Version30;
            return _version;
        }
        using var health = HealthRequest(HttpVersion.Version20);
        using var check = await _client.SendAsync(health, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!check.IsSuccessStatusCode || check.Version.Major != 2)
        {
            throw new TransportFailureException(TransportFailure.ProtocolUnavailable, "Authenticated H2 transport unavailable.");
        }
        _version = HttpVersion.Version20;
        return _version;
    }

    // H3 is only a preference. A failed probe falls back to H2 unless the caller gave up meanwhile.
    private async Task<bool> HealthAnswersAsync(Version version, CancellationToken cancellation)
    {
        using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        probeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var probe = HealthRequest(version);
            using var response = await _client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, probeDeadline.Token);
            return response.IsSuccessStatusCode && response.Version.Major == version.Major;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            _certificateRejection = null;
            return false;
        }
    }

    private HttpRequestMessage HealthRequest(Version version)
    {
        return new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "health"))
        {
            Version = version,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    public Task<JsonObject> CallAsync(Tool tool, JsonObject args, CancellationToken cancellation)
    {
        return CallAsync(ToolNames.Of(tool), args, cancellation);
    }

    // The action word is passed through as the client sent it; the node decides whether it names a tool.
    public async Task<JsonObject> CallAsync(string action, JsonObject args, CancellationToken cancellation)
    {
        await _serial.WaitAsync(cancellation);
        bool dispatched = false;
        _certificateRejection = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(_callDeadline);
        try
        {
            // Read first: a missing credential is a local fault and should not look like a network one.
            string credential = await ReadCredentialAsync(deadline.Token);
            Version protocol = await SelectProtocol(deadline.Token);
            var body = new JsonObject
            {
                ["action"] = action,
                ["args"] = args.DeepClone()
            };
            using var request = AuthenticatedPost("action", body, protocol, credential);
            dispatched = true;
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Rejection(response.StatusCode);
            }
            JsonObject result = await ReadResultAsync(response, deadline.Token);
            result["transport"] = protocol.Major == 3 ? "h3" : "h2";
            KeepSessionAlive(action);
            return result;
        }
        catch (Exception exception) when (IsTransportException(exception))
        {
            return TransportFailureResult(exception, cancellation, dispatched);
        }
        finally
        {
            _serial.Release();
        }
    }

    // Releases the desktop this client's session holds, so the next controller takes it at once instead of waiting
    // for the lease to expire. A client that never reached its node has nothing to release.
    public async Task EndDesktopSessionAsync(CancellationToken cancellation)
    {
        if (!_connected)
        {
            return;
        }
        var arguments = new JsonObject
        {
            ["request_id"] = Guid.CreateVersion7().ToString()
        };
        await CallAsync(Tool.EndSession, arguments, cancellation);
    }

    // Heartbeats keep the session's indicator on the node until the controller ends the session.
    private void KeepSessionAlive(string action)
    {
        bool endedSession = ToolNames.TryParse(action, out Tool sent) && sent == Tool.EndSession;
        _connected = !endedSession;
        _heartbeat ??= Task.Run(HeartbeatAsync);
    }

    // The next call negotiates the protocol again. Whether the node may have acted depends on whether the request was sent.
    private JsonObject TransportFailureResult(Exception exception, CancellationToken cancellation, bool dispatched)
    {
        _version = null;
        TransportFailure failure = Classify(exception, cancellation);
        Console.Error.WriteLine("Transport failure (" + failure.ToWireName() + "): " + Diagnostics.Describe(exception));

        ActionOutcome outcome = dispatched ? ActionOutcome.Unknown : ActionOutcome.NotDispatched;
        string message = "Transport failed (" + failure.ToWireName() + "). "
            + "Verify or query the operation; no automatic mutation retry.";
        return FailureResult(outcome, failure, message);
    }

    // Throws TransportFailureException when the credential is unavailable or invalid.
    private async Task<string> ReadCredentialAsync(CancellationToken cancellation)
    {
        string credential;
        if (_credentialEnvironment != null)
        {
            credential = Environment.GetEnvironmentVariable(_credentialEnvironment)
                ?? throw new TransportFailureException(TransportFailure.CredentialUnavailable, "Credential environment unavailable.");
        }
        else
        {
            credential = await ReadCredentialFileAsync(cancellation);
        }
        if (credential.Length is < 32 or > 256)
        {
            throw new TransportFailureException(TransportFailure.CredentialInvalid, "Invalid credential.");
        }
        return credential;
    }

    // The framework message names the file; the registry already does that, so it is not repeated.
    // Throws TransportFailureException when the credential file cannot be read.
    private async Task<string> ReadCredentialFileAsync(CancellationToken cancellation)
    {
        try
        {
            string content = await File.ReadAllTextAsync(_credentialFile!, cancellation);
            return content.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            string reason = exception.GetType().Name;
            throw new TransportFailureException(TransportFailure.CredentialUnavailable, "Credential file unreadable: " + reason + ".");
        }
    }

    private HttpRequestMessage AuthenticatedPost(string path, JsonObject body, Version protocol, string credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, path))
        {
            Version = protocol,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Add(ProtocolIdentity.SessionHeader, _session);
        request.Headers.Add(ProtocolIdentity.InstanceHeader, _instance);
        request.Headers.Add(SessionPresentation.Header, _presentation.Encode());
        request.Headers.Add("X-Request-ID", Guid.CreateVersion7().ToString());
        return request;
    }

    // Throws TransportFailureException for an oversized result or one that is no JSON object.
    private static async Task<JsonObject> ReadResultAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + count > ResultFrameLimit)
            {
                throw new TransportFailureException(TransportFailure.ResponseTooLarge, "Result frame too large.");
            }
            output.Write(buffer, 0, count);
        }
        if (JsonNode.Parse(output.ToArray()) is not JsonObject result)
        {
            throw new TransportFailureException(TransportFailure.ProtocolUnavailable, "Result frame is not an object.");
        }
        return result;
    }

    // Everything the network, TLS, the deadline or a malformed answer can throw. Anything else is a
    // fault in this client and is left to the caller's own error path instead of being renamed here.
    private static bool IsTransportException(Exception exception)
    {
        return exception is TransportFailureException
            or OperationCanceledException
            or HttpRequestException
            or IOException
            or JsonException;
    }

    private TransportFailure Classify(Exception exception, CancellationToken caller)
    {
        if (exception is TransportFailureException known)
        {
            return known.Failure;
        }
        if (_certificateRejection is { } rejection)
        {
            return rejection;
        }
        if (exception is OperationCanceledException)
        {
            return caller.IsCancellationRequested ? TransportFailure.Cancelled : TransportFailure.Timeout;
        }
        if (exception is HttpRequestException request)
        {
            return ClassifyRequestError(request.HttpRequestError);
        }
        if (exception is JsonException)
        {
            return TransportFailure.ProtocolUnavailable;
        }
        return TransportFailure.ConnectionFailed;
    }

    private static TransportFailure ClassifyRequestError(HttpRequestError error)
    {
        return error switch
        {
            HttpRequestError.SecureConnectionError => TransportFailure.TlsUntrusted,
            HttpRequestError.VersionNegotiationError => TransportFailure.ProtocolUnavailable,
            HttpRequestError.InvalidResponse => TransportFailure.ProtocolUnavailable,
            HttpRequestError.HttpProtocolError => TransportFailure.ProtocolUnavailable,
            HttpRequestError.ConfigurationLimitExceeded => TransportFailure.ProtocolUnavailable,
            _ => TransportFailure.ConnectionFailed
        };
    }

    private static JsonObject Rejection(HttpStatusCode status)
    {
        ActionOutcome outcome = RejectedBeforeDispatch(status) ? ActionOutcome.NotDispatched : ActionOutcome.Unknown;
        TransportFailure failure = status == HttpStatusCode.Unauthorized
            ? TransportFailure.CredentialInvalid
            : TransportFailure.NodeRejected;
        return FailureResult(outcome, failure, "Node rejected the request with status " + (int)status + ".");
    }

    // These statuses are answered before the node reads the action, so nothing ran.
    private static bool RejectedBeforeDispatch(HttpStatusCode status)
    {
        return status is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.BadRequest
            or HttpStatusCode.RequestEntityTooLarge
            or HttpStatusCode.UnsupportedMediaType;
    }

    private static JsonObject FailureResult(ActionOutcome outcome, TransportFailure failure, string message)
    {
        JsonObject result = ActionResults.Failure(ActionErrorCode.TransportError, outcome, message);
        result["failure"] = failure.ToWireName();
        return result;
    }

    private async Task SendPresenceAsync(bool connected, CancellationToken cancellation)
    {
        await _serial.WaitAsync(cancellation);
        try
        {
            if (_version == null)
            {
                return;
            }
            string credential = await ReadCredentialAsync(cancellation);
            var body = new JsonObject
            {
                ["event"] = connected ? "heartbeat" : "close"
            };
            using var request = AuthenticatedPost("session", body, _version, credential);
            using var response = await _client.SendAsync(request, cancellation);
        }
        finally
        {
            _serial.Release();
        }
    }

    // A missed heartbeat only lets the node's session indicator expire earlier; the next call reconnects.
    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(_heartbeatStop.Token))
            {
                if (_connected)
                {
                    await SendHeartbeatAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendHeartbeatAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_heartbeatStop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await SendPresenceAsync(true, deadline.Token);
        }
        catch (OperationCanceledException) when (!_heartbeatStop.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or TransportFailureException)
        {
        }
    }

    public void Dispose()
    {
        _heartbeatStop.Cancel();
        if (_connected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                SendPresenceAsync(false, deadline.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // Saying goodbye is a courtesy: the node expires the session on its own, and a failure
                // here must not keep the controller from releasing its sockets below.
            }
        }
        if (_heartbeat != null)
        {
            try
            {
                _heartbeat.GetAwaiter().GetResult();
            }
            catch
            {
                // Same reason: the heartbeat's own failure has no consequence once the client goes away.
            }
        }
        _client.Dispose();
        _root.Dispose();
        _heartbeatStop.Dispose();
        _serial.Dispose();
    }
}
