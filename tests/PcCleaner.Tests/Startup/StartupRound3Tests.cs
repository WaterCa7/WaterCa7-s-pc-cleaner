using PcCleaner.Core;
using PcCleaner.Modules.Startup;
using Xunit;

namespace PcCleaner.Tests.Startup;

/// <summary>
/// A known unwanted program found only by its entry name or publisher: reported once on the dashboard (by «Программы»)
/// when its file is validly signed, still a finding here when it is unsigned or has other signals.
/// </summary>
public class StartupRound3Tests
{
    private static readonly SignatureInfo Signed = new(SignatureStatus.Valid, "Launcher Co");
    private static readonly SignatureInfo Unsigned = new(SignatureStatus.Unsigned, null);
    private const string Pup = "Потенциально нежелательная программа: тест";

    public StartupRound3Tests() => TestEnvironment.Init();

    private static StartupItem Item(string name, SignatureInfo signature, string target = @"C:\Program Files\Launcher Co\launcher-app.exe")
    {
        var item = new StartupItem
        {
            Id = $"{StartupKind.RegistryRun}|{name}",
            Kind = StartupKind.RegistryRun,
            Scope = StartupScope.CurrentUser,
            Name = name,
            Command = $"\"{target}\" /autostart",
            TargetFile = target,
            IsEnabled = true,
        };
        item.Signature = signature;
        item.Publisher = signature.Publisher;
        item.TargetExists = true;
        return item;
    }

    [Fact]
    public void SignedEntryOfAKnownUnwantedProgram_IsOnlyInformational()
    {
        using var sandbox = new StartupSandbox();
        var item = Item("OneLaunch", Signed);

        sandbox.Service.Reanalyze(item);

        Assert.Equal(SuspicionLevel.Low, item.RiskLevel); // «Внимание» with the reason on this page
        Assert.False(item.IsSuspicious);
        Assert.Contains(item.Reasons, r => r.StartsWith("Потенциально нежелательная", StringComparison.Ordinal));
        Assert.DoesNotContain(sandbox.Service.BuildIssues(new[] { item }),
            i => i.Title.StartsWith("Подозрительная запись", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsignedEntryOfAKnownUnwantedProgram_IsStillReported()
    {
        using var sandbox = new StartupSandbox();
        var item = Item("OneLaunch", Unsigned);

        sandbox.Service.Reanalyze(item);

        Assert.True(item.IsSuspicious);
        var issue = Assert.Single(sandbox.Service.BuildIssues(new[] { item }),
            i => i.Title.StartsWith("Подозрительная запись", StringComparison.Ordinal));
        Assert.Contains("нежелательная", issue.Details);
    }

    [Fact]
    public void SignedEntryWithAnotherSignal_KeepsTheFullBoost()
    {
        using var sandbox = new StartupSandbox();
        // A signed file started from Temp: the location is a signal of its own.
        var item = Item("OneLaunch", Signed, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "launcher-app.exe"));

        sandbox.Service.Reanalyze(item);

        Assert.True(item.IsSuspicious);
        Assert.Contains(item.Reasons, r => r.Contains("Temp", StringComparison.Ordinal));
    }

    [Fact]
    public void AddPupMatch_CapsBelowMediumOnlyForTheSignedOnlySignal()
    {
        var signedOnly = StartupService.AddPupMatch(SuspicionResult.Clean, Pup, Signed);
        Assert.Equal(StartupService.BelowMediumScore, signedOnly.Score);
        Assert.Equal(SuspicionLevel.Low, signedOnly.Level);

        Assert.Equal(35, StartupService.AddPupMatch(SuspicionResult.Clean, Pup, Unsigned).Score);
        Assert.Equal(35, StartupService.AddPupMatch(SuspicionResult.Clean, Pup, null).Score); // file not checked
        Assert.Equal(40, StartupService.AddPupMatch(new SuspicionResult(5, new[] { "Запускается из временной папки (Temp)" }), Pup, Signed).Score);
        Assert.Equal(100, StartupService.AddPupMatch(new SuspicionResult(90, new[] { "x" }), Pup, Unsigned).Score);
    }

    [Fact]
    public void TrustKey_IsTheProgramFile_OrTheEntryForAGenericHost()
    {
        var program = Item("App", Signed);
        Assert.Equal(program.TargetFile, StartupService.TrustKey(program));

        var script = Item("Script", Signed, System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"));
        Assert.Equal(script.Id, StartupService.TrustKey(script));
    }
}
