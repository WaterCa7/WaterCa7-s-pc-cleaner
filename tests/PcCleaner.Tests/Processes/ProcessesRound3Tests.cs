using System.IO;
using PcCleaner.Core;
using PcCleaner.Modules.Processes;
using Xunit;

namespace PcCleaner.Tests.Processes;

/// <summary>Known unwanted programs reported once, quarantine refusals, the RAM boundary and Russian plurals.</summary>
public class ProcessesRound3Tests
{
    private static readonly SignatureInfo Signed = new(SignatureStatus.Valid, "OneLaunch Software");
    private static readonly SignatureInfo Unsigned = new(SignatureStatus.Unsigned, null);
    private static readonly string ProgramPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Launcher Co\launcher-app.exe");
    private static readonly FileMeta PupMeta = new("OneLaunch", "Launcher Co", null);

    public ProcessesRound3Tests() => TestEnvironment.Init();

    // ---------- known unwanted programs ----------

    [Fact]
    public void SignedKnownUnwantedProgram_WithNoOtherSignal_StaysInformational()
    {
        var result = ProcessesService.Evaluate(ProgramPath, Signed, null, PupMeta);

        Assert.Contains(result.Reasons, r => r.StartsWith("Потенциально нежелательная", StringComparison.Ordinal));
        Assert.False(result.IsSuspicious); // «Программы» reports it on the dashboard, not this page as well
        Assert.True(result.Score <= ProcessesService.BelowMediumScore);
    }

    [Fact]
    public void UnsignedKnownUnwantedProgram_KeepsTheFullBoost()
    {
        var result = ProcessesService.Evaluate(ProgramPath, Unsigned, null, PupMeta);
        Assert.True(result.IsSuspicious);
        Assert.Contains(result.Reasons, r => r.StartsWith("Потенциально нежелательная", StringComparison.Ordinal));
    }

    [Fact]
    public void AddPupMatch_CapsOnlyTheSignedOnlySignalCase()
    {
        const string pup = "Потенциально нежелательная программа: тест";

        var signedOnly = ProcessesService.AddPupMatch(SuspicionResult.Clean, pup, Signed);
        Assert.True(signedOnly.Level < SuspicionLevel.Medium);
        Assert.Equal(new[] { pup }, signedOnly.Reasons);

        // Another signal is already there: the signed file gets the usual small boost, which may reach Medium.
        var withOther = ProcessesService.AddPupMatch(new SuspicionResult(25, new[] { "Командная строка содержит интернет-адрес" }), pup, Signed);
        Assert.Equal(35, withOther.Score);
        Assert.True(withOther.IsSuspicious);

        var unsigned = ProcessesService.AddPupMatch(new SuspicionResult(20, new[] { "Нет цифровой подписи" }), pup, Unsigned);
        Assert.Equal(55, unsigned.Score);
        Assert.True(unsigned.IsSuspicious);

        var unknown = ProcessesService.AddPupMatch(SuspicionResult.Clean, pup, null);
        Assert.True(unknown.IsSuspicious); // no signature information: no reason to soften it
    }

    // ---------- quarantine refused by Core ----------

    [Fact]
    public async Task Quarantine_WhenTheQuarantineListCannotBeUsed_ShowsAFriendlyRussianMessage()
    {
        var dir = TestEnvironment.NewTempDir("quarantine-index");
        var exe = Path.Combine(dir, "unsigned-index-tool.exe");
        await File.WriteAllTextAsync(exe, "not a real program");
        var service = new ProcessesService(new FakeProcessSource(), new FakeDetails(), _ => Unsigned, canMoveFile: _ => true,
            addToQuarantine: (_, _) => throw new InvalidOperationException(
                "Список карантина (index.json в папке карантина) не читается, поэтому ничего не изменено.",
                new IOException("The process cannot access the file because it is being used by another process.")));

        var outcome = await service.QuarantineAsync(exe, Array.Empty<KillTarget>(), "тест", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.False(outcome.Cancelled);
        Assert.StartsWith("Файл не помещён в карантин", outcome.Message); // nothing was running: nothing was terminated
        Assert.Contains("список карантина", outcome.Message);
        Assert.Contains(exe, outcome.Message);
        Assert.Contains("«Настройки» → «Карантин»", outcome.Message);
        Assert.DoesNotContain("process cannot access", outcome.Message);
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public void QuarantineRefusedMessage_SaysWhetherTheProgramWasClosed()
    {
        var index = new InvalidOperationException("Не удалось записать список карантина: файл занят другой программой. Файл оставлен на месте.",
            new IOException("locked"));
        var killed = ProcessesService.QuarantineRefusedMessage(@"C:\Tools\tool.exe", index, killed: true);
        Assert.StartsWith("Программа завершена, но её файл не помещён в карантин: не удалось прочитать или записать список карантина", killed);
        Assert.Contains(@"C:\Tools\tool.exe", killed);

        // Not an index problem (no inner exception): Core's own Russian reason, starting with a lower-case letter.
        var other = ProcessesService.QuarantineRefusedMessage(@"C:\Tools\tool.exe",
            new InvalidOperationException("Системные файлы Windows нельзя помещать в карантин."), killed: false);
        Assert.StartsWith("Файл не помещён в карантин: системные файлы Windows нельзя помещать в карантин.", other);
    }

    [Fact]
    public void MoveFailedMessage_IsRussian_AndSuggestsElevationOnlyForAccessDenied()
    {
        var locked = ProcessesService.MoveFailedMessage(@"C:\Tools\tool.exe",
            new IOException("The process cannot access the file.", unchecked((int)0x80070020)), elevated: false, killed: true);
        Assert.StartsWith("Программа завершена, но её файл не удалось переместить в карантин: файл занят другой программой.", locked);
        Assert.Contains(@"C:\Tools\tool.exe", locked);
        Assert.DoesNotContain("администратора", locked);

        var denied = ProcessesService.MoveFailedMessage(@"C:\Tools\tool.exe", new UnauthorizedAccessException("Access denied"),
            elevated: false, killed: false);
        Assert.StartsWith("Файл не удалось переместить в карантин:", denied);
        Assert.Contains("Перезапустите программу от имени администратора", denied);
        Assert.DoesNotContain("Access denied", denied);

        var deniedElevated = ProcessesService.MoveFailedMessage(@"C:\Tools\tool.exe", new UnauthorizedAccessException("Access denied"),
            elevated: true, killed: true);
        Assert.DoesNotContain("Перезапустите", deniedElevated);
    }

    // ---------- RAM boundary shared with «Ускорение» ----------

    [Theory]
    [InlineData(84u, false)]
    [InlineData(85u, true)]
    [InlineData(94u, true)]
    public void LowMemory_IsReportedFrom85Percent(uint load, bool expected)
    {
        var items = new[] { IssueItems.Item(10, "app.exe", @"C:\Apps\app.exe", 0, 100L * 1024 * 1024, description: "Приложение") };
        var issues = ProcessIssues.Build(items, new MemoryStatus(16UL << 30, 2UL << 30, load), selfPid: 1);
        Assert.Equal(expected, issues.Any(i => i.Title == "Мало свободной оперативной памяти"));
    }

    // ---------- plurals ----------

    [Theory]
    [InlineData(3, "2 из 3 процессов")]
    [InlineData(21, "2 из 21 процесса")]
    [InlineData(25, "2 из 25 процессов")]
    public void OwnNote_UsesTheGenitiveAfterIz(int count, string expected)
    {
        var rows = Enumerable.Range(1, count)
            .Select(i => new ProcessRow(new ProcessStaticInfo(new ProcessKey(i, 1000 + i), "powershell.exe")))
            .ToList();
        rows[0].OwnRole = ProcessOwnRole.Helper;
        rows[1].OwnRole = ProcessOwnRole.Helper;
        var group = new ProcessGroup("powershell.exe", "powershell.exe");
        group.SetInstances(rows);

        Assert.StartsWith(expected + " — служебные процессы", group.OwnNote);
    }
}
