namespace PcCleaner.Modules.Security;

/// <summary>State of a Windows optional feature (Win32_OptionalFeature.InstallState / Get-WindowsOptionalFeature).</summary>
public enum FeatureState
{
    Unknown = 0,
    Enabled = 1,
    Disabled = 2,
    Absent = 3,
}

/// <summary>SMBv1 registry signals, used when the Windows feature list cannot be read.</summary>
/// <param name="Readable">The keys could be read (a missing key is readable: it means "not installed").</param>
/// <param name="ServerSmb1">LanmanServer\Parameters SMB1: 0 = off, 1 = on, null = not set.</param>
/// <param name="ServerDriverStart">Start of the "srv" service (SMB1 server driver), null = not installed.</param>
/// <param name="ClientDriverStart">Start of the "mrxsmb10" service (SMB1 client driver), null = not installed.</param>
public sealed record Smb1Registry(bool Readable, int? ServerSmb1, int? ServerDriverStart, int? ClientDriverStart)
{
    public static Smb1Registry Unreadable { get; } = new(false, null, null, null);
}

/// <summary>SMBv1 state: the Windows feature "SMB1Protocol" and its parts, plus the registry signals.</summary>
public sealed record Smb1State(FeatureState Feature, FeatureState Client, FeatureState Server, Smb1Registry Registry)
{
    public static Smb1State Unknown { get; } =
        new(FeatureState.Unknown, FeatureState.Unknown, FeatureState.Unknown, Smb1Registry.Unreadable);
}

/// <param name="Readable">The SecureBoot\State key could be read.</param>
/// <param name="Enabled">UEFISecureBootEnabled: 1 = on, 0 = off, null = missing (Legacy BIOS or not supported).</param>
/// <param name="IsUefi">Firmware type (GetFirmwareType), null = unknown.</param>
public sealed record SecureBootState(bool Readable, int? Enabled, bool? IsUefi);

/// <param name="Present">TPM found (null = could not check).</param>
/// <param name="Enabled">Enabled in the firmware (only known with administrator rights).</param>
/// <param name="Activated">Activated (only known with administrator rights).</param>
/// <param name="Version">"2.0", "1.2" or null.</param>
public sealed record TpmState(bool? Present, bool? Enabled, bool? Activated, string? Version)
{
    public static TpmState Unknown { get; } = new(null, null, null, null);
}

/// <summary>Encryption of the system drive (BitLocker / device encryption).</summary>
public enum DriveEncryption
{
    Unknown,
    NotSupported,
    Off,
    On,
    Encrypting,
    Decrypting,
    Suspended,
    Locked,

    /// <summary>Device encryption prepared the drive, but the key is not protected yet ("waiting for activation").</summary>
    Pending,
}

/// <param name="Drive">"C:".</param>
/// <param name="IsLaptop">A portable computer (battery and/or a portable chassis type).</param>
/// <param name="IsHomeEdition">Windows Home: no BitLocker, only "device encryption" on supported hardware.</param>
public sealed record BitLockerState(string Drive, DriveEncryption Encryption, bool IsLaptop, bool IsHomeEdition)
{
    public static BitLockerState Unknown { get; } = new("C:", DriveEncryption.Unknown, false, false);
}

/// <summary>Win32_DeviceGuard (virtualization-based security and memory integrity / HVCI).</summary>
/// <param name="Available">Win32_DeviceGuard answered.</param>
/// <param name="VbsStatus">VirtualizationBasedSecurityStatus: 0 = off, 1 = enabled but not running, 2 = running.</param>
/// <param name="ServicesRunning">SecurityServicesRunning (2 = HVCI).</param>
/// <param name="ServicesConfigured">SecurityServicesConfigured (2 = HVCI).</param>
/// <param name="HvciRegistryEnabled">DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity "Enabled" (1 = turned on).</param>
public sealed record DeviceGuardState(bool Available, int? VbsStatus, IReadOnlyList<int> ServicesRunning,
    IReadOnlyList<int> ServicesConfigured, int? HvciRegistryEnabled)
{
    public static DeviceGuardState Unknown { get; } = new(false, null, Array.Empty<int>(), Array.Empty<int>(), null);

    public const int HvciService = 2;

    public bool HvciRunning => ServicesRunning.Contains(HvciService);

    public bool HvciConfigured => ServicesConfigured.Contains(HvciService) || HvciRegistryEnabled == 1;
}
