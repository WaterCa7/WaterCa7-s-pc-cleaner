using PcCleaner.Core;

namespace PcCleaner.Modules.Programs;

public sealed class ProgramsViewModel : PageViewModel
{
    private readonly ProgramsService _service;

    public ProgramsViewModel(ProgramsService service)
    {
        _service = service;
    }

    public override string Title => "Программы";

    public override string Subtitle => "Установленные программы, нежелательные приложения и «битые» записи";

    public override string IconGlyph => "\uE71D";
}
