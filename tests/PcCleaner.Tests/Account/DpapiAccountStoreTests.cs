using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcCleaner.Core.Account;
using PcCleaner.Core.AccountWindows;
using Xunit;

namespace PcCleaner.Tests.Account;

public class DpapiAccountStoreTests
{
    private static string NewFile() => Path.Combine(TestEnvironment.NewTempDir("account"), "account.bin");

    private static readonly AccountData Full = new("tok-123", "user@example.com", 42, 7, "dGlja2V0", "c2ln", "11111111-2222-3333-4444-555555555555");

    [Fact]
    public void WhatIsSaved_IsReadBack()
    {
        TestEnvironment.Init();
        var file = NewFile();

        new DpapiAccountStore(file).Save(Full);

        Assert.Equal(Full, new DpapiAccountStore(file).Load());
    }

    [Fact]
    public void TheFile_IsNotPlainText()
    {
        TestEnvironment.Init();
        var file = NewFile();

        new DpapiAccountStore(file).Save(Full);
        var raw = File.ReadAllBytes(file);

        Assert.DoesNotContain("tok-123", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("tok-123", Encoding.Unicode.GetString(raw));
    }

    [Fact]
    public void TheFile_IsEncryptedWithTheStoresOwnEntropy()
    {
        TestEnvironment.Init();
        var file = NewFile();
        new DpapiAccountStore(file).Save(Full);
        var raw = File.ReadAllBytes(file);

        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser));
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(raw, Encoding.ASCII.GetBytes("another entropy"), DataProtectionScope.CurrentUser));
        Assert.NotEmpty(ProtectedData.Unprotect(raw, DpapiAccountStore.Entropy, DataProtectionScope.CurrentUser));
    }

    // DPAPI blob layout (DATA_BLOB of CryptProtectData): version(4) + provider GUID(16) + master key version(4) + master key GUID(16)
    // = 40 bytes, then dwFlags (uint32 LE). CRYPTPROTECT_LOCAL_MACHINE (0x4) is set only for DataProtectionScope.LocalMachine.
    // ProtectedData.Unprotect ignores its scope argument (the blob carries it), so the flag is the only way to read the scope back.
    private const int FlagsOffset = 40;
    private const uint LocalMachineFlag = 4;

    [Fact]
    public void TheFlagsOffset_ReadsTheScopeOfADpapiBlob()
    {
        var local = ProtectedData.Protect(new byte[] { 1 }, DpapiAccountStore.Entropy, DataProtectionScope.LocalMachine);
        var user = ProtectedData.Protect(new byte[] { 1 }, DpapiAccountStore.Entropy, DataProtectionScope.CurrentUser);

        Assert.Equal(LocalMachineFlag, BitConverter.ToUInt32(local, FlagsOffset) & LocalMachineFlag);
        Assert.Equal(0u, BitConverter.ToUInt32(user, FlagsOffset) & LocalMachineFlag);
    }

    [Fact]
    public void TheFile_IsProtectedForThisUserOnly_NotForTheMachine()
    {
        TestEnvironment.Init();
        var file = NewFile();
        new DpapiAccountStore(file).Save(Full);
        var raw = File.ReadAllBytes(file);

        Assert.Equal(0u, BitConverter.ToUInt32(raw, FlagsOffset) & LocalMachineFlag);
    }

    [Fact]
    public void NoFile_AndABrokenFile_AreEmpty()
    {
        TestEnvironment.Init();
        var file = NewFile();

        Assert.Equal(new AccountData(), new DpapiAccountStore(file).Load());
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        Assert.Equal(new AccountData(), new DpapiAccountStore(file).Load());
    }

    [Fact]
    public void AFileOfAnotherEntropy_IsEmpty()
    {
        TestEnvironment.Init();
        var file = NewFile();
        var foreign = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(Full), Encoding.ASCII.GetBytes("another entropy"), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(file, foreign);

        Assert.Equal(new AccountData(), new DpapiAccountStore(file).Load());
    }

    [Fact]
    public void ToString_DoesNotRevealTheTokenTicketOrSignature()
    {
        var text = Full.ToString();

        Assert.DoesNotContain("tok-123", text);
        Assert.DoesNotContain("dGlja2V0", text);
        Assert.DoesNotContain("c2ln", text);
        Assert.Contains("Token = <set>", text);
        Assert.Contains("Email = user@example.com", text);
    }

    [Fact]
    public void SigningOut_KeepsOnlyTheInstallNumber()
        => Assert.Equal(new AccountData(InstallId: Full.InstallId), Full.SignedOut());
}
