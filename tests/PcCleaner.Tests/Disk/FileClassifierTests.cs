using System.IO;
using PcCleaner.Modules.Disk;
using Xunit;

namespace PcCleaner.Tests.Disk;

public class FileClassifierTests
{
    [Theory]
    [InlineData("movie.MKV", FileCategory.Video)]
    [InlineData("clip.mp4", FileCategory.Video)]
    [InlineData("song.flac", FileCategory.Music)]
    [InlineData("photo.JPEG", FileCategory.Images)]
    [InlineData("design.psd", FileCategory.Images)]
    [InlineData("backup.7z", FileCategory.Archives)]
    [InlineData("data.tar.gz", FileCategory.Archives)]
    [InlineData("win11.iso", FileCategory.DiskImages)]
    [InlineData("ubuntu.vhdx", FileCategory.DiskImages)]
    [InlineData("tool.msi", FileCategory.Installers)]
    [InlineData("app.msixbundle", FileCategory.Installers)]
    [InlineData("report.docx", FileCategory.Documents)]
    [InlineData("book.pdf", FileCategory.Documents)]
    [InlineData("app.exe", FileCategory.Programs)]
    [InlineData("library.dll", FileCategory.Programs)]
    [InlineData("pagefile.sys", FileCategory.System)]
    [InlineData("noextension", FileCategory.Other)]
    [InlineData("weird.ext-that-is-way-too-long", FileCategory.Other)]
    [InlineData("data.bin~", FileCategory.Other)]
    [InlineData("trailingdot.", FileCategory.Other)]
    public void Classifies_by_extension(string name, FileCategory expected)
        => Assert.Equal(expected, FileClassifier.Classify(name, DirContext.None));

    [Fact]
    public void Exe_in_downloads_or_desktop_is_an_installer()
    {
        Assert.Equal(FileCategory.Installers, FileClassifier.Classify("setup.exe", DirContext.InstallerFolder));
        Assert.Equal(FileCategory.Video, FileClassifier.Classify("film.mp4", DirContext.InstallerFolder));
    }

    [Fact]
    public void Everything_in_program_and_system_folders_uses_the_folder_category()
    {
        Assert.Equal(FileCategory.Programs, FileClassifier.Classify("intro.mp4", DirContext.Programs));
        Assert.Equal(FileCategory.Programs, FileClassifier.Classify("setup.exe", DirContext.Programs));
        Assert.Equal(FileCategory.System, FileClassifier.Classify("photo.jpg", DirContext.System));
    }

    [Fact]
    public void Extension_key_is_case_insensitive_and_rejects_bad_chars()
    {
        Assert.Equal(FileClassifier.ExtensionKey("MP4"), FileClassifier.ExtensionKey("mp4"));
        Assert.NotEqual(FileClassifier.ExtensionKey("mp4"), FileClassifier.ExtensionKey("mp3"));
        Assert.Equal(0UL, FileClassifier.ExtensionKey("тест"));
        Assert.Equal(0UL, FileClassifier.ExtensionKey("a b"));
    }

    [Fact]
    public void Every_category_has_a_russian_name_icon_and_description()
    {
        foreach (var category in Enum.GetValues<FileCategory>())
        {
            var info = FileClassifier.Info(category);
            Assert.Equal(category, info.Category);
            Assert.False(string.IsNullOrWhiteSpace(info.Name));
            Assert.False(string.IsNullOrWhiteSpace(info.Icon));
            Assert.False(string.IsNullOrWhiteSpace(info.Description));
        }
    }

    [Fact]
    public void Path_rules_detect_known_roots_and_game_libraries()
    {
        var rules = new PathRules(new[] { @"C:\Windows" }, new[] { @"C:\Program Files" }, new[] { @"C:\Users\U\Downloads" });

        Assert.Equal(DirContext.System, rules.ForRoot(@"C:\Windows\System32"));
        Assert.Equal(DirContext.Programs, rules.ForRoot(@"C:\Program Files\App\bin"));
        Assert.Equal(DirContext.Programs, rules.ForRoot(@"D:\SteamLibrary\steamapps\common\Game"));
        Assert.Equal(DirContext.Programs, rules.ForRoot(@"E:\Epic Games\Fortnite"));
        Assert.Equal(DirContext.InstallerFolder, rules.ForRoot(@"C:\Users\U\Downloads\sub"));
        Assert.Equal(DirContext.None, rules.ForRoot(@"C:\Users\U\Documents"));
        Assert.Equal(DirContext.System, rules.ForRoot(@"D:\System Volume Information"));
        Assert.Equal(DirContext.None, rules.ForRoot(@"D:\Data\Recovery")); // only "Recovery" in a drive root is system
    }

    [Fact]
    public void Path_rules_children_inherit_or_detect_their_own_context()
    {
        var rules = new PathRules(new[] { @"C:\Windows" }, new[] { @"C:\Program Files" }, new[] { @"C:\Users\U\Downloads" });

        Assert.Equal(DirContext.Programs, rules.ForChild(DirContext.Programs, @"C:\Program Files\X", "X", false));
        Assert.Equal(DirContext.System, rules.ForChild(DirContext.None, @"C:\Windows", "Windows", true));
        Assert.Equal(DirContext.System, rules.ForChild(DirContext.None, @"C:\$Recycle.Bin", "$Recycle.Bin", true));
        Assert.Equal(DirContext.Programs, rules.ForChild(DirContext.InstallerFolder, @"C:\Users\U\Downloads\Steam", "Steam", false));
        Assert.Equal(DirContext.InstallerFolder, rules.ForChild(DirContext.InstallerFolder, @"C:\Users\U\Downloads\x", "x", false));
        Assert.Equal(DirContext.None, rules.ForChild(DirContext.None, @"C:\Users", "Users", true));
    }
}
