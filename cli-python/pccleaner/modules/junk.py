"""Поиск мусора: временные файлы, кэши, корзина, дампы, крупные файлы, пустые папки.

По умолчанию ничего не удаляется. Удаление — только `clean(..., apply=True)`.
"""
from __future__ import annotations

import os
import time
from dataclasses import dataclass
from pathlib import Path

from ..core import IS_MAC, IS_WINDOWS, ModuleResult, human_size


@dataclass
class Target:
    key: str
    title: str
    path: Path
    safe: bool = True       # можно чистить автоматически
    note: str = ""


def _env(name: str, default: str = "") -> Path:
    return Path(os.environ.get(name, default))


def targets() -> list[Target]:
    home = Path.home()
    t: list[Target] = []
    if IS_WINDOWS:
        local = _env("LOCALAPPDATA", str(home / "AppData/Local"))
        roaming = _env("APPDATA", str(home / "AppData/Roaming"))
        windir = _env("WINDIR", "C:/Windows")
        progdata = _env("PROGRAMDATA", "C:/ProgramData")
        t += [
            Target("user_temp", "Временные файлы пользователя", _env("TEMP", str(local / "Temp"))),
            Target("win_temp", "Временные файлы Windows", windir / "Temp"),
            Target("win_update", "Кэш загрузок Windows Update", windir / "SoftwareDistribution/Download",
                   note="Безопасно, Windows скачает заново при необходимости"),
            Target("delivery_opt", "Кэш оптимизации доставки",
                   windir / "ServiceProfiles/NetworkService/AppData/Local/Microsoft/Windows/DeliveryOptimization/Cache"),
            Target("crash_dumps", "Дампы падений программ", local / "CrashDumps"),
            Target("wer", "Отчёты об ошибках Windows", progdata / "Microsoft/Windows/WER"),
            Target("minidump", "Минидампы синих экранов", windir / "Minidump"),
            Target("thumbs", "Кэш эскизов", local / "Microsoft/Windows/Explorer",
                   note="Только файлы thumbcache_*.db"),
            Target("chrome_cache", "Кэш Google Chrome", local / "Google/Chrome/User Data/Default/Cache"),
            Target("edge_cache", "Кэш Microsoft Edge", local / "Microsoft/Edge/User Data/Default/Cache"),
            Target("yandex_cache", "Кэш Яндекс.Браузера", local / "Yandex/YandexBrowser/User Data/Default/Cache"),
            Target("opera_cache", "Кэш Opera", local / "Opera Software/Opera Stable/Cache"),
            Target("firefox_cache", "Кэш Firefox", local / "Mozilla/Firefox/Profiles"),
            Target("discord_cache", "Кэш Discord", roaming / "discord/Cache"),
            Target("steam_shader", "Кэш шейдеров Steam (htmlcache)", local / "Steam/htmlcache"),
            Target("nvidia_cache", "Кэш шейдеров NVIDIA", local / "NVIDIA/DXCache"),
            Target("d3d_cache", "Кэш шейдеров DirectX", local / "D3DSCache"),
            Target("recycle", "Корзина", Path(os.environ.get("SYSTEMDRIVE", "C:") + "/$Recycle.Bin"),
                   safe=False, note="Очищайте через проводник: правый клик по Корзине → Очистить"),
            Target("downloads", "Папка «Загрузки»", home / "Downloads", safe=False,
                   note="Только отчёт — разберите вручную"),
        ]
    else:
        cache = Path(os.environ.get("XDG_CACHE_HOME", home / ".cache"))
        t += [
            Target("user_cache", "Кэш приложений (~/.cache)", cache),
            Target("tmp", "Временные файлы /tmp", Path("/tmp")),
            Target("var_tmp", "Временные файлы /var/tmp", Path("/var/tmp")),
            Target("thumbs", "Кэш эскизов", cache / "thumbnails"),
            Target("trash", "Корзина", home / ".local/share/Trash", safe=False,
                   note="Очистите через файловый менеджер"),
            Target("apt_cache", "Кэш пакетов APT", Path("/var/cache/apt/archives"),
                   note="Безопаснее: sudo apt clean"),
            Target("journal", "Системный журнал", Path("/var/log/journal"), safe=False,
                   note="Сократить: sudo journalctl --vacuum-size=200M"),
            Target("downloads", "Папка «Загрузки»", home / "Downloads", safe=False,
                   note="Только отчёт — разберите вручную"),
        ]
        if IS_MAC:
            t.append(Target("mac_cache", "Кэши macOS", home / "Library/Caches"))
    return t


def _walk_files(root: Path):
    """Обойти файлы, не переходя по ссылкам и игнорируя ошибки доступа."""
    stack = [root]
    while stack:
        d = stack.pop()
        try:
            with os.scandir(d) as it:
                for e in it:
                    try:
                        if e.is_symlink():
                            continue
                        if e.is_dir(follow_symlinks=False):
                            stack.append(Path(e.path))
                        elif e.is_file(follow_symlinks=False):
                            yield e
                    except OSError:
                        continue
        except OSError:
            continue


def _matches(target: Target, entry: os.DirEntry) -> bool:
    if target.key == "thumbs" and IS_WINDOWS:
        return entry.name.lower().startswith("thumbcache_")
    if target.key == "firefox_cache":
        return "cache2" in entry.path.replace("\\", "/").split("/")
    return True


def measure(target: Target, min_age_hours: float = 0) -> tuple[int, int]:
    """Вернуть (кол-во файлов, байт) в цели, старше min_age_hours."""
    if not target.path.is_dir():
        return 0, 0
    cutoff = time.time() - min_age_hours * 3600
    count = size = 0
    for e in _walk_files(target.path):
        if not _matches(target, e):
            continue
        try:
            st = e.stat(follow_symlinks=False)
        except OSError:
            continue
        if st.st_mtime <= cutoff:
            count += 1
            size += st.st_size
    return count, size


def large_files(root: Path, min_size: int = 500 * 1024 ** 2, limit: int = 20) -> list[tuple[str, int]]:
    found = []
    for e in _walk_files(root):
        try:
            sz = e.stat(follow_symlinks=False).st_size
        except OSError:
            continue
        if sz >= min_size:
            found.append((e.path, sz))
    return sorted(found, key=lambda x: x[1], reverse=True)[:limit]


def empty_dirs(root: Path, limit: int = 200) -> list[str]:
    out: list[str] = []
    for dirpath, dirnames, filenames in os.walk(root, topdown=False):
        if "AppData" in dirpath or "/." in dirpath.replace("\\", "/"):
            continue
        if not dirnames and not filenames and dirpath != str(root):
            out.append(dirpath)
            if len(out) >= limit:
                break
    return out


def scan(min_age_hours: float = 24, scan_large: bool = True) -> ModuleResult:
    res = ModuleResult("junk", "Мусор и лишние файлы")
    total = 0
    rows = []
    for t in targets():
        cnt, sz = measure(t, min_age_hours if t.safe else 0)
        if cnt == 0:
            continue
        rows.append({"key": t.key, "title": t.title, "path": str(t.path),
                     "files": cnt, "size": sz, "safe": t.safe, "note": t.note})
        if t.safe:
            total += sz
    rows.sort(key=lambda r: r["size"], reverse=True)
    res.summary = {"можно освободить": human_size(total), "targets": rows}

    for r in rows:
        if r["size"] < 50 * 1024 ** 2:
            continue
        sev = "medium" if r["size"] > 1024 ** 3 else "low"
        rec = r["note"] or ("Очистите командой: pccleaner clean --apply" if r["safe"] else "Разберите вручную")
        res.add("Мусор", f"{r['title']}: {human_size(r['size'])}", severity=sev,
                detail=f"{r['files']} файлов в {r['path']}", recommendation=rec)

    if scan_large:
        big = large_files(Path.home())
        res.summary["large_files"] = [{"path": p, "size": s} for p, s in big]
        if big:
            res.add("Мусор", f"Найдено {len(big)} крупных файлов (>500 МБ) в домашней папке",
                    severity="info",
                    detail=", ".join(f"{Path(p).name} ({human_size(s)})" for p, s in big[:5]),
                    recommendation="Удалите ненужные образы, видео и архивы или перенесите на внешний диск.")
        empties = empty_dirs(Path.home())
        res.summary["пустых папок"] = len(empties)
        if len(empties) > 20:
            res.add("Мусор", f"{len(empties)} пустых папок в домашнем каталоге", severity="info",
                    recommendation="Пустые папки не занимают место, но мешают ориентироваться.")
    return res


def clean(keys: list[str] | None = None, min_age_hours: float = 24, apply: bool = False) -> dict:
    """Удалить файлы из безопасных целей. Без apply=True — только подсчёт."""
    cutoff = time.time() - min_age_hours * 3600
    report = {"deleted_files": 0, "freed": 0, "errors": 0, "targets": []}
    for t in targets():
        if not t.safe or (keys and t.key not in keys) or not t.path.is_dir():
            continue
        files = freed = errors = 0
        for e in _walk_files(t.path):
            if not _matches(t, e):
                continue
            try:
                st = e.stat(follow_symlinks=False)
                if st.st_mtime > cutoff:
                    continue
                if apply:
                    os.remove(e.path)
                files += 1
                freed += st.st_size
            except OSError:
                errors += 1  # файл занят программой или нет прав — пропускаем
        report["targets"].append({"key": t.key, "title": t.title, "files": files,
                                  "freed": freed, "errors": errors})
        report["deleted_files"] += files
        report["freed"] += freed
        report["errors"] += errors
    report["applied"] = apply
    return report
