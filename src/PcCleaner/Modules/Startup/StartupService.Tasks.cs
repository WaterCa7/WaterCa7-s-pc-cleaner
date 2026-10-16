using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Startup;

// Scheduled tasks with logon/boot triggers (Task Scheduler 2.0 COM API, late-bound).
public sealed partial class StartupService
{
    private const int TriggerBoot = 8;
    private const int TriggerLogon = 9;
    private const int ActionExec = 0;
    private const int ActionComHandler = 5;
    private const int EnumHidden = 1;

    private sealed class TaskScanContext
    {
        public int Denied;
        public required string UserName { get; init; }
        public required string ShortUserName { get; init; }
        public required string UserSid { get; init; }
        public required CancellationToken Token { get; init; }
    }

    /// <summary>Enumerates every task folder; tasks that cannot be read without admin rights are skipped quietly.</summary>
    internal List<StartupItem> ScanTasks(CancellationToken ct)
    {
        var list = new List<StartupItem>();
        object? service = null;
        object? root = null;
        TaskScanContext? context = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
                return list;
            service = Activator.CreateInstance(type);
            dynamic svc = service!;
            svc.Connect();
            using var identity = WindowsIdentity.GetCurrent();
            context = new TaskScanContext
            {
                UserName = identity.Name,
                ShortUserName = identity.Name.Contains('\\') ? identity.Name[(identity.Name.LastIndexOf('\\') + 1)..] : identity.Name,
                UserSid = identity.User?.Value ?? "",
                Token = ct,
            };
            root = svc.GetFolder("\\");
            WalkTaskFolder(root!, list, context, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Автозагрузка: планировщик заданий недоступен: {ex.Message}");
        }
        finally
        {
            StartupNative.Release(root);
            StartupNative.Release(service);
        }

        if (context is { Denied: > 0 })
            Log.Warn($"Автозагрузка: {Format.Count(context.Denied, "задание или папка", "задания или папки", "заданий или папок")} " +
                     "планировщика недоступны без прав администратора — пропущены");
        return list;
    }

    private void WalkTaskFolder(object folderObject, List<StartupItem> list, TaskScanContext context, int depth)
    {
        context.Token.ThrowIfCancellationRequested();
        if (depth > 32)
            return;
        dynamic folder = folderObject;

        object? tasks = null;
        try
        {
            tasks = folder.GetTasks(EnumHidden);
            foreach (var task in (System.Collections.IEnumerable)tasks!)
            {
                context.Token.ThrowIfCancellationRequested();
                try
                {
                    var item = ReadTask(task, context);
                    if (item is not null)
                        list.Add(item);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsAccessDenied(ex) || ex is COMException or FileNotFoundException)
                {
                    context.Denied++;
                }
                finally
                {
                    StartupNative.Release(task);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsAccessDenied(ex) || ex is COMException or FileNotFoundException)
        {
            context.Denied++;
        }
        finally
        {
            StartupNative.Release(tasks);
        }

        object? folders = null;
        try
        {
            folders = folder.GetFolders(0);
            foreach (var sub in (System.Collections.IEnumerable)folders!)
            {
                try
                {
                    WalkTaskFolder(sub, list, context, depth + 1);
                }
                finally
                {
                    StartupNative.Release(sub);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsAccessDenied(ex) || ex is COMException or FileNotFoundException)
        {
            context.Denied++;
        }
        finally
        {
            StartupNative.Release(folders);
        }
    }

    private StartupItem? ReadTask(object taskObject, TaskScanContext context)
    {
        dynamic task = taskObject;
        dynamic definition = task.Definition;
        try
        {
            bool boot = false, logon = false;
            foreach (var trigger in (System.Collections.IEnumerable)definition.Triggers)
            {
                dynamic t = trigger;
                int type = t.Type;
                if (type == TriggerBoot)
                    boot = true;
                else if (type == TriggerLogon)
                    logon = true;
                StartupNative.Release(trigger);
            }
            if (!boot && !logon)
                return null;

            string? exe = null, args = null, classId = null;
            foreach (var action in (System.Collections.IEnumerable)definition.Actions)
            {
                dynamic a = action;
                int type = a.Type;
                if (type == ActionExec && exe is null)
                {
                    exe = a.Path;
                    args = a.Arguments;
                }
                else if (type == ActionComHandler && classId is null)
                {
                    classId = a.ClassId;
                }
                StartupNative.Release(action);
            }

            string command;
            string? target;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var path = System.Environment.ExpandEnvironmentVariables(exe.Trim().Trim('"'));
                command = string.IsNullOrWhiteSpace(args) ? Quote(path) : $"{Quote(path)} {args.Trim()}";
                target = StartupTargets.Resolve(command);
            }
            else if (!string.IsNullOrWhiteSpace(classId))
            {
                target = StartupTargets.ComServer(classId);
                command = target is null ? $"COM-обработчик {classId}" : $"COM-обработчик {classId} ({target})";
            }
            else
            {
                command = "(нет команды запуска)";
                target = null;
            }

            string taskPath = task.Path;
            string taskName = task.Name;
            bool enabled = task.Enabled;
            string? author = null, description = null, userId = null;
            try
            {
                author = ResolveTaskString((string?)definition.RegistrationInfo.Author);
                description = ResolveTaskString((string?)definition.RegistrationInfo.Description);
                userId = definition.Principal.UserId;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                // optional details
            }

            var runsAsMe = IsCurrentUser(userId, context);
            var triggerText = boot && logon ? "при включении и входе" : boot ? "при включении ПК" : "при входе";
            return new StartupItem
            {
                Id = $"task|{taskPath}",
                Kind = StartupKind.ScheduledTask,
                Scope = runsAsMe ? StartupScope.CurrentUser : StartupScope.AllUsers,
                Name = StartupTargets.FriendlyTaskName(taskName, taskPath),
                Command = command,
                TargetFile = target,
                LocationText = taskPath,
                TaskPath = taskPath,
                TaskAuthor = author,
                TriggerText = triggerText,
                TaskRunsAsCurrentUser = runsAsMe,
                Description = description,
                IsEnabled = enabled,
            };
        }
        finally
        {
            StartupNative.Release((object)definition);
        }
    }

    private static bool IsCurrentUser(string? userId, TaskScanContext context)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return false;
        return userId.Equals(context.UserName, StringComparison.OrdinalIgnoreCase) ||
               userId.Equals(context.ShortUserName, StringComparison.OrdinalIgnoreCase) ||
               userId.Equals(context.UserSid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"$(@%SystemRoot%\system32\x.dll,-600)" -> resolved text.</summary>
    internal static string? ResolveTaskString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var m = Regex.Match(value, @"^\$\((@.+)\)$");
        if (m.Success)
            return StartupNative.ResolveIndirectString(m.Groups[1].Value);
        return value.StartsWith('@') ? StartupNative.ResolveIndirectString(value) : value.Trim();
    }

    private static (string Folder, string Name) SplitTaskPath(string taskPath)
    {
        var index = taskPath.LastIndexOf('\\');
        if (index <= 0)
            return ("\\", taskPath.TrimStart('\\'));
        return (taskPath[..index], taskPath[(index + 1)..]);
    }

    /// <summary>Runs an action against a freshly opened registered task (on a background thread).</summary>
    private static Task RunOnTaskAsync(string taskPath, Action<object, object> action, CancellationToken ct) => Task.Run(() =>
    {
        object? service = null, folder = null, task = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Планировщик заданий недоступен.");
            service = Activator.CreateInstance(type);
            dynamic svc = service!;
            svc.Connect();
            var (folderPath, name) = SplitTaskPath(taskPath);
            folder = svc.GetFolder(folderPath);
            dynamic dynFolder = folder!;
            task = dynFolder.GetTask(name);
            action(folder!, task!);
        }
        finally
        {
            StartupNative.Release(task);
            StartupNative.Release(folder);
            StartupNative.Release(service);
        }
    }, ct);

    private async Task<StartupActionResult> SetTaskEnabledAsync(StartupItem item, bool enable, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.TaskPath))
            return StartupActionResult.Fail("Неизвестный путь задания.");
        await RunOnTaskAsync(item.TaskPath, (_, task) =>
        {
            dynamic t = task;
            t.Enabled = enable;
        }, ct);

        item.IsEnabled = enable;
        Log.Action($"Автозагрузка: задание планировщика «{item.TaskPath}» {(enable ? "включено" : "отключено")}.");
        return StartupActionResult.Ok(enable
            ? $"Задание «{item.DisplayName}» снова включено."
            : $"Задание «{item.DisplayName}» отключено. Оно осталось в планировщике — включить его можно здесь в любой момент.");
    }

    private async Task<StartupActionResult> DeleteTaskAsync(StartupItem item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.TaskPath))
            return StartupActionResult.Fail("Неизвестный путь задания.");

        string? backupFile = null;
        await RunOnTaskAsync(item.TaskPath, (folder, task) =>
        {
            dynamic t = task;
            string xml = t.Xml;
            Directory.CreateDirectory(AppPaths.BackupsDir);
            var safeName = Regex.Replace(item.TaskPath.Trim('\\'), @"[^\w\-]+", "_");
            if (safeName.Length > 80)
                safeName = safeName[^80..];
            var file = Path.Combine(AppPaths.BackupsDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_Task_{safeName}.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            if (!File.Exists(file) || new FileInfo(file).Length == 0)
                throw new IOException("Не удалось сохранить резервную копию задания");
            backupFile = file;

            dynamic f = folder;
            string name = t.Name;
            f.DeleteTask(name, 0);
        }, ct);

        Log.Action($"Автозагрузка: удалено задание планировщика «{item.TaskPath}» (команда: {item.Command}). " +
                   $"Резервная копия: {Path.GetFileName(backupFile)}");
        return StartupActionResult.Ok(
            $"Задание «{item.DisplayName}» удалено. Его копия сохранена в {backupFile} — восстановить можно через " +
            "«Планировщик заданий» → «Импортировать задачу».", backupFile);
    }
}
