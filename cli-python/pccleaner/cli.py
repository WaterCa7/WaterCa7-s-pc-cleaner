"""Командная строка: pccleaner scan | clean | kill | hash."""
from __future__ import annotations

import argparse
import hashlib
import os
import sys
import webbrowser
from pathlib import Path

from . import __version__
from .core import IS_WINDOWS, ModuleResult, human_size, is_admin
from .modules import apps, hardware, junk, network, processes, security
from .report import print_console, to_html, to_json

MODULES = {
    "processes": lambda a: processes.scan(),
    "junk": lambda a: junk.scan(min_age_hours=a.min_age, scan_large=not a.quick),
    "apps": lambda a: apps.scan(),
    "hardware": lambda a: hardware.scan(),
    "security": lambda a: security.scan(),
    "network": lambda a: network.scan(latency=not a.offline),
}


def cmd_scan(a: argparse.Namespace) -> int:
    chosen = a.only.split(",") if a.only else list(MODULES)
    if not is_admin():
        print("Подсказка: запустите от имени администратора — так будет видно больше (соединения, TPM, службы).")
    results: list[ModuleResult] = []
    for name in chosen:
        if name not in MODULES:
            print(f"Неизвестный модуль: {name}. Доступны: {', '.join(MODULES)}")
            return 2
        print(f"… {name}", flush=True)
        try:
            results.append(MODULES[name](a))
        except Exception as e:  # noqa: BLE001 — один упавший модуль не должен ломать весь отчёт
            r = ModuleResult(name, name)
            r.error = f"ошибка модуля: {e}"
            results.append(r)
    print_console(results, color=sys.stdout.isatty())
    if a.json:
        Path(a.json).write_text(to_json(results), encoding="utf-8")
        print(f"JSON сохранён: {a.json}")
    out = Path(a.html)
    out.write_text(to_html(results), encoding="utf-8")
    print(f"HTML-отчёт: {out.resolve()}")
    if a.open:
        webbrowser.open(out.resolve().as_uri())
    return 0


def cmd_clean(a: argparse.Namespace) -> int:
    keys = a.targets.split(",") if a.targets else None
    rep = junk.clean(keys, min_age_hours=a.min_age, apply=False)
    for t in rep["targets"]:
        if t["files"]:
            print(f"  {t['title']}: {t['files']} файлов, {human_size(t['freed'])}")
    print(f"Итого можно освободить: {human_size(rep['freed'])} ({rep['deleted_files']} файлов)")
    if not a.apply:
        print("Это пробный запуск. Для удаления добавьте --apply")
        return 0
    if not a.yes and input("Удалить эти файлы? [y/N] ").strip().lower() not in ("y", "yes", "д", "да"):
        print("Отменено.")
        return 1
    rep = junk.clean(keys, min_age_hours=a.min_age, apply=True)
    print(f"Удалено {rep['deleted_files']} файлов, освобождено {human_size(rep['freed'])}. "
          f"Пропущено (заняты/нет прав): {rep['errors']}")
    return 0


def cmd_kill(a: argparse.Namespace) -> int:
    try:
        name = processes.kill(a.pid)
    except Exception as e:  # noqa: BLE001
        print(f"Не удалось: {e}")
        return 1
    print(f"Процесс {name} (PID {a.pid}) завершён.")
    return 0


def cmd_hash(a: argparse.Namespace) -> int:
    h = hashlib.sha256()
    with open(a.file, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    digest = h.hexdigest()
    print(f"SHA-256: {digest}\nПроверить: https://www.virustotal.com/gui/file/{digest}")
    return 0


def cmd_targets(_: argparse.Namespace) -> int:
    for t in junk.targets():
        print(f"  {t.key:<15} {'авто' if t.safe else 'вручную':<8} {t.title} — {t.path}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="pccleaner", description="Бесплатная проверка и очистка ПК")
    p.add_argument("--version", action="version", version=__version__)
    sub = p.add_subparsers(dest="cmd")

    s = sub.add_parser("scan", help="полная проверка и отчёт (ничего не удаляет)")
    s.add_argument("--only", help="модули через запятую: " + ",".join(MODULES))
    s.add_argument("--html", default="pccleaner-report.html", help="куда сохранить HTML-отчёт")
    s.add_argument("--json", help="сохранить также JSON")
    s.add_argument("--open", action="store_true", help="открыть отчёт в браузере")
    s.add_argument("--quick", action="store_true", help="не искать крупные файлы")
    s.add_argument("--offline", action="store_true", help="не проверять задержки в интернет")
    s.add_argument("--min-age", type=float, default=24, help="считать мусором файлы старше N часов")
    s.set_defaults(func=cmd_scan)

    c = sub.add_parser("clean", help="очистка временных файлов и кэшей")
    c.add_argument("--targets", help="ключи целей через запятую (см. pccleaner targets)")
    c.add_argument("--min-age", type=float, default=24, help="удалять только файлы старше N часов")
    c.add_argument("--apply", action="store_true", help="действительно удалить (иначе пробный запуск)")
    c.add_argument("-y", "--yes", action="store_true", help="не спрашивать подтверждение")
    c.set_defaults(func=cmd_clean)

    t = sub.add_parser("targets", help="список мест, которые умеет чистить программа")
    t.set_defaults(func=cmd_targets)

    k = sub.add_parser("kill", help="завершить процесс по PID")
    k.add_argument("pid", type=int)
    k.set_defaults(func=cmd_kill)

    h = sub.add_parser("hash", help="SHA-256 файла и ссылка на VirusTotal")
    h.add_argument("file")
    h.set_defaults(func=cmd_hash)
    return p


def main(argv: list[str] | None = None) -> int:
    if IS_WINDOWS:
        os.system("")  # включает ANSI-цвета в консоли Windows
        try:
            sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
        except Exception:  # noqa: BLE001
            pass
    p = build_parser()
    a = p.parse_args(argv)
    if not a.cmd:
        a = p.parse_args(["scan", "--open"] + (argv or []))
    return a.func(a)
