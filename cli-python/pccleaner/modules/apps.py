"""Установленные программы, автозагрузка, «хвосты» удалённых приложений, нежелательный софт."""
from __future__ import annotations

import os
import re
from pathlib import Path

from ..core import IS_WINDOWS, ModuleResult, human_size, powershell, run

# Потенциально нежелательные программы (PUP): рекламное ПО, «оптимизаторы»,
# навязанные тулбары. Список можно пополнять.
PUP_PATTERNS = {
    r"\bmail\.ru\b|спутник|guard mail": "навязанный софт Mail.ru",
    r"amigo|амиго": "браузер Amigo (навязанный)",
    r"\bask toolbar|ask\.com": "рекламный тулбар Ask",
    r"\bbaidu\b": "софт Baidu (часто ставится без спроса)",
    r"driver ?pack|driverpack": "DriverPack (ставит лишние программы)",
    r"reimage|restoro|fortect": "«ремонтник» с агрессивной рекламой",
    r"pc ?cleaner pro|advanced systemcare|wise care|auslogics boostspeed|reg ?clean": "сомнительный «ускоритель»",
    r"mcafee webadvisor|avast secure browser|avg secure browser": "часто устанавливается в довесок",
    r"opera gx|yandex\b.*browser|яндекс\.браузер": "дополнительный браузер — удалите, если не пользуетесь",
    r"\bwave browser|onelaunch|shift browser": "рекламный браузер (PUP)",
    r"\bsearch ?protect|conduit|babylon|mywebsearch": "угонщик поисковой страницы",
    r"\butorrent\b": "uTorrent содержит рекламу — замените на qBittorrent",
    r"\bcandy crush|bubble witch|disney magic|march of empires": "предустановленная игра Windows",
}

SAFE_STARTUP_HINTS = ("securityhealth", "windows defender", "realtek", "onedrive", "ctfmon")


def match_pup(name: str) -> str | None:
    lname = name.lower()
    for pat, why in PUP_PATTERNS.items():
        if re.search(pat, lname):
            return why
    return None


# ---------------------------------------------------------------- Windows ----

def _win_installed() -> list[dict]:
    import winreg  # type: ignore

    apps: dict[str, dict] = {}
    roots = [
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    ]
    for hive, path in roots:
        try:
            key = winreg.OpenKey(hive, path)
        except OSError:
            continue
        for i in range(winreg.QueryInfoKey(key)[0]):
            try:
                sub = winreg.OpenKey(key, winreg.EnumKey(key, i))
            except OSError:
                continue

            def val(n: str, _sub=sub):
                try:
                    return winreg.QueryValueEx(_sub, n)[0]
                except OSError:
                    return None

            name = val("DisplayName")
            if not name or val("SystemComponent") == 1 or val("ParentKeyName"):
                continue
            apps[name] = {
                "name": name,
                "version": val("DisplayVersion") or "",
                "publisher": val("Publisher") or "",
                "install_date": val("InstallDate") or "",
                "size": (val("EstimatedSize") or 0) * 1024,
                "location": val("InstallLocation") or "",
                "uninstall": val("QuietUninstallString") or val("UninstallString") or "",
            }
    return sorted(apps.values(), key=lambda a: a["name"].lower())


def _win_startup() -> list[dict]:
    import winreg  # type: ignore

    items = []
    keys = [
        (winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU\\Run"),
        (winreg.HKEY_LOCAL_MACHINE, r"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM\\Run"),
        (winreg.HKEY_LOCAL_MACHINE, r"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM\\Run32"),
        (winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU\\RunOnce"),
        (winreg.HKEY_LOCAL_MACHINE, r"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM\\RunOnce"),
    ]
    for hive, path, label in keys:
        try:
            key = winreg.OpenKey(hive, path)
        except OSError:
            continue
        for i in range(winreg.QueryInfoKey(key)[1]):
            try:
                name, cmd, _ = winreg.EnumValue(key, i)
                items.append({"name": name, "command": str(cmd), "source": label})
            except OSError:
                continue
    folders = [
        Path(os.environ.get("APPDATA", "")) / "Microsoft/Windows/Start Menu/Programs/Startup",
        Path(os.environ.get("PROGRAMDATA", "")) / "Microsoft/Windows/Start Menu/Programs/StartUp",
    ]
    for f in folders:
        if f.is_dir():
            for p in f.iterdir():
                if p.name.lower() != "desktop.ini":
                    items.append({"name": p.stem, "command": str(p), "source": "Папка автозагрузки"})

    # Сторонние задачи планировщика, запускающиеся при входе/старте.
    out = powershell(
        "Get-ScheduledTask | Where-Object {$_.TaskPath -notlike '\\Microsoft\\*' -and $_.State -ne 'Disabled'} | "
        "ForEach-Object { $_.TaskName + '|' + (($_.Actions | ForEach-Object {$_.Execute}) -join ' ') }"
    )
    for line in out.splitlines():
        if "|" in line:
            name, cmd = line.split("|", 1)
            items.append({"name": name.strip(), "command": cmd.strip(), "source": "Планировщик задач"})
    return items


# ------------------------------------------------------------------ Linux ----

def _linux_installed() -> list[dict]:
    out = run(["dpkg-query", "-W", "-f", "${Package}\t${Version}\t${Installed-Size}\n"])
    apps = []
    if out:
        for line in out.splitlines():
            parts = line.split("\t")
            if len(parts) == 3:
                size = int(parts[2]) * 1024 if parts[2].isdigit() else 0
                apps.append({"name": parts[0], "version": parts[1], "size": size,
                             "publisher": "", "install_date": "", "location": "", "uninstall": ""})
        return apps
    out = run(["rpm", "-qa", "--qf", "%{NAME}\t%{VERSION}\t%{SIZE}\n"])
    for line in out.splitlines():
        parts = line.split("\t")
        if len(parts) == 3:
            apps.append({"name": parts[0], "version": parts[1],
                         "size": int(parts[2]) if parts[2].isdigit() else 0,
                         "publisher": "", "install_date": "", "location": "", "uninstall": ""})
    return apps


def _linux_startup() -> list[dict]:
    items = []
    for d in (Path.home() / ".config/autostart", Path("/etc/xdg/autostart")):
        if d.is_dir():
            for p in d.glob("*.desktop"):
                txt = p.read_text(errors="replace")
                m = re.search(r"^Exec=(.*)$", txt, re.M)
                if "Hidden=true" in txt:
                    continue
                items.append({"name": p.stem, "command": m.group(1) if m else "",
                              "source": str(d)})
    out = run(["systemctl", "--user", "list-unit-files", "--state=enabled", "--no-legend"])
    for line in out.splitlines():
        if line.strip():
            items.append({"name": line.split()[0], "command": "", "source": "systemd --user"})
    return items


# --------------------------------------------------------------- leftovers ---

def leftover_dirs(installed: list[dict]) -> list[dict]:
    """Папки в AppData/Program Files, которым не соответствует ни одна установленная программа."""
    if not IS_WINDOWS:
        return []
    names = " ".join((a["name"] + " " + a["publisher"] + " " + a["location"]).lower() for a in installed)
    ignore = {"microsoft", "windows", "packages", "temp", "programs", "common files",
              "windowsapps", "internet explorer", "windows defender", "windows nt",
              "modifiableswindowsapps", "crashdumps", "d3dscache", "connecteddevicesplatform",
              "comms", "publishers", "virtualstore", "history", "uninstall information",
              "reference assemblies", "msbuild", "dotnet", "package cache", "nvidia corporation",
              "intel", "amd", "google", "mozilla", "nvidia", "squirreltemp", "fontcache",
              "windowspowershell", "wsl", "powershell", "microsoft.net", "windows kits",
              "windows sidebar", "windows mail", "windows media player", "windows photo viewer",
              "windows portable devices", "windows security", "microsoft update health tools",
              "application verifier", "iis", "iis express", "msecache", "dell", "hp", "lenovo",
              "asus", "msi", "realtek", "packagemanagement", "github", "pip", "programs"}
    roots = [os.environ.get(v, "") for v in ("PROGRAMFILES", "PROGRAMFILES(X86)", "LOCALAPPDATA", "APPDATA")]
    out = []
    for r in filter(None, roots):
        root = Path(r)
        if not root.is_dir():
            continue
        for d in root.iterdir():
            n = d.name.lower()
            if not d.is_dir() or n in ignore or n.startswith((".", "{", "microsoft", "windows")):
                continue
            token = re.split(r"[\s_\-.]+", n)[0]
            if len(token) >= 3 and token not in names:
                out.append({"path": str(d)})
    return out


def dir_size(path: Path, cap: int = 20000) -> int:
    total = 0
    for i, (dp, _, files) in enumerate(os.walk(path)):
        if i > cap:
            break
        for f in files:
            try:
                total += os.path.getsize(os.path.join(dp, f))
            except OSError:
                pass
    return total


def scan() -> ModuleResult:
    res = ModuleResult("apps", "Программы и автозагрузка")
    try:
        installed = _win_installed() if IS_WINDOWS else _linux_installed()
    except Exception as e:  # noqa: BLE001
        installed, res.error = [], f"не удалось прочитать список программ: {e}"
    try:
        startup = _win_startup() if IS_WINDOWS else _linux_startup()
    except Exception:  # noqa: BLE001
        startup = []

    res.summary = {"установлено программ": len(installed), "в автозагрузке": len(startup),
                   "installed": installed, "startup": startup}

    for a in installed:
        why = match_pup(a["name"])
        if why:
            res.add("Программы", f"Нежелательная программа: {a['name']}", severity="medium",
                    detail=why, recommendation="Удалите через «Параметры → Приложения», если она вам не нужна.",
                    data={"uninstall": a["uninstall"]})

    if len(startup) > 10:
        res.add("Автозагрузка", f"{len(startup)} программ запускаются вместе с системой",
                severity="medium" if len(startup) > 15 else "low",
                recommendation="Отключите лишнее: Диспетчер задач → Автозагрузка. "
                               "Каждая программа замедляет старт и ест RAM.")
    from .processes import suspicion_reasons
    for s in startup:
        cmd = s["command"].strip().strip('"')
        exe = cmd.split('"')[0] if cmd else ""
        reasons = suspicion_reasons(Path(exe).name or s["name"], exe)
        if any(h in s["name"].lower() for h in SAFE_STARTUP_HINTS):
            continue
        if re.search(r"powershell.*(-enc|-e\s|frombase64|iex|downloadstring)", cmd, re.I):
            reasons.append("автозапуск скрытого PowerShell-скрипта")
        if re.search(r"(mshta|wscript|cscript|regsvr32).*(http|\.vbs|\.js|\.hta)", cmd, re.I):
            reasons.append("автозапуск скрипта через системную утилиту (типичный приём вирусов)")
        if reasons:
            res.add("Автозагрузка", f"Подозрительная запись автозагрузки: {s['name']}",
                    severity="high", detail="; ".join(reasons) + f" — {s['command']}",
                    recommendation="Проверьте файл антивирусом. Отключить: Диспетчер задач → "
                                   "Автозагрузка, или Autoruns от Microsoft.")

    left = leftover_dirs(installed)
    big_left = []
    for d in left:
        sz = dir_size(Path(d["path"]))
        if sz > 100 * 1024 ** 2:
            big_left.append({"path": d["path"], "size": sz})
    res.summary["leftovers"] = big_left
    for d in sorted(big_left, key=lambda x: x["size"], reverse=True)[:10]:
        res.add("Хвосты программ", f"Возможный остаток удалённой программы: {Path(d['path']).name}",
                severity="low", detail=f"{d['path']} — {human_size(d['size'])}",
                recommendation="Если программы больше нет, папку можно удалить вручную. "
                               "Сначала убедитесь, что она не нужна другим приложениям.")
    return res
