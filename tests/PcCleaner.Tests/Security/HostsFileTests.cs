using System.IO;
using System.Text;
using PcCleaner.Modules.Security;
using Xunit;

namespace PcCleaner.Tests.Security;

public class HostsFileTests
{
    public HostsFileTests()
    {
        TestEnvironment.Init();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private const string Sample =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
        "#\r\n" +
        "#      102.54.94.97     rhino.acme.com          # source server\r\n" +
        "\r\n" +
        "127.0.0.1\tlocalhost\r\n" +
        "::1             localhost\r\n" +
        "0.0.0.0 ads.example.com tracker.example.net   # block ads\r\n" +
        "185.12.34.56   vk.com www.vk.com\r\n" +
        "\t127.0.0.1\twww.kaspersky.ru\r\n" +
        "31.13.72.12 staticxx.facebook.com #gu_acc\r\n" +
        "192.168.1.20 mynas\r\n" +
        "not-an-ip example.com\r\n" +
        "10.0.0.1";

    [Fact]
    public void Parse_SkipsCommentsAndBlankLines()
    {
        var entries = HostsFile.Parse(Sample);
        Assert.Equal(9, entries.Count);
        Assert.DoesNotContain(entries, e => e.Hostnames.Contains("rhino.acme.com"));
    }

    [Fact]
    public void Parse_HandlesTabsMultipleHostnamesIpv6AndInlineComments()
    {
        var entries = HostsFile.Parse(Sample);

        var tabbed = entries.Single(e => e.Address == "127.0.0.1" && e.Hostnames.Contains("localhost"));
        Assert.Equal(4, tabbed.LineIndex);
        Assert.Equal(5, tabbed.LineNumber);

        var v6 = entries.Single(e => e.Address == "::1");
        Assert.True(v6.IsValidAddress);
        Assert.Equal(new[] { "localhost" }, v6.Hostnames);

        var ads = entries.Single(e => e.Address == "0.0.0.0");
        Assert.Equal(new[] { "ads.example.com", "tracker.example.net" }, ads.Hostnames);
        Assert.Equal("block ads", ads.Comment);

        var fb = entries.Single(e => e.Hostnames.Contains("staticxx.facebook.com"));
        Assert.Equal("gu_acc", fb.Comment);
        Assert.Equal("31.13.72.12 staticxx.facebook.com #gu_acc", fb.RawLine);

        Assert.False(entries.Single(e => e.Address == "not-an-ip").IsValidAddress);
        Assert.Empty(entries.Single(e => e.Address == "10.0.0.1").Hostnames);
    }

    [Fact]
    public void Classify_RedirectBlockAndAdblock()
    {
        var entries = HostsFile.Parse(Sample);
        foreach (var e in entries)
            HostsRules.Classify(e);

        Assert.Equal(SecurityStatus.Danger, entries.Single(e => e.Hostnames.Contains("vk.com")).Risk);
        Assert.Equal(SecurityStatus.Warning, entries.Single(e => e.Hostnames.Contains("www.kaspersky.ru")).Risk);
        Assert.Equal(SecurityStatus.Ok, entries.Single(e => e.Address == "0.0.0.0").Risk);
        Assert.Equal(SecurityStatus.Ok, entries.Single(e => e.Address == "::1").Risk);
        // Facebook host pointing to Meta's own network: softened to a warning.
        Assert.Equal(SecurityStatus.Warning, entries.Single(e => e.Hostnames.Contains("staticxx.facebook.com")).Risk);
        Assert.Equal(SecurityStatus.Info, entries.Single(e => e.Hostnames.Contains("mynas")).Risk);
        Assert.Equal(SecurityStatus.Info, entries.Single(e => e.Address == "not-an-ip").Risk);
    }

    [Theory]
    [InlineData("1.2.3.4 www.google.com", SecurityStatus.Danger)]
    [InlineData("1.2.3.4 google.ru", SecurityStatus.Danger)]
    [InlineData("1.2.3.4 online.sberbank.ru", SecurityStatus.Danger)]
    [InlineData("1.2.3.4 www.gosuslugi.ru", SecurityStatus.Danger)]
    [InlineData("1.2.3.4 store.steampowered.com", SecurityStatus.Danger)]
    [InlineData("1.2.3.4 yandex.ru", SecurityStatus.Danger)]
    [InlineData("142.250.74.14 www.youtube.com", SecurityStatus.Warning)]
    [InlineData("127.0.0.1 update.microsoft.com", SecurityStatus.Warning)]
    [InlineData("0.0.0.0 download.windowsupdate.com", SecurityStatus.Warning)]
    [InlineData("0.0.0.0 www.virustotal.com", SecurityStatus.Warning)]
    [InlineData("0.0.0.0 esetnod32.ru", SecurityStatus.Warning)]
    [InlineData("0.0.0.0 vortex.data.microsoft.com", SecurityStatus.Info)]
    [InlineData("0.0.0.0 doubleclick.net", SecurityStatus.Ok)]
    [InlineData("1.2.3.4 fvk.com", SecurityStatus.Info)]
    [InlineData("1.2.3.4 reset.example.org", SecurityStatus.Info)]
    [InlineData("1.2.3.4 vk.com.evil.example", SecurityStatus.Info)]
    public void Classify_Domains(string line, SecurityStatus expected)
    {
        var e = HostsFile.Parse(line).Single();
        Assert.Equal(expected, HostsRules.Evaluate(e).Risk);
    }

    [Fact]
    public void Evaluate_HostsReport_DangerWhenRedirect()
    {
        var entries = HostsFile.Parse("1.2.3.4 vk.com\n0.0.0.0 ads.example.com");
        foreach (var e in entries)
            HostsRules.Classify(e);
        var r = SecurityRules.EvaluateHosts(new HostsReport { Path = "hosts", Entries = entries });
        Assert.Equal(SecurityStatus.Danger, r.Status);

        var clean = SecurityRules.EvaluateHosts(new HostsReport { Path = "hosts", Entries = new List<HostsEntry>() });
        Assert.Equal(SecurityStatus.Ok, clean.Status);
    }

    [Fact]
    public void RemoveLines_PreservesOtherLinesLineEndingsAndUtf8Bom()
    {
        var dir = TestEnvironment.NewTempDir("hosts");
        var path = Path.Combine(dir, "hosts");
        var text = "# заголовок\r\n127.0.0.1 localhost\n1.2.3.4 vk.com\r\n# конец\r\n0.0.0.0 ads.example.com";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        File.WriteAllBytes(path, bytes);
        var backups = Path.Combine(dir, "backups");

        var report = HostsFile.Read(path);
        var target = report.Entries.Single(e => e.Hostnames.Contains("vk.com"));
        var backup = HostsFile.RemoveLines(path, new[] { target }, backups);

        var expected = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("# заголовок\r\n127.0.0.1 localhost\n# конец\r\n0.0.0.0 ads.example.com"))
            .ToArray();
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Equal(bytes, File.ReadAllBytes(backup));
        Assert.StartsWith(backups, backup);
    }

    [Fact]
    public void RemoveLines_PreservesAnsiEncoding()
    {
        var dir = TestEnvironment.NewTempDir("hosts-ansi");
        var path = Path.Combine(dir, "hosts");
        var cp1251 = Encoding.GetEncoding(1251);
        File.WriteAllBytes(path, cp1251.GetBytes("# Мой файл\r\n1.2.3.4 www.google.com # подмена\r\n10.0.0.5 nas # мой NAS\r\n"));

        var entries = HostsFile.Read(path).Entries;
        HostsFile.RemoveLines(path, entries.Where(e => e.Risk == SecurityStatus.Danger).ToList(), Path.Combine(dir, "b"));

        Assert.Equal(cp1251.GetBytes("# Мой файл\r\n10.0.0.5 nas # мой NAS\r\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public void RemoveLines_FileChanged_ThrowsAndKeepsFile()
    {
        var dir = TestEnvironment.NewTempDir("hosts-changed");
        var path = Path.Combine(dir, "hosts");
        File.WriteAllText(path, "1.2.3.4 vk.com\r\n");
        var entry = HostsFile.Read(path).Entries.Single();
        File.WriteAllText(path, "5.6.7.8 ok.ru\r\n");

        Assert.Throws<HostsChangedException>(() => HostsFile.RemoveLines(path, new[] { entry }, Path.Combine(dir, "b")));
        Assert.Equal("5.6.7.8 ok.ru\r\n", File.ReadAllText(path));
        Assert.False(Directory.Exists(Path.Combine(dir, "b")));
    }

    [Fact]
    public void RemoveLines_ReadOnlyFile_IsRestoredReadOnly()
    {
        var dir = TestEnvironment.NewTempDir("hosts-ro");
        var path = Path.Combine(dir, "hosts");
        File.WriteAllText(path, "1.2.3.4 vk.com\r\n127.0.0.1 localhost\r\n");
        new FileInfo(path).IsReadOnly = true;
        try
        {
            var report = HostsFile.Read(path);
            Assert.True(report.IsReadOnly);
            HostsFile.RemoveLines(path, report.Entries.Where(e => e.Hostnames.Contains("vk.com")).ToList(), Path.Combine(dir, "b"));
            Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(path));
            Assert.True(new FileInfo(path).IsReadOnly);
        }
        finally
        {
            new FileInfo(path).IsReadOnly = false;
        }
    }

    [Fact]
    public void Read_MissingFile_IsOk()
    {
        var report = HostsFile.Read(Path.Combine(TestEnvironment.NewTempDir("nohosts"), "hosts"));
        Assert.False(report.Exists);
        Assert.Equal(SecurityStatus.Ok, SecurityRules.EvaluateHosts(report).Status);
    }
}
