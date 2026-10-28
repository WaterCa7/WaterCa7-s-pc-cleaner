namespace PcCleaner.Modules.Profile;

/// <summary>A row of «Мои компьютеры».</summary>
public sealed record DeviceItem(long Id, string Name, string Details, bool IsCurrent, string? Whose);
