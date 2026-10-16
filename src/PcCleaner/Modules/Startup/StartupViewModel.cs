using PcCleaner.Core;

namespace PcCleaner.Modules.Startup;

public sealed class StartupViewModel : PageViewModel
{
    private readonly StartupService _service;

    public StartupViewModel(StartupService service)
    {
        _service = service;
    }

    public override string Title => "Автозагрузка";

    public override string Subtitle => "Программы, службы и задачи, которые запускаются вместе с Windows";

    public override string IconGlyph => "\uE7E8";
}
