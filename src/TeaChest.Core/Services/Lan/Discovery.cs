using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace TeaChest.Services.Lan;

public sealed record DiscoveredDevice(DeviceInfo Device, IPAddress Address, DateTimeOffset LastSeen)
{
    public IPEndPoint Endpoint => new(Address, Device.TcpPort);
}

/// <summary>
/// Answers discovery queries while sharing is on. Only the sharing PC listens for inbound UDP;
/// the browsing PC sends a broadcast and gets unicast replies, which the Windows firewall lets
/// through as return traffic, so the new PC needs no firewall rule.
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    private readonly Func<DeviceInfo> _describe;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public DiscoveryResponder(Func<DeviceInfo> describe) => _describe = describe;

    public void Start(int port = LanProtocol.DiscoveryPort)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _ = ReceiveLoopAsync(_udp, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _cts.Dispose();
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // Windows reports ICMP "port unreachable" from an earlier send as a receive error.
                continue;
            }

            if (!LanProtocol.IsLocalAddress(received.RemoteEndPoint.Address)) continue;
            DiscoveryMessage? message = Discovery.Parse(received.Buffer);
            if (message?.Kind != "query") continue;

            byte[] reply = JsonSerializer.SerializeToUtf8Bytes(
                new DiscoveryMessage { Kind = "reply", Device = _describe() }, LanProtocol.JsonOptions);
            try
            {
                await udp.SendAsync(reply, received.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }
}

public static class Discovery
{
    /// <summary>Broadcasts queries until cancelled, reporting each reply.</summary>
    public static async Task BrowseAsync(
        Action<DiscoveredDevice> onFound,
        CancellationToken cancellationToken,
        int port = LanProtocol.DiscoveryPort,
        TimeSpan? interval = null)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        byte[] query = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryMessage { Kind = "query" }, LanProtocol.JsonOptions);

        Task receive = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }

                if (!LanProtocol.IsLocalAddress(received.RemoteEndPoint.Address)) continue;
                DiscoveryMessage? message = Parse(received.Buffer);
                if (message?.Kind != "reply" || message.Device is not { } device) continue;
                if (device.ProtocolVersion != LanProtocol.Version || device.TcpPort is <= 0 or > 65535) continue;
                onFound(new DiscoveredDevice(device, received.RemoteEndPoint.Address, DateTimeOffset.Now));
            }
        }, CancellationToken.None);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (IPEndPoint target in BroadcastTargets(port))
                {
                    try
                    {
                        await udp.SendAsync(query, target, cancellationToken).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // Some adapters (VPNs, disconnected Wi-Fi) refuse broadcasts; try the rest.
                    }
                }
                await Task.Delay(interval ?? TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        await receive.ConfigureAwait(false);
    }

    internal static DiscoveryMessage? Parse(byte[] buffer)
    {
        if (buffer.Length == 0 || buffer.Length > LanProtocol.MaxDatagramBytes) return null;
        try
        {
            DiscoveryMessage? message = JsonSerializer.Deserialize<DiscoveryMessage>(buffer, LanProtocol.JsonOptions);
            return message?.Magic == LanProtocol.DiscoveryMagic ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // TODO: IPv4 only for now. An IPv6-only network (rare at home, but it happens) would need a
    // link-local multicast group as well.
    /// <summary>The limited broadcast, each adapter's directed broadcast, and loopback (for a second copy on the same PC).</summary>
    internal static IEnumerable<IPEndPoint> BroadcastTargets(int port)
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null) continue;
                    byte[] address = unicast.Address.GetAddressBytes();
                    byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 0)) continue;
                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++) broadcast[i] = (byte)(address[i] | ~mask[i]);
                    targets.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        return targets.Select(a => new IPEndPoint(a, port));
    }
}
