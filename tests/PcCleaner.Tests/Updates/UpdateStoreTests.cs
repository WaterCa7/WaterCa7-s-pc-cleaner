using System.IO;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

public class UpdateStoreTests
{
    public UpdateStoreTests() => TestEnvironment.Init();

    /// <summary>Stages <paramref name="version"/> like UpdateService does: exe, update.json, update.json.sig.</summary>
    internal static UpdateManifest Stage(UpdateStore store, UpdateTestKit kit, string version, byte[] exe)
    {
        var json = UpdateTestKit.ManifestJson(version, exe.Length, UpdateTestKit.Sha256(exe), notes: $"Версия {version}");
        var manifest = UpdateManifest.Parse(json, out _)!;
        Directory.CreateDirectory(store.DirFor(manifest.Version));
        File.WriteAllBytes(store.ExePathFor(manifest), exe);
        store.SaveManifest(manifest, json, kit.Sign(json));
        return manifest;
    }

    [Fact]
    public void FindStaged_ReturnsTheNewestCompleteSignedVersionAboveCurrent()
    {
        using var kit = new UpdateTestKit();
        var store = new UpdateStore(TestEnvironment.NewTempDir("upd-store"));
        Stage(store, kit, "1.1.0", UpdateTestKit.FakeExe(1));
        Stage(store, kit, "1.3.0", UpdateTestKit.FakeExe(3));
        Stage(store, kit, "1.2.0", UpdateTestKit.FakeExe(2));

        var staged = store.FindStaged(new Version(1, 1, 0, 0), kit.PublicKeyPem);

        Assert.Equal(new Version(1, 3, 0), staged!.Manifest.Version);
        Assert.Equal(store.ExePathFor(staged.Manifest), staged.ExePath);
        Assert.Null(store.FindStaged(new Version(1, 3, 0), kit.PublicKeyPem));
    }

    [Fact]
    public void FindStaged_SkipsUnsignedIncompleteAndMisplacedVersions()
    {
        using var kit = new UpdateTestKit();
        using var other = new UpdateTestKit();
        var store = new UpdateStore(TestEnvironment.NewTempDir("upd-store"));
        var unsigned = Stage(store, kit, "1.2.0", UpdateTestKit.FakeExe(2));
        File.WriteAllBytes(Path.Combine(store.DirFor(unsigned.Version), UpdateStore.SignatureName), other.Sign(new byte[] { 1 }));
        var incomplete = Stage(store, kit, "1.3.0", UpdateTestKit.FakeExe(3));
        File.WriteAllBytes(store.ExePathFor(incomplete), new byte[10]);
        var misplaced = Stage(store, kit, "1.4.0", UpdateTestKit.FakeExe(4));
        Directory.Move(store.DirFor(misplaced.Version), Path.Combine(store.Root, "9.9.9"));

        Assert.Null(store.FindStaged(new Version(1, 1, 0), kit.PublicKeyPem));
    }

    [Fact]
    public void Failures_AreCountedPerVersion()
    {
        var store = new UpdateStore(TestEnvironment.NewTempDir("upd-store"));
        var v = new Version(1, 2, 0);
        Assert.Equal(0, store.FailuresFor(v));
        store.RecordFailure(v);
        store.RecordFailure(v);
        Assert.Equal(2, store.FailuresFor(v));
        store.RecordFailure(new Version(1, 3, 0));
        Assert.Equal(0, store.FailuresFor(v));
        Assert.Equal(1, store.FailuresFor(new Version(1, 3, 0)));
    }

    [Fact]
    public void Clean_KeepsOnlyTheGivenVersion_OrRemovesEverything()
    {
        using var kit = new UpdateTestKit();
        var store = new UpdateStore(TestEnvironment.NewTempDir("upd-store"));
        Stage(store, kit, "1.2.0", UpdateTestKit.FakeExe(2));
        Stage(store, kit, "1.3.0", UpdateTestKit.FakeExe(3));
        store.RecordFailure(new Version(1, 3, 0));

        store.Clean(keep: new Version(1, 3, 0));
        Assert.False(Directory.Exists(store.DirFor(new Version(1, 2, 0))));
        Assert.True(Directory.Exists(store.DirFor(new Version(1, 3, 0))));
        Assert.Equal(1, store.FailuresFor(new Version(1, 3, 0)));

        store.Clean();
        Assert.Empty(Directory.EnumerateFileSystemEntries(store.Root));
    }
}
