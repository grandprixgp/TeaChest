using System.Security.Cryptography;
using TeaChest.Services.Lan;

namespace TeaChest.Tests;

public sealed class DpapiKeyProtectorTests
{
    // AppMigrate and every TeaChest so far wrote this name. A new one would mean pairing again.
    [Fact]
    public void KeepsTheFileNameExistingPairingsUse()
        => Assert.Equal("device.pfx.dpapi", new DpapiKeyProtector().FileName);

    [Fact]
    public void RoundTripsWithoutKeepingTheBytesInTheClear()
    {
        var protector = new DpapiKeyProtector();
        byte[] secret = RandomNumberGenerator.GetBytes(64);

        byte[] stored = protector.Protect(secret);

        Assert.NotEqual(secret, stored);
        Assert.Equal(secret, protector.Unprotect(stored));
    }

    [Fact]
    public void SomeoneElsesDataThrowsACryptographicException()
        => Assert.ThrowsAny<CryptographicException>(() => new DpapiKeyProtector().Unprotect(new byte[] { 1, 2, 3, 4 }));
}
