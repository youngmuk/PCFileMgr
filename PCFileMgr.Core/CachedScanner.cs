using System.Diagnostics;

namespace PCFileMgr.Core;

// Called serially by the UI worker. Watcher callbacks only touch the tracker's locked event queue.
public sealed class CachedScanner : IDisposable
{
    private readonly ScanCacheStore store;
    private readonly Func<string, string, IChangeTracker> trackerFactory;
    private IChangeTracker? tracker;
    private ScanResult? snapshot;
    private string? activeRoot;
    private bool disposed;
    private readonly Dictionary<string, TrackingSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private sealed class TrackingSession(IChangeTracker tracker)
    {
        public IChangeTracker Tracker { get; } = tracker;
        public Guid? SavedGeneration { get; set; }
        public long LastUse { get; set; }
    }
    public string? LastRoot => store.ReadLastRoot();
    public bool HasChanges => tracker?.HasChanges == true;
    public bool IsWatching => tracker?.IsReliable == true;
    public DateTime LastChangeUtc => tracker?.LastChangeUtc ?? DateTime.MinValue;

    public CachedScanner(ScanCacheStore store, Func<string, string, IChangeTracker>? trackerFactory = null)
    { this.store = store; this.trackerFactory = trackerFactory ?? ((root, excluded) => new DirectoryChangeTracker(root, excluded)); }

    public ScanResult Analyze(string path, bool force, IProgress<ScanProgress>? progress, CancellationToken token,
        Action<ScanResult>? preview = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        path = PathPolicy.ValidateLocalDirectory(path);
        PathPolicy.EnsureNoReparseAncestors(path);
        var timer = Stopwatch.StartNew();
        bool newSession = activeRoot == null || !DiskScanner.Same(path, activeRoot);
        if (newSession) snapshot = null;
        bool tracked = sessions.TryGetValue(path, out var session) && session.Tracker.IsReliable;
        if (!tracked)
        {
            session?.Tracker.Dispose();
            // Subscribe BEFORE reading the cache or scanning; concurrent changes remain pending.
            session = new(trackerFactory(path, store.DirectoryPath)); sessions[path] = session;
        }
        activeRoot = path; tracker = session!.Tracker; session.LastUse = Stopwatch.GetTimestamp();
        // Keep watchers (not large trees) for recently visited roots, so switching drives is incremental.
        while (sessions.Count > 8)
        {
            var oldest = sessions.Where(s => !DiskScanner.Same(s.Key, path)).MinBy(s => s.Value.LastUse);
            oldest.Value.Tracker.Dispose(); sessions.Remove(oldest.Key);
        }
        var currentTracker = tracker!;
        var batch = currentTracker.Take();
        try
        {
            var prior = force ? null : snapshot ?? store.Load(path, token);
            if (prior != null) preview?.Invoke(prior);
            IReadOnlyCollection<string>? dirty = null;
            if (!force && tracked && prior != null && (!newSession || session.SavedGeneration == prior.CacheGeneration) && !batch.RequiresValidation)
            {
                var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in batch.Paths)
                {
                    // Include both a directory itself and its containing directory. This handles
                    // disappearance, rename, and file/directory replacements without stale branches.
                    if (DiskScanner.FindDirectory(prior!.Root, entry) != null) changed.Add(entry);
                    var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(entry));
                    while (parent != null && DiskScanner.FindDirectory(prior!.Root, parent) == null) parent = Path.GetDirectoryName(parent);
                    if (parent != null) changed.Add(parent);
                }
                dirty = changed;
                if (dirty.Count == 0)
                {
                    if (newSession)
                    {
                        try { store.RememberRoot(path); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    return snapshot = prior with { Origin = ScanOrigin.MemoryCache, Duration = timer.Elapsed, VerifiedAtUtc = DateTime.UtcNow,
                        EnumeratedDirectories = 0, ReusedDirectories = prior.Root.FolderCount + 1, ChangedItems = 0 };
                }
            }
            var result = new DiskScanner().Scan(path, progress, token, prior, dirty, store.DirectoryPath, shareUnchanged: true);
            token.ThrowIfCancellationRequested();
            try { result = store.Save(result, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { result = result with { CacheWarning = "캐시 저장 실패: " + ex.Message }; }
            // Commit shared branches only after cancellable scanning / persistence has finished.
            result.CommitParentLinks();
            snapshot = result with { Duration = timer.Elapsed, ParentRepairs = [], EnumeratedNodes = [] };
            if (result.CacheWarning == null) session.SavedGeneration = result.CacheGeneration;
            return snapshot;
        }
        catch { currentTracker.Invalidate(); throw; }
    }

    public void MarkChanged(string path) => tracker?.MarkChanged(path);
    public void Invalidate() => tracker?.Invalidate();
    public void Dispose() { disposed = true; foreach (var session in sessions.Values) session.Tracker.Dispose(); tracker = null; }
}
