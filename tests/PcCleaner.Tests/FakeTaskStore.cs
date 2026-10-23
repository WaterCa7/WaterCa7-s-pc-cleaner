using PcCleaner.Core;

namespace PcCleaner.Tests;

/// <summary>Task Scheduler in memory: tests never register, run or delete a real task.</summary>
internal sealed class FakeTaskStore : ITaskStore
{
    public Dictionary<string, (string Xml, string Sid)> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Runs { get; } = new();
    public bool FailRun { get; set; }
    public bool FolderDeleted { get; private set; }

    public bool Exists(string name) => Tasks.ContainsKey(name);

    public void Run(string name)
    {
        if (FailRun || !Tasks.ContainsKey(name))
            throw new System.Runtime.InteropServices.COMException("run failed");
        Runs.Add(name);
    }

    public void Register(string name, string xml, string userSid) => Tasks[name] = (xml, userSid);

    public void Delete(string name) => Tasks.Remove(name);

    public void DeleteFolder()
    {
        Tasks.Clear();
        FolderDeleted = true;
    }
}
