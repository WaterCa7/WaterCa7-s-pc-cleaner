using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Profile;

// «Мои компьютеры»: the list of GET /devices and «Отвязать».
public sealed partial class ProfileViewModel
{
    private string? _devicesText;

    // Whose sign-in the list was last asked for (null: nobody). A change from null / another account to a signed-in one loads the list,
    // so a sign-in made from the guest card while the page is open fills it, and repeated Changed events do not reload it.
    private string? _devicesOwner;

    public ObservableCollection<DeviceItem> Devices { get; } = new();

    /// <summary>«Компьютеров: N из M», or why the list could not be loaded / a computer could not be unbound.</summary>
    public string? DevicesText { get => _devicesText; private set => Set(ref _devicesText, value); }

    public AsyncRelayCommand UnbindCommand { get; private set; } = null!;

    private void InitDevices()
    {
        _devicesOwner = IsSignedIn ? Email : null; // an account that is already signed in loads its list on navigation
        UnbindCommand = new AsyncRelayCommand(p => p is DeviceItem d ? UnbindAsync(d) : Task.CompletedTask, p => !IsBusy && p is DeviceItem);
    }

    /// <summary>Called by <see cref="Refresh"/>: forgets the list on sign-out and starts loading it after a sign-in.</summary>
    private void SyncDevicesWithAccount()
    {
        if (!IsSignedIn)
        {
            _devicesOwner = null;
            DevicesText = null;
            Devices.Clear();
            return;
        }
        var owner = Email ?? "";
        if (_devicesOwner == owner)
            return;
        _devicesOwner = owner;
        Devices.Clear();
        _ = LoadDevicesAsync();
    }

    public async Task LoadDevicesAsync()
    {
        if (!IsSignedIn)
            return;
        try
        {
            var list = await _account.DevicesAsync(CancellationToken.None);
            if (!IsSignedIn)
                return; // signed out while the server was answering
            Devices.Clear();
            foreach (var d in list.Devices)
                Devices.Add(new DeviceItem(d.Id, d.Name, ProfileTexts.DeviceDetails(d, _now()), d.Current, d.Whose));
            DevicesText = T.F("Компьютеров: {0} из {1}", list.Devices.Count, list.BindLimit);
        }
        catch (ApiException ex)
        {
            DevicesText = SignInErrors.Text(ex.Error);
            Refresh(); // TOKEN_REVOKED signed out
        }
    }

    internal Task UnbindForTestAsync(DeviceItem device) => UnbindAsync(device);

    private async Task UnbindAsync(DeviceItem device)
    {
        var question = device.IsCurrent
            ? T._("Отвязать этот компьютер? Вы выйдете из аккаунта, программа перейдёт в бесплатный режим.")
            : T.F("Отвязать компьютер «{0}»? На нём программа перейдёт в бесплатный режим. Отвязывать можно раз в 7 дней.", device.Name);
        if (!_confirm(T._("Отвязать компьютер"), question))
            return;
        try
        {
            await _account.UnbindAsync(device.IsCurrent ? null : device.Id, CancellationToken.None);
        }
        catch (ApiException ex)
        {
            DevicesText = SignInErrors.Text(ex.Error);
            Refresh();
            return;
        }
        Refresh();
        await LoadDevicesAsync();
    }
}
