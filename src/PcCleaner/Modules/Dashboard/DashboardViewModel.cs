using PcCleaner.Core;

namespace PcCleaner.Modules.Dashboard;

public sealed class DashboardViewModel : PageViewModel
{
    public override string Title => "Главная";

    public override string Subtitle => "Общее состояние компьютера и быстрая проверка в один клик";

    public override string IconGlyph => "\uE80F";
}
