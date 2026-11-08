using System.IO;
using System.Net;
using TeaChest.Models;
using TeaChest.Services;
using TeaChest.Services.Lan;

namespace TeaChest.Tests;

public class PairingTests
{
    [Fact]
    public void CommitVerifiesOnlyTheCommittedNonce()
    {
        byte[] nonce = Pairing.NewNonce();
        byte[] commitment = Pairing.Commit(nonce);

        Assert.True(Pairing.VerifyCommit(commitment, nonce));
        Assert.False(Pairing.VerifyCommit(commitment, Pairing.NewNonce()));
    }

    [Fact]
    public void CodeChangesWhenAnyCertificateChanges()
    {
        byte[] server = new byte[32], client = new byte[32], nc = Pairing.NewNonce(), ns = Pairing.NewNonce();
        string code = Pairing.Code(server, client, nc, ns);

        byte[] otherServer = (byte[])server.Clone();
        otherServer[0] = 1;

        Assert.Matches(@"^\d{3} \d{3}$", code);
        Assert.Equal(code, Pairing.Code(server, client, nc, ns));
        Assert.NotEqual(code, Pairing.Code(otherServer, client, nc, ns));
    }

    [Fact]
    public void MalformedPairingDataIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => Pairing.DecodeBase64("not base64!", 32));
        Assert.Throws<InvalidDataException>(() => Pairing.DecodeBase64(Convert.ToBase64String(new byte[5]), 32));
    }
}

public class FrameCodecTests
{
    [Fact]
    public async Task RoundTrips()
    {
        var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new PeerMessage { Type = PeerMessageTypes.Hello, Known = true }, CancellationToken.None);
        stream.Position = 0;

        PeerMessage message = await FrameCodec.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(PeerMessageTypes.Hello, message.Type);
        Assert.True(message.Known);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(LanProtocol.MaxFrameBytes + 1)]
    public async Task RejectsBadLengths(int length)
    {
        byte[] header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, length);

        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(new MemoryStream(header), CancellationToken.None));
    }

    [Fact]
    public async Task ErrorFrameBecomesRejection()
    {
        var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new PeerMessage { Type = PeerMessageTypes.Error, Error = "nope" }, CancellationToken.None);
        stream.Position = 0;

        var ex = await Assert.ThrowsAsync<PeerRejectedException>(() => FrameCodec.ExpectAsync(stream, PeerMessageTypes.Hello, CancellationToken.None));
        Assert.Equal("nope", ex.Message);
    }
}

public class LocalAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("172.32.1.1", false)]
    [InlineData("169.254.3.4", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("2001:4860::8888", false)]
    [InlineData("::ffff:192.168.0.2", true)]
    public void OnlyLanAddresses(string address, bool local)
        => Assert.Equal(local, LanProtocol.IsLocalAddress(IPAddress.Parse(address)));
}

public class PeerSessionTests : IAsyncLifetime
{
    private sealed class FixedInventory : IInventoryProvider
    {
        public int AppCount => 2;
        public Task<ManifestDocument> GetInventoryAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ManifestDocument
            {
                Apps = new List<AppRecord>
                {
                    new() { Name = "Git", Id = "Git.Git", Source = "winget", InstallType = "winget" },
                    new() { Name = "Tool", Id = "Vendor.Tool", Source = "winget", InstallType = "winget", WingetArgs = "--override /evil" }
                }
            });
    }

    private readonly DeviceIdentity _serverIdentity = DeviceIdentity.CreateEphemeral("OLD-PC");
    private readonly DeviceIdentity _clientIdentity = DeviceIdentity.CreateEphemeral("NEW-PC");
    private readonly KnownPeers _serverPeers = KnownPeers.InMemory();
    private readonly KnownPeers _clientPeers = KnownPeers.InMemory();
    private readonly List<PairingRequest> _requests = new();
    private bool _accept = true;
    private PeerServer _server = null!;

    public Task InitializeAsync()
    {
        _server = new PeerServer(_serverIdentity, _serverPeers, new FixedInventory(), (request, _) =>
        {
            lock (_requests) _requests.Add(request);
            return Task.FromResult(_accept);
        });
        _server.Start(0);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _serverIdentity.Dispose();
        _clientIdentity.Dispose();
    }

    private Task<InventoryResult> FetchAsync(Action<string>? showCode = null)
        => new PeerClient(_clientIdentity, _clientPeers)
            .FetchInventoryAsync(new IPEndPoint(IPAddress.Loopback, _server.Port), 0, showCode, CancellationToken.None);

    [Fact]
    public async Task FirstConnectionPairsAndBothSidesSeeTheSameCode()
    {
        string? clientCode = null;

        InventoryResult result = await FetchAsync(code => clientCode = code);

        PairingRequest request = Assert.Single(_requests);
        Assert.Equal(request.Code, clientCode);
        Assert.Equal("NEW-PC", request.Peer.Name);
        Assert.Equal("OLD-PC", result.Device.Name);
        Assert.True(result.NewlyPaired);
        Assert.Equal(2, result.Inventory.Apps.Count);
    }

    [Fact]
    public async Task SecondConnectionSkipsPairing()
    {
        await FetchAsync();
        InventoryResult again = await FetchAsync(_ => throw new InvalidOperationException("should not ask for a code"));

        Assert.False(again.NewlyPaired);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task ForgottenPeerHasToPairAgain()
    {
        await FetchAsync();
        foreach (KnownPeer peer in _serverPeers.All()) _serverPeers.Remove(peer.Fingerprint);

        InventoryResult again = await FetchAsync();

        Assert.True(again.NewlyPaired);
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task DeclinedPairingSendsNothing()
    {
        _accept = false;

        await Assert.ThrowsAsync<PeerRejectedException>(() => FetchAsync());
        Assert.Empty(_clientPeers.All());
        Assert.Empty(_serverPeers.All());
    }

    [Fact]
    public async Task ReceivedInventoryIsSanitizedLikeAnyNetworkManifest()
    {
        InventoryResult result = await FetchAsync();

        List<string> warnings = ManifestSanitizer.Sanitize(result.Inventory, ManifestTrust.Network);

        Assert.Equal("", result.Inventory.Apps[1].WingetArgs);
        Assert.Single(warnings);
    }
}

public class DiscoveryTests
{
    [Fact]
    public async Task FindsResponderOverLoopback()
    {
        int port = Random.Shared.Next(48000, 49000);
        using var identity = DeviceIdentity.CreateEphemeral("OLD-PC");
        using var responder = new DiscoveryResponder(() => identity.Describe(42, 47821));
        responder.Start(port);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        DiscoveredDevice? found = null;
        await Discovery.BrowseAsync(device =>
        {
            found = device;
            cts.Cancel();
        }, cts.Token, port, TimeSpan.FromMilliseconds(200));

        Assert.NotNull(found);
        Assert.Equal("OLD-PC", found!.Device.Name);
        Assert.Equal(42, found.Device.AppCount);
        Assert.Equal(47821, found.Endpoint.Port);
    }

    [Fact]
    public void IgnoresForeignDatagrams()
    {
        Assert.Null(Discovery.Parse("{\"magic\":\"other\",\"kind\":\"query\"}"u8.ToArray()));
        Assert.Null(Discovery.Parse("not json"u8.ToArray()));
        Assert.Null(Discovery.Parse(new byte[LanProtocol.MaxDatagramBytes + 1]));
    }
}

public class WireCompatibilityTests
{
    // These values are what AppMigrate 1.2 sends. If one changes, 1.2 and TeaChest stop seeing each other.
    [Fact]
    public void DiscoveryMagicStillMatchesAppMigrate()
        => Assert.Equal("AppMigrate.Nearby", LanProtocol.DiscoveryMagic);

    [Fact]
    public void PairingStillMatchesAppMigrate()
    {
        byte[] zero = new byte[32];

        Assert.Equal("D97C8AD4444273FD4D84BC1D8D8AD35925E98702674BE5D4B2A285ADD1AA9A4D", Convert.ToHexString(Pairing.Commit(zero)));
        Assert.Equal("370 454", Pairing.Code(zero, zero, zero, zero));
    }
}
