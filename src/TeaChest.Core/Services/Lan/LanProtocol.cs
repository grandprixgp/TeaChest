using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeaChest.Models;

namespace TeaChest.Services.Lan;

public static class LanProtocol
{
    public const int Version = 1;
    public const int DiscoveryPort = 47820;
    public const int DefaultTcpPort = 47821;
    // The next value and the pairing labels and SNI name in Pairing and PeerClient keep the old
    // name on purpose. They are wire constants, never shown to anyone, and changing them would
    // stop TeaChest from finding or pairing with a PC still running AppMigrate 1.2.
    public const string DiscoveryMagic = "AppMigrate.Nearby";
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public const int MaxDatagramBytes = 2048;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Only peers on the local network are allowed: loopback, RFC 1918, link-local, and IPv6
    /// unique-local. The firewall rule says the same thing; this is a second check.
    /// </summary>
    public static bool IsLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte first = address.GetAddressBytes()[0];
            return address.IsIPv6LinkLocal || (first & 0xFE) == 0xFC;
        }
        return false;
    }
}

public sealed record DeviceInfo(string DeviceId, string Name, string Os, int AppCount, int TcpPort, int ProtocolVersion);

public sealed class DiscoveryMessage
{
    public string Magic { get; set; } = LanProtocol.DiscoveryMagic;
    public string Kind { get; set; } = "";
    public DeviceInfo? Device { get; set; }
}

public static class PeerMessageTypes
{
    public const string Hello = "hello";
    public const string Commit = "commit";
    public const string Nonce = "nonce";
    public const string Reveal = "reveal";
    public const string PairResult = "pair-result";
    public const string GetInventory = "get-inventory";
    public const string Inventory = "inventory";
    public const string Error = "error";
}

public sealed class PeerMessage
{
    public string Type { get; set; } = "";
    public DeviceInfo? Device { get; set; }

    /// <summary>On a server hello: whether the server already trusts this client.</summary>
    public bool? Known { get; set; }

    /// <summary>Base64 commitment or nonce during pairing.</summary>
    public string? Data { get; set; }

    public bool? Accepted { get; set; }
    public string? Error { get; set; }
    public ManifestDocument? Inventory { get; set; }
}

/// <summary>Length-prefixed JSON frames: a 4-byte big-endian length, then UTF-8 JSON.</summary>
public static class FrameCodec
{
    public static async Task WriteAsync(Stream stream, PeerMessage message, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, LanProtocol.JsonOptions);
        if (body.Length > LanProtocol.MaxFrameBytes) throw new InvalidDataException("Message is too large to send.");

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PeerMessage> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > LanProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"Peer sent an invalid frame length ({length}).");
        }

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PeerMessage>(body, LanProtocol.JsonOptions)
               ?? throw new InvalidDataException("Peer sent an empty message.");
    }

    public static async Task<PeerMessage> ExpectAsync(Stream stream, string type, CancellationToken cancellationToken)
    {
        PeerMessage message = await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (message.Type == PeerMessageTypes.Error)
        {
            throw new PeerRejectedException(message.Error ?? "The other device ended the session.");
        }
        if (message.Type != type)
        {
            throw new InvalidDataException($"Expected '{type}' from the other device but got '{message.Type}'.");
        }
        return message;
    }
}

public sealed class PeerRejectedException : Exception
{
    public PeerRejectedException(string message) : base(message) { }
}
