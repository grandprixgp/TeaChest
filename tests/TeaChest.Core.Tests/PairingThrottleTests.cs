using System.Net;
using TeaChest.Models;
using TeaChest.Services.Lan;

namespace TeaChest.Tests;

public class PairingThrottleTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static readonly IPAddress Peer = IPAddress.Parse("192.168.1.20");

    private static PairingThrottle NewThrottle(FakeClock clock) => new(clock.Read);

    [Fact]
    public void AnAddressWithNoHistoryCanAsk()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        Assert.True(throttle.TryBegin(Peer));
    }

    [Fact]
    public void ADeclineBlocksTheAddressForThirtySeconds()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        throttle.Declined(Peer);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(throttle.TryBegin(Peer));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryBegin(Peer));
    }

    [Fact]
    public void ASecondDeclineStillOnlyCostsThirtySeconds()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(throttle.TryBegin(Peer));
        throttle.Declined(Peer);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(throttle.TryBegin(Peer));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryBegin(Peer));
    }

    [Fact]
    public void AThirdDeclineWithinTenMinutesCostsFiveMinutes()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromSeconds(31));
        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromSeconds(31));
        throttle.Declined(Peer);

        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.False(throttle.TryBegin(Peer));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryBegin(Peer));
    }

    [Fact]
    public void DeclinesSpreadPastTenMinutesDoNotEscalate()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromMinutes(6));
        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromMinutes(6));
        // The first decline is now outside the window, so this is only the second inside it.
        throttle.Declined(Peer);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(throttle.TryBegin(Peer));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryBegin(Peer));
    }

    [Fact]
    public void AnAcceptedPairingClearsTheHistory()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromSeconds(31));
        throttle.Declined(Peer);
        throttle.Accepted(Peer);

        // If the two earlier declines had survived, the next one would already cost five minutes.
        throttle.Declined(Peer);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(throttle.TryBegin(Peer));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryBegin(Peer));
    }

    [Fact]
    public void MappedAndPlainAddressesShareOneCooldown()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);

        throttle.Declined(IPAddress.Parse("::ffff:10.0.0.1"));

        Assert.False(throttle.TryBegin(IPAddress.Parse("10.0.0.1")));
        Assert.False(throttle.TryBegin(IPAddress.Parse("::ffff:10.0.0.1")));

        throttle.Declined(IPAddress.Parse("10.0.0.1"));
        throttle.Declined(IPAddress.Parse("::ffff:10.0.0.1"));

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(throttle.TryBegin(IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void TheOldestAddressIsDroppedPastTheCap()
    {
        var clock = new FakeClock();
        PairingThrottle throttle = NewThrottle(clock);
        var oldest = IPAddress.Parse("10.0.0.1");

        throttle.Declined(oldest);
        throttle.Declined(oldest);
        for (int i = 0; i < 256; i++)
        {
            throttle.Declined(new IPAddress(new byte[] { 10, 10, 0, (byte)i }));
        }

        // Dropped at the cap, so this decline starts over at thirty seconds. If its history had
        // survived it would be the third decline and the address would wait five minutes.
        throttle.Declined(oldest);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(throttle.TryBegin(oldest));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryBegin(oldest));
    }
}

public class PairingThrottleSessionTests : IAsyncLifetime
{
    private sealed class TinyInventory : IInventoryProvider
    {
        public int AppCount => 1;
        public Task<ManifestDocument> GetInventoryAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ManifestDocument
            {
                Apps = new List<AppRecord> { new() { Name = "Git", Id = "Git.Git", InstallType = "winget", Source = "winget" } }
            });
    }

    private readonly DeviceIdentity _serverIdentity = DeviceIdentity.CreateEphemeral("OLD-PC");
    private readonly List<DeviceIdentity> _clientIdentities = new();
    private readonly KnownPeers _serverPeers = KnownPeers.InMemory();
    private int _confirmCalls;
    private TaskCompletionSource<bool>? _hold;
    private PeerServer _server = null!;

    private IPEndPoint Endpoint => new(IPAddress.Loopback, _server.Port);

    public Task InitializeAsync()
    {
        _server = new PeerServer(_serverIdentity, _serverPeers, new TinyInventory(), ConfirmAsync);
        _server.Start(0);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _hold?.TrySetResult(false);
        await _server.DisposeAsync();
        _serverIdentity.Dispose();
        foreach (DeviceIdentity identity in _clientIdentities) identity.Dispose();
    }

    private async Task<bool> ConfirmAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _confirmCalls);
        TaskCompletionSource<bool>? hold = _hold;
        return hold is null
            ? false
            : await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private PeerClient NewClient(string name)
    {
        DeviceIdentity identity = DeviceIdentity.CreateEphemeral(name);
        _clientIdentities.Add(identity);
        return new PeerClient(identity, KnownPeers.InMemory());
    }

    private Task<InventoryResult> FetchAsync(PeerClient client, CancellationToken token = default)
        => client.FetchInventoryAsync(Endpoint, 0, null, token);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ADeclinedClientCannotPromptAgainStraightAway()
    {
        var throttled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Activity += line =>
        {
            if (line.Contains("too many attempts", StringComparison.OrdinalIgnoreCase)) throttled.TrySetResult(line);
        };

        PeerClient client = NewClient("NEW-PC");
        await Assert.ThrowsAsync<PeerRejectedException>(() => FetchAsync(client));
        Assert.Equal(1, _confirmCalls);

        var second = await Assert.ThrowsAsync<PeerRejectedException>(() => FetchAsync(client));
        Assert.Equal("Too many pairing attempts. Wait a moment and try again.", second.Message);
        Assert.Equal(1, _confirmCalls);

        await throttled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AClientThatHangsUpStillLeavesItsDeclineBehind()
    {
        _hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnActivity(string line)
        {
            // Either outcome proves the decline was recorded first: the write failed, or it went through.
            if (line.Contains("NEW-PC-HANGUP", StringComparison.Ordinal)) settled.TrySetResult(line);
        }

        _server.Activity += OnActivity;
        try
        {
            PeerClient client = NewClient("NEW-PC-HANGUP");
            using var cancel = new CancellationTokenSource();
            Task<InventoryResult> first = FetchAsync(client, cancel.Token);
            await WaitUntil(() => Volatile.Read(ref _confirmCalls) == 1, "the pairing prompt");

            cancel.Cancel();
            Assert.NotNull(await Record.ExceptionAsync(() => first));
            _hold.SetResult(false);
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var retry = await Assert.ThrowsAsync<PeerRejectedException>(() => FetchAsync(client));
            Assert.Equal("Too many pairing attempts. Wait a moment and try again.", retry.Message);
            Assert.Equal(1, Volatile.Read(ref _confirmCalls));
        }
        finally
        {
            _server.Activity -= OnActivity;
            _hold.TrySetResult(false);
        }
    }

    [Fact]
    public async Task AnotherRequestCannotTakeAnOpenPromptSlot()
    {
        _hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PeerClient firstClient = NewClient("NEW-PC-FIRST");
        Task<InventoryResult> first = FetchAsync(firstClient);
        await WaitUntil(() => Volatile.Read(ref _confirmCalls) == 1, "the first pairing prompt");

        foreach (string name in new[] { "NEW-PC-SECOND", "NEW-PC-THIRD" })
        {
            PeerClient other = NewClient(name);
            var refused = await Assert.ThrowsAsync<PeerRejectedException>(() => FetchAsync(other));

            // A plain "not accepted", not the throttle error: another PC's prompt must not
            // count against this address.
            Assert.Equal("OLD-PC did not accept the pairing request.", refused.Message);
            Assert.DoesNotContain("Too many", refused.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(1, Volatile.Read(ref _confirmCalls));

        _hold.SetResult(true);
        InventoryResult result = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.NewlyPaired);
        Assert.Equal(1, Volatile.Read(ref _confirmCalls));
    }
}
