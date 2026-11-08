using System.IO;
using TeaChest.Services;

namespace TeaChest.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "teachest-tests-" + Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_root, "AppMigrate");
    private string Current => Path.Combine(_root, "TeaChest");

    public AppPathsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void MovesFilesAndRemovesTheOldFolder()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "device.pfx.dpapi"), "key");
        File.WriteAllText(Path.Combine(Legacy, "known-peers.json"), "[]");

        AppPaths.MoveLegacyData(Legacy, Current);

        Assert.Equal("key", File.ReadAllText(Path.Combine(Current, "device.pfx.dpapi")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(Current, "known-peers.json")));
        Assert.False(Directory.Exists(Legacy));
    }

    [Fact]
    public void NeverOverwritesExistingData()
    {
        Directory.CreateDirectory(Legacy);
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Legacy, "device.pfx.dpapi"), "old");
        File.WriteAllText(Path.Combine(Legacy, "known-peers.json"), "[]");
        File.WriteAllText(Path.Combine(Current, "device.pfx.dpapi"), "new");

        AppPaths.MoveLegacyData(Legacy, Current);

        Assert.Equal("new", File.ReadAllText(Path.Combine(Current, "device.pfx.dpapi")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(Current, "known-peers.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Legacy, "device.pfx.dpapi")));
    }

    [Fact]
    public void DoesNothingWithoutAnOldFolder()
    {
        AppPaths.MoveLegacyData(Legacy, Current);

        Assert.False(Directory.Exists(Current));
    }
}
