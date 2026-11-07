"""Оценка «здоровья» ПК и общие советы по ускорению."""
from __future__ import annotations

from .core import IS_WINDOWS, ModuleResult

PENALTY = {"info": 0, "low": 2, "medium": 6, "high": 15}

GENERAL_TIPS_WINDOWS = [
    "Отключите лишнее в автозагрузке: Ctrl+Shift+Esc → Автозагрузка.",
    "Включите «Контроль памяти»: Параметры → Система → Память — Windows сама будет чистить временные файлы.",
    "Обновите драйверы чипсета, видеокарты и сетевой карты с сайтов производителей (не через «драйверпаки»).",
    "В BIOS включите XMP/EXPO для памяти и Resizable BAR (для видеокарт RTX 30+/RX 6000+).",
    "Держите на системном диске свободными 15–20% места.",
    "Раз в полгода чистите ПК от пыли и проверяйте температуры (HWiNFO).",
    "Не ставьте «ускорители» и «чистильщики реестра» — чистка реестра не ускоряет Windows и может её сломать.",
    "Делайте резервные копии важных файлов (OneDrive, внешний диск) — диск может умереть без предупреждения.",
]
GENERAL_TIPS_LINUX = [
    "Проверьте автозапуск: systemctl list-unit-files --state=enabled и ~/.config/autostart.",
    "Чистите кэш пакетов: sudo apt autoremove && sudo apt clean.",
    "Ограничьте размер журнала: sudo journalctl --vacuum-size=200M.",
    "Для SSD включите периодический TRIM: sudo systemctl enable --now fstrim.timer.",
    "Следите за температурами: sensors (пакет lm-sensors).",
]


def score(results: list[ModuleResult]) -> int:
    penalty = sum(PENALTY[f.severity] for r in results for f in r.findings)
    return max(0, 100 - penalty)


def grade(value: int) -> str:
    if value >= 85:
        return "Отлично"
    if value >= 65:
        return "Хорошо"
    if value >= 40:
        return "Требует внимания"
    return "Плохо"


def tips() -> list[str]:
    return GENERAL_TIPS_WINDOWS if IS_WINDOWS else GENERAL_TIPS_LINUX
