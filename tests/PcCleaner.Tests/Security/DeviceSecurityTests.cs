using Microsoft.Win32;
using PcCleaner.Core;
using PcCleaner.Modules.Dashboard;
using PcCleaner.Modules.Security;
using Xunit;

namespace PcCleaner.Tests.Security;

/// <summary>SMBv1, Secure Boot, TPM, drive encryption, memory integrity, several antiviruses and Defender threat actions.</summary>
[Collection(SecuritySandbox.Collection)]
public class DeviceSecurityTests
{
    public DeviceSecurityTests() => TestEnvironment.Init();

    private static List<SecurityCheckResult> AllOk()
        => SecurityCheckIds.All.Select(id => new SecurityCheckResult(id, SecurityStatus.Ok, "ok", "")).ToList();

    private static List<SecurityCheckResult> With(List<SecurityCheckResult> checks, SecurityCheckResult replacement)
    {
        checks[checks.FindIndex(c => c.Id == replacement.Id)] = replacement;
        return checks;
    }

    private static readonly DefenderStatus WorkingDefender = new()
    {
        ServiceEnabled = true, AntivirusEnabled = true, RealTimeEnabled = true, SignatureAgeDays = 0, RunningMode = "Normal",
    };

    // ===================== SMBv1 =====================

    [Fact]
    public void Smb1_FeatureDisabledOrAbsent_IsOk_EvenWithAStaleDriverKey()
    {
        var stale = new Smb1Registry(true, null, 3, 2);
        foreach (var state in new[] { FeatureState.Disabled, FeatureState.Absent })
        {
            var r = SecurityRules.EvaluateSmb1(new Smb1State(state, state, state, stale), isAdmin: false);
            Assert.Equal(SecurityStatus.Ok, r.Status);
            Assert.Equal("SMBv1 выключен", r.State);
            Assert.DoesNotContain("права администратора", r.Details); // the feature list is exact without admin
        }
    }

    [Fact]
    public void Smb1_OnlyClientEnabled_IsWarning()
    {
        var s = new Smb1State(FeatureState.Enabled, FeatureState.Enabled, FeatureState.Disabled, new Smb1Registry(true, null, null, 2));

        var r = SecurityRules.EvaluateSmb1(s, isAdmin: false);

        Assert.Equal(SecurityStatus.Warning, r.Status);
        Assert.Equal("Включён клиент SMBv1", r.State);
        Assert.Contains("WannaCry", r.Details);
        Assert.Equal((true, false, true), SecurityRules.ResolveSmb1(s));
    }

    [Fact]
    public void Smb1_ServerEnabled_IsDanger()
    {
        var s = new Smb1State(FeatureState.Enabled, FeatureState.Enabled, FeatureState.Enabled, new Smb1Registry(true, null, 3, 2));

        var r = SecurityRules.EvaluateSmb1(s, isAdmin: true);

        Assert.Equal(SecurityStatus.Danger, r.Status);
        Assert.Equal("Включён устаревший протокол SMBv1", r.State);
        Assert.Contains("WannaCry", r.Details);
    }

    [Fact]
    public void Smb1_ParentFeatureWithoutSubFeatures_AssumesBothParts()
    {
        // Older Windows 10 has no SMB1Protocol-Client/-Server sub-features.
        var s = new Smb1State(FeatureState.Enabled, FeatureState.Unknown, FeatureState.Unknown, Smb1Registry.Unreadable);
        Assert.Equal((true, true, true), SecurityRules.ResolveSmb1(s));
        Assert.Equal(SecurityStatus.Danger, SecurityRules.EvaluateSmb1(s, false).Status);
    }

    [Fact]
    public void Smb1_InstalledButSwitchedOffInRegistry_CountsAsOff()
    {
        var s = new Smb1State(FeatureState.Enabled, FeatureState.Enabled, FeatureState.Enabled, new Smb1Registry(true, 0, 3, 4));
        Assert.Equal((false, false, true), SecurityRules.ResolveSmb1(s));
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateSmb1(s, false).Status);
    }

    [Theory]
    [InlineData(null, null, null, false, false)] // nothing installed
    [InlineData(null, null, 4, false, false)] // client driver disabled
    [InlineData(null, null, 2, true, false)] // client driver starts automatically
    [InlineData(null, 3, null, false, true)] // server driver installed, SMB1 value not set
    [InlineData(0, 3, null, false, false)] // server driver installed but SMB1 = 0
    [InlineData(1, 4, 3, true, false)] // server driver disabled
    public void Smb1_RegistryFallback(int? serverSmb1, int? serverStart, int? clientStart, bool client, bool server)
    {
        var s = Smb1State.Unknown with { Registry = new Smb1Registry(true, serverSmb1, serverStart, clientStart) };
        Assert.Equal((client, server, false), SecurityRules.ResolveSmb1(s));
    }

    [Fact]
    public void Smb1_RegistryOnly_SaysWhenAdminRightsWouldHelp()
    {
        var s = Smb1State.Unknown with { Registry = new Smb1Registry(true, null, null, 3) };

        var user = SecurityRules.EvaluateSmb1(s, isAdmin: false);
        var admin = SecurityRules.EvaluateSmb1(s, isAdmin: true);

        Assert.Equal(SecurityStatus.Warning, user.Status);
        Assert.Contains("Для точной проверки нужны права администратора", user.Details);
        Assert.DoesNotContain("права администратора", admin.Details);
        Assert.Contains("по реестру", admin.Details);
        Assert.Equal(SecurityStatus.Unknown, SecurityRules.EvaluateSmb1(Smb1State.Unknown, false).Status);
    }

    [Fact]
    public void Smb1_FeatureStatesAreParsed()
    {
        Assert.Equal(FeatureState.Enabled, SecurityRules.MapInstallState(1));
        Assert.Equal(FeatureState.Disabled, SecurityRules.MapInstallState(2));
        Assert.Equal(FeatureState.Absent, SecurityRules.MapInstallState(3));
        Assert.Equal(FeatureState.Unknown, SecurityRules.MapInstallState(4));
        Assert.Equal(FeatureState.Unknown, SecurityRules.MapInstallState(null));

        var list = SecurityRules.ParseFeatureList(
            "SMB1Protocol=Enabled\r\nSMB1Protocol-Client=EnablePending\r\nSMB1Protocol-Server=DisabledWithPayloadRemoved\r\nmusor\r\n");
        Assert.Equal(FeatureState.Enabled, list["smb1protocol"]);
        Assert.Equal(FeatureState.Enabled, list["SMB1Protocol-Client"]);
        Assert.Equal(FeatureState.Absent, list["SMB1Protocol-Server"]);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public async Task Smb1_Sandbox_IssueHasFix_ThatChangesNothingInTests()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.Smb1ClientDriver))
            key.SetValue("Start", 2, RegistryValueKind.DWord);

        var report = await sb.Service.ScanAsync(null, CancellationToken.None);

        var check = report.Check(SecurityCheckIds.Smb1)!;
        Assert.Equal(SecurityStatus.Warning, check.Status);
        var issue = sb.Service.BuildIssues(report).Single(i => i.Title == "Включён устаревший протокол SMBv1");
        Assert.Equal(IssueSeverity.Medium, issue.Severity);
        Assert.False(issue.AutoFixSafe);
        Assert.Contains("перезагрузка", issue.Details);
        Assert.DoesNotContain("права администратора", issue.Details); // the hint line stays on the card
        var message = await issue.FixAsync!(CancellationToken.None);
        Assert.Contains("ничего не изменено", message);
        Assert.True(HealthScoring.LooksLikeFailure(message));
        using var after = sb.Env.Smb1ClientDriver.Open();
        Assert.Equal(2, after!.GetValue("Start"));
    }

    [Fact]
    public async Task Smb1_DisabledButRebootPending_SaysSo_AndOffersNothingAgain()
    {
        using var sb = new SecuritySandbox();
        var vm = new SecurityViewModel(sb.Service);
        // Before the reboot Windows may still list the feature as enabled — or already as disabled.
        var stillEnabled = new Smb1State(FeatureState.Enabled, FeatureState.Enabled, FeatureState.Disabled, new Smb1Registry(true, null, null, 2));
        var alreadyOff = new Smb1State(FeatureState.Disabled, FeatureState.Disabled, FeatureState.Disabled, new Smb1Registry(true, null, null, 2));
        foreach (var state in new[] { stillEnabled, alreadyOff })
        {
            var check = SecurityRules.EvaluateSmb1(state, isAdmin: true, rebootPending: true);
            Assert.Equal(SecurityStatus.Info, check.Status);
            Assert.Equal("SMBv1 отключится после перезагрузки", check.State);
            var report = new SecurityReport { Checks = With(AllOk(), check) };
            Assert.Empty(sb.Service.BuildIssues(report));
            var card = vm.BuildCards(report).Single(c => c.Id == SecurityCheckIds.Smb1);
            Assert.DoesNotContain(card.Actions, a => a.Label == "Отключить SMBv1");
        }

        // The scan passes the service's flag on (it is set only after a successful Disable-WindowsOptionalFeature).
        using (var key = sb.Create(sb.Env.Smb1ClientDriver))
            key.SetValue("Start", 2, RegistryValueKind.DWord);
        Assert.Equal(SecurityStatus.Warning, (await sb.Service.ScanAsync(null, CancellationToken.None)).Check(SecurityCheckIds.Smb1)!.Status);
        sb.Service.Smb1RebootPending = true;
        Assert.Equal(SecurityStatus.Info, (await sb.Service.ScanAsync(null, CancellationToken.None)).Check(SecurityCheckIds.Smb1)!.Status);
    }

    [Fact]
    public void Smb1_IssueSaysRestorePointAndHowToUndo()
    {
        using var sb = new SecuritySandbox();
        var report = new SecurityReport
        {
            Checks = With(AllOk(), new SecurityCheckResult(SecurityCheckIds.Smb1, SecurityStatus.Warning, "Включён клиент SMBv1", "x")),
        };
        var issue = Assert.Single(sb.Service.BuildIssues(report));
        Assert.Contains("точку восстановления", issue.Details);
        Assert.Contains("Включение или отключение компонентов Windows", issue.Details);
    }

    [Fact]
    public void Smb1_ServerIssue_IsHigh()
    {
        using var sb = new SecuritySandbox();
        var report = new SecurityReport
        {
            Checks = With(AllOk(), new SecurityCheckResult(SecurityCheckIds.Smb1, SecurityStatus.Danger, "Включён сервер SMBv1", "x")),
        };
        var issue = Assert.Single(sb.Service.BuildIssues(report));
        Assert.Equal(IssueSeverity.High, issue.Severity);
        Assert.NotNull(issue.FixAsync);
        Assert.Equal("Security", issue.TargetPageKey);
    }

    // ===================== Secure Boot =====================

    [Theory]
    [InlineData(1, null, SecurityStatus.Ok, "Secure Boot включён")]
    [InlineData(0, true, SecurityStatus.Warning, "Secure Boot выключен")]
    [InlineData(null, false, SecurityStatus.Info, "Загрузка в режиме Legacy BIOS")]
    [InlineData(null, true, SecurityStatus.Info, "Secure Boot не поддерживается")]
    [InlineData(null, null, SecurityStatus.Info, "Secure Boot не поддерживается")]
    public void SecureBoot_Values(int? enabled, bool? uefi, SecurityStatus status, string state)
    {
        var r = SecurityRules.EvaluateSecureBoot(new SecureBootState(true, enabled, uefi));
        Assert.Equal(status, r.Status);
        Assert.Equal(state, r.State);
    }

    [Fact]
    public void SecureBoot_Off_MentionsWhereAndWhy_AndIsManualMediumIssue()
    {
        var off = SecurityRules.EvaluateSecureBoot(new SecureBootState(true, 0, true));
        Assert.Contains("BIOS", off.Details);
        Assert.Contains("Windows 11", off.Details);
        Assert.Contains("Vanguard", off.Details);
        Assert.Contains("FACEIT", off.Details);
        Assert.Equal(SecurityStatus.Unknown, SecurityRules.EvaluateSecureBoot(new SecureBootState(false, null, null)).Status);

        using var sb = new SecuritySandbox();
        var issue = Assert.Single(sb.Service.BuildIssues(new SecurityReport { Checks = With(AllOk(), off) }));
        Assert.Equal("Secure Boot выключен", issue.Title);
        Assert.Equal(IssueSeverity.Medium, issue.Severity);
        Assert.Null(issue.FixAsync);
    }

    [Fact]
    public async Task SecureBoot_Sandbox_ReadsStateKey()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.SecureBootState))
            key.SetValue("UEFISecureBootEnabled", 0, RegistryValueKind.DWord);

        var report = await sb.Service.ScanAsync(null, CancellationToken.None);

        Assert.Equal(SecurityStatus.Warning, report.Check(SecurityCheckIds.SecureBoot)!.Status);
        Assert.Equal(0, report.SecureBoot.Enabled);
    }

    // ===================== TPM =====================

    private const string TpmToolEnglish =
        "\r\n-TPM Present: True\r\n-TPM Version: 2.0\r\n-TPM Manufacturer ID: INTC\r\n-TPM Manufacturer Full Name: Intel\r\n" +
        "-TPM Manufacturer Version: 500.14.0.0\r\n-PPI Version: 1.3\r\n-Is Initialized: True\r\n-TPM Spec Version: 1.38\r\n" +
        "-Lockout Information: \r\n\t-Is Locked Out: False\r\n";

    private const string TpmToolRussian =
        "\r\n-Наличие TPM: Да\r\n-Версия спецификации TPM: 1.59\r\n-Версия TPM: 2.0\r\n-Идентификатор производителя TPM: AMD\r\n" +
        "-Версия производителя TPM: 3.87.0.5\r\n-Инициализирован: Да\r\n";

    [Fact]
    public void TpmTool_English_IsParsed()
    {
        var t = SecurityRules.ParseTpmTool(TpmToolEnglish);
        Assert.True(t.Present);
        Assert.Equal("2.0", t.Version);
    }

    [Fact]
    public void TpmTool_Russian_IsParsed()
    {
        var t = SecurityRules.ParseTpmTool(TpmToolRussian);
        Assert.True(t.Present);
        Assert.Equal("2.0", t.Version); // not the spec version 1.59 nor the manufacturer version

        var withTrue = SecurityRules.ParseTpmTool("-Наличие TPM: True\n-Версия TPM: 1.2\n");
        Assert.True(withTrue.Present);
        Assert.Equal("1.2", withTrue.Version);
    }

    [Fact]
    public void TpmTool_NotPresentAndGarbage()
    {
        var absent = SecurityRules.ParseTpmTool("-TPM Present: False\r\n");
        Assert.False(absent.Present);
        Assert.Null(absent.Version);
        Assert.Null(SecurityRules.ParseTpmTool("Ошибка: доступ запрещён.").Present);
        Assert.Null(SecurityRules.ParseTpmTool(null).Present);
    }

    [Fact]
    public void Tpm_TbsResultMapping()
    {
        Assert.Equal(new TpmState(true, null, null, "2.0"), SecurityRules.TpmFromTbs(0, 2));
        Assert.Equal(new TpmState(true, null, null, "1.2"), SecurityRules.TpmFromTbs(0, 1));
        Assert.Equal(new TpmState(true, null, null, null), SecurityRules.TpmFromTbs(0, 7));
        Assert.Equal(new TpmState(false, null, null, null), SecurityRules.TpmFromTbs(0x8028400F, 0));
        Assert.Null(SecurityRules.TpmFromTbs(0x80284008, 0).Present); // TBS service not running: unknown
    }

    [Theory]
    [InlineData("2.0, 0, 1.38", "2.0")]
    [InlineData("1.2, 2, 3", "1.2")]
    [InlineData("2.0", "2.0")]
    [InlineData("", null)]
    [InlineData("нет", null)]
    public void TpmVersion_IsNormalized(string text, string? expected)
        => Assert.Equal(expected, SecurityRules.NormalizeTpmVersion(text));

    [Fact]
    public void Tpm_Evaluation()
    {
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateTpm(new TpmState(true, true, true, "2.0"), true).Status);
        Assert.Equal("TPM 2.0 работает", SecurityRules.EvaluateTpm(new TpmState(true, null, null, "2.0"), false).State);
        Assert.Equal(SecurityStatus.Info, SecurityRules.EvaluateTpm(new TpmState(true, null, null, "1.2"), false).Status);

        var absent = SecurityRules.EvaluateTpm(new TpmState(false, null, null, null), false);
        Assert.Equal(SecurityStatus.Warning, absent.Status);
        Assert.Contains("fTPM", absent.Details);
        Assert.Contains("PTT", absent.Details);
        Assert.Contains("Windows 11", absent.Details);

        var disabled = SecurityRules.EvaluateTpm(new TpmState(true, false, true, "2.0"), true);
        Assert.Equal(SecurityStatus.Warning, disabled.Status);
        Assert.Equal("TPM выключен", disabled.State);

        var unknownUser = SecurityRules.EvaluateTpm(TpmState.Unknown, isAdmin: false);
        Assert.Equal(SecurityStatus.Unknown, unknownUser.Status);
        Assert.Contains("права администратора", unknownUser.Details);
        Assert.DoesNotContain("права администратора", SecurityRules.EvaluateTpm(TpmState.Unknown, isAdmin: true).Details);
    }

    [Fact]
    public void Tpm_Missing_IsLowManualIssue()
    {
        using var sb = new SecuritySandbox();
        var tpm = new TpmState(false, null, null, null);
        var report = new SecurityReport { Tpm = tpm, Checks = With(AllOk(), SecurityRules.EvaluateTpm(tpm, false)) };
        var issue = Assert.Single(sb.Service.BuildIssues(report));
        Assert.Equal(IssueSeverity.Low, issue.Severity);
        Assert.Null(issue.FixAsync);
        Assert.Contains("TPM", issue.Title);
    }

    // ===================== Drive encryption =====================

    [Theory]
    [InlineData(0, DriveEncryption.NotSupported)]
    [InlineData(1, DriveEncryption.On)]
    [InlineData(2, DriveEncryption.Off)]
    [InlineData(3, DriveEncryption.Encrypting)]
    [InlineData(4, DriveEncryption.Decrypting)]
    [InlineData(5, DriveEncryption.Suspended)]
    [InlineData(6, DriveEncryption.Locked)]
    [InlineData(8, DriveEncryption.Pending)]
    [InlineData(7, DriveEncryption.Unknown)]
    public void BitLocker_ShellPropertyMapping(int value, DriveEncryption expected)
        => Assert.Equal(expected, SecurityRules.MapShellBitLocker(value));

    [Fact]
    public void BitLocker_ShellPropertyMapping_EmptyAndText()
    {
        Assert.Equal(DriveEncryption.NotSupported, SecurityRules.MapShellBitLocker(null));
        Assert.Equal(DriveEncryption.NotSupported, SecurityRules.MapShellBitLocker(""));
        Assert.Equal(DriveEncryption.Off, SecurityRules.MapShellBitLocker("2")); // PowerShell fallback returns text
        Assert.Equal(DriveEncryption.Unknown, SecurityRules.MapShellBitLocker("что-то"));
    }

    [Theory]
    [InlineData(1L, 1L, DriveEncryption.On)]
    [InlineData(0L, 0L, DriveEncryption.Off)]
    [InlineData(0L, 1L, DriveEncryption.Suspended)]
    [InlineData(1L, 2L, DriveEncryption.Encrypting)]
    [InlineData(0L, 4L, DriveEncryption.Encrypting)]
    [InlineData(0L, 3L, DriveEncryption.Decrypting)]
    [InlineData(2L, 1L, DriveEncryption.Locked)]
    [InlineData(null, null, DriveEncryption.Unknown)]
    public void BitLocker_WmiMapping(long? protection, long? conversion, DriveEncryption expected)
        => Assert.Equal(expected, SecurityRules.MapWmiBitLocker(protection, conversion));

    [Theory]
    [InlineData(0, new[] { 3 }, false)] // desktop
    [InlineData(1, new[] { 3 }, false)] // desktop with a USB UPS
    [InlineData(1, new int[0], true)] // battery, chassis unknown
    [InlineData(1, new[] { 2 }, true)] // battery, chassis "Unknown"
    [InlineData(0, new[] { 10 }, true)] // notebook chassis
    [InlineData(1, new[] { 31 }, true)] // convertible
    [InlineData(0, new int[0], false)]
    public void BitLocker_LaptopDetection(int batteries, int[] chassis, bool expected)
        => Assert.Equal(expected, SecurityRules.IsLaptop(batteries, chassis));

    [Fact]
    public void BitLocker_HomeEdition()
    {
        Assert.True(SecurityRules.IsHomeEdition("Core"));
        Assert.True(SecurityRules.IsHomeEdition("CoreSingleLanguage"));
        Assert.False(SecurityRules.IsHomeEdition("Professional"));
        Assert.False(SecurityRules.IsHomeEdition(null));
    }

    [Fact]
    public void BitLocker_LaptopOff_IsWarningAndLowIssue_DesktopOnlyInfo()
    {
        using var sb = new SecuritySandbox();
        var laptop = new BitLockerState("C:", DriveEncryption.Off, IsLaptop: true, IsHomeEdition: false);
        var desktop = laptop with { IsLaptop = false };

        var laptopCheck = SecurityRules.EvaluateBitLocker(laptop);
        Assert.Equal(SecurityStatus.Warning, laptopCheck.Status);
        Assert.Contains("украдут", laptopCheck.Details);
        var issue = Assert.Single(sb.Service.BuildIssues(new SecurityReport { BitLocker = laptop, Checks = With(AllOk(), laptopCheck) }));
        Assert.Equal("Системный диск ноутбука не зашифрован", issue.Title);
        Assert.Equal(IssueSeverity.Low, issue.Severity);
        Assert.Null(issue.FixAsync);

        var desktopCheck = SecurityRules.EvaluateBitLocker(desktop);
        Assert.Equal(SecurityStatus.Info, desktopCheck.Status);
        Assert.Empty(sb.Service.BuildIssues(new SecurityReport { BitLocker = desktop, Checks = With(AllOk(), desktopCheck) }));
    }

    [Fact]
    public void BitLocker_OtherStates()
    {
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateBitLocker(new BitLockerState("C:", DriveEncryption.On, true, false)).Status);
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateBitLocker(new BitLockerState("C:", DriveEncryption.Encrypting, true, false)).Status);
        Assert.Equal(SecurityStatus.Warning, SecurityRules.EvaluateBitLocker(new BitLockerState("C:", DriveEncryption.Suspended, true, false)).Status);
        Assert.Equal(SecurityStatus.Info, SecurityRules.EvaluateBitLocker(new BitLockerState("C:", DriveEncryption.Suspended, false, false)).Status);
        var home = SecurityRules.EvaluateBitLocker(new BitLockerState("C:", DriveEncryption.NotSupported, true, true));
        Assert.Equal(SecurityStatus.Info, home.Status);
        Assert.Contains("домашней", home.Details);
        Assert.Equal(SecurityStatus.Unknown, SecurityRules.EvaluateBitLocker(BitLockerState.Unknown).Status);
        Assert.True(SecurityRules.LaptopDriveUnprotected(new BitLockerState("C:", DriveEncryption.Pending, true, true)));
        Assert.False(SecurityRules.LaptopDriveUnprotected(new BitLockerState("C:", DriveEncryption.NotSupported, true, true)));
    }

    // ===================== Memory integrity =====================

    [Fact]
    public void MemoryIntegrity_Running_IsOk()
    {
        var d = new DeviceGuardState(true, 2, new[] { 2 }, new[] { 2 }, null);
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateMemoryIntegrity(d).Status);
    }

    [Fact]
    public void MemoryIntegrity_Off_IsInfoWithNeutralAdviceAndManualTip()
    {
        using var sb = new SecuritySandbox();
        var d = new DeviceGuardState(true, 2, new[] { 0 }, new[] { 0 }, null);

        var check = SecurityRules.EvaluateMemoryIntegrity(d);

        Assert.Equal(SecurityStatus.Info, check.Status);
        Assert.Equal("Целостность памяти выключена", check.State);
        Assert.Contains("«Безопасность устройства» → «Изоляция ядра»", check.Details);
        Assert.Contains("FPS", check.Details);
        var issue = Assert.Single(sb.Service.BuildIssues(new SecurityReport { DeviceGuard = d, Checks = With(AllOk(), check) }));
        Assert.Equal(IssueSeverity.Info, issue.Severity);
        Assert.Null(issue.FixAsync);
        Assert.Equal("Security", issue.TargetPageKey);
    }

    [Fact]
    public void MemoryIntegrity_ConfiguredButNotRunning_AsksForReboot_NoIssue()
    {
        using var sb = new SecuritySandbox();
        foreach (var d in new[]
                 {
                     new DeviceGuardState(true, 1, Array.Empty<int>(), new[] { 2 }, null),
                     new DeviceGuardState(false, null, Array.Empty<int>(), Array.Empty<int>(), 1), // registry only
                 })
        {
            var check = SecurityRules.EvaluateMemoryIntegrity(d);
            Assert.Equal(SecurityStatus.Info, check.Status);
            Assert.Contains("после перезагрузки", check.State);
            Assert.Empty(sb.Service.BuildIssues(new SecurityReport { DeviceGuard = d, Checks = With(AllOk(), check) }));
        }
        Assert.Equal(SecurityStatus.Unknown, SecurityRules.EvaluateMemoryIntegrity(DeviceGuardState.Unknown).Status);
    }

    [Fact]
    public async Task MemoryIntegrity_Sandbox_ReadsScenarioKey()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.HvciScenario))
            key.SetValue("Enabled", 1, RegistryValueKind.DWord);

        var report = await sb.Service.ScanAsync(null, CancellationToken.None);

        Assert.Contains("после перезагрузки", report.Check(SecurityCheckIds.MemoryIntegrity)!.State);
    }

    // ===================== Several antiviruses =====================

    private static SecurityProduct Product(string name, bool enabled = true)
        => SecurityRules.MakeProduct(name, enabled ? 0x41000 : 0x40000);

    [Fact]
    public void MultipleAntiviruses_TwoThirdPartyProducts()
    {
        using var sb = new SecuritySandbox();
        var products = new[] { Product("ESET Security"), Product("Kaspersky Anti-Virus"), Product("Windows Defender", false) };
        var passiveDefender = new DefenderStatus { ServiceEnabled = true, RunningMode = "Passive Mode" };

        Assert.True(SecurityRules.HasMultipleAntiviruses(products, passiveDefender));
        var check = SecurityRules.EvaluateAntivirus(passiveDefender, products, Array.Empty<ThreatDetection>());
        Assert.Equal(SecurityStatus.Warning, check.Status);
        Assert.Equal("Работают сразу несколько антивирусов", check.State);
        Assert.Contains("ESET Security, Kaspersky Anti-Virus", check.Details);

        var report = new SecurityReport { Defender = passiveDefender, Antiviruses = products, Checks = AllOk() };
        var issue = Assert.Single(sb.Service.BuildIssues(report));
        Assert.Equal("Установлено несколько антивирусов", issue.Title);
        Assert.Equal(IssueSeverity.Medium, issue.Severity);
        Assert.Equal("Programs", issue.TargetPageKey);
        Assert.Null(issue.FixAsync);
    }

    [Fact]
    public void MultipleAntiviruses_DefenderStillActiveNextToAnotherAntivirus()
    {
        var products = new[] { Product("Avast Antivirus"), SecurityRules.MakeProduct("Windows Defender", 397568) };

        var names = SecurityRules.ActiveAntivirusNames(products, WorkingDefender);

        Assert.Equal(new[] { "Защитник Windows", "Avast Antivirus" }, names);
    }

    [Fact]
    public void MultipleAntiviruses_NormalSetupsAreNotFlagged()
    {
        var passive = new DefenderStatus { ServiceEnabled = true, RealTimeEnabled = false, RunningMode = "Passive Mode" };
        // One third-party antivirus, Defender passive.
        Assert.False(SecurityRules.HasMultipleAntiviruses(new[] { Product("ESET Security"), Product("Windows Defender", false) }, passive));
        // The same product registered twice.
        Assert.False(SecurityRules.HasMultipleAntiviruses(new[] { Product("ESET Security"), Product("eset security") }, passive));
        // Only Microsoft Defender.
        Assert.False(SecurityRules.HasMultipleAntiviruses(new[] { SecurityRules.MakeProduct("Windows Defender", 397568) }, WorkingDefender));
        // A disabled leftover of an old antivirus.
        Assert.False(SecurityRules.HasMultipleAntiviruses(
            new[] { Product("Kaspersky", enabled: false), SecurityRules.MakeProduct("Windows Defender", 397568) }, WorkingDefender));
    }

    // ===================== Defender threats =====================

    private static ThreatDetection Threat(string name, int status) => new() { Name = name, StatusId = status, ThreatId = name.Length };

    [Fact]
    public void ThreatAction_IsGated()
    {
        var active = new[] { Threat("Trojan:Win32/Test", 1) };
        var handled = new[] { Threat("Trojan:Win32/Old", 3), Threat("HackTool:Win32/Keygen", 5) };

        Assert.True(SecurityRules.CanRemoveThreats(WorkingDefender, active));
        Assert.False(SecurityRules.CanRemoveThreats(WorkingDefender, handled)); // quarantined or allowed by the user
        Assert.False(SecurityRules.CanRemoveThreats(null, active)); // another antivirus, no Defender data
        Assert.False(SecurityRules.CanRemoveThreats(new DefenderStatus { ServiceEnabled = false }, active));
        Assert.False(SecurityRules.CanRemoveThreats(WorkingDefender, Array.Empty<ThreatDetection>()));
    }

    [Fact]
    public async Task ThreatAction_CardButtonIssueFix_AndNothingRunsInTests()
    {
        using var sb = new SecuritySandbox();
        var vm = new SecurityViewModel(sb.Service);
        var threats = new[] { Threat("Trojan:Win32/Test", 1), Threat("Trojan:Win32/Old", 3) };
        var report = new SecurityReport
        {
            Defender = WorkingDefender,
            Threats = threats,
            Checks = With(AllOk(), SecurityRules.EvaluateAntivirus(WorkingDefender, Array.Empty<SecurityProduct>(), threats)),
        };

        var card = vm.BuildCards(report).Single(c => c.Id == SecurityCheckIds.Antivirus);
        var labels = card.Actions.Select(a => a.Label).ToList();
        Assert.Contains("Обезвредить угрозы", labels);
        Assert.True(labels.IndexOf("Обезвредить угрозы") < labels.IndexOf("Найденные угрозы"));
        Assert.Equal(CardActionKind.Danger, card.Actions.Single(a => a.Label == "Обезвредить угрозы").Kind);

        var issue = sb.Service.BuildIssues(report).Single(i => i.Title.StartsWith("Есть необезвреженные угрозы"));
        Assert.Equal(IssueSeverity.High, issue.Severity);
        Assert.False(issue.AutoFixSafe);
        var message = await issue.FixAsync!(CancellationToken.None);
        Assert.True(HealthScoring.LooksLikeFailure(message)); // sandbox: Remove-MpThreat is never started
        Assert.False((await sb.Service.RemoveActiveThreatsAsync(CancellationToken.None)).Success);

        var resolved = new SecurityReport
        {
            Defender = WorkingDefender,
            Threats = new[] { Threat("Trojan:Win32/Old", 3) },
            Checks = AllOk(),
        };
        var calm = vm.BuildCards(resolved).Single(c => c.Id == SecurityCheckIds.Antivirus);
        Assert.DoesNotContain(calm.Actions, a => a.Label == "Обезвредить угрозы");
        Assert.Contains(calm.Actions, a => a.Label == "Найденные угрозы");
    }

    [Fact]
    public void ThreatRemoval_OnlyAllGoneIsSuccess()
    {
        var all = SecurityRules.ThreatRemovalOutcome(2, 0, null);
        Assert.True(all.Success);
        Assert.False(HealthScoring.LooksLikeFailure(all.Message));
        Assert.Contains("Журнал защиты", all.Message); // how to get a needed file back

        // Some threats remain: the dashboard must not mark the issue as fixed.
        var partly = SecurityRules.ThreatRemovalOutcome(3, 1, null);
        Assert.False(partly.Success);
        Assert.True(HealthScoring.LooksLikeFailure(partly.Message));
        Assert.Contains("2 из 3", partly.Message);

        var none = SecurityRules.ThreatRemovalOutcome(2, 2, "Отказано в доступе.");
        Assert.False(none.Success);
        Assert.True(HealthScoring.LooksLikeFailure(none.Message));
        Assert.Contains("Отказано в доступе.", none.Message);
        Assert.DoesNotContain("..", none.Message);
    }

    [Fact]
    public void ThreatCard_PointsToTheButtonOnlyWhenItIsShown()
    {
        var active = new[] { Threat("Trojan:Win32/Test", 1) };

        var withButton = SecurityRules.EvaluateAntivirus(WorkingDefender, Array.Empty<SecurityProduct>(), active);
        Assert.Contains("«Обезвредить угрозы»", withButton.Details);

        // Another antivirus and no Defender data: no button, so the card points to the protection history.
        var noButton = SecurityRules.EvaluateAntivirus(null, new[] { Product("ESET Security") }, active);
        Assert.DoesNotContain("«Обезвредить угрозы»", noButton.Details);
        Assert.Contains("«Журнал защиты»", noButton.Details);
    }

    // ===================== Page layout =====================

    [Fact]
    public void Cards_DangersFirst_ThenWarnings_Unknown_Info_Ok_InOrderOfImportance()
    {
        using var sb = new SecuritySandbox();
        var vm = new SecurityViewModel(sb.Service);
        var checks = AllOk();
        With(checks, new SecurityCheckResult(SecurityCheckIds.Smb1, SecurityStatus.Danger, "s", ""));
        With(checks, new SecurityCheckResult(SecurityCheckIds.Hosts, SecurityStatus.Danger, "s", ""));
        With(checks, new SecurityCheckResult(SecurityCheckIds.Ifeo, SecurityStatus.Warning, "s", ""));
        With(checks, new SecurityCheckResult(SecurityCheckIds.Tpm, SecurityStatus.Unknown, "s", ""));
        With(checks, new SecurityCheckResult(SecurityCheckIds.MemoryIntegrity, SecurityStatus.Info, "s", ""));

        var ids = vm.BuildCards(new SecurityReport { Checks = checks }).Select(c => c.Id).ToList();

        Assert.Equal(new[]
        {
            SecurityCheckIds.Hosts, SecurityCheckIds.Smb1, SecurityCheckIds.Ifeo, SecurityCheckIds.Tpm,
            SecurityCheckIds.MemoryIntegrity, SecurityCheckIds.Antivirus, SecurityCheckIds.Firewall,
        }, ids.Take(7));
        Assert.Equal(SecurityCheckIds.All.Length, ids.Distinct().Count());
    }

    [Fact]
    public async Task Page_SplitsCardsIntoWorthALookAndFine_FineCardsAreCompact()
    {
        using var sb = new SecuritySandbox();
        using (var key = sb.Create(sb.Env.Winlogon))
        {
            key.SetValue("Shell", "custom-shell.exe");
            key.SetValue("Userinit", SecurityRules.DefaultUserinit(sb.Env.WindowsDir));
        }
        var vm = new SecurityViewModel(sb.Service);

        await vm.PreviewScanAsync(CancellationToken.None);

        Assert.Equal(vm.Checks.Count, vm.AttentionChecks.Count + vm.OkChecks.Count);
        Assert.Equal(SecurityCheckIds.Winlogon, vm.AttentionChecks[0].Id);
        Assert.All(vm.AttentionChecks, c => Assert.NotEqual(SecurityStatus.Ok, c.Status));
        Assert.All(vm.OkChecks, c =>
        {
            Assert.Equal(SecurityStatus.Ok, c.Status);
            Assert.True(c.IsCompact);
            Assert.False(c.ShowDetails);
            // The card tooltip (also shown on keyboard focus) holds the hidden details, then what the check is about.
            Assert.NotNull(c.CardToolTip);
            if (!string.IsNullOrWhiteSpace(c.Details))
                Assert.StartsWith(c.Details.Trim(), c.CardToolTip);
            Assert.EndsWith(c.Explanation, c.CardToolTip);
        });
        Assert.All(vm.AttentionChecks, c =>
        {
            Assert.False(c.IsCompact);
            Assert.Null(c.CardToolTip); // everything is on the card already
        });
        Assert.Equal(vm.OkChecks.Count, vm.OkCount);
        Assert.Equal($"В порядке · {vm.OkChecks.Count}", vm.OkHeader);
        Assert.True(vm.HasAttentionChecks);
    }

    [Fact]
    public void Grid_WideCardDoesNotLeaveAHole()
    {
        // 3 columns: [1][1][2]... the wide card does not fit after two cards, so the next narrow card moves up.
        var placed = ResponsiveGrid.Pack(new[] { 1, 1, 2, 1, 1, 1 }, 3);

        Assert.Equal(new (int, int, int, int)[]
        {
            (0, 0, 0, 1), (1, 0, 1, 1), (3, 0, 2, 1),
            (2, 1, 0, 2), (4, 1, 2, 1),
            (5, 2, 0, 3), // the last row is filled by its only card
        }, placed);
    }

    [Fact]
    public void Grid_NothingFits_StretchesTheRow_AndSingleColumnWorks()
    {
        Assert.Equal(new (int, int, int, int)[] { (0, 0, 0, 2), (1, 1, 0, 2) }, ResponsiveGrid.Pack(new[] { 1, 2 }, 2));
        Assert.Equal(new (int, int, int, int)[] { (0, 0, 0, 1), (1, 1, 0, 1), (2, 2, 0, 1) }, ResponsiveGrid.Pack(new[] { 2, 1, 1 }, 1));
        Assert.Empty(ResponsiveGrid.Pack(Array.Empty<int>(), 3));
    }

    // ===================== Misc =====================

    [Theory]
    [InlineData(0x3, 0x1)] // proxy on → direct
    [InlineData(0x5, 0x1)] // PAC script on → direct
    [InlineData(0xF, 0x9)] // everything on → direct, automatic detection kept
    [InlineData(0x9, 0x9)]
    public void Proxy_DirectFlags_KeepAutoDetect(int current, int expected)
        => Assert.Equal(expected, SecurityRules.DirectProxyFlags(current));

    [Fact]
    public void RestorePointText_IsDistinctForEveryResult()
    {
        var texts = Enum.GetValues<RestorePointResult>().Select(SecurityRules.RestorePointText).ToList();
        Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [Fact]
    public void NewChecks_HaveTitlesGlyphsAndExplanations()
    {
        foreach (var id in new[]
                 {
                     SecurityCheckIds.Smb1, SecurityCheckIds.SecureBoot, SecurityCheckIds.Tpm, SecurityCheckIds.BitLocker,
                     SecurityCheckIds.MemoryIntegrity,
                 })
        {
            Assert.NotEqual(id, SecurityCheckIds.Title(id));
            Assert.NotEqual("", SecurityCheckIds.Glyph(id));
            Assert.False(string.IsNullOrWhiteSpace(SecurityCheckIds.Explanation(id)));
        }
    }
}
