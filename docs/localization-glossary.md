# Localization glossary (Russian | English)

The Russian text is the key of every UI text (`T._("…")`, `{loc:T '…'}`); the English text lives in
`src/PcCleaner/Core/Localization/en.json`. Use these terms everywhere so the Windows app and the Mac app say the same thing.
Style: short, plain, sentence case, active voice, “you” for the user; product name «WaterCa7s PC Cleaner» is never translated;
names of languages stay in their own language (Русский / English).

## Pages and tabs

| Русский | English |
|---|---|
| Главная | Home |
| Очистка | Cleanup |
| Процессы | Processes |
| Автозагрузка | Startup |
| Программы | Programs |
| Диски | Disks |
| Ускорение | Speed up |
| Сеть | Network |
| Безопасность | Security |
| Настройки | Settings |
| Основные (tab) | General |
| Журнал (tab) | Log |
| Резервные копии (tab) | Backups |
| Карантин (tab) | Quarantine |
| Восстановление системы (tab) | System Restore |
| О программе (tab) | About |
| раздел (a page of the program) | page; in a sentence about several: section(s) |
| раздел реестра | registry key |

## Words

| Русский | English |
|---|---|
| Проверить / проверка | Check / check |
| Проверить и исправить | Check and fix |
| Исправить / Исправить всё | Fix / Fix all |
| Подробнее | Details |
| Корзина | Recycle Bin |
| права администратора | administrator rights |
| Перезапустить от имени администратора | Restart as administrator |
| Перезапустить сейчас | Restart now |
| точка восстановления | restore point |
| Защита системы | System Protection (the Windows window); “system protection” in a sentence |
| Настроить защиту | Configure protection |
| Защитник Windows | Microsoft Defender |
| брандмауэр | firewall |
| кэш | cache |
| временные файлы | temporary files |
| нежелательная программа | unwanted program |
| подозрительный | suspicious |
| битая запись | broken entry |
| Отмена | Cancel |
| Да / Нет | Yes / No |
| Простой / Опытный режим | Simple / Expert mode |
| Пропустить | Skip |
| Скрыть | Hide |
| Что нового | What's new |
| Скачать вручную | Download manually |
| Восстановить | Restore |
| Удалить навсегда | Delete permanently |
| Исходная папка | Original folder |
| В карантин (button) | Quarantine |
| исключения очистки | cleanup exclusions |
| доверенные программы и файлы | trusted programs and files |
| резервная копия | backup |
| копия реестра | registry backup |
| Сброс настроек | Reset settings |
| Ограниченный режим | Limited mode |
| Права администратора (status) | Administrator rights |
| Готово (status bar) | Ready |
| Сделано для WaterCa7 | Made for WaterCa7 |

## Severity badges and log levels

| Русский | English |
|---|---|
| Критично | Critical |
| Важно | Important |
| Внимание (severity and log level) | Warning |
| Мелочь | Minor |
| Совет | Tip |
| Инфо | Info |
| Действие | Action |
| Ошибка | Error |

## Rules that are easy to get wrong

- Quotes: `«…»` in Russian text becomes `“…”` in English text. A name put in quotes by code goes through `T.Quote(name)`.
- Plurals: English uses the `one` and `many` entries only. A word that is the same in two Russian forms (`"байт", "байта", "байт"`)
  has one English entry; use the plural form (“bytes”).
- Dates and sizes come from `Format` (Oct 18, 2026 14:05; KB/MB/GB), never from a hand-written format.
- Do not translate: page keys, registry paths and value names, command lines, file names, URLs, values saved in settings,
  `update.json`, `.reg`, `rstrui.exe` and other names of files and tools.
- A sentence with a value inside is one entry with a placeholder (`"Версия {0} установлена"`), never two fragments.
