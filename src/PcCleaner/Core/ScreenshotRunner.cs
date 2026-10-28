using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

/// <summary>
/// Non-interactive verification mode:
///   WaterCa7sPcCleaner.exe --screenshots &lt;dir&gt; [--page Dashboard,System,Computer/Hardware] [--no-scan] [--mode simple|expert] [--lang ru|en] [--welcome]
/// With --welcome it first takes a picture of the first-run screen (00-Welcome.png) over the blurred window and closes it
/// without choosing anything, so the pictures of the pages are the same as without the switch.
/// Opens each tab of the mode (NN is the number of the tab in the full list, so a tab keeps its file name in both modes),
/// runs its read-only PreviewScanAsync, saves NN-Key.png (a section of one page) or NN-Section-Tab.png and log.txt, then exits.
/// All confirmations are auto-declined (Dialogs.UseSilentMode), so nothing on the system is changed.
/// Exit code: 0 = no errors logged, 1 = errors were logged (see log.txt).
/// </summary>
public static class ScreenshotRunner
{
    /// <summary>The picture of the first-run screen (<c>--welcome</c>); 00 puts it before the pages, which start at 01.</summary>
    internal const string WelcomeFile = "00-Welcome.png";

    public static async Task<int> RunAsync(Window window, MainViewModel vm, string outDir, string[]? requests, bool scan, bool welcome = false)
    {
        Directory.CreateDirectory(outDir);
        var errors = 0;
        Log.EntryAdded += e =>
        {
            if (e.Level == LogLevel.Error)
                errors++;
        };

        if (welcome)
            await ShootWelcomeAsync(window, vm, outDir);

        var allPages = vm.Pages.ToList();
        foreach (var key in HiddenRequested(allPages, vm.VisiblePages, vm.Sections, requests))
            Log.Info(T.F("[screenshot] {0}: пропущено — в этом режиме раздела нет", key));

        foreach (var (index, page) in PagesToShoot(allPages, vm.VisiblePages, vm.Sections, requests))
        {
            var sw = Stopwatch.StartNew();
            Log.Info(T.F("[screenshot] {0}: открытие", page.Key));
            await vm.NavigateAsync(page);
            await WaitIdleAsync();

            if (scan)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                try
                {
                    await page.PreviewScanAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    Log.Error($"[screenshot] {page.Key}: PreviewScanAsync", ex);
                }
            }

            var waitStart = Stopwatch.StartNew();
            while (page.IsBusy && waitStart.Elapsed < TimeSpan.FromMinutes(4))
                await Task.Delay(250);

            await Task.Delay(500);
            await WaitIdleAsync();
            var file = Path.Combine(outDir, FileName(index, page, vm.Sections));
            Save(window, file);
            Log.Info(T.F("[screenshot] {0}: готово за {1:0.0} с -> {2}", page.Key, sw.Elapsed.TotalSeconds, file));
        }

        await WaitIdleAsync();
        File.WriteAllLines(Path.Combine(outDir, "log.txt"),
            Log.Entries.Select(e => $"{e.TimeText} [{e.Level}] {e.Message}"));
        return errors > 0 ? 1 : 0;
    }

    /// <summary>
    /// The first-run screen, once: shown even when a mode is chosen (the settings of a screenshot run are a temp copy), given
    /// the time to fade in over the blur, photographed, and closed without a choice (nothing is saved) with time to fade out.
    /// </summary>
    private static async Task ShootWelcomeAsync(Window window, MainViewModel vm, string outDir)
    {
        var sw = Stopwatch.StartNew();
        Log.Info(T._("[screenshot] Welcome: открытие"));
        vm.Welcome.ShowAtStart(screenshotRun: true, forced: true);
        await Task.Delay(700); // the fade-in is 250 ms
        await WaitIdleAsync();
        var file = Path.Combine(outDir, WelcomeFile);
        Save(window, file);
        vm.Welcome.Hide();
        await Task.Delay(700); // the fade-out is 250 ms; then the blur is dropped
        await WaitIdleAsync();
        Log.Info(T.F("[screenshot] Welcome: готово за {0:0.0} с -> {1}", sw.Elapsed.TotalSeconds, file));
    }

    /// <summary>
    /// The tabs to photograph: the visible ones of the current mode (the simple mode hides most), only those named in
    /// <paramref name="requests"/> when it is given (a page key, a section key or «Section/Tab»). <c>Number</c> is the place
    /// in <paramref name="all"/> (1-based), so a tab keeps its file name in every mode and the two modes compare by name.
    /// </summary>
    internal static List<(int Number, PageViewModel Page)> PagesToShoot(IReadOnlyList<PageViewModel> all, IEnumerable<PageViewModel> visible,
        IReadOnlyList<SectionViewModel> sections, string[]? requests)
    {
        var shown = visible.ToList();
        return all
            .Select((page, index) => (Number: index + 1, Page: page))
            .Where(p => shown.Contains(p.Page) &&
                        (requests is not { Length: > 0 } || requests.Any(r => Matches(r, SectionOf(sections, p.Page), p.Page))))
            .ToList();
    }

    /// <summary>The requests (<c>--page</c>) that name an existing tab or section of which nothing is visible in this mode.</summary>
    internal static List<string> HiddenRequested(IReadOnlyList<PageViewModel> all, IEnumerable<PageViewModel> visible,
        IReadOnlyList<SectionViewModel> sections, string[]? requests)
    {
        var shown = visible.ToList();
        return (requests ?? Array.Empty<string>())
            .Where(r => all.Any(p => Matches(r, SectionOf(sections, p), p)) &&
                        !shown.Any(p => Matches(r, SectionOf(sections, p), p)))
            .ToList();
    }

    /// <summary>NN-Key.png for a section of one page with the same key (01-Dashboard.png), else NN-Section-Tab.png (08-System-Disk.png).</summary>
    internal static string FileName(int number, PageViewModel page, IReadOnlyList<SectionViewModel> sections)
        => SectionOf(sections, page) is { } section && !string.Equals(section.Key, page.Key, StringComparison.OrdinalIgnoreCase)
            ? $"{number:00}-{section.Key}-{page.Key}.png"
            : $"{number:00}-{page.Key}.png";

    private static SectionViewModel? SectionOf(IReadOnlyList<SectionViewModel> sections, PageViewModel page)
        => sections.FirstOrDefault(s => s.Contains(page));

    private static bool Matches(string request, SectionViewModel? section, PageViewModel page)
    {
        var parts = request.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
            return section is not null && Same(parts[0], section.Key) && Same(parts[1], page.Key);
        return Same(request.Trim(), page.Key) || (section is not null && Same(request.Trim(), section.Key));
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static async Task WaitIdleAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string file)
    {
        if (window.Content is not FrameworkElement root)
            return;
        root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(root);
        var width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
