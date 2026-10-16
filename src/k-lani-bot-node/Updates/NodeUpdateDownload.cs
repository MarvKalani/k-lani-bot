using System.Net;
using System.Net.Http.Headers;
using System.Net.Quic;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Fleet;
using KLaniBot.Platforms.Windows;
using KLaniBot.Transport;

namespace KLaniBot.Updates;

// The node's side of fetching an update from its paired master, authenticated with the node credential and the
// pinned master CA. A version that was rolled back on this node before is skipped, so a faulty build is not
// installed again on every offer.
public static class NodeUpdateDownload
{
    private const int MaximumManifestBytes = 16384;
    private const int BufferBytes = 65536;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProtocolProbeTimeout = TimeSpan.FromSeconds(2);

    // Throws InvalidDataException for a missing or oversized manifest or file, and UnauthorizedAccessException for a release that rolled
    // back before or whose version differs from its executable.
    public static async Task<NodeUpdateManifest?> DownloadAsync(
        JsonObject paired,
        string node,
        string directory,
        string? blockedVersion,
        string currentVersion,
        CancellationToken cancellation)
    {
        using HttpClient client = PinnedClient(paired, node);
        Version protocol = await SelectProtocolAsync(client, cancellation);
        NodeUpdateManifest? manifest = await ReadManifestAsync(client, protocol, cancellation);
        if (manifest == null)
        {
            return null;
        }
        if (blockedVersion != null && manifest.Version == blockedVersion)
        {
            throw new UnauthorizedAccessException("This release previously rolled back; publish a corrected version.");
        }
        manifest.Validate(paired["root"]!.ToString(), currentVersion, DateTimeOffset.UtcNow);

        PairingClient.CreatePrivateDirectory(directory);
        string binary = Path.Combine(directory, "k-lani-bot-node-" + manifest.Version + ".exe");
        CartridgeManifest.RejectLinks(binary);
        await DownloadExecutableAsync(client, protocol, manifest, binary, cancellation);
        manifest.VerifyFile(binary);
        if (WindowsExecutableMetadata.ReadProductVersion(binary) != manifest.Version)
        {
            throw new UnauthorizedAccessException("Signed update version differs from its executable metadata.");
        }
        PairingProtocol.WritePrivate(Path.Combine(directory, "update-manifest.json"), JsonSerializer.Serialize(manifest), true);
        return manifest;
    }

    // Authenticated with the node credential; trusts only the master CA saved at pairing, with no proxy, cookies or redirects.
    public static HttpClient PinnedClient(JsonObject paired, string node)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = ConnectTimeout
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            PairingProtocol.ValidateCertificate(certificate, errors, paired["root"]!.ToString());

        var client = new HttpClient(handler) { BaseAddress = new Uri(paired["master"]!.ToString()), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", paired["credential"]!.ToString());
        client.DefaultRequestHeaders.Add(ProtocolIdentity.NodeHeader, node);
        return client;
    }

    // Null when the master has published nothing.
    // Throws InvalidDataException for a manifest beyond its size limit or an empty one.
    private static async Task<NodeUpdateManifest?> ReadManifestAsync(HttpClient client, Version protocol, CancellationToken cancellation)
    {
        using var manifestRequest = Request(ProtocolIdentity.UpdateManifestRoute, protocol);
        using var manifestResponse = await client.SendAsync(manifestRequest, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (manifestResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        manifestResponse.EnsureSuccessStatusCode();

        await using var manifestStream = await manifestResponse.Content.ReadAsStreamAsync(cancellation);
        using var content = new MemoryStream();
        byte[] buffer = new byte[BufferBytes];
        int count;
        while ((count = await manifestStream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (content.Length + count > MaximumManifestBytes)
            {
                throw new InvalidDataException("Update manifest exceeds the size limit.");
            }
            content.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<NodeUpdateManifest>(content.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Update manifest missing.");
    }

    // Throws InvalidDataException when the master sends more bytes than the signed size.
    private static async Task DownloadExecutableAsync(HttpClient client, Version protocol, NodeUpdateManifest manifest, string binary,
        CancellationToken cancellation)
    {
        using var binaryRequest = Request(ProtocolIdentity.UpdateFileRoutePrefix + manifest.Version, protocol);
        using var response = await client.SendAsync(binaryRequest, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();

        await using var download = await response.Content.ReadAsStreamAsync(cancellation);
        await using var file = new FileStream(binary, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[BufferBytes];
        long received = 0;
        int count;
        while ((count = await download.ReadAsync(buffer, cancellation)) > 0)
        {
            received += count;
            if (received > manifest.Size)
            {
                throw new InvalidDataException("Update exceeds its signed size.");
            }
            await file.WriteAsync(buffer.AsMemory(0, count), cancellation);
        }
        await file.FlushAsync(cancellation);
        file.Flush(true);
    }

    private static HttpRequestMessage Request(string path, Version protocol)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Version = protocol,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Add("X-Request-ID", Guid.CreateVersion7().ToString());
        return request;
    }

    private static async Task<Version> SelectProtocolAsync(HttpClient client, CancellationToken cancellation)
    {
        if (QuicConnection.IsSupported)
        {
            using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            probeDeadline.CancelAfter(ProtocolProbeTimeout);
            try
            {
                using var probe = Request(ProtocolIdentity.UpdateManifestRoute, HttpVersion.Version30);
                using var response = await client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, probeDeadline.Token);
                if (response.Version.Major == 3)
                {
                    return HttpVersion.Version30;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                cancellation.ThrowIfCancellationRequested();
            }
        }
        return HttpVersion.Version20;
    }
}
