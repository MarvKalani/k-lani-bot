using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Common;

namespace KLaniBot.Advisory;

// Settings of the Laya service, read from an advisor configuration file whose "provider" is "laya".
public sealed class LayaAdvisoryConfig
{
    public int Version { get; set; } = 1;
    public bool Enabled
    {
        get; set;
    }
    public string Endpoint { get; set; } = "";
    public string CredentialFile { get; set; } = "";
    public string Model { get; set; } = "multilingual";
    public string ModelRevision { get; set; } = "";
    public string? CertificateSha256
    {
        get; set;
    }
    public bool AllowLoopbackHttp
    {
        get; set;
    }
    public int TimeoutMs { get; set; } = 10000;
    public int MaxTokens { get; set; } = 8192;
    public string? CalibrationReportFile
    {
        get; set;
    }

    // Throws InvalidDataException when the document is no Laya configuration.
    public static LayaAdvisoryConfig FromJson(JsonObject document)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return document.Deserialize<LayaAdvisoryConfig>(options) ?? throw new InvalidDataException("Invalid Laya module configuration.");
    }
}

// Use case: the one advisory provider that ships with the node. Laya is a separate Docker service reached
// over a fixed IP; the node holds only this HTTP client and never loads Python, tensors, a tokenizer or
// model weights. Everything that is specific to that service lives in this file (its endpoint, its model
// names, its revision pin, its certificate pin); what callers may rely on is in IAdvisoryEvaluator.
public sealed class LayaAdvisoryEvaluator : IAdvisoryEvaluator
{
    public const string Id = "laya";

    private readonly LayaAdvisoryConfig _config;
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _slots = new(2);

    public LayaAdvisoryEvaluator(LayaAdvisoryConfig config, HttpMessageHandler? testingHandler = null)
    {
        _config = config;
        Uri uri = ValidateConfiguration(config);
        _endpoint = new Uri(uri, "/v1/systemone");
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        if (config.CertificateSha256 != null)
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors)
                => MatchesPinnedCertificate(config.CertificateSha256, certificate, errors);
        }
        _client = new HttpClient(testingHandler ?? handler) { Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs) };
    }

    public string ProviderId => Id;

    // Throws ArgumentException when the request exceeds the evaluation limit.
    public async Task<AdvisoryResult> EvaluateAsync(JsonNode state, JsonObject questions, CancellationToken cancellation = default)
    {
        AdvisoryQuestionSchema.ValidateQuestions(questions);
        string rubricHash = CanonicalJson.Hash(questions);
        string requestId = Guid.CreateVersion7().ToString();
        var payload = new JsonObject
        {
            ["state"] = state.DeepClone(),
            ["questions"] = questions.DeepClone(),
            ["model"] = _config.Model,
            ["max_len"] = _config.MaxTokens
        };
        string body = payload.ToJsonString();
        if (Encoding.UTF8.GetByteCount(body) > 128 * 1024)
        {
            throw new ArgumentException("Classifier input exceeds the bounded evaluation request.");
        }
        await _slots.WaitAsync(cancellation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(_config.TimeoutMs);
        try
        {
            return await AskServiceAsync(body, questions, requestId, rubricHash, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return Uncertain(requestId, rubricHash, "Classifier deadline exceeded.");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or InvalidDataException or ArgumentException)
        {
            return Uncertain(requestId, rubricHash, "Classifier unavailable or invalid response; no task success is inferred.");
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _slots.Dispose();
    }

    // Throws InvalidDataException when the credential file is invalid.
    private async Task<AdvisoryResult> AskServiceAsync(
        string body,
        JsonObject questions,
        string requestId,
        string rubricHash,
        CancellationToken token)
    {
        string credential = (await File.ReadAllTextAsync(_config.CredentialFile, token)).Trim();
        if (credential.Length is < 16 or > 256 || credential.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("Invalid Laya credential.");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Add("X-Request-ID", requestId);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            return Uncertain(requestId, rubricHash, "Classifier endpoint rejected the request.");
        }
        byte[]? content = await ReadBoundedAsync(response, token);
        if (content == null)
        {
            return Uncertain(requestId, rubricHash, "Classifier response too large.");
        }
        var result = JsonNode.Parse(content)!.AsObject();
        AdvisoryQuestionSchema.ValidateAnswers(questions, result);
        bool truncated = result["usage"]?["truncated"]?.ToString() is { } flag && flag is not ("false" or "0");
        bool calibrated = AdvisoryCalibration.Covers(_config.CalibrationReportFile, _config.ModelRevision, rubricHash);
        return new AdvisoryResult(
            true,
            truncated ? AdvisoryOutcome.Uncertain : AdvisoryOutcome.Advisory,
            requestId,
            Id,
            _config.ModelRevision,
            rubricHash,
            result["answers"]!.AsObject().DeepClone().AsObject(),
            result["usage"]?.DeepClone(),
            _config.Model,
            calibrated,
            truncated || !calibrated,
            null);
    }

    // Null means the answer exceeded the limit; the node never buffers more than one megabyte of it.
    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var data = new MemoryStream();
        byte[] buffer = new byte[4096];
        int length;
        while ((length = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (data.Length + length > 1024 * 1024)
            {
                return null;
            }
            data.Write(buffer, 0, length);
        }
        return data.ToArray();
    }

    private AdvisoryResult Uncertain(string requestId, string rubricHash, string message)
    {
        return AdvisoryResult.Uncertain(requestId, Id, _config.ModelRevision, rubricHash, message);
    }

    // Throws ArgumentException when the endpoint, model or certificate pin is not explicitly and safely configured.
    private static Uri ValidateConfiguration(LayaAdvisoryConfig config)
    {
        bool validEndpoint = Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var uri)
            && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)
            && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == ""
            && (uri.Scheme == "https" || uri.Scheme == "http" && IPAddress.IsLoopback(address) && config.AllowLoopbackHttp);
        bool validSettings = config.Version == 1 && config.Enabled
            && config.Model is "multilingual" or "english" or "typed-decisions"
            && config.ModelRevision.Length == 40
            && config.TimeoutMs is >= 100 and <= 60000
            && config.MaxTokens is >= 64 and <= 8192;
        if (!validEndpoint || !validSettings)
        {
            throw new ArgumentException(
                "Laya requires an explicitly enabled, fixed IP endpoint, HTTPS (or explicitly allowed loopback HTTP), a pinned model "
                + "revision and bounded inference settings.");
        }
        if (config.CertificateSha256 != null && !System.Text.RegularExpressions.Regex.IsMatch(config.CertificateSha256, "^[0-9a-f]{64}$"))
        {
            throw new ArgumentException("Invalid Laya certificate pin.");
        }
        return new Uri(config.Endpoint);
    }

    private static bool MatchesPinnedCertificate(
        string pinnedSha256,
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate,
        SslPolicyErrors errors)
    {
        return errors == SslPolicyErrors.None
            && certificate != null
            && Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)) == pinnedSha256;
    }
}
