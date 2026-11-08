using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using TeaChest.Models;

namespace TeaChest.Services.Lan;

public sealed record InventoryResult(DeviceInfo Device, ManifestDocument Inventory, bool NewlyPaired, IPEndPoint Endpoint);

public sealed class PeerClient
{
    private readonly DeviceIdentity _identity;
    private readonly KnownPeers _known;

    public PeerClient(DeviceIdentity identity, KnownPeers known)
    {
        _identity = identity;
        _known = known;
    }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Connects to a sharing device and downloads its inventory, pairing first if either side
    /// does not recognise the other. <paramref name="showCode"/> receives the pairing code so it
    /// can be displayed while the other PC's user compares and confirms it.
    /// </summary>
    public async Task<InventoryResult> FetchInventoryAsync(
        IPEndPoint endpoint,
        int localAppCount,
        Action<string>? showCode,
        CancellationToken cancellationToken)
    {
        if (!LanProtocol.IsLocalAddress(endpoint.Address))
        {
            throw new PeerRejectedException("Only devices on your local network can be reached.");
        }

        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        session.CancelAfter(SessionTimeout);
        CancellationToken token = session.Token;

        using var tcp = new TcpClient(endpoint.AddressFamily);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connect.CancelAfter(ConnectTimeout);
            try
            {
                await tcp.ConnectAsync(endpoint, connect.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"No answer from {endpoint}. Check that sharing is on and the firewall allows it.");
            }
        }

        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "appmigrate.nearby",
            ClientCertificates = new X509CertificateCollection { _identity.Certificate },
            // SChannel would otherwise only offer a certificate issued by a CA the server trusts.
            LocalCertificateSelectionCallback = (_, _, _, _, _) => _identity.Certificate,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate != null
        }, token).ConfigureAwait(false);

        byte[] serverFingerprint = Pairing.Fingerprint(ssl.RemoteCertificate!);

        await FrameCodec.WriteAsync(ssl, new PeerMessage
        {
            Type = PeerMessageTypes.Hello,
            Device = _identity.Describe(localAppCount, 0)
        }, token).ConfigureAwait(false);
        PeerMessage hello = await FrameCodec.ExpectAsync(ssl, PeerMessageTypes.Hello, token).ConfigureAwait(false);
        DeviceInfo server = hello.Device ?? throw new InvalidDataException("The other device did not identify itself.");

        // "Known" from the server is only a hint; we always check its certificate ourselves.
        bool newlyPaired = false;
        if (!_known.Contains(serverFingerprint) || hello.Known != true)
        {
            byte[] clientNonce = Pairing.NewNonce();
            await FrameCodec.WriteAsync(ssl, new PeerMessage
            {
                Type = PeerMessageTypes.Commit,
                Data = Convert.ToBase64String(Pairing.Commit(clientNonce))
            }, token).ConfigureAwait(false);

            PeerMessage nonce = await FrameCodec.ExpectAsync(ssl, PeerMessageTypes.Nonce, token).ConfigureAwait(false);
            byte[] serverNonce = Pairing.DecodeBase64(nonce.Data, Pairing.NonceLength);

            await FrameCodec.WriteAsync(ssl, new PeerMessage
            {
                Type = PeerMessageTypes.Reveal,
                Data = Convert.ToBase64String(clientNonce)
            }, token).ConfigureAwait(false);

            showCode?.Invoke(Pairing.Code(serverFingerprint, _identity.Fingerprint, clientNonce, serverNonce));

            PeerMessage result = await FrameCodec.ExpectAsync(ssl, PeerMessageTypes.PairResult, token).ConfigureAwait(false);
            if (result.Accepted != true) throw new PeerRejectedException($"{server.Name} did not accept the pairing request.");
            _known.Add(serverFingerprint, server);
            newlyPaired = true;
        }

        // TODO: v2 idea - ask for settings files too (VS Code, Terminal, Git config), not just
        // the app list. Would need a per-app "recipe" of paths and an explicit opt-in per item.
        // Bigger version: a "smart" LAN file explorer that recommends what's worth bringing over
        // (recently and often touched folders, project dirs, game saves). Reading the NTFS USN
        // journal would give us that activity history cheaply, without crawling the whole disk.
        await FrameCodec.WriteAsync(ssl, new PeerMessage { Type = PeerMessageTypes.GetInventory }, token).ConfigureAwait(false);
        PeerMessage inventory = await FrameCodec.ExpectAsync(ssl, PeerMessageTypes.Inventory, token).ConfigureAwait(false);
        if (inventory.Inventory == null) throw new InvalidDataException($"{server.Name} sent an empty inventory.");
        return new InventoryResult(server, inventory.Inventory, newlyPaired, endpoint);
    }
}
