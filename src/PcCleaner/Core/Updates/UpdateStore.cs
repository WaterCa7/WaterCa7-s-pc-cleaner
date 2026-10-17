using System.IO;
using System.Text.Json;

namespace PcCleaner.Core.Updates;

/// <summary>A downloaded update with a valid signature, waiting to be installed.</summary>
public sealed record StagedUpdate(UpdateManifest Manifest, string ExePath);

/// <summary>
/// %LOCALAPPDATA%\WaterCa7sPcCleaner\Updates: a folder per version (exe, update.json, update.json.sig) and state.json with
/// the count of failed installs. Everything here is user-writable, so it is verified again before use.
/// </summary>
public sealed class UpdateStore
{
    public const string ManifestName = "update.json";
    public const string SignatureName = "update.json.sig";
    public const int MaxInstallFailures = 2;

    public UpdateStore(string root) => Root = root;

    public string Root { get; }

    private string StatePath => Path.Combine(Root, "state.json");

    public string DirFor(Version version) => Path.Combine(Root, version.ToString(3));

    public string ExePathFor(UpdateManifest manifest) => Path.Combine(DirFor(manifest.Version), manifest.Download.Name);

    public void SaveManifest(UpdateManifest manifest, byte[] json, byte[] signature)
    {
        var dir = AppPaths.EnsureDir(DirFor(manifest.Version));
        File.WriteAllBytes(Path.Combine(dir, ManifestName), json);
        File.WriteAllBytes(Path.Combine(dir, SignatureName), signature);
    }

    /// <summary>The signed manifest stored for <paramref name="version"/>, or null.</summary>
    public UpdateManifest? LoadVerified(Version version, string publicKeyPem)
    {
        try
        {
            var dir = DirFor(version);
            var json = File.ReadAllBytes(Path.Combine(dir, ManifestName));
            if (!UpdateSignature.Verify(json, File.ReadAllBytes(Path.Combine(dir, SignatureName)), publicKeyPem))
                return null;
            var manifest = UpdateManifest.Parse(json, out _);
            return manifest?.Version == UpdateManifest.Normalize(version) ? manifest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The newest version above <paramref name="current"/> whose files are complete and whose update.json is signed.</summary>
    public StagedUpdate? FindStaged(Version current, string publicKeyPem)
    {
        if (!Directory.Exists(Root))
            return null;
        StagedUpdate? best = null;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (!UpdateManifest.TryParseVersion(Path.GetFileName(dir), out var version))
                continue;
            var manifest = LoadVerified(version, publicKeyPem);
            if (manifest is null || !manifest.IsNewerThan(current))
                continue;
            var exe = ExePathFor(manifest);
            if (!File.Exists(exe) || new FileInfo(exe).Length != manifest.Download.Size)
                continue;
            if (best is null || manifest.Version > best.Manifest.Version)
                best = new StagedUpdate(manifest, exe);
        }
        return best;
    }

    public int FailuresFor(Version version)
    {
        var state = ReadState();
        return state.Version == version.ToString(3) ? state.Failures : 0;
    }

    public void RecordFailure(Version version)
    {
        var state = ReadState();
        var v = version.ToString(3);
        AppPaths.EnsureDir(Root);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new State { Version = v, Failures = state.Version == v ? state.Failures + 1 : 1 }));
    }

    /// <summary>Removes every version folder except <paramref name="keep"/>; with no <paramref name="keep"/> also state.json.</summary>
    public void Clean(Version? keep = null)
    {
        if (!Directory.Exists(Root))
            return;
        var keepDir = keep is null ? null : DirFor(keep);
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (keepDir is not null && string.Equals(dir, keepDir, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Обновление: не удалось удалить {dir}: {ex.Message}");
            }
        }
        if (keep is null)
            UpdateInstaller.TryDelete(StatePath);
    }

    private State ReadState()
    {
        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllBytes(StatePath)) ?? new State();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new State();
        }
    }

    private sealed class State
    {
        public string? Version { get; set; }
        public int Failures { get; set; }
    }
}
