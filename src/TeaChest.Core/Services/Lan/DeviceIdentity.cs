using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace TeaChest.Services.Lan;

/// <summary>
/// This PC's long-lived TLS certificate. Peers remember it by fingerprint, so it must stay the
/// same between runs. The PFX is kept by an <see cref="IDeviceKeyProtector"/>, which on Windows
/// encrypts it for the current user with DPAPI.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private DeviceIdentity(X509Certificate2 certificate, string name)
    {
        Certificate = certificate;
        Name = name;
        Fingerprint = Pairing.Fingerprint(certificate);
        DeviceId = Convert.ToHexString(Fingerprint, 0, 8).ToLowerInvariant();
    }

    public X509Certificate2 Certificate { get; }
    public byte[] Fingerprint { get; }
    public string DeviceId { get; }
    public string Name { get; }

    public static string DefaultDirectory
        => AppPaths.DataDirectory;

    public static DeviceIdentity LoadOrCreate(IDeviceKeyProtector protector, string? directory = null, string? name = null)
    {
        directory ??= DefaultDirectory;
        name ??= Environment.MachineName;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, protector.FileName);

        if (File.Exists(path))
        {
            try
            {
                byte[] pfx = protector.Unprotect(File.ReadAllBytes(path));
                return new DeviceIdentity(LoadPfx(pfx), name);
            }
            catch (CryptographicException)
            {
                // Unreadable (copied from another user or machine): start over with a new identity.
            }
        }

        byte[] created = CreatePfx(name);
        File.WriteAllBytes(path, protector.Protect(created));
        return new DeviceIdentity(LoadPfx(created), name);
    }

    /// <summary>A throwaway identity, used by tests.</summary>
    public static DeviceIdentity CreateEphemeral(string name) => new(LoadPfx(CreatePfx(name)), name);

    public DeviceInfo Describe(int appCount, int tcpPort)
        => new(DeviceId, Name, Environment.OSVersion.VersionString, appCount, tcpPort, LanProtocol.Version);

    public void Dispose() => Certificate.Dispose();

    private static byte[] CreatePfx(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=TeaChest {SanitizeCn(name)}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, critical: false));
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        return certificate.Export(X509ContentType.Pfx);
    }

    // SChannel cannot use an in-memory ephemeral key for TLS, so the key is imported into the
    // user's key store for the lifetime of this process.
    private static X509Certificate2 LoadPfx(byte[] pfx)
        => new(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);

    private static string SanitizeCn(string name)
        => new(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').Take(48).ToArray());
}

public sealed class KnownPeer
{
    public string Fingerprint { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; }
}

/// <summary>Peers confirmed with a pairing code once. Later sessions with them skip the code.</summary>
public sealed class KnownPeers
{
    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, KnownPeer> _peers = new(StringComparer.OrdinalIgnoreCase);

    private KnownPeers(string? path) => _path = path;

    public static KnownPeers Load(string? directory = null)
    {
        string path = Path.Combine(directory ?? DeviceIdentity.DefaultDirectory, "known-peers.json");
        var store = new KnownPeers(path);
        try
        {
            if (File.Exists(path))
            {
                foreach (KnownPeer peer in JsonSerializer.Deserialize<List<KnownPeer>>(File.ReadAllText(path)) ?? new())
                {
                    if (peer.Fingerprint.Length > 0) store._peers[peer.Fingerprint] = peer;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // A damaged file only means peers have to pair again.
        }
        return store;
    }

    /// <summary>A store that is never written to disk, used by tests.</summary>
    public static KnownPeers InMemory() => new(null);

    public bool Contains(byte[] fingerprint)
    {
        lock (_gate) return _peers.ContainsKey(Convert.ToHexString(fingerprint));
    }

    public IReadOnlyList<KnownPeer> All()
    {
        lock (_gate) return _peers.Values.OrderBy(p => p.Name).ToList();
    }

    public void Add(byte[] fingerprint, DeviceInfo device)
    {
        lock (_gate)
        {
            _peers[Convert.ToHexString(fingerprint)] = new KnownPeer
            {
                Fingerprint = Convert.ToHexString(fingerprint),
                Name = device.Name,
                DeviceId = device.DeviceId,
                PairedAt = DateTimeOffset.Now
            };
            Save();
        }
    }

    public void Remove(string fingerprint)
    {
        lock (_gate)
        {
            if (_peers.Remove(fingerprint)) Save();
        }
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_peers.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // Not fatal; the peer will be asked to pair again next time.
        }
    }
}
