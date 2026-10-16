using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Common;

namespace KLaniBot.Fleet;

// Use case: the node side of pairing (see PairingProtocol). The node creates its own P-256 identity, which never
// leaves the machine, and stores the master's address, CA and its node credential in paired-master.json only
// after the person has entered the matching code. A code that does not match rejects the pairing on the master
// as well; there is no second attempt with the same ticket.
public sealed class PairingClient : IDisposable
{
    private const int CredentialBytes = 32;
    private static readonly TimeSpan ApprovalPollInterval = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly ECDsa _identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private byte[]? _certificate;
    private string _transcript = "";
    private string _root = "";
    private string _ticket = "";
    private DateTimeOffset _expires;
    private int _attempted;
    public int CodeDigits
    {
        get; private set;
    }
    public Uri Master
    {
        get;
    }

    // Throws ArgumentException when the address carries credentials, a path or no host.
    public PairingClient(string address)
    {
        Master = new Uri(address.Contains("://", StringComparison.Ordinal) ? address : "https://" + address + ":5444");
        if (Master.Scheme != "https" || Master.UserInfo != "" || Master.Query != "" || Master.Fragment != "" || Master.AbsolutePath != "/")
        {
            throw new ArgumentException("Enter a master HTTPS address without credentials or a path.");
        }
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) => CaptureCertificate(certificate, errors);
        _http = new HttpClient(handler) { BaseAddress = Master, Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 16384 };
    }

    private bool CaptureCertificate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate == null
            || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }
        byte[] received = certificate.GetRawCertData();
        byte[] selected = Interlocked.CompareExchange(ref _certificate, received, null) ?? received;
        // This provisional pin carries no operational authority. Human code comparison
        // binds it to the intended master before any credential or desktop access exists.
        return CryptographicOperations.FixedTimeEquals(selected, received);
    }

    // Throws InvalidDataException for an invalid lifetime, IOException without a TLS identity, and UnauthorizedAccessException when the
    // master's root or commitment does not hold.
    public async Task BeginAsync(string nodeName, CancellationToken cancellation)
    {
        string publicKey = Convert.ToBase64String(_identity.ExportSubjectPublicKeyInfo());
        JsonObject start = await PostAsync("pair/start", new JsonObject
        {
            ["node"] = nodeName,
            ["commitment"] = PairingProtocol.Commitment(_nonce, publicKey)
        }, cancellation);
        _ticket = start["ticket"]!.ToString();
        _root = start["root"]!.ToString();
        CodeDigits = start["digits"]!.GetValue<int>();
        new PairingOptions { CodeDigits = CodeDigits }.Validate();
        _expires = DateTimeOffset.Parse(start["expires_at"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        if (_expires <= DateTimeOffset.UtcNow || _expires > DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(5))
        {
            throw new InvalidDataException("Invalid pairing lifetime.");
        }
        using var selected = X509CertificateLoader.LoadCertificate(_certificate ?? throw new IOException("TLS identity unavailable."));
        if (!PairingProtocol.ValidateCertificate(selected, SslPolicyErrors.None, _root))
        {
            throw new UnauthorizedAccessException("Pairing root does not certify the selected TLS endpoint.");
        }
        JsonObject reveal = await PostAsync("pair/reveal", new JsonObject
        {
            ["ticket"] = _ticket,
            ["nonce"] = _nonce,
            ["public_key"] = publicKey
        }, cancellation);
        string masterNonce = reveal["nonce"]!.ToString();
        if (PairingProtocol.Commitment(masterNonce, _root) != start["commitment"]!.ToString())
        {
            throw new UnauthorizedAccessException("Master commitment changed.");
        }
        _transcript = PairingProtocol.Transcript(_ticket, nodeName, _nonce, masterNonce, publicKey, _root);
    }

    // Throws UnauthorizedAccessException for a second attempt, a wrong code or an expired approval, and InvalidDataException for an invalid
    // credential.
    public async Task FinishAsync(string code, string stateDirectory, CancellationToken cancellation)
    {
        if (_transcript == "" || Interlocked.Exchange(ref _attempted, 1) != 0 || DateTimeOffset.UtcNow >= _expires)
        {
            throw new UnauthorizedAccessException("Only one attempt is allowed for this pairing.");
        }
        string signature = Convert.ToBase64String(_identity.SignData(Encoding.UTF8.GetBytes(_transcript), HashAlgorithmName.SHA256));
        var proof = new JsonObject { ["ticket"] = _ticket, ["signature"] = signature };
        string expected = PairingProtocol.Code(_transcript, CodeDigits);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(code)))
        {
            await RejectQuietlyAsync(proof, cancellation);
            throw new UnauthorizedAccessException("Code does not match. Pairing rejected; no second attempt.");
        }

        // The master answers Conflict until its operator approves, so the client asks again until the request expires.
        while (DateTimeOffset.UtcNow < _expires)
        {
            using var response = await SendAsync("pair/complete", proof, cancellation);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                await Task.Delay(ApprovalPollInterval, cancellation);
                continue;
            }
            response.EnsureSuccessStatusCode();
            JsonObject result = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))!.AsObject();
            SavePairing(result["credential"]!.ToString(), stateDirectory);
            return;
        }
        throw new UnauthorizedAccessException("Master approval expired.");
    }

    // A disconnected master still expires the request and requires fresh approval.
    private async Task RejectQuietlyAsync(JsonObject proof, CancellationToken cancellation)
    {
        try
        {
            using var rejected = await SendAsync("pair/reject", proof, cancellation);
        }
        catch (HttpRequestException)
        {
        }
    }

    // Throws InvalidDataException when the credential is not 32 bytes.
    private void SavePairing(string credential, string stateDirectory)
    {
        if (Convert.FromBase64String(credential).Length != CredentialBytes)
        {
            throw new InvalidDataException("Invalid device credential.");
        }

        var config = new JsonObject
        {
            ["version"] = 1,
            ["master"] = Master.ToString(),
            ["root"] = _root,
            ["credential"] = credential,
            ["auto_update"] = true
        };
        PairingProtocol.WritePrivate(Path.Combine(stateDirectory, "device-identity.key"), _identity.ExportPkcs8PrivateKeyPem(), true);
        PairingProtocol.WritePrivate(Path.Combine(stateDirectory, "paired-master.json"), config.ToJsonString());
    }

    // Throws InvalidDataException when the answer exceeds its size limit.
    private async Task<JsonObject> PostAsync(string path, JsonObject body, CancellationToken cancellation)
    {
        using var response = await SendAsync(path, body, cancellation);
        response.EnsureSuccessStatusCode();
        string content = await response.Content.ReadAsStringAsync(cancellation);
        if (content.Length > 16384)
        {
            throw new InvalidDataException("Pairing response too large.");
        }
        return JsonNode.Parse(content)!.AsObject();
    }

    private Task<HttpResponseMessage> SendAsync(string path, JsonObject body, CancellationToken cancellation)
    {
        return SendOwnedRequestAsync(path, body, cancellation);
    }

    private async Task<HttpResponseMessage> SendOwnedRequestAsync(string path, JsonObject body, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Request-ID", Guid.CreateVersion7().ToString());
        return await _http.SendAsync(request, cancellation);
    }

    public static void CreatePrivateDirectory(string directory)
    {
        CartridgeManifest.RejectLinks(directory);
        PrivateFile.CreateDirectory(directory);
#if WINDOWS
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
#endif
    }

    public void Dispose()
    {
        _http.Dispose();
        _identity.Dispose();
    }
}
