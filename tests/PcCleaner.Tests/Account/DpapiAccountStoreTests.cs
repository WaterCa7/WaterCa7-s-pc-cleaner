using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    public void TheFile_IsProtectedForThisUser_WithTheStoresOwnEntropy()
    {
        TestEnvironment.Init();
        var file = NewFile();
        new DpapiAccountStore(file).Save(Full);
        var raw = File.ReadAllBytes(file);

        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser));
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(raw, Encoding.ASCII.GetBytes("another entropy"), DataProtectionScope.CurrentUser));
        Assert.NotEmpty(ProtectedData.Unprotect(raw, DpapiAccountStore.Entropy, DataProtectionScope.CurrentUser));
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
    public void SigningOut_KeepsOnlyTheInstallNumber()
        => Assert.Equal(new AccountData(InstallId: Full.InstallId), Full.SignedOut());
}
