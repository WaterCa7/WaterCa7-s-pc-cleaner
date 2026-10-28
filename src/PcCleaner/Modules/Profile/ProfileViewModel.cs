using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

/// <summary>
/// «Профиль» (the bottom of the sidebar). A placeholder of the new menu: the account, Pro, the computers and the promo code
/// come with the accounts work (spec 2026-10-27, part B). Nothing here touches the system or the network.
/// </summary>
public sealed class ProfileViewModel : PageViewModel
{
    public override string Title => T._("Профиль");

    public override string Subtitle => T._("Аккаунт WaterCa7, Pro и ваши компьютеры");

    public override string IconGlyph => "";
}
