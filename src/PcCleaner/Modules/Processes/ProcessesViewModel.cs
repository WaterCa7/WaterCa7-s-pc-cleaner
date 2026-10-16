using PcCleaner.Core;

namespace PcCleaner.Modules.Processes;

public sealed class ProcessesViewModel : PageViewModel
{
    private readonly ProcessesService _service;

    public ProcessesViewModel(ProcessesService service)
    {
        _service = service;
    }

    public override string Title => "Процессы";

    public override string Subtitle => "Что сейчас работает в фоне, сколько ресурсов потребляет и нет ли подозрительного";

    public override string IconGlyph => "\uE9F5";
}
