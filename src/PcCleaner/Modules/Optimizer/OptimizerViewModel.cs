using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

public sealed class OptimizerViewModel : PageViewModel
{
    private readonly OptimizerService _service;

    public OptimizerViewModel(OptimizerService service)
    {
        _service = service;
    }

    public override string Title => "Ускорение";

    public override string Subtitle => "Рекомендации по железу и настройкам Windows для быстрой работы";

    public override string IconGlyph => "\uE945";
}
