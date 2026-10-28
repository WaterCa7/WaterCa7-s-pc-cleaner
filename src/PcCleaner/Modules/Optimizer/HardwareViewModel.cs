using System.ComponentModel;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Optimizer;

/// <summary>
/// «Комплектующие»: the hardware cards of <see cref="OptimizerViewModel"/> on a tab of their own (menu «Компьютер»). One scan
/// serves both tabs: opening either starts or waits for the same load, and this page mirrors the optimizer's busy strip.
/// Its own Cancel stays disabled (the work belongs to the optimizer); the strip on «Ускорение» can cancel it.
/// </summary>
public sealed class HardwareViewModel : PageViewModel
{
    public HardwareViewModel(OptimizerViewModel optimizer)
    {
        Optimizer = optimizer;
        optimizer.PropertyChanged += OnOptimizerChanged;
    }

    public OptimizerViewModel Optimizer { get; }

    public override string Title => T._("Комплектующие");

    public override string Subtitle => T._("Процессор, память, видеокарта, диски и мониторы вашего компьютера");

    public override string IconGlyph => "";

    /// <summary>The «Подробнее» link of a card (the card template binds to the page's DataContext).</summary>
    public AsyncRelayCommand CardActionCommand => Optimizer.CardActionCommand;

    public override Task OnNavigatedToAsync() => Optimizer.OnNavigatedToAsync();

    /// <summary>Read-only: the optimizer's scan, unless it has already run (the «Ускорение» tab runs its own preview).</summary>
    public override Task PreviewScanAsync(CancellationToken ct) => Optimizer.HasData ? Task.CompletedTask : Optimizer.PreviewScanAsync(ct);

    private void OnOptimizerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IsBusy):
                IsBusy = Optimizer.IsBusy;
                break;
            case nameof(BusyText):
                BusyText = Optimizer.BusyText;
                break;
            case nameof(Progress):
                Progress = Optimizer.Progress;
                break;
        }
    }
}
