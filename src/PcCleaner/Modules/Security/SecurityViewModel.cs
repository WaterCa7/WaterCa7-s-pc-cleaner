using PcCleaner.Core;

namespace PcCleaner.Modules.Security;

public sealed class SecurityViewModel : PageViewModel
{
    private readonly SecurityService _service;

    public SecurityViewModel(SecurityService service)
    {
        _service = service;
    }

    public override string Title => "Безопасность";

    public override string Subtitle => "Антивирус, брандмауэр, файл hosts и другие проверки защиты";

    public override string IconGlyph => "\uEA18";
}
