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

## Core: errors, reasons, restart, report

| Русский | English |
|---|---|
| нет доступа — нужны права администратора, или объект защищён Windows | access denied — administrator rights are needed, or Windows protects the item |
| файл занят другой программой | the file is in use by another program |
| файл или папка не найдены | file or folder not found |
| на диске недостаточно места | not enough space on the disk |
| ошибка Windows (код N) | Windows error (code N) |
| непредвиденная ошибка — подробности записаны в журнал | unexpected error — details are in the log |
| Потенциально нежелательная программа | Potentially unwanted program |
| Высокий риск / Подозрительно / Внимание (verdict of a file) | High risk / Suspicious / Warning |
| Нет цифровой подписи / Без подписи | No digital signature / Unsigned |
| Подписан / Недоверенная подпись | Signed / Untrusted signature |
| Запускается из … / Установлена в … (a risky folder) | Runs from … / Installed in … |
| ставится «в нагрузку» | installed along with other programs |
| угонщик поиска / домашней страницы | search hijacker / home page hijacker |
| навязчивый «оптимизатор» / «антивирус» | pushy “optimizer” / “antivirus” |
| «оптимизатор» с запугиванием | scare-tactic “optimizer” |
| Перезагрузить компьютер | Restart the computer |
| Карантин / в карантин | quarantine / put in quarantine |
| Резервная копия реестра | Registry backup |
| Отчёт о проверке компьютера | Computer check report |
| Проверенные разделы / Найденные проблемы | Checked sections / Problems found |
| Результат исправления / Попытка исправления | Fix result / Fix attempt |
| Не исправлено | Not fixed |
| Что даст | What you gain |
| Отмечено: N / Снять отметки (bulk bar) | Checked: N / Uncheck all |

How the code treats them:

- A text of an exception that the app wrote itself is passed through `T._` where it is thrown and marked with `ErrorText.Own(…)`;
  `ErrorText.For/Sentence` then shows it as it is. An unmarked exception of an unknown type gets the general phrase in English.
- A reason of suspicion is text for the user; what the program decides from it (score, level, unwanted program already found,
  kind of install folder) comes from `SuspicionFlags` / `RiskyLocation`, never from the words.

## Processes

| Русский | English |
|---|---|
| Завершить / Завершить дерево процессов / Завершить отмеченные | End / End process tree / End checked |
| принудительно завершить | force-end |
| Оценка (the column of the verdict on a process) | Rating |
| Высокий риск / Подозрительно / Внимание / Доверенный | High risk / Suspicious / Warning / Trusted |
| Служебный (process of the cleaner itself) | Helper |
| Доверять / Не доверять | Trust / Stop trusting |
| Открыть расположение файла | Open file location |
| Найти в интернете | Search the web |
| Проверить Защитником Windows | Check with Microsoft Defender |
| Частный рабочий набор | Private working set |
| родительский процесс | parent process |
| копия (of a program) | copy / copies |

## Startup

| Русский | English |
|---|---|
| запись (of the startup list) | entry |
| Включено / Отключено / Однократно | Enabled / Disabled / Once |
| Отключить / Включить (an entry) | Disable / Enable |
| Удалить из автозагрузки | Delete from startup |
| Реестр: Run / Папка «Автозагрузка» / Планировщик заданий / Служба Windows | Registry: Run / Startup folder / Task Scheduler / Windows service |
| для вас / для всех пользователей | for you / for all users |
| отложенный запуск | delayed start |
| ручной режим (of a service) | manual start |
| задания Windows / службы Microsoft (hidden by default) | Windows tasks / Microsoft services |
| Битые записи | Broken entries |

## Disks

| Русский | English |
|---|---|
| Обзор / Что занимает место / Большие файлы / Советы по месту (tabs) | Overview / What takes up space / Large files / Space tips |
| накопитель / физический диск | drive / physical drive |
| том | volume |
| Исправен / Предупреждение / Неисправен | Healthy / Warning / Unhealthy |
| Износ / Наработка / Ошибки чтения | Wear / Power-on time / Read errors |
| Оптимизировать (a drive) | Optimize |
| дефрагментация / ReTrim | defragmentation / ReTrim |
| файл гибернации / файл подкачки | hibernation file / page file |
| точки восстановления системы | system restore points |
| «Очистка диска» / «Защита системы» (Windows tools) | “Disk Cleanup” / “System Protection” |
| Отметить / отмечено (a checkbox) | Check / checked |
| не меньше N (a size cut short) | at least N |
| В Корзину / Удалить в корзину | To Recycle Bin / Move to Recycle Bin |

## Speed up

| Русский | English |
|---|---|
| Ускорение | Speed up |
| рекомендация / Рекомендации | recommendation / Recommendations |
| Уже в порядке | Already fine |
| Применить / Применить все рекомендуемые | Apply / Apply all recommended |
| Вернуть (a setting to its previous value) | Revert |
| Как сделать (steps) | How to do it |
| Ваш компьютер (hardware cards) | Your computer |
| Высокий / Средний / Низкий эффект | High / Medium / Low impact |
| Для сведения | For information |
| Игровой режим Windows | Windows Game Mode |
| планирование GPU с аппаратным ускорением | hardware-accelerated GPU scheduling |
| быстрый запуск Windows | Windows Fast Startup |
| файл подкачки | page file |
| паспортная частота (memory) | rated speed |
| одноканальный / двухканальный режим | single-channel / dual-channel mode |
| план электропитания | power plan |
| Параметры быстродействия (Windows window) | Performance Options |
| Изменено программой | Changed by the program |

Power plan names and other values that Windows gives (plan names, OS caption, monitor names) are shown as they are; only the program's own
texts are translated. A tweak's title is computed in the current language (`RegistryTweak.Title`), not stored.

Notes for the other pages:

- The Russian «Готово» is both the status bar text and the title of a dialog after an action; one key has one English text, so it is “Ready”
  everywhere (a test pins the status bar).
- «Не удалось удалить ({0}):» is shared by Programs and Startup, so it says “Could not remove ({0}):”.
- A decision the program makes after a Defender scan comes from `DefenderScanResult.ThreatFound` (`FileTools.ScanWithDefenderAsync`), never from the
  words of the text; a decision about a process or an entry comes from its flags (`SuspicionFlags`, `SustainedCpu` for the miner heuristic).

## Network

| Русский | English |
|---|---|
| соединение (of a program) / открытый порт | connection / open port |
| Задержка (пинг) | Latency (ping) |
| Основное (the adapter that carries the traffic) | Main |
| Роутер / шлюз | Router / gateway |
| Параметры сети / Параметры Wi-Fi / Параметры адаптера | Network settings / Wi-Fi settings / Adapter settings |
| Очистить кэш DNS / Сбросить сетевые настройки | Clear DNS cache / Reset network settings |
| Как сменить DNS | How to change DNS |
| Проверить IP (AbuseIPDB) | Check IP (AbuseIPDB) |
| Необычный порт / Подозрительно (badge of a connection) | Unusual port / Suspicious |
| Опасный / Стандартный / Обычный / Безопасный (kind of a port) | Risky / Standard / Ordinary / Safe |
| из сети / только этот ПК (access of a port) | from the network / this PC only |
| Установлено / Подключается / Ждёт подключений | Established / Connecting / Listening |
| 100 Мбит/с, 2,4 ГГц, 12 мс, 1,2 МБ/с | 100 Mbps, 2.4 GHz, 12 ms, 1.2 MB/s |
| отлично / нормально / высокая / очень высокая (latency) | excellent / fine / high / very high |
| Нет доступа в интернет | No internet access |

Notes:

- The names of the services behind a port (“HTTPS — веб-сервер”), the reasons of a remote port (“порт майнинг-пулов …”) and the friendly names
  of Windows processes are tables of texts in `NetworkRules` / `ProcessIdentities`; they are read when asked (`switch`), never kept in a static
  table, so a language change shows at once.
- The plural words of a XAML `ConverterParameter` cannot be a text: `PluralConverter` takes the *name* of a word set (`"connection"`, `"port"`),
  the Russian forms and their English entries live in `PluralConverter.Words`.
- A reason of suspicion gets a lower-case first letter inside a sentence (“… raises questions: no digital signature; a downloader …”) except
  a reason that starts with the name of a program: `KnownPups` marks each row for the language where the name comes first (`ReasonStart`:
  “Babylon — …”, “Baidu programs …”, “XMRig crypto miner …” keep their capitals; “Программы Baidu …” does not start with it) and
  `NetworkRules.ProgramWarningText` asks `KnownPups.StartsWithProductName`. Nothing is guessed from how a word is spelled; a new row whose reason
  starts with a name must be marked (a test lists the ordinary first words).
- The words of `netsh` output (any Windows language) are data (`WlanParser`, the authentication words), never shown.

## Security

| Русский | English |
|---|---|
| В порядке / К сведению / Внимание / Опасно / Нет данных (status of a check) | Fine / For information / Warning / Dangerous / No data |
| Антивирус / Брандмауэр / Файл hosts | Antivirus / Firewall / Hosts file |
| Запуск рабочего стола (Winlogon) | Desktop startup |
| Перехват запуска программ (IFEO) | Program launch hijacking |
| Прокси-сервер | Proxy server |
| Изоляция ядра / Целостность памяти | Core isolation / Memory integrity |
| Шифрование диска / Шифрование устройства | Disk encryption / Device encryption |
| Журнал защиты | Protection history |
| Обезвредить угрозы | Neutralize threats |
| Защита в реальном времени / Защита от подделки | Real-time protection / Tamper protection |
| активатор (a cracking tool) | “activator” |
| групповая политика | Group Policy |
| доменная / частная / общедоступная сеть | domain / private / public network |
| Убрать подмену (hosts) / Убрать перехват / Выключить прокси / Восстановить | Remove the replacement / Remove the hijack / Turn off proxy / Restore |

Notes:

- The title of the “Windows Update service is off” issue comes from `UpdatesState.ServiceDisabled`, and the “too broad exclusions” state of the
  antivirus card from the state of Defender, never from the words of a card.
- A failed action whose text already opens with the failure sentence is made with `SecurityActionResult.Failed(…)` (`OpensWithFailure`);
  `SecurityService.DashboardText` does not put its “Не удалось исправить.” / “Could not fix.” in front of it again. A Russian result built
  with plain `Fail(…)` and starting with «Не удалось» is still recognised (a Russian-only rule inside a data region).
- A side note about a restore point that is added to a successful result does not use a failure phrase in English: “No restore point was created
  (system protection may be off).”, not “Could not create a restore point.”.
- The exception texts of the hosts file (`HostsChangedException`) and of `Shell.Application` are marked `ErrorText.Own(…)`.
