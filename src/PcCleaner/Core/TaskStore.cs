using System.Runtime.InteropServices;

namespace PcCleaner.Core;

/// <summary>The few things <see cref="ElevatedTask"/> asks of the Task Scheduler (tests use an in-memory store).</summary>
internal interface ITaskStore
{
    bool Exists(string name);

    /// <summary>Starts the task now. Throws when it cannot.</summary>
    void Run(string name);

    /// <summary>Creates or replaces the task; only <paramref name="userSid"/> (and administrators) may read and run it.</summary>
    void Register(string name, string xml, string userSid);

    void Delete(string name);

    /// <summary>Deletes every task of the program's folder and the folder.</summary>
    void DeleteFolder();
}

/// <summary>
/// Task Scheduler 2.0 through its COM API ("Schedule.Service"): the task XML goes in as a string, so no file that another program
/// could edit is ever read by the elevated copy. Late-bound (dynamic) to need no interop assembly.
/// </summary>
internal sealed class ComTaskStore : ITaskStore
{
    private const int CreateOrUpdate = 6;
    private const int InteractiveTokenLogon = 3;
    private const int IncludeHidden = 1;
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);

    private static string FolderName => ElevatedTask.Folder.TrimStart('\\');

    public bool Exists(string name)
    {
        var folder = Folder(Connect(), create: false);
        if (folder is null)
            return false;
        try
        {
            folder.GetTask(name);
            return true;
        }
        catch (Exception ex) when (ex.HResult is FileNotFound or PathNotFound)
        {
            return false;
        }
    }

    public void Run(string name)
    {
        var folder = Folder(Connect(), create: false) ?? throw new COMException("The task folder does not exist", FileNotFound);
        folder.GetTask(name).Run(null);
    }

    public void Register(string name, string xml, string userSid)
    {
        var folder = Folder(Connect(), create: true)!;
        // Administrators and SYSTEM: everything; the user: read and run (a copy without the rights starts it by the shortcut).
        var sddl = $"D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;{userSid})";
        folder.RegisterTask(name, xml, CreateOrUpdate, null, null, InteractiveTokenLogon, sddl);
    }

    public void Delete(string name)
    {
        var folder = Folder(Connect(), create: false);
        if (folder is null)
            return;
        try
        {
            folder.DeleteTask(name, 0);
        }
        catch (Exception ex) when (ex.HResult is FileNotFound or PathNotFound)
        {
            // already gone
        }
    }

    public void DeleteFolder()
    {
        var service = Connect();
        var folder = Folder(service, create: false);
        if (folder is null)
            return;
        var names = new List<string>();
        foreach (var task in folder.GetTasks(IncludeHidden))
            names.Add((string)task.Name);
        foreach (var name in names)
            folder.DeleteTask(name, 0);
        service.GetFolder(@"\").DeleteFolder(FolderName, 0);
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("The Task Scheduler is not available");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static dynamic? Folder(dynamic service, bool create)
    {
        try
        {
            return service.GetFolder(ElevatedTask.Folder);
        }
        catch (Exception ex) when (ex.HResult is FileNotFound or PathNotFound)
        {
            if (!create)
                return null;
            // Everyone signed in may see the folder; only administrators and SYSTEM change it.
            return service.GetFolder(@"\").CreateFolder(FolderName, "D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GR;;;AU)");
        }
    }
}
