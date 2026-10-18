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

## Home (the scan, the score, the fixes)

| Русский | English |
|---|---|
| оценка (0–100) | score |
| проблема / совет | problem / tip |
| раздел (a part of the scan: a module) | section(s) |
| Критично / Важно / Средне / Низко / Советы (groups) | Critical / Important / Medium / Low / Tips |
| Отлично / Хорошо / Нужно внимание / Плохо | Excellent / Good / Needs attention / Poor |
| Проверка неполная | Check incomplete |
| Проверить компьютер / Проверить снова | Check computer / Check again |
| Исправить / Исправить всё | Fix / Fix all |
| Безопасно исправить | Safe to fix |
| Освободит … / Можно освободить … | Frees up … / You can free up … |
| Очистка диска (the Windows tool) | Disk Cleanup |
| Системный диск | System drive |
| Оперативная память | Memory (RAM) |
| Время работы | Uptime |
| Видеокарта / Видеокарты | Graphics card / Graphics cards |
| Права (administrator or standard user) | Rights |
| Сохранить отчёт / Скопировать отчёт | Save report / Copy report |
| Быстрый переход | Quick links |
| Об этом компьютере | About this computer |
| сегодня в 14:32 / вчера в 09:05 | today at 14:32 / yesterday at 09:05 |
| 12 октября в 14:32 / 12 октября 2025 г. в 14:32 | Oct 12 at 14:32 / Oct 12, 2025 at 14:32 |
| Главная: … (log line prefix) | Home: … |
| Windows 11 Домашняя (edition from the registry) | Windows 11 Home (the registry name as it is) |

## Rules that are easy to get wrong

- Quotes: `«…»` in Russian text becomes `“…”` in English text. A name put in quotes by code goes through `T.Quote(name)`.
- Plurals: English uses the `one` and `many` entries only. A word that is the same in two Russian forms (`"байт", "байта", "байт"`)
  has one English entry; use the plural form (“bytes”).
- Dates and sizes come from `Format` (Oct 18, 2026 14:05; KB/MB/GB), never from a hand-written format.
- Do not translate: page keys, registry paths and value names, command lines, file names, URLs, values saved in settings,
  `update.json`, `.reg`, `rstrui.exe` and other names of files and tools.
- A sentence with a value inside is one entry with a placeholder (`"Версия {0} установлена"`), never two fragments.

## Cleanup (the Cleaner page)

| Русский | English |
|---|---|
| Анализ (button) / анализ | Analyze / analysis |
| Очистить выбранное | Clean selected |
| Выбрать рекомендуемое / Снять все | Select recommended / Uncheck all |
| Можно освободить … | You can free up … |
| мусор | junk |
| Временные файлы пользователя / Windows | User temporary files / Windows temporary files |
| Кэш эскизов | Thumbnail cache |
| Отчёты об ошибках | Error reports |
| Дампы памяти | Memory dumps |
| Кэш обновлений Windows | Windows Update cache |
| Кэш оптимизации доставки | Delivery Optimization cache |
| Кэш шейдеров | shader cache |
| Журналы Windows | Windows logs |
| Не рекомендуется (badge) | Not recommended |
| Глубокая очистка | Deep cleanup |
| Очистка компонентов Windows (DISM) | Windows component cleanup (DISM) |
| Мастер очистки диска | Disk Cleanup wizard |
| Проводник | File Explorer |
| занятые файлы (open in a running program) | files in use |
| файлы без доступа | files without access |
| Свежие файлы / моложе 24 часов | Fresh files / newer than 24 hours |
| «Освобождено 3,2 ГБ, удалено 12 345 файлов, пропущено 87 занятых» | Freed 3.2 GB, deleted 12,345 files, skipped 87 in use |

## Programs

| Русский | English |
|---|---|
| Удалить программу / Удалить приложение | Uninstall program / Uninstall app |
| Тихое удаление | Silent uninstall |
| деинсталлятор | uninstaller |
| Убрать запись из списка | Remove entry from the list |
| Удалить отмеченные | Uninstall checked |
| Удалить лишние (preinstalled apps) | Remove extras |
| Нежелательные / Недавние / Большие / Битые (filters) | Unwanted / Recent / Large / Broken |
| Предустановленная, можно удалить | Preinstalled, can be removed |
| Нужна другим программам — не удалять | Needed by other programs — do not uninstall |
| Системное — не удалять | System — do not remove |
| Доверять / Убрать из доверенных | Trust / Remove from trusted |
| Приложения Microsoft Store | Microsoft Store apps |
| учётная запись (user account) | account |
| контроль учётных записей (UAC) | User Account Control |
| Проверить на VirusTotal | Check on VirusTotal |
| Не указан / Неизвестен | Not specified / Unknown |

The names of Microsoft Store apps keep the English names Windows itself uses (Новости → News, Калькулятор → Calculator,
Безопасность Windows → Windows Security); product names stay as they are.
