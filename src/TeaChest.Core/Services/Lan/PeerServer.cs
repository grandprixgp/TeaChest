using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TeaChest.Models;

namespace TeaChest.Services.Lan;

public interface IInventoryProvider
{
    int AppCount { get; }
    Task<ManifestDocument> GetInventoryAsync(CancellationToken cancellationToken);
}

public sealed record PairingRequest(DeviceInfo Peer, string Code, IPAddress Address);

/// <summary>
/// Serves this PC's inventory to paired devices over mutually authenticated TLS. Read-only: the
/// only thing a peer can ask for is the app list.
/// </summary>
public sealed class PeerServer : IAsyncDisposable
{
    private const int MaxSessions = 4;

    private readonly DeviceIdentity _identity;
    private readonly KnownPeers _known;
    private readonly IInventoryProvider _inventory;
    private readonly Func<PairingRequest, CancellationToken, Task<bool>> _confirmPairing;
    private readonly PairingThrottle _pairingThrottle = new(() => DateTimeOffset.UtcNow);
    private int _promptOpen;
    private readonly SemaphoreSlim _sessions = new(MaxSessions, MaxSessions);
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public PeerServer(
        DeviceIdentity identity,
        KnownPeers known,
        IInventoryProvider inventory,
        Func<PairingRequest, CancellationToken, Task<bool>> confirmPairing)
    {
        _identity = identity;
        _known = known;
        _inventory = inventory;
        _confirmPairing = confirmPairing;
    }

    public event Action<string>? Activity;

    public int Port { get; private set; }
    public TimeSpan PairingTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public void Start(int port = LanProtocol.DefaultTcpPort)
    {
        try
        {
            _listener = Listen(port);
        }
        catch (SocketException) when (port != 0)
        {
            // Preferred port taken; any port works because discovery advertises the real one.
            _listener = Listen(0);
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }

    private static TcpListener Listen(int port)
    {
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();
        return listener;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (cancellationToken.IsCancellationRequested) return;
                continue;
            }

            if (!_sessions.Wait(0))
            {
                client.Dispose();
                continue;
            }
            _ = Task.Run(() => HandleAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        string who = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            using (client)
            {
                IPAddress remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                if (!LanProtocol.IsLocalAddress(remote))
                {
                    Activity?.Invoke($"Refused a connection from {remote}: not on the local network.");
                    return;
                }

                using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                session.CancelAfter(SessionTimeout);
                CancellationToken token = session.Token;

                await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _identity.Certificate,
                    ClientCertificateRequired = true,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    // Certificates are self-signed; trust comes from pairing, not from a CA.
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate != null
                }, token).ConfigureAwait(false);

                if (ssl.RemoteCertificate == null) return;
                byte[] clientFingerprint = Pairing.Fingerprint(ssl.RemoteCertificate);

                PeerMessage hello = await FrameCodec.ExpectAsync(ssl, PeerMessageTypes.Hello, token).ConfigureAwait(false);
                DeviceInfo peer = hello.Device ?? throw new InvalidDataException("The other device did not identify itself.");
                who = $"{peer.Name} ({remote})";
                if (peer.ProtocolVersion != LanProtocol.Version)
                {
                    await SendErrorAsync(ssl, $"Version mismatch: update TeaChest on both PCs.", token).ConfigureAwait(false);
                    return;
                }

                bool paired = _known.Contains(clientFingerprint);
                await FrameCodec.WriteAsync(ssl, new PeerMessage
                {
                    Type = PeerMessageTypes.Hello,
                    Device = _identity.Describe(_inventory.AppCount, Port),
                    Known = paired
                }, token).ConfigureAwait(false);

                PeerMessage next = await FrameCodec.ReadAsync(ssl, token).ConfigureAwait(false);
                if (next.Type == PeerMessageTypes.Commit)
                {
                    if (!await PairAsync(ssl, next, peer, remote, clientFingerprint, token).ConfigureAwait(false)) return;
                    paired = true;
                    next = await FrameCodec.ReadAsync(ssl, token).ConfigureAwait(false);
                }

                if (next.Type != PeerMessageTypes.GetInventory)
                {
                    await SendErrorAsync(ssl, $"Unexpected request '{next.Type}'.", token).ConfigureAwait(false);
                    return;
                }
                if (!paired)
                {
                    await SendErrorAsync(ssl, "This device has not been paired yet.", token).ConfigureAwait(false);
                    return;
                }

                ManifestDocument inventory = await _inventory.GetInventoryAsync(token).ConfigureAwait(false);
                await FrameCodec.WriteAsync(ssl, new PeerMessage { Type = PeerMessageTypes.Inventory, Inventory = inventory }, token).ConfigureAwait(false);
                Activity?.Invoke($"Shared {inventory.Apps.Count} apps with {peer.Name}.");
            }
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or InvalidDataException or JsonException
                                       or OperationCanceledException or SocketException or PeerRejectedException)
        {
            if (!cancellationToken.IsCancellationRequested) Activity?.Invoke($"Session with {who} ended: {ex.Message}");
        }
        finally
        {
            _sessions.Release();
        }
    }

    private async Task<bool> PairAsync(SslStream ssl, PeerMessage commit, DeviceInfo peer, IPAddress remote, byte[] clientFingerprint, CancellationToken token)
    {
        byte[] commitment = Pairing.DecodeBase64(commit.Data, Pairing.HashBytes);
        byte[] serverNonce = Pairing.NewNonce();
        await FrameCodec.WriteAsync(ssl, new PeerMessage { Type = PeerMessageTypes.Nonce, Data = Convert.ToBase64String(serverNonce) }, token).ConfigureAwait(false);

        PeerMessage reveal = await FrameCodec.ExpectAsync(ssl, PeerMessageTypes.Reveal, token).ConfigureAwait(false);
        byte[] clientNonce = Pairing.DecodeBase64(reveal.Data, Pairing.NonceLength);
        if (!Pairing.VerifyCommit(commitment, clientNonce))
        {
            await SendErrorAsync(ssl, "Pairing failed.", token).ConfigureAwait(false);
            Activity?.Invoke($"Pairing with {peer.Name} failed: the commitment did not match.");
            return false;
        }

        if (!_pairingThrottle.TryBegin(remote))
        {
            await SendErrorAsync(ssl, "Too many pairing attempts. Wait a moment and try again.", token).ConfigureAwait(false);
            Activity?.Invoke($"Ignored a pairing request from {peer.Name} ({remote}): too many attempts in a row.");
            return false;
        }

        if (Interlocked.CompareExchange(ref _promptOpen, 1, 0) != 0)
        {
            // Another PC is already waiting on a prompt. This refusal must not count as a decline,
            // or a genuine peer could land in the cooldown because of someone else's request.
            await FrameCodec.WriteAsync(ssl, new PeerMessage { Type = PeerMessageTypes.PairResult, Accepted = false }, token).ConfigureAwait(false);
            Activity?.Invoke($"Another pairing request is already waiting, so {peer.Name} ({remote}) was turned away.");
            return false;
        }

        string code = Pairing.Code(_identity.Fingerprint, clientFingerprint, clientNonce, serverNonce);
        bool accepted;
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(PairingTimeout);
            try
            {
                accepted = await _confirmPairing(new PairingRequest(peer, code, remote), wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                accepted = false;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _promptOpen, 0);
        }

        // Record the decline before telling the peer. A caller that already hung up makes the
        // write below throw, and the cooldown still has to stick.
        if (!accepted) _pairingThrottle.Declined(remote);

        await FrameCodec.WriteAsync(ssl, new PeerMessage { Type = PeerMessageTypes.PairResult, Accepted = accepted }, token).ConfigureAwait(false);
        if (!accepted)
        {
            Activity?.Invoke($"Pairing with {peer.Name} was declined.");
            return false;
        }

        _pairingThrottle.Accepted(remote);
        _known.Add(clientFingerprint, peer);
        Activity?.Invoke($"Paired with {peer.Name}.");
        return true;
    }

    private static Task SendErrorAsync(Stream stream, string error, CancellationToken token)
        => FrameCodec.WriteAsync(stream, new PeerMessage { Type = PeerMessageTypes.Error, Error = error }, token);
}
