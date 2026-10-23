using PcCleaner.Core;

namespace PcCleaner.Tests;

/// <summary>Task Scheduler in memory: tests never register, run or delete a real task.</summary>
internal sealed class FakeTaskStore : ITaskStore
{
    public Dictionary<string, (string Xml, string Sid)> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Runs { get; } = new();
    public bool FailRun { get; set; }

    /// <summary>When set, every call throws it: what the real store throws (FileNotFoundException, UnauthorizedAccessException, ...).</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, only <see cref="Run"/> throws it.</summary>
    public Exception? RunFailure { get; set; }

    public bool FolderDeleted { get; private set; }

    public bool Exists(string name)
    {
        if (Failure is not null)
            throw Failure;
        return Tasks.ContainsKey(name);
    }

    public void Run(string name)
    {
        if (Failure is not null)
            throw Failure;
        if (RunFailure is not null)
            throw RunFailure;
        if (FailRun || !Tasks.ContainsKey(name))
            throw new System.Runtime.InteropServices.COMException("run failed");
        Runs.Add(name);
    }

    public void Register(string name, string xml, string userSid)
    {
        if (Failure is not null)
            throw Failure;
        Tasks[name] = (xml, userSid);
    }

    public void Delete(string name)
    {
        if (Failure is not null)
            throw Failure;
        Tasks.Remove(name);
    }

    public void DeleteFolder()
    {
        if (Failure is not null)
            throw Failure;
        Tasks.Clear();
        FolderDeleted = true;
    }
}
