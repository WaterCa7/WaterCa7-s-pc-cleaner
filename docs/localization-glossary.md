# Localization glossary (Russian | English)

The Russian text is the key of a UI text (`T._("…")`, `{loc:T '…'}`); the English text lives in
`src/PcCleaner/Core/Localization/en.json`. When one Russian text needs different English wording in different places, the key also
carries a context (`T.Ctx("status", "Готово")`, see below). Use these terms everywhere so the Windows app and the Mac app say the same thing.
Style: short, plain, sentence case, active voice, “you” for the user; product name «WaterCa7s PC Cleaner» is never translated;
names of languages stay in their own language (Русский / English).

## How to add or change a text

1. **Wrap it.** C#: `T._("Текст")`; with values `T.F("Найдено {0} из {1}", a, b)` (placeholders numbered in the order of the Russian text,
   format specifiers stay: `{0:N0}`); a word after a number `T.Plural(n, "файл", "файла", "файлов")` (`Format.Plural` / `Format.Count`
   go through it; English uses the `one` and `many` entries only). XAML: `Text="{loc:T 'Проверить'}"` (also `Content`, `ToolTip`, `Header`,
   `Title`, `AutomationProperties.Name`; the root needs `xmlns:loc="clr-namespace:PcCleaner.Core.Localization"`; an apostrophe inside is written `\'`).
   The arguments are string literals or consts, never variables. Quotes around a name put by code: `T.Quote(name)`.
2. **Add the English text** to `en.json`: a flat object `"Русский текст": "English text"`, keys sorted (ordinal), UTF-8, no comments, the same
   `{n}` set as the key. Style and terms: this glossary.
3. **Different English for the same Russian text** (gettext's msgctxt): `T.Ctx("context", "Текст")`, `T.CtxF`, `T.CtxPlural`, XAML
   `{loc:T 'Текст', Context=name}`. The key is the context, U+0004 and the text (`"status\u0004Готово"`); without an entry for the context the
   plain entry of the text counts. A context is a short Latin name. Use it only when one English text would be wrong in one of the places.
4. **The Russian text is the key.** When you edit a Russian text, its en.json entry no longer matches: read the English value again (the meaning
   may have changed) and re-key it. The catalog test fails on a missing text and on a stale entry; it covers the whole source tree
   (`LocalizationCatalogTests.Enforced`), C# and XAML, and reports a Cyrillic literal that is not wrapped.
5. **Whole sentences.** One entry per sentence with placeholders (`"Версия {0} установлена"`), never fragments glued together: word order differs
   in English. Numbers, sizes and dates come from `Format` (it follows the UI language).
6. **What not to wrap:** identifiers and data — page keys, setting names, registry paths and value names, command lines, file names, strings
   parsed from tool output or compared with it, JSON properties, values saved to settings or backups, regexes, URLs. A text that is both shown and
   compared is split: keep the key, wrap only the shown part. Russian words the program only *matches* (Windows output in Russian, the Russian
   failure rules) go between the comment lines `// l10n:data-begin — why` and `// l10n:data-end`; a text that is shown never goes inside
   (an unclosed `data-begin` fails the test).
7. **Exceptions.** Show and log an exception with `ErrorText.For(ex)` / `ErrorText.Sentence(ex)`, never `ex.Message` (a Windows message in
   another language would appear raw). A handled failure goes to the log as `Log.Warn(T.F("…: {0}", ErrorText.For(ex)), ex)`: the journal
   gets the phrase, the log file also the type and the original message. A text the app writes itself is passed through `T._` where it is
   thrown and marked `throw ErrorText.Own(new InvalidOperationException(T._("…")))` (`UpdateException` marks itself).
8. **Failure texts stay recognisable.** The Dashboard decides whether a fix failed or needs administrator rights from the text
   (`FixResultClassifier`): every en.json entry whose Russian key the Russian rules call a failure becomes an English matcher. So keep such an
   English value a whole sentence ("Could not turn on the firewall: {0}." — not `"{0}"`, `"{0}: {1}"` or "Error"; at least 8 non-space
   characters and 2 words outside the placeholders) and keep the failure meaning ("Could not …", "… was not changed", "… needs administrator
   rights"). A side note appended to a successful result must not read like a failure: give it other wording ("No restore point was created.")
   or, if its Russian key starts with «Не удалось», list the key in `FixResultClassifier.SideNoteKeys`.
9. **Check.** `dotnet test tests\PcCleaner.Tests` (Russian output must not change: the tests run in Russian), then
   `WaterCa7sPcCleaner.exe --screenshots <dir> --lang en --mode expert --no-scan` and look at the pictures for Russian leftovers and cut-off text.

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
| Готово (status bar, `T.Ctx("status", …)`) | Ready |
| Готово (title of a dialog or a result) | Done |
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
| Майкрософт Windows 11 Домашняя (caption of a Russian Windows, Speed up card) | Microsoft Windows 11 Home (`SystemInfo.EnglishWindowsCaption`) |

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
| Не удалось удалить ({0}): (summary of the uninstall, context `uninstall`) | Could not uninstall ({0}): |
| Удалено: {0} из {1}. (summary of the uninstall, context `uninstall`) | Uninstalled {0} of {1}. |
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

Power plan names and other values that Windows gives (plan names, monitor names) are shown as they are; only the program's own
texts are translated. The one exception is the caption of the operating system on the Speed up card: the English run maps the edition of a
Russian Windows (“Майкрософт Windows 11 Домашняя”) to English (“Microsoft Windows 11 Home”, `SystemInfo.EnglishWindowsCaption`); an
edition that is not in `SystemInfo.Editions` stays as Windows gave it. A tweak's title is computed in the current language
(`RegistryTweak.Title`), not stored.

Notes for the other pages:

- The Russian «Готово» has two English texts: “Ready” in the status bar (`T.Ctx("status", "Готово")`, a test pins it) and “Done” as the title
  of a dialog or a result (the plain entry).
- «Не удалось удалить ({0}):» is “Could not uninstall ({0}):” in the summary of an uninstall in Programs (context `uninstall`) and “Could not
  remove ({0}):” in Startup and in the “Remove extras” fix of Programs (the plain entry). «Удалено: {0} из {1}.» is “Uninstalled {0} of {1}.” in
  Programs (context `uninstall`) and “Deleted: {0} of {1}.” in the quarantine list (the plain entry).
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
