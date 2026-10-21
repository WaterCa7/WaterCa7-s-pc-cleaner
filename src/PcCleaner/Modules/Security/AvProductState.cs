using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Security;

/// <summary>
/// Decodes the undocumented but well-known Security Center "productState" bit field
/// (root\SecurityCenter2 AntiVirusProduct / FirewallProduct).
///   bits 12..15: 0 = off, 1 = on, 2 = snoozed, 3 = expired
///   bits  4..7 : 0 = definitions up to date, 1 = out of date
/// </summary>
public readonly record struct AvProductState(int Raw)
{
    public int ScannerState => (Raw >> 12) & 0xF;

    public int SignatureState => (Raw >> 4) & 0xF;

    public bool Enabled => ScannerState == 1;

    public bool Snoozed => ScannerState == 2;

    public bool Expired => ScannerState == 3;

    public bool UpToDate => SignatureState == 0;

    public static AvProductState Decode(long productState) => new((int)(productState & 0x7FFFFFFF));

    public string StateText => ScannerState switch
    {
        1 => T._("включён"),
        2 => T._("приостановлен"),
        3 => T._("срок лицензии истёк"),
        _ => T._("выключен"),
    };
}
