using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PcCleaner.Core;

/// <summary>
/// Non-interactive verification mode:
///   WaterCa7sPcCleaner.exe --screenshots &lt;dir&gt; [--page Cleaner,Startup] [--no-scan]
/// Opens each page, runs its read-only PreviewScanAsync, saves NN-Key.png and log.txt, then exits.
/// All confirmations are auto-declined (Dialogs.UseSilentMode), so nothing on the system is changed.
/// Exit code: 0 = no errors logged, 1 = errors were logged (see log.txt).
/// </summary>
public static class ScreenshotRunner
{
    public static async Task<int> RunAsync(Window window, MainViewModel vm, string outDir, string[]? pageKeys, bool scan)
    {
        Directory.CreateDirectory(outDir);
        var errors = 0;
        Log.EntryAdded += e =>
        {
            if (e.Level == LogLevel.Error)
                errors++;
        };

        var index = 0;
        foreach (var page in vm.Pages.ToList())
        {
            index++;
            if (pageKeys is { Length: > 0 } &&
                !pageKeys.Any(k => string.Equals(k, page.Key, StringComparison.OrdinalIgnoreCase)))
                continue;

            var sw = Stopwatch.StartNew();
            Log.Info($"[screenshot] {page.Key}: открытие");
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
            var file = Path.Combine(outDir, $"{index:00}-{page.Key}.png");
            Save(window, file);
            Log.Info($"[screenshot] {page.Key}: готово за {sw.Elapsed.TotalSeconds:0.0} с -> {file}");
        }

        await WaitIdleAsync();
        File.WriteAllLines(Path.Combine(outDir, "log.txt"),
            Log.Entries.Select(e => $"{e.TimeText} [{e.Level}] {e.Message}"));
        return errors > 0 ? 1 : 0;
    }

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
