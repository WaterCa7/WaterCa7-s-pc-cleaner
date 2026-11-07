"""Анализ запущенных процессов: топ потребителей и подозрительные признаки."""
from __future__ import annotations

import math
import os
import re
import time
from collections import Counter

import psutil

from ..core import IS_WINDOWS, ModuleResult, human_size

# Системные процессы Windows и каталоги, из которых они обязаны запускаться.
WINDOWS_SYSTEM_PROCS = {
    "svchost.exe", "csrss.exe", "lsass.exe", "winlogon.exe", "services.exe",
    "smss.exe", "wininit.exe", "explorer.exe", "taskhostw.exe", "dwm.exe",
    "spoolsv.exe", "conhost.exe", "rundll32.exe", "lsm.exe", "ctfmon.exe",
    "sihost.exe", "lsaiso.exe", "fontdrvhost.exe", "dllhost.exe", "taskhost.exe",
}
# Процессы, которые не нужно проверять вовсе (псевдопроцессы ядра).
IGNORED_PROCS = {"system idle process", "idle", "system", "registry", "memory compression", "secure system"}
WINDOWS_SYSTEM_DIRS = ("\\windows\\system32\\", "\\windows\\syswow64\\", "\\windows\\")

# Каталоги, откуда легальный софт обычно не запускается постоянно.
SUSPICIOUS_DIR_MARKERS = (
    "\\appdata\\local\\temp\\", "\\windows\\temp\\", "\\users\\public\\",
    "\\downloads\\", "\\$recycle.bin\\", "\\programdata\\temp",
    "/tmp/", "/var/tmp/", "/dev/shm/",
)

DOC_EXTS = ("pdf", "doc", "docx", "xls", "xlsx", "jpg", "png", "txt", "zip", "mp4")
EXEC_EXTS = ("exe", "scr", "bat", "cmd", "com", "pif", "vbs", "js")

# Известные майнеры и инструменты удалённого доступа (могут быть легальными, но
# если вы их не ставили — это повод насторожиться).
KNOWN_RISKY = {
    "xmrig": "криптомайнер XMRig",
    "nicehash": "майнер NiceHash",
    "nbminer": "криптомайнер",
    "t-rex": "криптомайнер",
    "phoenixminer": "криптомайнер",
    "lolminer": "криптомайнер",
    "anydesk": "удалённый доступ AnyDesk",
    "teamviewer": "удалённый доступ TeamViewer",
    "rustdesk": "удалённый доступ RustDesk",
    "ammyy": "удалённый доступ Ammyy Admin",
    "radmin": "удалённый доступ Radmin",
    "ngrok": "туннель ngrok",
    "psexec": "удалённое выполнение PsExec",
    "mimikatz": "инструмент кражи паролей",
}

# Похожие на системные имена (тайпсквоттинг): svch0st, scvhost, lsasss…
LOOKALIKE_TARGETS = ("svchost", "csrss", "lsass", "winlogon", "explorer", "services", "smss")


def _entropy(s: str) -> float:
    if not s:
        return 0.0
    counts = Counter(s)
    return -sum(c / len(s) * math.log2(c / len(s)) for c in counts.values())


def _levenshtein(a: str, b: str) -> int:
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


def suspicion_reasons(name: str, exe: str, cpu: float = 0.0) -> list[str]:
    """Вернуть список причин, по которым процесс выглядит подозрительно."""
    reasons: list[str] = []
    lname = name.lower()
    lexe = (exe or "").lower().replace("/", "\\") if IS_WINDOWS else (exe or "").lower()
    stem = lname.rsplit(".", 1)[0]

    if IS_WINDOWS and lname in WINDOWS_SYSTEM_PROCS and lexe:
        if not any(d in lexe for d in WINDOWS_SYSTEM_DIRS):
            reasons.append(f"системное имя «{name}», но запущен не из Windows: {exe}")

    if lname in IGNORED_PROCS:
        return reasons
    in_windir = IS_WINDOWS and any(d in lexe for d in WINDOWS_SYSTEM_DIRS)
    known = lname in WINDOWS_SYSTEM_PROCS or stem in LOOKALIKE_TARGETS
    for target in LOOKALIKE_TARGETS if not (known or in_windir) else ():
        if 0 < _levenshtein(stem, target) <= 2 and len(stem) >= 5:
            reasons.append(f"имя похоже на системный процесс «{target}» (подделка?)")
            break

    check = lexe if IS_WINDOWS else (exe or "").lower()
    for marker in SUSPICIOUS_DIR_MARKERS:
        if marker in check:
            reasons.append(f"запущен из временной/общей папки: {exe}")
            break

    parts = lname.split(".")
    if len(parts) >= 3 and parts[-1] in EXEC_EXTS and parts[-2] in DOC_EXTS:
        reasons.append("двойное расширение (маскировка под документ)")

    if (len(stem) >= 10 and re.fullmatch(r"[a-z0-9]+", stem) and re.search(r"\d.*\d", stem)
            and not re.search(r"[aeiou]{2}", stem) and _entropy(stem) > 3.2):
        reasons.append("случайное на вид имя файла")

    for key, label in KNOWN_RISKY.items():
        if key in lname:
            reasons.append(f"{label} — убедитесь, что вы это устанавливали")
            break

    if exe and exe.endswith(" (deleted)"):
        reasons.append("исполняемый файл удалён с диска, а процесс работает")

    if cpu >= 80:
        reasons.append(f"очень высокая загрузка CPU ({cpu:.0f}%) — возможен майнер")

    return reasons


def scan(sample_seconds: float = 1.0, top: int = 10) -> ModuleResult:
    res = ModuleResult("processes", "Запущенные процессы")
    procs = list(psutil.process_iter(["pid", "name", "exe", "username", "memory_info"]))

    # Первый вызов cpu_percent «взводит» счётчик, второй — даёт реальное значение.
    for p in procs:
        try:
            p.cpu_percent(None)
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
    time.sleep(sample_seconds)

    ncpu = psutil.cpu_count() or 1
    rows = []
    for p in procs:
        try:
            cpu = p.cpu_percent(None) / ncpu
            info = p.info
            mem = info["memory_info"].rss if info.get("memory_info") else 0
            rows.append({
                "pid": info["pid"], "name": info.get("name") or "?",
                "exe": info.get("exe") or "", "user": info.get("username") or "",
                "cpu": round(cpu, 1), "mem": mem,
            })
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue

    total_mem = psutil.virtual_memory().total
    res.summary = {
        "всего процессов": len(rows),
        "загрузка CPU": f"{psutil.cpu_percent(None):.0f}%",
        "занято RAM": f"{psutil.virtual_memory().percent:.0f}%",
    }
    res.summary["top_cpu"] = sorted(rows, key=lambda r: r["cpu"], reverse=True)[:top]
    res.summary["top_mem"] = sorted(rows, key=lambda r: r["mem"], reverse=True)[:top]

    # Одинаковые процессы, запущенные десятками копий (браузеры, утечки).
    by_name: dict[str, list[dict]] = {}
    for r in rows:
        by_name.setdefault(r["name"], []).append(r)

    for r in rows:
        if r["pid"] == 0:
            continue
        reasons = suspicion_reasons(r["name"], r["exe"], r["cpu"])
        if reasons:
            sev = "high" if len(reasons) >= 2 or "подделка" in " ".join(reasons) else "medium"
            res.add(
                "Процессы", f"Подозрительный процесс: {r['name']} (PID {r['pid']})",
                severity=sev, detail="; ".join(reasons),
                recommendation="Проверьте файл на virustotal.com (кнопка «hash» в отчёте) "
                               "и, если не узнаёте программу, завершите и удалите её.",
                data={"pid": r["pid"], "exe": r["exe"]},
            )

    for r in res.summary["top_mem"][:3]:
        if total_mem and r["mem"] / total_mem > 0.25:
            res.add("Процессы", f"{r['name']} занимает {human_size(r['mem'])} RAM",
                    severity="low",
                    detail=f"Это {r['mem'] / total_mem:.0%} всей памяти.",
                    recommendation="Если программа не нужна сейчас — закройте её или перезапустите.")

    for name, items in by_name.items():
        group_mem = sum(i["mem"] for i in items)
        if len(items) >= 25 and total_mem and group_mem / total_mem > 0.2:
            res.add("Процессы", f"{len(items)} копий {name} ({human_size(group_mem)})",
                    severity="low",
                    recommendation="Закройте лишние вкладки/окна или включите в браузере "
                                   "режим экономии памяти.")
    return res


def kill(pid: int) -> str:
    """Завершить процесс по PID (вызывается только явно из CLI)."""
    p = psutil.Process(pid)
    name = p.name()
    if IS_WINDOWS and name.lower() in WINDOWS_SYSTEM_PROCS and \
            any(d in (p.exe() or "").lower() for d in WINDOWS_SYSTEM_DIRS):
        raise PermissionError(f"{name} — системный процесс Windows, завершать нельзя")
    if not IS_WINDOWS and pid in (0, 1, os.getpid()):
        raise PermissionError("этот процесс завершать нельзя")
    p.terminate()
    try:
        p.wait(timeout=5)
    except psutil.TimeoutExpired:
        p.kill()
    return name
