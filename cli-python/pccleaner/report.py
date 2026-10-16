"""Вывод результатов: консоль, JSON и автономный HTML-отчёт."""
from __future__ import annotations

import html
import json
import platform
from datetime import datetime
from typing import Any

from . import __version__
from .advisor import grade, score, tips
from .core import ModuleResult, human_size

SEV_ORDER = {"high": 0, "medium": 1, "low": 2, "info": 3}
SEV_LABEL = {"high": "Важно", "medium": "Средне", "low": "Низко", "info": "Инфо"}
SEV_COLOR = {"high": "\033[91m", "medium": "\033[93m", "low": "\033[96m", "info": "\033[90m"}
RESET = "\033[0m"


def _scalar_summary(r: ModuleResult) -> dict[str, Any]:
    return {k: v for k, v in r.summary.items() if isinstance(v, (str, int, float)) and v != ""}


def print_console(results: list[ModuleResult], color: bool = True) -> None:
    sc = score(results)
    print(f"\n=== Оценка системы: {sc}/100 — {grade(sc)} ===")
    for r in results:
        print(f"\n## {r.title}")
        if r.error:
            print(f"  ! {r.error}")
        for k, v in _scalar_summary(r).items():
            print(f"  {k}: {v}")
        for f in sorted(r.findings, key=lambda f: SEV_ORDER[f.severity]):
            c, e = (SEV_COLOR[f.severity], RESET) if color else ("", "")
            print(f"  {c}[{SEV_LABEL[f.severity]}]{e} {f.title}")
            if f.detail:
                print(f"      {f.detail}")
            if f.recommendation:
                print(f"      → {f.recommendation}")
    print()


def to_json(results: list[ModuleResult]) -> str:
    return json.dumps({
        "version": __version__, "generated": datetime.now().isoformat(timespec="seconds"),
        "host": platform.node(), "score": score(results),
        "modules": [r.to_dict() for r in results],
    }, ensure_ascii=False, indent=2, default=str)


def _e(x: Any) -> str:
    return html.escape(str(x))


def _table(rows: list[dict], cols: list[tuple[str, str]], fmt: dict | None = None) -> str:
    if not rows:
        return ""
    fmt = fmt or {}
    head = "".join(f"<th>{_e(t)}</th>" for _, t in cols)
    body = "".join(
        "<tr>" + "".join(f"<td>{_e(fmt.get(k, lambda v: v)(row.get(k, '')))}</td>" for k, _ in cols) + "</tr>"
        for row in rows)
    return f"<div class=tw><table><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table></div>"


def _details(r: ModuleResult) -> str:
    s = r.summary
    parts = []
    if r.name == "processes":
        parts.append("<h4>Топ по CPU</h4>" + _table(s.get("top_cpu", []),
                     [("name", "Процесс"), ("pid", "PID"), ("cpu", "CPU %"), ("mem", "RAM"), ("exe", "Путь")],
                     {"mem": human_size}))
        parts.append("<h4>Топ по памяти</h4>" + _table(s.get("top_mem", []),
                     [("name", "Процесс"), ("pid", "PID"), ("mem", "RAM"), ("exe", "Путь")], {"mem": human_size}))
    elif r.name == "junk":
        parts.append(_table(s.get("targets", []),
                     [("title", "Что"), ("files", "Файлов"), ("size", "Размер"), ("path", "Путь")],
                     {"size": human_size}))
        if s.get("large_files"):
            parts.append("<h4>Крупные файлы</h4>" + _table(s["large_files"], [("path", "Файл"), ("size", "Размер")],
                                                            {"size": human_size}))
    elif r.name == "apps":
        parts.append("<h4>Автозагрузка</h4>" + _table(s.get("startup", []),
                     [("name", "Имя"), ("source", "Где"), ("command", "Команда")]))
        inst = sorted(s.get("installed", []), key=lambda a: a.get("size") or 0, reverse=True)
        parts.append(f"<details><summary>Установленные программы ({len(inst)})</summary>" + _table(
            inst, [("name", "Программа"), ("version", "Версия"), ("publisher", "Издатель"),
                   ("size", "Размер"), ("install_date", "Установлена")],
            {"size": lambda v: human_size(v) if v else ""}) + "</details>")
    elif r.name == "network":
        parts.append("<h4>Интерфейсы</h4>" + _table(s.get("interfaces", []),
                     [("name", "Адаптер"), ("speed_mbps", "Мбит/с"), ("ips", "IPv4")],
                     {"ips": lambda v: ", ".join(v)}))
        lat = s.get("latency_ms") or {}
        if lat:
            parts.append("<h4>Задержка (TCP)</h4>" + _table(
                [{"t": k, "v": f"{v} мс" if v is not None else "недоступен"} for k, v in lat.items()],
                [("t", "Узел"), ("v", "Задержка")]))
        parts.append("<h4>Соединения по процессам</h4>" + _table(
            [{"p": p, "n": n} for p, n in s.get("connections_by_process", [])], [("p", "Процесс"), ("n", "Соединений")]))
        parts.append("<details><summary>Открытые порты</summary>" + _table(
            s.get("listening", []), [("port", "Порт"), ("ip", "Адрес"), ("process", "Процесс")]) + "</details>")
        parts.append("<details><summary>Активные соединения</summary>" + _table(
            s.get("established", []), [("process", "Процесс"), ("remote", "Удалённый адрес")]) + "</details>")
    return "".join(parts)


CSS = """
:root{--bg:#f6f7f9;--card:#fff;--fg:#1d2330;--mut:#667085;--line:#e4e7ec;--high:#d92d20;--medium:#dc6803;--low:#0e7090;--info:#667085;--acc:#1570ef}
@media (prefers-color-scheme:dark){:root{--bg:#0f1218;--card:#171b24;--fg:#e6e9ef;--mut:#98a2b3;--line:#273041;--acc:#53a1ff}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,Segoe UI,Roboto,sans-serif}
main{max-width:1100px;margin:auto;padding:24px 16px}h1{margin:0 0 4px}.mut{color:var(--mut)}
.score{display:flex;gap:24px;align-items:center;background:var(--card);border:1px solid var(--line);border-radius:12px;padding:20px;margin:16px 0}
.big{font-size:48px;font-weight:700;color:var(--acc)}section{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:16px 20px;margin:16px 0}
.kv{display:grid;grid-template-columns:minmax(160px,auto) 1fr;gap:2px 16px}.kv dt{color:var(--mut)}.kv dd{margin:0;overflow-wrap:anywhere}
.f{border-left:4px solid var(--info);padding:6px 12px;margin:8px 0}.f.high{border-color:var(--high)}.f.medium{border-color:var(--medium)}.f.low{border-color:var(--low)}
.tag{font-size:12px;font-weight:600;text-transform:uppercase;margin-right:6px}.high .tag{color:var(--high)}.medium .tag{color:var(--medium)}.low .tag{color:var(--low)}.info .tag{color:var(--info)}
.rec{color:var(--mut)}.tw{overflow-x:auto}table{border-collapse:collapse;width:100%;font-size:13px}th,td{text-align:left;padding:4px 8px;border-bottom:1px solid var(--line);vertical-align:top}
td{overflow-wrap:anywhere;max-width:420px}summary{cursor:pointer;margin:8px 0;color:var(--acc)}ul{padding-left:20px}
"""


def to_html(results: list[ModuleResult]) -> str:
    sc = score(results)
    counts = {k: sum(1 for r in results for f in r.findings if f.severity == k) for k in SEV_ORDER}
    sections = []
    for r in results:
        kv = "".join(f"<dt>{_e(k)}</dt><dd>{_e(v)}</dd>" for k, v in _scalar_summary(r).items())
        fs = "".join(
            f"<div class='f {f.severity}'><span class=tag>{SEV_LABEL[f.severity]}</span><b>{_e(f.title)}</b>"
            + (f"<div>{_e(f.detail)}</div>" if f.detail else "")
            + (f"<div class=rec>→ {_e(f.recommendation)}</div>" if f.recommendation else "") + "</div>"
            for f in sorted(r.findings, key=lambda f: SEV_ORDER[f.severity]))
        err = f"<p class=rec>⚠ {_e(r.error)}</p>" if r.error else ""
        sections.append(f"<section><h2>{_e(r.title)}</h2>{err}<dl class=kv>{kv}</dl>{fs or '<p class=mut>Проблем не найдено.</p>'}"
                        f"{_details(r)}</section>")
    tip_list = "".join(f"<li>{_e(t)}</li>" for t in tips())
    return f"""<!doctype html><html lang=ru><head><meta charset=utf-8>
<meta name=viewport content="width=device-width,initial-scale=1"><title>Отчёт PC Cleaner</title><style>{CSS}</style></head>
<body><main><h1>Отчёт о состоянии компьютера</h1>
<div class=mut>{_e(platform.node())} · {datetime.now():%Y-%m-%d %H:%M} · WaterCa7 PC Cleaner {__version__}</div>
<div class=score><div class=big>{sc}</div><div><b>{grade(sc)}</b><div class=mut>
Важных: {counts['high']} · средних: {counts['medium']} · низких: {counts['low']} · инфо: {counts['info']}</div></div></div>
{''.join(sections)}
<section><h2>Общие советы</h2><ul>{tip_list}</ul></section>
</main></body></html>"""
