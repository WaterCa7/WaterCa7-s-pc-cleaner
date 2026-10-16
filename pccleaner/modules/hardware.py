"""Железо и прошивка: CPU, RAM, диски, GPU, батарея, температуры, BIOS/UEFI, Secure Boot, TPM."""
from __future__ import annotations

import json
import os
import platform
import re
from datetime import datetime
from pathlib import Path

import psutil

from ..core import IS_LINUX, IS_WINDOWS, ModuleResult, human_size, powershell, read_text, run

# Один вызов PowerShell, возвращающий всё нужное в JSON — так быстрее, чем много wmic.
WIN_PS = r"""
$ErrorActionPreference = 'SilentlyContinue'
$r = [ordered]@{}
$r.cpu   = Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,VirtualizationFirmwareEnabled
$r.ram   = Get-CimInstance Win32_PhysicalMemory | Select-Object Capacity,Speed,ConfiguredClockSpeed,Manufacturer,PartNumber,SMBIOSMemoryType,DeviceLocator
$r.slots = (Get-CimInstance Win32_PhysicalMemoryArray | Measure-Object -Property MemoryDevices -Sum).Sum
$r.gpu   = Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,@{n='DriverDate';e={$_.DriverDate.ToString('yyyy-MM-dd')}},AdapterRAM
$r.disks = Get-PhysicalDisk | Select-Object FriendlyName,MediaType,BusType,HealthStatus,Size
$r.bios  = Get-CimInstance Win32_BIOS | Select-Object Manufacturer,SMBIOSBIOSVersion,@{n='ReleaseDate';e={$_.ReleaseDate.ToString('yyyy-MM-dd')}}
$r.board = Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer,Product
$r.system = Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer,Model
$r.os    = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,@{n='LastBoot';e={$_.LastBootUpTime.ToString('yyyy-MM-dd HH:mm')}}
$r.fwtype = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control').PEFirmwareType
$r.secureboot = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State').UEFISecureBootEnabled
$r.tpm   = Get-CimInstance -Namespace root\cimv2\security\microsofttpm -ClassName Win32_Tpm | Select-Object IsEnabled_InitialValue,IsActivated_InitialValue,SpecVersion
$r.fastboot = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power').HiberbootEnabled
$r.power = (powercfg /getactivescheme) -join ''
$r.hvci  = (Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard).VirtualizationBasedSecurityStatus
$r.gamemode = (Get-ItemProperty 'HKCU:\Software\Microsoft\GameBar').AutoGameModeEnabled
$r.hags = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers').HwSchMode
$r.bitlocker = (Get-BitLockerVolume -MountPoint $env:SystemDrive).ProtectionStatus
$r.pagefile = Get-CimInstance Win32_PageFileUsage | Select-Object Name,AllocatedBaseSize,CurrentUsage
$r.battery = Get-CimInstance -Namespace root\wmi -ClassName BatteryFullChargedCapacity | Select-Object FullChargedCapacity
$r.batdesign = Get-CimInstance -Namespace root\wmi -ClassName BatteryStaticData | Select-Object DesignedCapacity
$r.thermal = Get-CimInstance -Namespace root\wmi -ClassName MSAcpi_ThermalZoneTemperature | Select-Object CurrentTemperature
$r | ConvertTo-Json -Depth 4 -Compress
"""

DDR_TYPES = {20: "DDR", 21: "DDR2", 24: "DDR3", 26: "DDR4", 34: "DDR5"}
# Базовые частоты JEDEC, на которых память работает без включённого XMP/EXPO.
JEDEC_DEFAULTS = {"DDR4": (2133, 2400, 2666), "DDR5": (4800,)}


def _as_list(x):
    if x is None:
        return []
    return x if isinstance(x, list) else [x]


def _win_info() -> dict:
    out = powershell(WIN_PS, timeout=60)
    try:
        return json.loads(out) if out else {}
    except json.JSONDecodeError:
        return {}


def _linux_info() -> dict:
    dmi = Path("/sys/class/dmi/id")
    info: dict = {
        "bios": {"Manufacturer": read_text(str(dmi / "bios_vendor")),
                 "SMBIOSBIOSVersion": read_text(str(dmi / "bios_version")),
                 "ReleaseDate": read_text(str(dmi / "bios_date"))},
        "board": {"Manufacturer": read_text(str(dmi / "board_vendor")),
                  "Product": read_text(str(dmi / "board_name"))},
        "system": {"Manufacturer": read_text(str(dmi / "sys_vendor")),
                   "Model": read_text(str(dmi / "product_name"))},
        "fwtype": 2 if Path("/sys/firmware/efi").exists() else 1,
    }
    sb = run(["mokutil", "--sb-state"])
    info["secureboot"] = 1 if "enabled" in sb.lower() else (0 if sb else None)
    tpm = Path("/sys/class/tpm/tpm0")
    if tpm.exists():
        major = read_text(str(tpm / "tpm_version_major"))
        info["tpm"] = {"IsEnabled_InitialValue": True, "SpecVersion": major or "?"}
    cpuinfo = read_text("/proc/cpuinfo")
    m = re.search(r"model name\s*:\s*(.+)", cpuinfo)
    info["cpu"] = {"Name": m.group(1) if m else platform.processor(),
                   "VirtualizationFirmwareEnabled": bool(re.search(r"\b(vmx|svm)\b", cpuinfo))}
    disks = []
    blk = Path("/sys/block")
    if blk.is_dir():
        for d in blk.iterdir():
            if d.name.startswith(("loop", "ram", "zram", "dm-")):
                continue
            rot = read_text(str(d / "queue/rotational"))
            sectors = read_text(str(d / "size"))
            disks.append({"FriendlyName": read_text(str(d / "device/model")) or d.name,
                          "MediaType": "HDD" if rot == "1" else "SSD",
                          "Size": int(sectors) * 512 if sectors.isdigit() else 0})
    info["disks"] = disks
    gpus = run(["lspci"]).splitlines()
    info["gpu"] = [{"Name": g.split(": ", 1)[-1]} for g in gpus if re.search(r"VGA|3D controller", g)]
    gov = read_text("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor")
    info["power"] = f"governor: {gov}" if gov else ""
    return info


def _parse_date(s: str) -> datetime | None:
    for fmt in ("%Y-%m-%d", "%m/%d/%Y", "%Y%m%d"):
        try:
            return datetime.strptime(s.strip()[:10], fmt)
        except (ValueError, AttributeError):
            continue
    return None


def scan() -> ModuleResult:
    res = ModuleResult("hardware", "Железо и BIOS/UEFI")
    info = _win_info() if IS_WINDOWS else _linux_info() if IS_LINUX else {}
    vm = psutil.virtual_memory()
    swap = psutil.swap_memory()
    cpu = (_as_list(info.get("cpu")) or [{}])[0]

    s: dict = {
        "ОС": f"{platform.system()} {platform.release()} ({platform.version()})",
        "Компьютер": " ".join(filter(None, [(info.get("system") or {}).get("Manufacturer"),
                                            (info.get("system") or {}).get("Model")])),
        "Материнская плата": " ".join(filter(None, [(info.get("board") or {}).get("Manufacturer"),
                                                    (info.get("board") or {}).get("Product")])),
        "Процессор": cpu.get("Name") or platform.processor(),
        "Ядер / потоков": f"{psutil.cpu_count(False)} / {psutil.cpu_count()}",
        "Оперативная память": human_size(vm.total),
        "Файл подкачки/swap": f"{human_size(swap.used)} из {human_size(swap.total)}",
    }
    freq = psutil.cpu_freq()
    if freq:
        s["Частота CPU"] = f"{freq.current:.0f} МГц" + (f" (макс. {freq.max:.0f})" if freq.max else "")
    bios = info.get("bios") or {}
    if bios.get("SMBIOSBIOSVersion") or bios.get("Manufacturer"):
        s["BIOS"] = f"{bios.get('Manufacturer', '')} {bios.get('SMBIOSBIOSVersion', '')} от {bios.get('ReleaseDate', '?')}"
    fw = info.get("fwtype")
    s["Режим загрузки"] = {1: "Legacy BIOS", 2: "UEFI"}.get(fw, "неизвестно")
    sb = info.get("secureboot")
    s["Secure Boot"] = {1: "включён", 0: "выключен"}.get(sb, "неизвестно")
    tpm = info.get("tpm")
    s["TPM"] = f"есть (версия {tpm.get('SpecVersion', '?').split(',')[0]})" if tpm else "не найден/нет доступа"
    s["Виртуализация (VT-x/AMD-V)"] = {True: "включена", False: "выключена"}.get(
        cpu.get("VirtualizationFirmwareEnabled"), "неизвестно")
    res.summary = s

    # --- BIOS / прошивка -----------------------------------------------------
    d = _parse_date(bios.get("ReleaseDate", "")) if bios else None
    if d and (datetime.now() - d).days > 2 * 365:
        res.add("BIOS", f"BIOS не обновлялся с {d:%Y-%m-%d}", severity="low",
                detail=f"Версия {bios.get('SMBIOSBIOSVersion', '?')}.",
                recommendation="Скачайте свежую прошивку с сайта производителя платы/ноутбука: "
                               "обновления закрывают уязвимости процессора и улучшают совместимость с памятью. "
                               "Не прерывайте питание во время прошивки.")
    if fw == 1:
        res.add("BIOS", "Система загружается в режиме Legacy BIOS", severity="medium",
                recommendation="UEFI быстрее загружается и нужен для Secure Boot и Windows 11. "
                               "Конвертируйте диск в GPT (mbr2gpt /convert) и включите UEFI в BIOS.")
    if sb == 0:
        res.add("BIOS", "Secure Boot выключен", severity="medium",
                recommendation="Включите Secure Boot в настройках BIOS (раздел Boot/Security) — "
                               "защищает от буткитов. Нужен для Windows 11 и античитов некоторых игр.")
    if not tpm and IS_WINDOWS:
        res.add("BIOS", "TPM не обнаружен", severity="low",
                recommendation="Включите fTPM (AMD) или PTT (Intel) в BIOS — нужен для Windows 11 и BitLocker. "
                               "(Если запускали без прав администратора, данные могут быть неполными.)")
    if cpu.get("VirtualizationFirmwareEnabled") is False:
        res.add("BIOS", "Аппаратная виртуализация выключена", severity="info",
                recommendation="Включите Intel VT-x / AMD SVM в BIOS, если пользуетесь WSL, Docker, "
                               "эмуляторами Android или виртуальными машинами.")

    # --- Память -------------------------------------------------------------
    sticks = _as_list(info.get("ram"))
    if sticks:
        kind = DDR_TYPES.get(sticks[0].get("SMBIOSMemoryType"), "")
        speeds = [x.get("ConfiguredClockSpeed") or x.get("Speed") or 0 for x in sticks]
        s["Модули RAM"] = ", ".join(
            f"{human_size(int(x.get('Capacity') or 0))} {kind} {sp} МГц" for x, sp in zip(sticks, speeds))
        if kind in JEDEC_DEFAULTS and speeds and max(speeds) in JEDEC_DEFAULTS[kind]:
            res.add("Железо", f"Память {kind} работает на базовой частоте {max(speeds)} МГц", severity="medium",
                    detail="Большинство модулей рассчитаны на большую частоту через профиль XMP/EXPO.",
                    recommendation="Зайдите в BIOS и включите XMP (Intel) или EXPO/DOCP (AMD). "
                                   "Это бесплатно даёт +5–15% в играх и тяжёлых задачах.")
        if len(sticks) == 1 and (info.get("slots") or 0) >= 2:
            res.add("Железо", "Установлен один модуль памяти (одноканальный режим)", severity="medium",
                    recommendation="Добавьте второй такой же модуль в слот по инструкции к плате "
                                   "(обычно A2+B2) — двухканальный режим почти удваивает пропускную способность.")
    if vm.total < 8 * 1024 ** 3:
        res.add("Железо", f"Мало оперативной памяти: {human_size(vm.total)}", severity="medium",
                recommendation="Для современной Windows и браузера рекомендуем от 16 ГБ.")
    if vm.percent > 85:
        res.add("Железо", f"Память занята на {vm.percent:.0f}%", severity="medium",
                recommendation="Закройте тяжёлые программы, проверьте автозагрузку; при постоянной нехватке — докупите RAM.")
    if swap.total and swap.percent > 50:
        res.add("Железо", f"Активно используется файл подкачки ({swap.percent:.0f}%)", severity="low",
                recommendation="Признак нехватки RAM. Убедитесь, что файл подкачки лежит на SSD.")

    # --- Диски --------------------------------------------------------------
    disks = _as_list(info.get("disks"))
    s["Диски"] = ", ".join(f"{x.get('FriendlyName', '?')} ({x.get('MediaType', '?')}, "
                           f"{human_size(int(x.get('Size') or 0))})" for x in disks)
    for x in disks:
        health = str(x.get("HealthStatus", "")).lower()
        if health and health not in ("healthy", "0"):
            res.add("Диски", f"Диск {x.get('FriendlyName')} — состояние: {x.get('HealthStatus')}",
                    severity="high", recommendation="Срочно сделайте резервную копию важных данных! "
                                                    "Проверьте SMART в CrystalDiskInfo и замените диск.")
    media = {str(x.get("MediaType")) for x in disks}
    if media & {"HDD", "3"} and not media & {"SSD", "4"}:
        res.add("Диски", "Система работает на HDD", severity="medium",
                recommendation="Замена на SSD — самый заметный апгрейд: загрузка Windows в 3–5 раз быстрее.")
    for part in psutil.disk_partitions(all=False):
        if "cdrom" in part.opts or not part.fstype or part.mountpoint.startswith(("/snap", "/boot/efi")):
            continue
        try:
            u = psutil.disk_usage(part.mountpoint)
        except OSError:
            continue
        if u.total > 5 * 1024 ** 3 and u.percent > 85:
            res.add("Диски", f"На {part.mountpoint} занято {u.percent:.0f}% ({human_size(u.free)} свободно)",
                    severity="high" if u.percent > 95 else "medium",
                    recommendation="Держите свободными 15–20% — иначе SSD замедляется, а Windows не может обновиться. "
                                   "Запустите очистку мусора и поищите крупные файлы.")

    # --- Видеокарта ---------------------------------------------------------
    gpus = _as_list(info.get("gpu"))
    s["Видеокарта"] = ", ".join(g.get("Name", "?") for g in gpus)
    for g in gpus:
        dd = _parse_date(g.get("DriverDate") or "")
        name = g.get("Name", "")
        if dd and (datetime.now() - dd).days > 365 and re.search(r"nvidia|amd|radeon|geforce|intel", name, re.I):
            res.add("Драйверы", f"Драйвер видеокарты {name} от {dd:%Y-%m-%d}", severity="low",
                    recommendation="Обновите драйвер с сайта NVIDIA/AMD/Intel — новые версии ускоряют игры и чинят ошибки.")
        if "microsoft basic display" in name.lower():
            res.add("Драйверы", "Не установлен драйвер видеокарты", severity="high",
                    recommendation="Используется стандартный драйвер Microsoft. Установите драйвер производителя.")

    # --- Температуры, батарея, электропитание --------------------------------
    temps = []
    if hasattr(psutil, "sensors_temperatures"):
        try:
            for chip, entries in (psutil.sensors_temperatures() or {}).items():
                temps += [(f"{chip}/{e.label or 'temp'}", e.current) for e in entries if e.current]
        except Exception:  # noqa: BLE001
            pass
    for t in _as_list(info.get("thermal")):
        k = t.get("CurrentTemperature")
        if k:
            temps.append(("ACPI", k / 10 - 273.15))
    if temps:
        hottest = max(temps, key=lambda x: x[1])
        s["Макс. температура"] = f"{hottest[1]:.0f}°C ({hottest[0]})"
        if hottest[1] >= 85:
            res.add("Железо", f"Высокая температура: {hottest[1]:.0f}°C", severity="high",
                    recommendation="Почистите систему охлаждения от пыли, замените термопасту, "
                                   "проверьте вентиляторы. Перегрев снижает частоты (троттлинг).")
    bat = psutil.sensors_battery() if hasattr(psutil, "sensors_battery") else None
    if bat:
        s["Батарея"] = f"{bat.percent:.0f}%{' (на зарядке)' if bat.power_plugged else ''}"
        full = (_as_list(info.get("battery")) or [{}])[0].get("FullChargedCapacity")
        design = (_as_list(info.get("batdesign")) or [{}])[0].get("DesignedCapacity")
        if full and design:
            wear = 1 - full / design
            s["Износ батареи"] = f"{wear:.0%}"
            if wear > 0.3:
                res.add("Железо", f"Батарея изношена на {wear:.0%}", severity="low",
                        recommendation="Подробнее: powercfg /batteryreport. При износе >40% батарею стоит заменить.")
    power = info.get("power") or ""
    if power:
        s["Электропитание"] = re.sub(r"^.*?:\s*", "", power)
    if IS_WINDOWS and re.search(r"power saver|экономия энергии|энергосбережение", power, re.I) and not bat:
        res.add("Оптимизация", "Включён режим энергосбережения на настольном ПК", severity="medium",
                recommendation="Панель управления → Электропитание → «Сбалансированная» или «Высокая производительность».")
    if IS_LINUX and "powersave" in power and not bat:
        res.add("Оптимизация", "CPU governor = powersave на настольном ПК", severity="low",
                recommendation="Для производительности: sudo cpupower frequency-set -g performance (или schedutil).")

    # --- Настройки Windows, влияющие на скорость и безопасность --------------
    if IS_WINDOWS:
        if info.get("fastboot") == 1:
            res.add("Оптимизация", "Включён «Быстрый запуск» Windows", severity="info",
                    detail="Компьютер не выключается полностью — бывают проблемы с драйверами, обновлениями и dual-boot.",
                    recommendation="Если ПК работает нестабильно после включения, отключите: "
                                   "Электропитание → Действия кнопок питания → «Включить быстрый запуск».")
        if info.get("gamemode") == 0:
            res.add("Оптимизация", "Игровой режим Windows выключен", severity="info",
                    recommendation="Параметры → Игры → Игровой режим: включите, если играете.")
        if info.get("hags") == 1:
            res.add("Оптимизация", "Аппаратное планирование GPU (HAGS) выключено", severity="info",
                    recommendation="Параметры → Дисплей → Графика → «Планирование GPU с аппаратным ускорением». "
                                   "Может снизить задержки на современных видеокартах.")
        if info.get("bitlocker") == 0 and bat:
            res.add("Безопасность", "Системный диск ноутбука не зашифрован", severity="low",
                    recommendation="Включите BitLocker/«Шифрование устройства» — защитит данные при краже ноутбука.")
        os_info = info.get("os") or {}
        if os_info.get("LastBoot"):
            s["Последняя перезагрузка"] = os_info["LastBoot"]
            lb = _parse_date(os_info["LastBoot"])
            if lb and (datetime.now() - lb).days > 14:
                res.add("Оптимизация", f"Компьютер не перезагружался с {lb:%Y-%m-%d}", severity="low",
                        recommendation="Перезагрузка применяет обновления и освобождает утёкшую память.")
    elif IS_LINUX:
        boot = datetime.fromtimestamp(psutil.boot_time())
        s["Последняя перезагрузка"] = f"{boot:%Y-%m-%d %H:%M}"

    if os.cpu_count() and psutil.cpu_percent(interval=0.5) > 90:
        res.add("Железо", "Процессор загружен более чем на 90%", severity="medium",
                recommendation="Посмотрите раздел «Процессы» — какая программа нагружает CPU.")
    return res
