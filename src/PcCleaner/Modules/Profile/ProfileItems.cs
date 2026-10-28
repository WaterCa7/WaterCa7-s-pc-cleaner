using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

/// <summary>A row of «Мои компьютеры».</summary>
public sealed record DeviceItem(long Id, string Name, string Details, bool IsCurrent, string? Whose)
{
    /// <summary>«чей: …» for a computer that belongs to someone else's sign-in; null when the server did not say.</summary>
    public string? WhoseText => string.IsNullOrWhiteSpace(Whose) ? null : T.F("чей: {0}", Whose);
}
