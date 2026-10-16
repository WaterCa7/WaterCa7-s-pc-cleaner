using PcCleaner.Core;

namespace PcCleaner.Modules.Cleaner;

public sealed class CleanerViewModel : PageViewModel
{
    private readonly CleanerService _service;

    public CleanerViewModel(CleanerService service)
    {
        _service = service;
    }

    public override string Title => "Очистка";

    public override string Subtitle => "Временные файлы, кэш браузеров, корзина и другой мусор";

    public override string IconGlyph => "\uE74D";
}
