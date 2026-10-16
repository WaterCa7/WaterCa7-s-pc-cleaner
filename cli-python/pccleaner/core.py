"""Общие типы и утилиты: модель находки, запуск команд, форматирование."""
from __future__ import annotations

import os
import platform
import shutil
import subprocess
from dataclasses import dataclass, field, asdict
from typing import Any

IS_WINDOWS = os.name == "nt"
IS_LINUX = platform.system() == "Linux"
IS_MAC = platform.system() == "Darwin"

SEVERITIES = ("info", "low", "medium", "high")


@dataclass
class Finding:
    """Одна находка сканера: что нашли, насколько это важно и что делать."""

    category: str
    title: str
    severity: str = "info"
    detail: str = ""
    recommendation: str = ""
    data: dict[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        if self.severity not in SEVERITIES:
            raise ValueError(f"unknown severity: {self.severity}")

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass
class ModuleResult:
    """Результат работы одного модуля сканирования."""

    name: str
    title: str
    summary: dict[str, Any] = field(default_factory=dict)
    findings: list[Finding] = field(default_factory=list)
    error: str | None = None

    def add(self, *args: Any, **kwargs: Any) -> Finding:
        f = Finding(*args, **kwargs)
        self.findings.append(f)
        return f

    def to_dict(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "title": self.title,
            "summary": self.summary,
            "findings": [f.to_dict() for f in self.findings],
            "error": self.error,
        }


def run(cmd: list[str], timeout: int = 20) -> str:
    """Запустить команду и вернуть stdout; при любой ошибке вернуть ''."""
    if not shutil.which(cmd[0]):
        return ""
    try:
        kwargs: dict[str, Any] = {}
        if IS_WINDOWS:
            kwargs["creationflags"] = 0x08000000  # CREATE_NO_WINDOW
        out = subprocess.run(
            cmd, capture_output=True, text=True, timeout=timeout,
            errors="replace", **kwargs,
        )
        return out.stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return ""


def powershell(script: str, timeout: int = 30) -> str:
    """Выполнить PowerShell-скрипт (только Windows)."""
    if not IS_WINDOWS:
        return ""
    return run(
        ["powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy",
         "Bypass", "-Command", script],
        timeout=timeout,
    )


def read_text(path: str) -> str:
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            return fh.read().strip()
    except OSError:
        return ""


def human_size(num: float) -> str:
    for unit in ("Б", "КБ", "МБ", "ГБ", "ТБ"):
        if abs(num) < 1024:
            return f"{num:.1f} {unit}" if unit != "Б" else f"{int(num)} {unit}"
        num /= 1024
    return f"{num:.1f} ПБ"


def is_admin() -> bool:
    try:
        if IS_WINDOWS:
            import ctypes
            return bool(ctypes.windll.shell32.IsUserAnAdmin())
        return os.geteuid() == 0
    except Exception:
        return False
