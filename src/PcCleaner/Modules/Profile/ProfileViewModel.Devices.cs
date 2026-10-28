using System.Collections.ObjectModel;

namespace PcCleaner.Modules.Profile;

// Placeholder so that Профиль builds before «Мои компьютеры»: task 8 replaces this file with the real list and unbinding.
public sealed partial class ProfileViewModel
{
    public ObservableCollection<DeviceItem> Devices { get; } = new();

    private void InitDevices()
    {
    }

    public Task LoadDevicesAsync() => Task.CompletedTask;
}
