using System.IO;
using System.Security;
using System.Text;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Settings;

/// <summary>The check handed to RegistryBackup.ImportCheckedAsync, and Russian error phrases in result lines.</summary>
public class SettingsRound3Tests
{
    private const string Header = "Windows Registry Editor Version 5.00\r\n\r\n";
    private const string Root = @"HKEY_CURRENT_USER\Software\WaterCa7sPcCleanerTests\Settings\Round3";

    public SettingsRound3Tests() => TestEnvironment.Init();

    private static byte[] Utf16(string text) => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();

    [Fact]
    public void SnapshotMatches_OnlyTheExactBytesThatWereConfirmed()
    {
        var dir = TestEnvironment.NewTempDir("snapshot");
        var confirmed = Utf16(Header + $"[{Root}]\r\n\"Value\"=dword:00000001\r\n\r\n");
        var snapshot = Path.Combine(dir, "snapshot.reg");
        File.WriteAllBytes(snapshot, confirmed);
        Assert.True(SettingsViewModel.SnapshotMatches(snapshot, confirmed));

        // Rewritten after the confirmation (another value, same key): refused.
        File.WriteAllBytes(snapshot, Utf16(Header + $"[{Root}]\r\n\"Value\"=dword:00000002\r\n\r\n"));
        Assert.False(SettingsViewModel.SnapshotMatches(snapshot, confirmed));

        // Gone: refused.
        Assert.False(SettingsViewModel.SnapshotMatches(Path.Combine(dir, "missing.reg"), confirmed));
    }

    [Fact]
    public void SnapshotMatches_RefusesAFileThatIsNoPlainExport_EvenIfTheBytesAreTheSame()
    {
        var dir = TestEnvironment.NewTempDir("snapshot");
        var deleting = Utf16(Header + $"[-{Root}]\r\n\r\n");
        var snapshot = Path.Combine(dir, "deleting.reg");
        File.WriteAllBytes(snapshot, deleting);
        Assert.False(SettingsViewModel.SnapshotMatches(snapshot, deleting));
    }

    [Fact]
    public void ErrorPhrase_IsRussianAndFitsAfterAColon()
    {
        Assert.Equal("файл занят другой программой",
            QuarantineActions.ErrorPhrase(new IOException("The process cannot access the file.", unchecked((int)0x80070020))));
        Assert.StartsWith("нет доступа", QuarantineActions.ErrorPhrase(new SecurityException("Requested registry access is not allowed.")));
        // Core's own Russian explanation (quarantine list unreadable) is kept, starting with a lower-case letter.
        Assert.Equal("список карантина не читается, поэтому ничего не изменено",
            QuarantineActions.ErrorPhrase(new InvalidOperationException("Список карантина не читается, поэтому ничего не изменено.")));
    }
}
