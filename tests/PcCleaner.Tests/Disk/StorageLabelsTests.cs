using PcCleaner.Modules.Disk;
using Xunit;

namespace PcCleaner.Tests.Disk;

public class StorageLabelsTests
{
    [Theory]
    [InlineData(3L, 11L, 7200L, DriveKind.Hdd)]
    [InlineData(4L, 11L, 0L, DriveKind.Ssd)]
    [InlineData(4L, 17L, 0L, DriveKind.NvmeSsd)]
    [InlineData(0L, 17L, null, DriveKind.NvmeSsd)]
    [InlineData(5L, 18L, 0L, DriveKind.Scm)]
    [InlineData(0L, 11L, 5400L, DriveKind.Hdd)]
    [InlineData(0L, 7L, 0L, DriveKind.Ssd)]
    [InlineData(0L, 7L, 4294967295L, DriveKind.Unknown)]
    [InlineData(null, null, null, DriveKind.Unknown)]
    public void Infers_drive_kind(long? media, long? bus, long? spindle, DriveKind expected)
        => Assert.Equal(expected, StorageLabels.InferKind(media, bus, spindle));

    [Theory]
    [InlineData(DriveKind.Hdd, "HDD")]
    [InlineData(DriveKind.Ssd, "SSD")]
    [InlineData(DriveKind.NvmeSsd, "NVMe SSD")]
    [InlineData(DriveKind.Unknown, "Тип неизвестен")]
    public void Kind_labels(DriveKind kind, string expected)
        => Assert.Equal(expected, StorageLabels.KindLabel(kind));

    [Theory]
    [InlineData(11L, "SATA")]
    [InlineData(17L, "NVMe")]
    [InlineData(7L, "USB")]
    [InlineData(16L, "Дисковые пространства")]
    [InlineData(99L, "Неизвестно")]
    [InlineData(null, "Неизвестно")]
    public void Bus_labels(long? bus, string expected)
        => Assert.Equal(expected, StorageLabels.BusLabel(bus));

    [Theory]
    [InlineData(0L, DiskHealth.Healthy, "Исправен")]
    [InlineData(1L, DiskHealth.Warning, "Предупреждение")]
    [InlineData(2L, DiskHealth.Unhealthy, "Неисправен")]
    [InlineData(5L, DiskHealth.Unknown, "Нет данных")]
    [InlineData(null, DiskHealth.Unknown, "Нет данных")]
    public void Health_status(long? raw, DiskHealth health, string label)
    {
        Assert.Equal(health, StorageLabels.ToHealth(raw));
        Assert.Equal(label, StorageLabels.HealthLabel(health));
    }

    [Fact]
    public void Operational_status_array_from_wmi()
    {
        Assert.Equal("Работает нормально", StorageLabels.OperationalStatusLabel(new ushort[] { 2 }));
        Assert.True(StorageLabels.IsOperationalOk(new ushort[] { 2 }));
        Assert.Equal("Прогнозируется сбой, Ошибка", StorageLabels.OperationalStatusLabel(new ushort[] { 5, 6 }));
        Assert.False(StorageLabels.IsOperationalOk(new ushort[] { 5 }));
        Assert.Equal("Нет данных", StorageLabels.OperationalStatusLabel((object?)null));
        Assert.Equal("Код состояния 53264", StorageLabels.OperationalStatusLabel(new ushort[] { 0xD010 }));
    }

    [Theory]
    [InlineData(7200L, "7200 об/мин")]
    [InlineData(0L, null)]
    [InlineData(4294967295L, null)]
    public void Spindle_labels(long rpm, string? expected)
        => Assert.Equal(expected, StorageLabels.SpindleLabel(rpm));

    [Fact]
    public void Drive_letter_from_wmi_char16()
    {
        Assert.Equal('C', DiskService.ToLetter('C'));
        Assert.Equal('D', DiskService.ToLetter((ushort)'d'));
        Assert.Null(DiskService.ToLetter('\0'));
        Assert.Null(DiskService.ToLetter(null));
        Assert.Equal('E', DiskService.ToLetter("E"));
    }

    [Fact]
    public void Drives_are_mapped_to_physical_disks()
    {
        var drives = new List<DriveSummary>
        {
            new() { Letter = 'C', RootPath = @"C:\" },
            new() { Letter = 'A', RootPath = @"A:\" },
            new() { Letter = 'Z', RootPath = @"Z:\" },
        };
        var disks = new List<PhysicalDiskInfo>
        {
            new() { DeviceId = "0", Number = 0, Model = "ST6000", Kind = DriveKind.Hdd },
            new() { DeviceId = "1", Number = 1, Model = "Samsung SSD", Kind = DriveKind.Ssd },
        };
        var letters = new Dictionary<char, int> { ['A'] = 0, ['C'] = 1 };

        DiskService.Attach(drives, letters, disks);

        Assert.Equal("Samsung SSD", drives[0].Disk?.Model);
        Assert.Equal(DriveKind.Hdd, drives[1].Disk?.Kind);
        Assert.Null(drives[2].Disk);
    }
}
