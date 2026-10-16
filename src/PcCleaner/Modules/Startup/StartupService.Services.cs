using System.ComponentModel;
using System.IO;
using System.Security;
using System.ServiceProcess;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Startup;

// Auto-start Win32 services (kernel drivers are out of scope).
public sealed partial class StartupService
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const int StartAuto = 2;
    private const int StartManual = 3;

    internal static string ServiceSettingKey(string serviceName) => "svc:" + serviceName;

    /// <summary>Only "auto" and "delayed-auto" are ever restored (guards against a tampered settings file).</summary>
    internal static string NormalizeAutoMode(string? mode)
        => string.Equals(mode, "delayed-auto", StringComparison.OrdinalIgnoreCase) ? "delayed-auto" : "auto";

    /// <summary>Auto-start services, plus services this app switched to manual (so they can be switched back).</summary>
    internal List<StartupItem> ScanServices(CancellationToken ct)
    {
        var list = new List<StartupItem>();
        ServiceController[] services;
        try
        {
            services = ServiceController.GetServices();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            Log.Warn($"Автозагрузка: не удалось получить список служб: {ex.Message}");
            return list;
        }

        RegistryKey? servicesKey = null;
        try
        {
            servicesKey = OpenKey(RegistryHive.LocalMachine, ServicesKey, false);
            if (servicesKey is null)
                return list;
            var settings = AppServices.Settings;
            foreach (var service in services)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var item = ReadService(service, servicesKey, settings);
                    if (item is not null)
                        list.Add(item);
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
                {
                    // one unreadable service must not break the list
                }
                finally
                {
                    service.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Автозагрузка: нет доступа к списку служб в реестре: {ex.Message}");
        }
        finally
        {
            servicesKey?.Dispose();
            foreach (var service in services)
                service.Dispose();
        }
        return list;
    }

    private static StartupItem? ReadService(ServiceController service, RegistryKey servicesKey, AppSettings settings)
    {
        var name = service.ServiceName;
        using var key = servicesKey.OpenSubKey(name);
        if (key is null)
            return null;

        var start = key.GetValue("Start") is int s ? s : -1;
        var remembered = settings.GetModuleValue(ModuleKey, ServiceSettingKey(name));
        if (start != StartAuto && !(start == StartManual && remembered is not null))
            return null;
        var type = key.GetValue("Type") is int t ? t : 0;
        if ((type & 0x30) == 0)
            return null; // not a Win32 service

        var delayed = key.GetValue("DelayedAutostart") is int d && d == 1;
        var (imagePath, target) = StartupTargets.ServiceTarget(key);

        string displayName;
        string status;
        try
        {
            displayName = string.IsNullOrWhiteSpace(service.DisplayName) ? name : service.DisplayName;
            status = service.Status switch
            {
                ServiceControllerStatus.Running => "Работает",
                ServiceControllerStatus.Stopped => "Остановлена",
                ServiceControllerStatus.Paused => "Приостановлена",
                _ => "Запускается или останавливается",
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            displayName = name;
            status = "Неизвестно";
        }

        var description = StartupNative.ResolveIndirectString(key.GetValue("Description") as string);
        var mode = start == StartAuto ? (delayed ? "delayed-auto" : "auto") : NormalizeAutoMode(remembered);
        return new StartupItem
        {
            Id = $"svc|{name}",
            Kind = StartupKind.Service,
            Scope = StartupScope.AllUsers,
            Name = displayName,
            Command = imagePath,
            TargetFile = target,
            LocationText = $@"HKLM\{ServicesKey}\{name}",
            ServiceName = name,
            ServiceStatusText = status,
            ServiceStartMode = mode,
            Description = description,
            IsEnabled = start == StartAuto,
        };
    }

    private async Task<StartupActionResult> SetServiceEnabledAsync(StartupItem item, bool enable, CancellationToken ct)
    {
        var name = item.ServiceName;
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0)
            return StartupActionResult.Fail("Некорректное имя службы.");
        if (item.IsProtected)
            return StartupActionResult.Fail(item.ToggleBlockedReason);
        if (!_env.IsElevated())
            return StartupActionResult.Admin("Тип запуска службы можно изменить только с правами администратора.");

        var settings = AppServices.Settings;
        var settingKey = ServiceSettingKey(name);
        var previous = NormalizeAutoMode(item.ServiceStartMode);
        var mode = enable ? NormalizeAutoMode(settings.GetModuleValue(ModuleKey, settingKey) ?? previous) : "demand";

        var sc = Path.Combine(System.Environment.SystemDirectory, "sc.exe");
        var result = await ProcessRunner.RunAsync(sc, $"config \"{name}\" start= {mode}", TimeSpan.FromSeconds(30), ct);
        if (result.ExitCode == 5)
            return StartupActionResult.Admin("Windows не разрешила изменить службу: нужны права администратора.");
        if (!result.Success)
        {
            var text = (result.StdOut + " " + result.StdErr).Trim();
            Log.Warn($"Автозагрузка: sc.exe config {name} start= {mode} -> код {result.ExitCode}: {text}");
            return StartupActionResult.Fail($"Не удалось изменить тип запуска службы (код {result.ExitCode}).");
        }

        if (enable)
            settings.SetModuleValue(ModuleKey, settingKey, null);
        else
            settings.SetModuleValue(ModuleKey, settingKey, previous);
        item.IsEnabled = enable;

        var modeText = mode == "delayed-auto" ? "автоматически (отложенный запуск)" : "автоматически";
        Log.Action(enable
            ? $"Автозагрузка: службе «{item.DisplayName}» ({name}) возвращён тип запуска «{modeText}»."
            : $"Автозагрузка: служба «{item.DisplayName}» ({name}) переведена в ручной режим запуска (было: {previous}).");
        return StartupActionResult.Ok(enable
            ? $"Служба «{item.DisplayName}» снова запускается {modeText}."
            : $"Служба «{item.DisplayName}» переведена в ручной режим: она больше не стартует вместе с Windows, " +
              "но программы смогут запустить её при необходимости. Кнопка «Включить» вернёт прежний режим.");
    }
}
