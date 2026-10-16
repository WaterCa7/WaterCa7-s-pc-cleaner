using PcCleaner.Core;

namespace PcCleaner.Modules.Disk;

public sealed class DiskViewModel : PageViewModel
{
    private readonly DiskService _service;

    public DiskViewModel(DiskService service)
    {
        _service = service;
    }

    public override string Title => "Диски";

    public override string Subtitle => "Свободное место, самые большие папки и файлы, здоровье накопителей";

    public override string IconGlyph => "\uEDA2";
}
