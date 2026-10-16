"""Базовая проверка защиты: антивирус, брандмауэр, обновления, UAC."""
from __future__ import annotations

import json

from ..core import IS_WINDOWS, ModuleResult, powershell, run

WIN_PS = r"""
$ErrorActionPreference = 'SilentlyContinue'
$r = [ordered]@{}
$r.av = Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct | Select-Object displayName,productState
$r.defender = Get-MpComputerStatus | Select-Object RealTimeProtectionEnabled,AntivirusSignatureAge,QuickScanAge
$r.fw = Get-NetFirewallProfile | Select-Object Name,Enabled
$r.uac = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System').EnableLUA
$r.hotfix = (Get-HotFix | Sort-Object InstalledOn -Descending | Select-Object -First 1).InstalledOn
if ($r.hotfix) { $r.hotfix = $r.hotfix.ToString('yyyy-MM-dd') }
$r.smb1 = (Get-SmbServerConfiguration).EnableSMB1Protocol
$r.rdp = (Get-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Terminal Server').fDenyTSConnections
$r | ConvertTo-Json -Depth 3 -Compress
"""


def _list(x):
    return [] if x is None else x if isinstance(x, list) else [x]


def scan() -> ModuleResult:
    res = ModuleResult("security", "Защита системы")
    if not IS_WINDOWS:
        ufw = run(["ufw", "status"])
        res.summary["Брандмауэр (ufw)"] = ufw.splitlines()[0] if ufw else "не установлен/нет прав"
        if ufw and "inactive" in ufw:
            res.add("Безопасность", "Брандмауэр ufw выключен", severity="low",
                    recommendation="sudo ufw enable")
        return res

    out = powershell(WIN_PS, timeout=60)
    try:
        info = json.loads(out) if out else {}
    except json.JSONDecodeError:
        info = {}
    avs = _list(info.get("av"))
    res.summary["Антивирус"] = ", ".join(a.get("displayName", "?") for a in avs) or "не найден"
    if not avs:
        res.add("Безопасность", "Антивирус не обнаружен", severity="high",
                recommendation="Включите встроенный Microsoft Defender: Параметры → Безопасность Windows.")
    if len(avs) > 2:
        res.add("Безопасность", f"Установлено несколько антивирусов ({len(avs)})", severity="medium",
                detail=res.summary["Антивирус"],
                recommendation="Два антивируса конфликтуют и тормозят систему. Оставьте один.")
    d = info.get("defender") or {}
    if d.get("RealTimeProtectionEnabled") is False and len(avs) <= 1:
        res.add("Безопасность", "Защита Defender в реальном времени выключена", severity="high",
                recommendation="Безопасность Windows → Защита от вирусов → включите защиту в реальном времени.")
    if (d.get("AntivirusSignatureAge") or 0) > 7:
        res.add("Безопасность", f"Антивирусные базы устарели на {d['AntivirusSignatureAge']} дн.",
                severity="medium", recommendation="Запустите обновление Windows/Defender.")
    for p in _list(info.get("fw")):
        if p.get("Enabled") in (False, 0):
            res.add("Безопасность", f"Брандмауэр выключен для профиля {p.get('Name')}", severity="high",
                    recommendation="Включите брандмауэр: Безопасность Windows → Брандмауэр и защита сети.")
    if info.get("uac") == 0:
        res.add("Безопасность", "Контроль учётных записей (UAC) отключён", severity="high",
                recommendation="Любая программа получает права администратора без вопроса. Включите UAC.")
    if info.get("smb1"):
        res.add("Безопасность", "Включён устаревший протокол SMBv1", severity="high",
                recommendation="Отключите: PowerShell от админа → "
                               "Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol")
    if info.get("rdp") == 0:
        res.add("Безопасность", "Разрешён удалённый рабочий стол (RDP)", severity="low",
                recommendation="Если не пользуетесь — отключите: Параметры → Система → Удалённый рабочий стол.")
    res.summary["Последнее обновление"] = info.get("hotfix") or "?"
    if info.get("hotfix"):
        from datetime import datetime
        try:
            age = (datetime.now() - datetime.strptime(info["hotfix"], "%Y-%m-%d")).days
            if age > 45:
                res.add("Безопасность", f"Обновления Windows не ставились {age} дн.", severity="medium",
                        recommendation="Параметры → Центр обновления Windows → Проверить наличие обновлений.")
        except ValueError:
            pass
    return res
