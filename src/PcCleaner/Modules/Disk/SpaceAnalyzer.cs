using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace PcCleaner.Modules.Disk;

/// <summary>A folder in the analysed tree. Sizes are filled in after the scan.</summary>
public sealed class FolderNode
{
    internal static readonly FolderNode[] NoChildren = Array.Empty<FolderNode>();

    internal FolderNode(string name, FolderNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    /// <summary>Folder name (for the root: its full path).</summary>
    public string Name { get; }

    public FolderNode? Parent { get; }

    /// <summary>Subfolders sorted by size (largest first) once the scan has finished.</summary>
    public FolderNode[] Children { get; internal set; } = NoChildren;

    /// <summary>Size of the files directly in this folder.</summary>
    public long OwnBytes { get; internal set; }

    public long OwnFiles { get; internal set; }

    public long TotalBytes { get; internal set; }

    public long TotalFiles { get; internal set; }

    public long TotalFolders { get; internal set; }

    /// <summary>Junction / symbolic link / mount point: not followed and not counted.</summary>
    public bool IsLink { get; internal set; }

    /// <summary>The folder could not be read (usually: needs administrator rights).</summary>
    public bool IsInaccessible { get; internal set; }

    /// <summary>False if the scan was cancelled before this folder was read.</summary>
    public bool IsScanned { get; internal set; }

    public string FullPath => Parent is null ? Name : Path.Join(Parent.FullPath, Name);

    public int Depth
    {
        get
        {
            var depth = 0;
            for (var p = Parent; p is not null; p = p.Parent)
                depth++;
            return depth;
        }
    }
}

public sealed record FileHit(string Path, long Size, DateTime ModifiedUtc, FileCategory Category)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
}

public sealed record CategoryStat(FileCategory Category, long Bytes, long Files);

public sealed class SpaceAnalysisResult
{
    public required FolderNode Root { get; init; }
    public required IReadOnlyList<FileHit> LargestFiles { get; init; }
    public required IReadOnlyList<CategoryStat> Categories { get; init; }
    public int InaccessibleFolders { get; init; }
    public int SkippedLinks { get; init; }
    public long CloudOnlyFiles { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>The scan was cancelled (or timed out): sizes are lower than in reality.</summary>
    public bool IsPartial { get; init; }

    public long TotalBytes => Root.TotalBytes;
    public long FileCount => Root.TotalFiles;
    public long FolderCount => Root.TotalFolders;
}

/// <summary>
/// Fast parallel folder-size analyser. Read-only. Never follows junctions / symbolic links (they would count the
/// same files twice), counts inaccessible folders silently and returns partial results when cancelled.
/// </summary>
public sealed class SpaceAnalyzer
{
    private const int ErrorAccessDenied = 5;
    private const int AttributeRecallOnDataAccess = 0x00400000;
    private const int AttributeOffline = 0x00001000;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false, // errors are handled (and counted) in ContinueOnError
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private readonly PathRules _rules;
    private readonly int _topCount;
    private readonly int _workerCount;

    private long _files;
    private long _bytes;
    private long _folders;
    private int _inaccessible;
    private int _links;
    private long _cloud;
    private string? _currentPath;

    internal SpaceAnalyzer(PathRules? rules = null, int topCount = 100, int? workers = null)
    {
        _rules = rules ?? PathRules.Default;
        _topCount = Math.Max(1, topCount);
        _workerCount = workers ?? Math.Clamp(Environment.ProcessorCount, 2, 8);
    }

    public SpaceAnalyzer()
        : this(null)
    {
    }

    public long FilesScanned => Interlocked.Read(ref _files);

    public long BytesScanned => Interlocked.Read(ref _bytes);

    public long FoldersScanned => Interlocked.Read(ref _folders);

    public string? CurrentPath => Volatile.Read(ref _currentPath);

    /// <summary>Scans <paramref name="rootPath"/>. Cancellation does not throw: a partial result is returned.</summary>
    public async Task<SpaceAnalysisResult> RunAsync(string rootPath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var full = Path.GetFullPath(rootPath);
        var pathRoot = Path.GetPathRoot(full);
        var isDriveRoot = pathRoot is not null &&
                          string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(pathRoot),
                              StringComparison.OrdinalIgnoreCase);
        if (!isDriveRoot)
            full = Path.TrimEndingDirectorySeparator(full);

        var root = new FolderNode(full, null);
        var shared = new Shared(ct);
        shared.Stack.Push(new WorkItem(root, full, _rules.ForRoot(full), isDriveRoot));

        var states = new WorkerState[_workerCount];
        var tasks = new Task[_workerCount];
        for (var i = 0; i < _workerCount; i++)
        {
            var state = states[i] = new WorkerState(_topCount);
            tasks[i] = Task.Factory.StartNew(() => Work(shared, state), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return await Task.Run(() => Finish(root, states, shared, sw)).ConfigureAwait(false);
    }

    private SpaceAnalysisResult Finish(FolderNode root, WorkerState[] states, Shared shared, Stopwatch sw)
    {
        Aggregate(root);

        var categories = new long[FileClassifier.CategoryCount];
        var counts = new long[FileClassifier.CategoryCount];
        var largest = new List<FileHit>();
        foreach (var s in states)
        {
            for (var i = 0; i < categories.Length; i++)
            {
                categories[i] += s.CategoryBytes[i];
                counts[i] += s.CategoryFiles[i];
            }
            while (s.Top.TryDequeue(out var hit, out _))
                largest.Add(hit);
        }

        return new SpaceAnalysisResult
        {
            Root = root,
            LargestFiles = TopN(largest, _topCount),
            Categories = Enumerable.Range(0, categories.Length)
                .Select(i => new CategoryStat((FileCategory)i, categories[i], counts[i]))
                .ToList(),
            InaccessibleFolders = Volatile.Read(ref _inaccessible),
            SkippedLinks = Volatile.Read(ref _links),
            CloudOnlyFiles = Interlocked.Read(ref _cloud),
            Elapsed = sw.Elapsed,
            IsPartial = Volatile.Read(ref shared.Pending) > 0,
        };
    }

    /// <summary>Largest <paramref name="count"/> files, biggest first.</summary>
    internal static List<FileHit> TopN(IEnumerable<FileHit> files, int count)
        => files.OrderByDescending(f => f.Size).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(count).ToList();

    private void Work(Shared shared, WorkerState state)
    {
        var spin = new SpinWait();
        while (true)
        {
            if (shared.Ct.IsCancellationRequested)
                return;
            if (shared.Stack.TryPop(out var item))
            {
                spin.Reset();
                try
                {
                    ProcessDirectory(item, shared, state);
                }
                catch (Exception)
                {
                    // A single odd folder must never stop the whole analysis.
                    item.Node.IsInaccessible = true;
                    item.Node.IsScanned = true;
                    state.Children.Clear();
                    state.NewWork.Clear();
                }
                if (Interlocked.Decrement(ref shared.Pending) == 0)
                    return;
            }
            else
            {
                if (Volatile.Read(ref shared.Pending) == 0)
                    return;
                spin.SpinOnce();
            }
        }
    }

    private void ProcessDirectory(WorkItem item, Shared shared, WorkerState state)
    {
        Volatile.Write(ref _currentPath, item.Path);
        var node = item.Node;

        using (var enumerator = new DirectoryEnumerator(item.Path, this, state, item))
        {
            while (enumerator.MoveNext())
            {
                // all work happens in ShouldIncludeEntry
            }
            node.OwnBytes = enumerator.Bytes;
            node.OwnFiles = enumerator.Files;
            if (enumerator.Error == ErrorAccessDenied)
            {
                node.IsInaccessible = true;
                Interlocked.Increment(ref _inaccessible);
            }
            Interlocked.Add(ref _files, enumerator.Files);
            Interlocked.Add(ref _bytes, enumerator.Bytes);
        }
        node.IsScanned = true;
        Interlocked.Increment(ref _folders);

        var children = state.Children;
        if (children.Count == 0)
            return;
        node.Children = children.ToArray();
        children.Clear();

        var work = state.NewWork;
        if (work.Count > 0)
        {
            Interlocked.Add(ref shared.Pending, work.Count);
            shared.Stack.PushRange(work.ToArray());
            work.Clear();
        }
    }

    private void OnSubdirectory(ref FileSystemEntry entry, WorkerState state, WorkItem parent)
    {
        var name = entry.FileName.ToString();
        var fullPath = Path.Join(parent.Path, name);
        var child = new FolderNode(name, parent.Node);
        state.Children.Add(child);

        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && ReparsePoints.IsLinkOrUnknown(fullPath))
        {
            // Junctions / symlinks / mount points point to data counted elsewhere (or on another drive).
            child.IsLink = true;
            child.IsScanned = true;
            Interlocked.Increment(ref _links);
            return;
        }

        var context = _rules.ForChild(parent.Context, fullPath, name, parent.IsDriveRoot);
        state.NewWork.Add(new WorkItem(child, fullPath, context, false));
    }

    private void OnFile(ref FileSystemEntry entry, WorkerState state, WorkItem parent, ref long files, ref long bytes)
    {
        var attributes = (int)entry.Attributes;
        var length = entry.Length;
        if ((attributes & (AttributeRecallOnDataAccess | AttributeOffline)) != 0)
        {
            // Cloud placeholder (OneDrive "available online only"): takes no space on this disk.
            Interlocked.Increment(ref _cloud);
            length = 0;
        }


        files++;
        bytes += length;
        var category = FileClassifier.Classify(entry.FileName, parent.Context);
        state.CategoryBytes[(int)category] += length;
        state.CategoryFiles[(int)category]++;

        var top = state.Top;
        if (length > 0 && (top.Count < _topCount || (top.TryPeek(out _, out var min) && length > min)))
        {
            var hit = new FileHit(entry.ToFullPath(), length, entry.LastWriteTimeUtc.UtcDateTime, category);
            if (top.Count < _topCount)
                top.Enqueue(hit, length);
            else
                top.EnqueueDequeue(hit, length);
        }
    }

    /// <summary>Computes totals bottom-up and sorts children by size (largest first).</summary>
    internal static void Aggregate(FolderNode root)
    {
        var stack = new Stack<(FolderNode Node, bool Visited)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (node, visited) = stack.Pop();
            if (!visited)
            {
                stack.Push((node, true));
                foreach (var child in node.Children)
                    stack.Push((child, false));
                continue;
            }

            long bytes = node.OwnBytes, files = node.OwnFiles, folders = 0;
            foreach (var child in node.Children)
            {
                bytes += child.TotalBytes;
                files += child.TotalFiles;
                if (!child.IsLink)
                    folders += 1 + child.TotalFolders;
            }
            node.TotalBytes = bytes;
            node.TotalFiles = files;
            node.TotalFolders = folders;
            if (node.Children.Length > 1)
                Array.Sort(node.Children, CompareBySize);
        }
    }

    /// <summary>Re-sorts the children of a folder after its sizes changed.</summary>
    internal static void Resort(FolderNode node)
    {
        if (node.Children.Length > 1)
            Array.Sort(node.Children, CompareBySize);
    }

    private static int CompareBySize(FolderNode a, FolderNode b)
    {
        var c = b.TotalBytes.CompareTo(a.TotalBytes);
        return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record WorkItem(FolderNode Node, string Path, DirContext Context, bool IsDriveRoot);

    private sealed class Shared
    {
        public Shared(CancellationToken ct) => Ct = ct;

        public readonly ConcurrentStack<WorkItem> Stack = new();
        public readonly CancellationToken Ct;
        public int Pending = 1;
    }

    private sealed class WorkerState
    {
        public WorkerState(int topCount)
        {
            Top = new PriorityQueue<FileHit, long>(topCount + 1);
        }

        public readonly long[] CategoryBytes = new long[FileClassifier.CategoryCount];
        public readonly long[] CategoryFiles = new long[FileClassifier.CategoryCount];
        public readonly PriorityQueue<FileHit, long> Top;
        public readonly List<FolderNode> Children = new();
        public readonly List<WorkItem> NewWork = new();
    }

    private sealed class DirectoryEnumerator : FileSystemEnumerator<byte>
    {
        private readonly SpaceAnalyzer _owner;
        private readonly WorkerState _state;
        private readonly WorkItem _item;

        // Assigned from ContinueOnError, which may run inside the base constructor.
        public int Error;
        public long Files;
        public long Bytes;

        public DirectoryEnumerator(string path, SpaceAnalyzer owner, WorkerState state, WorkItem item)
            : base(path, Options)
        {
            _owner = owner;
            _state = state;
            _item = item;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (entry.IsDirectory)
                _owner.OnSubdirectory(ref entry, _state, _item);
            else
                _owner.OnFile(ref entry, _state, _item, ref Files, ref Bytes);
            return false;
        }

        protected override byte TransformEntry(ref FileSystemEntry entry) => 0;

        protected override bool ContinueOnError(int error)
        {
            if (Error == 0)
                Error = error;
            return true;
        }
    }
}

/// <summary>Reparse point inspection (junction / symlink vs. cloud placeholder folders).</summary>
internal static class ReparsePoints
{
    private const uint NameSurrogateBit = 0x20000000;
    private const int FileAttributeReparsePoint = 0x400;

    /// <summary>
    /// True if the reparse point is a junction, symbolic link or mount point ("name surrogate"), or if its tag cannot be
    /// read (be conservative: never follow what we cannot identify). Cloud placeholder folders (OneDrive) return false.
    /// </summary>
    public static bool IsLinkOrUnknown(string fullPath)
    {
        var tag = GetTag(fullPath);
        return tag is null || (tag.Value & NameSurrogateBit) != 0;
    }

    public static uint? GetTag(string fullPath)
    {
        var path = fullPath.Length >= 248 && !fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath
            : fullPath;
        var handle = FindFirstFileExW(path, 1 /* FindExInfoBasic */, out var data, 0 /* FindExSearchNameMatch */,
            IntPtr.Zero, 0);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return null;
        FindClose(handle);
        return (data.dwFileAttributes & FileAttributeReparsePoint) != 0 ? data.dwReserved0 : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public int dwFileAttributes;
        public uint ftCreationTimeLow;
        public uint ftCreationTimeHigh;
        public uint ftLastAccessTimeLow;
        public uint ftLastAccessTimeHigh;
        public uint ftLastWriteTimeLow;
        public uint ftLastWriteTimeHigh;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATAW lpFindFileData,
        int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);
}
