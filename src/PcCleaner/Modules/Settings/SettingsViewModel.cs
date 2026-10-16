using PcCleaner.Core;

namespace PcCleaner.Modules.Settings;

public sealed class SettingsViewModel : PageViewModel
{
    public override string Title => "Настройки";

    public override string Subtitle => "Параметры, журнал действий, резервные копии и карантин";

    public override string IconGlyph => "\uE713";
}
