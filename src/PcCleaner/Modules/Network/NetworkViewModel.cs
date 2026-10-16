using PcCleaner.Core;

namespace PcCleaner.Modules.Network;

public sealed class NetworkViewModel : PageViewModel
{
    private readonly NetworkService _service;

    public NetworkViewModel(NetworkService service)
    {
        _service = service;
    }

    public override string Title => "Сеть";

    public override string Subtitle => "Скорость подключения, задержки, Wi-Fi, открытые порты и соединения программ";

    public override string IconGlyph => "\uE968";
}
