using System.Security.Cryptography;
using TeaChest.Services.Lan;

namespace TeaChest.Tests;

public sealed class DeviceIdentityTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("teachest-tests-");

    public void Dispose() => _temp.Delete(recursive: true);

    // Stands in for DPAPI: reversible, and a file it didn't write fails to open, the way DPAPI does.
    private sealed class FakeProtector : IDeviceKeyProtector
    {
        private static readonly byte[] Marker = "fake:"u8.ToArray();

        public int Protects { get; private set; }

        public string FileName => "device.pfx.fake";

        public byte[] Protect(byte[] data)
        {
            Protects++;
            return Marker.Concat(data).ToArray();
        }

        public byte[] Unprotect(byte[] data)
        {
            if (!data.AsSpan().StartsWith(Marker)) throw new CryptographicException("Not written by this protector.");
            return data[Marker.Length..];
        }
    }

    [Fact]
    public void SecondLoadKeepsTheSameIdentity()
    {
        var protector = new FakeProtector();

        using DeviceIdentity first = DeviceIdentity.LoadOrCreate(protector, _temp.FullName, "OLD-PC");
        using DeviceIdentity second = DeviceIdentity.LoadOrCreate(protector, _temp.FullName, "OLD-PC");

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(1, protector.Protects);
    }

    [Fact]
    public void KeyFileIsNamedByTheProtectorAndGoesThroughIt()
    {
        using DeviceIdentity identity = DeviceIdentity.LoadOrCreate(new FakeProtector(), _temp.FullName, "OLD-PC");

        string file = Assert.Single(Directory.GetFiles(_temp.FullName));
        Assert.Equal("device.pfx.fake", Path.GetFileName(file));
        Assert.True(File.ReadAllBytes(file).AsSpan().StartsWith("fake:"u8));
    }

    [Fact]
    public void UnreadableKeyStartsOverWithANewIdentityThatSticks()
    {
        File.WriteAllBytes(Path.Combine(_temp.FullName, "device.pfx.fake"), "copied from another user"u8.ToArray());
        var protector = new FakeProtector();

        using DeviceIdentity fresh = DeviceIdentity.LoadOrCreate(protector, _temp.FullName, "NEW-PC");
        using DeviceIdentity again = DeviceIdentity.LoadOrCreate(protector, _temp.FullName, "NEW-PC");

        Assert.Equal(fresh.DeviceId, again.DeviceId);
        Assert.Equal(1, protector.Protects);
    }
}
