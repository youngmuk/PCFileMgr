namespace PCFileMgr.Core;

public sealed record ChangeBatch(bool RequiresValidation, IReadOnlyCollection<string> Paths);

public interface IChangeTracker : IDisposable
{
    bool HasChanges { get; }
    bool IsReliable { get; }
    DateTime LastChangeUtc { get; }
    ChangeBatch Take();
    void Invalidate();
    void MarkChanged(string path);
}

public sealed class DirectoryChangeTracker : IChangeTracker
{
    private readonly object gate = new();
    private readonly string root, excluded;
    private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? watcher;
    private bool validate, reliable;
    private DateTime lastChange;
    public bool HasChanges { get { lock (gate) return validate || paths.Count > 0; } }
    public bool IsReliable { get { lock (gate) return reliable; } }
    public DateTime LastChangeUtc { get { lock (gate) return lastChange; } }

    public DirectoryChangeTracker(string root, string excluded)
    {
        this.root = root; this.excluded = excluded;
        try
        {
            watcher = new(root)
            {
                IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                    NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.CreationTime | NotifyFilters.Security
            };
            watcher.Created += Changed; watcher.Changed += Changed; watcher.Deleted += Changed;
            watcher.Renamed += (_, e) => { MarkChanged(e.OldFullPath); MarkChanged(e.FullPath); };
            watcher.Error += (_, _) => { lock (gate) { reliable = false; validate = true; lastChange = DateTime.UtcNow; } };
            reliable = true; watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { watcher?.Dispose(); watcher = null; reliable = false; }
    }

    private void Changed(object sender, FileSystemEventArgs e) => MarkChanged(e.FullPath);
    public void MarkChanged(string path)
    {
        if (DiskScanner.Same(path, excluded) || PathPolicy.IsWithin(excluded, path)) return;
        if (!DiskScanner.Same(path, root) && !PathPolicy.IsWithin(root, path)) return;
        lock (gate)
        {
            lastChange = DateTime.UtcNow;
            if (validate) return;
            paths.Add(path);
            if (paths.Count > 4096) { paths.Clear(); validate = true; }
        }
    }
    public void Invalidate() { lock (gate) { validate = true; lastChange = DateTime.UtcNow; } }
    public ChangeBatch Take()
    {
        lock (gate)
        {
            var batch = new ChangeBatch(validate || !reliable, paths.ToArray());
            validate = false; paths.Clear(); return batch;
        }
    }
    public void Dispose() { watcher?.Dispose(); watcher = null; }
}
