using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport;

// The Windows setup finds masters on the local network here, and a running master answers the same multicast query.
public static class LanDiscovery
{
    private const int Port = 45444;
    private static readonly IPAddress Group = IPAddress.Parse("239.255.77.77");
    private const string Query = ProtocolIdentity.MasterDiscoveryQuery;

    public static async Task<string[]> FindAsync(CancellationToken cancellation)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Ttl = 1;
        byte[] query = Encoding.UTF8.GetBytes(Query);
        await socket.SendAsync(query, new IPEndPoint(Group, Port), cancellation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var results = new HashSet<string>();
        try
        {
            while (results.Count < 16)
            {
                UdpReceiveResult response = await socket.ReceiveAsync(deadline.Token);
                if (response.Buffer.Length > 1024)
                {
                    continue;
                }
                try
                {
                    JsonObject message = JsonNode.Parse(response.Buffer)!.AsObject();
                    string? address = message["master"]?.ToString();
                    if (message["protocol"]?.ToString() == Query && Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) &&
                        uri.Scheme == "https"
                            && IPAddress.TryParse(uri.Host, out IPAddress? host)
                            && host.Equals(response.RemoteEndPoint.Address))
                    {
                        results.Add(uri.ToString());
                    }
                }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
                {
                    // Discovery packets are untrusted hints, never enrollment or operational commands.
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
        }
        return results.Order().ToArray();
    }

    public static async Task AdvertiseAsync(string origin, CancellationToken cancellation)
    {
        try
        {
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            socket.JoinMulticastGroup(Group);
            socket.Ttl = 1;
            byte[] response = Encoding.UTF8.GetBytes(new JsonObject { ["protocol"] = Query, ["master"] = origin }.ToJsonString());
            DateTimeOffset reset = DateTimeOffset.UtcNow.AddSeconds(1);
            int replies = 0;
            while (!cancellation.IsCancellationRequested)
            {
                UdpReceiveResult request = await socket.ReceiveAsync(cancellation);
                if (DateTimeOffset.UtcNow >= reset)
                {
                    reset = DateTimeOffset.UtcNow.AddSeconds(1);
                    replies = 0;
                }
                if (request.Buffer.Length != Encoding.UTF8.GetByteCount(Query)
                    || Encoding.UTF8.GetString(request.Buffer) != Query
                    || replies++ >= 20)
                {
                    continue;
                }
                await socket.SendAsync(response, request.RemoteEndPoint, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (SocketException)
        {
            Console.Error.WriteLine("LAN discovery unavailable; enter the master IP manually.");
        }
    }
}
