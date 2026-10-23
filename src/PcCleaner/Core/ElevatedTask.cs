using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using PcCleaner.Core.Localization;
using PcCleaner.Core.Updates;

namespace PcCleaner.Core;

/// <summary>
/// «Запускать с правами администратора» without the UAC prompt for the installed program: a Task Scheduler task per user
/// (folder <see cref="Folder"/>, «Start &lt;SID&gt;») starts the exe with the highest rights. Windows asks once, when the task is
/// made: by the setup (<see cref="RegisterArg"/>) or by the first elevated start of another administrator. Only an exe inside
/// Program Files with the setup's marker gets a task — a task on an exe in a folder the user can write to would hand
/// administrator rights to whatever replaces that file.
/// </summary>
public static class ElevatedTask
{
    /// <summary>The argument of the task's action: this copy was started by the task (it waits for the copy that ran it).</summary>
    public const string FromTaskArg = "--from-task";

    /// <summary>The setup, at the end of an install: register the task for this user and exit without a window.</summary>
    public const string RegisterArg = "--register-task";

    /// <summary>The uninstaller: remove the tasks of every user and exit without a window.</summary>
    public const string UnregisterArg = "--unregister-tasks";

    public const string Folder = @"\WaterCa7s PC Cleaner";

    /// <summary>Task Scheduler in production; an in-memory fake in tests (TestEnvironment).</summary>
    internal static ITaskStore Store { get; set; } = new ComTaskStore();

    public static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? "";
    }

    public static string NameFor(string userSid) => "Start " + userSid;

    /// <summary>True only for the installed exe: inside <paramref name="programFiles"/>, named as ours, with the setup's marker.</summary>
    public static bool MayRegister(string exePath, string programFiles)
    {
        if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(programFiles))
            return false;
        var exe = Path.GetFullPath(exePath);
        return UpdateEnvironment.IsInside(exe, programFiles) &&
               string.Equals(Path.GetFileName(exe), UpdateEnvironment.ExeName, StringComparison.OrdinalIgnoreCase) &&
               File.Exists(exe) &&
               File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, UpdateEnvironment.InstalledMarkerName));
    }

    /// <summary>The task: this exe, elevated, in the user's own session, at normal priority, never stopped.</summary>
    internal static string TaskXml(string exePath, string userSid)
    {
        var exe = SecurityElement.Escape(exePath);
        var dir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "");
        var sid = SecurityElement.Escape(userSid);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts WaterCa7s PC Cleaner as administrator without the UAC prompt.</Description>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>{FromTaskArg}</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>
    /// The start by the shortcut, without the rights: runs this user's task. True = the elevated copy is starting, this one exits.
    /// False (no task, not the installed exe, the task did not run) = ask through UAC as before.
    /// </summary>
    public static bool TryStart(string exePath, string programFiles, string userSid)
    {
        if (!MayRegister(exePath, programFiles))
            return false;
        var name = NameFor(userSid);
        try
        {
            if (!Store.Exists(name))
                return false;
            // This start is the user's click: it may hand the right to come to the front to the copy the task starts.
            AllowSetForegroundWindow(AsfwAny);
            Store.Run(name);
            Log.Info(T._("Запуск с правами администратора через задачу Планировщика"));
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn(T.F("Задача Планировщика не запустилась: {0}", ErrorText.For(ex)), ex);
            return false;
        }
    }

    /// <summary>
    /// An elevated copy makes the task follow the setting: on + installed → the task exists (created or refreshed); off → it is
    /// removed. A copy without the rights changes nothing (it could not). False when the Task Scheduler refused.
    /// </summary>
    public static bool Sync(bool runAsAdmin, bool isElevated, string exePath, string programFiles, string userSid)
    {
        if (!isElevated || string.IsNullOrEmpty(userSid))
            return true;
        var name = NameFor(userSid);
        try
        {
            if (runAsAdmin && MayRegister(exePath, programFiles))
                Store.Register(name, TaskXml(Path.GetFullPath(exePath), userSid), userSid);
            else if (!runAsAdmin && Store.Exists(name))
            {
                Store.Delete(name);
                Log.Info(T._("Задача запуска с правами администратора удалена"));
            }
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn(T.F("Не удалось изменить задачу запуска с правами администратора: {0}", ErrorText.For(ex)), ex);
            return false;
        }
    }

    /// <summary>The setup's command in these arguments (<see cref="RegisterArg"/> / <see cref="UnregisterArg"/>), lower case, or null.</summary>
    public static string? CommandIn(IReadOnlyList<string> args)
        => args.Select(a => a.ToLowerInvariant()).FirstOrDefault(a => a is RegisterArg or UnregisterArg);

    /// <summary>Runs a setup command; the exit code of the program: 0 = done, 1 = not done (logged).</summary>
    public static int RunCommand(string command, bool runAsAdmin, bool isElevated, string exePath, string programFiles, string userSid)
    {
        if (!isElevated)
        {
            Log.Warn(T.F("Команда {0} требует прав администратора", command));
            return 1;
        }
        if (command == RegisterArg)
            return Sync(runAsAdmin, isElevated, exePath, programFiles, userSid) ? 0 : 1;
        try
        {
            Store.DeleteFolder();
            Log.Info(T._("Задачи запуска с правами администратора удалены"));
            return 0;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn(T.F("Не удалось удалить задачи запуска с правами администратора: {0}", ErrorText.For(ex)), ex);
            return 1;
        }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
