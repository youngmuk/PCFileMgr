using System.IO;
using PCFileMgr.Core;

internal static class CacheTests
{
    public static void Run(string fixture, Action<string, bool> check)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "data")).FullName;
        var a = Directory.CreateDirectory(Path.Combine(root, "a")).FullName;
        var deep = Directory.CreateDirectory(Path.Combine(a, "deep")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(root, "b")).FullName;
        var one = Path.Combine(a, "one.dat"); var two = Path.Combine(b, "two.dat");
        Write(one, 100); Write(two, 200); Write(Path.Combine(deep, "deep.dat"), 40); Write(Path.Combine(root, "empty"), 0);
        var store = new ScanCacheStore(Path.Combine(fixture, "cache"));
        var tracker = new FakeTracker();
        using var service = new CachedScanner(store, (_, _) => tracker);
        var first = service.Analyze(root, false, null, default);
        check("cache initial full scan", first.Origin == ScanOrigin.FullScan && first.EnumeratedDirectories == 4 && first.Root.Size == 340);
        var disk = store.Load(root, default)!;
        check("persistent cache round trip", disk != null && disk.Root.Size == 340 && disk.Root.FileCount == 4 && disk.Root.FolderCount == 3);
        check("cache preserves parent links", disk!.Root.EnumerateFiles(true).All(f => f.Parent != null && f.Parent.Children.Contains(f)));
        check("cache preserves creation and analysis timestamps", disk.AnalyzedAtUtc == first.AnalyzedAtUtc && disk.Root.EnumerateFiles(true).First(f => f.Name == "one.dat").CreationUtc == File.GetCreationTimeUtc(one));
        check("cache remembers last root", store.ReadLastRoot() == root);
        var originalBytes = File.ReadAllBytes(store.GetCachePath(root));
        var unchanged = service.Analyze(root, false, null, default);
        check("unchanged scan performs zero directory enumeration", unchanged.Origin == ScanOrigin.MemoryCache && unchanged.EnumeratedDirectories == 0 && unchanged.ReusedDirectories == 4);
        check("unchanged scan reuses snapshot and timestamp", ReferenceEquals(first.Root, unchanged.Root) && first.AnalyzedAtUtc == unchanged.AnalyzedAtUtc);
        check("unchanged scan avoids cache rewrite", originalBytes.SequenceEqual(File.ReadAllBytes(store.GetCachePath(root))));

        Write(one, 175); tracker.MarkChanged(one);
        var edited = service.Analyze(root, false, null, default);
        check($"changed folder alone is enumerated ({edited.Origin}, checked={edited.EnumeratedDirectories}, reused={edited.ReusedDirectories})", edited.Origin == ScanOrigin.Incremental && edited.EnumeratedDirectories == 1 && edited.ReusedDirectories == 3);
        check("incremental size reaches ancestors", edited.Root.Size == 415 && edited.Root.Children.First(n => n.Name == "a").Size == 215);
        check("prior snapshot stays immutable", first.Root.Size == 340 && first.Root.EnumerateFiles(true).First(f => f.Name == "one.dat").Size == 100);
        check("unchanged branch shared rather than copied", ReferenceEquals(first.Root.Children.First(n => n.Name == "b"), edited.Root.Children.First(n => n.Name == "b")));
        check("shared branch points to current parent", ReferenceEquals(edited.Root.Children.First(n => n.Name == "b").Parent, edited.Root));
        check("incremental persistence does not rewrite base snapshot", originalBytes.SequenceEqual(File.ReadAllBytes(store.GetCachePath(root))) && Directory.GetFiles(store.DirectoryPath, "*.delta.gz").Length == 1);
        check("persisted delta replays correct totals", store.Load(root, default)?.Root.Size == 415);

        var parentTime = Directory.GetLastWriteTimeUtc(deep);
        var backdated = Path.Combine(deep, "backdated.dat"); Write(backdated, 30);
        File.SetLastWriteTimeUtc(backdated, first.AnalyzedAtUtc.AddYears(-3)); Directory.SetLastWriteTimeUtc(deep, parentTime);
        tracker.MarkChanged(backdated);
        var added = service.Analyze(root, false, null, default);
        check("backdated new file detected without directory timestamp change", added.Root.Size == 445 && added.Root.FileCount == 5 && added.EnumeratedDirectories == 1);

        File.Delete(two); File.Delete(Path.Combine(root, "empty")); tracker.MarkChanged(two); tracker.MarkChanged(Path.Combine(root, "empty"));
        var removed = service.Analyze(root, false, null, default);
        check("deletions update totals and keep empty directories", removed.Root.Size == 245 && removed.Root.FileCount == 3 && removed.Root.Children.First(n => n.Name == "b").Size == 0);

        var renamed = Path.Combine(root, "renamed"); Directory.Move(a, renamed); tracker.MarkChanged(a); tracker.MarkChanged(renamed);
        var moved = service.Analyze(root, false, null, default);
        check("directory rename rebuilds descendant paths", moved.Root.Size == 245 && moved.Root.EnumerateFiles(true).All(n => n.FullPath.StartsWith(renamed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        check("delta replay handles create delete and directory rename", store.Load(root, default)?.Root.EnumerateFiles(true).All(n => n.FullPath.StartsWith(renamed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) == true && store.Load(root, default)?.Root.Size == 245);
        one = Path.Combine(renamed, "one.dat");
        File.SetAttributes(one, FileAttributes.ReadOnly); tracker.MarkChanged(one);
        var attributes = service.Analyze(root, false, null, default);
        check("attribute changes update cleanup eligibility", !FileFilter.Parse("", "", "", 0, "", true).Matches(attributes.Root.EnumerateFiles(true).First(f => f.Name == "one.dat")));
        File.SetAttributes(one, FileAttributes.Normal); tracker.MarkChanged(one);
        service.Analyze(root, false, null, default);

        var beforeCancel = File.ReadAllBytes(store.GetCachePath(root));
        Write(one, 200); tracker.MarkChanged(one);
        using (var cancelled = new CancellationTokenSource())
        {
            bool caught = false;
            try { service.Analyze(root, false, null, cancelled.Token, _ => cancelled.Cancel()); }
            catch (OperationCanceledException) { caught = true; }
            check("cancellation preserves existing disk cache", caught && beforeCancel.SequenceEqual(File.ReadAllBytes(store.GetCachePath(root))));
            check("cancelled event batch remains invalidated", service.HasChanges);
        }
        var recovered = service.Analyze(root, false, null, default);
        check("cancelled update recovers changes on retry", recovered.Root.Size == 270 && recovered.Root.FileCount == 3);

        using (var restarted = new CachedScanner(store, (_, _) => new FakeTracker()))
        {
            var cacheBefore = File.ReadAllBytes(store.GetCachePath(root));
            bool previewed = false;
            var verified = restarted.Analyze(root, false, null, default, _ => previewed = true);
            check("restart previews cache then verifies all directories", previewed && verified.Origin == ScanOrigin.VerifiedCache && verified.EnumeratedDirectories == 4);
            check("unchanged restart preserves original analysis time", verified.ChangedItems == 0 && verified.AnalyzedAtUtc == recovered.AnalyzedAtUtc);
            check("unchanged restart avoids snapshot recompression", cacheBefore.SequenceEqual(File.ReadAllBytes(store.GetCachePath(root))));
        }

        var migratedStore = new ScanCacheStore(Path.Combine(fixture, "documents", "PCFileMgr", "Cache"), store.DirectoryPath);
        check("new location discovers legacy last root", migratedStore.ReadLastRoot() == root);
        check("legacy snapshot and deltas load on demand", migratedStore.Load(root, default)?.Root.Size == recovered.Root.Size && !Directory.Exists(migratedStore.DirectoryPath));
        var legacyBytes = File.ReadAllBytes(store.GetCachePath(root));
        using (var migrated = new CachedScanner(migratedStore, (_, _) => new FakeTracker()))
        {
            var imported = migrated.Analyze(root, false, null, default);
            check("verified legacy cache saved in new location", File.Exists(migratedStore.GetCachePath(root)) && imported.Root.Size == recovered.Root.Size);
            check("legacy cache preserved during import", legacyBytes.SequenceEqual(File.ReadAllBytes(store.GetCachePath(root))));
        }
        var newOnly = new ScanCacheStore(migratedStore.DirectoryPath);
        check("imported cache works without legacy store", newOnly.Load(root, default)?.Root.Size == recovered.Root.Size && newOnly.ReadLastRoot() == root);
        var ownStore = ScanCacheStore.ForCurrentUser();
        check("cache location uses Windows Documents", ownStore.DirectoryPath == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify), "PCFileMgr", "Cache"));
        var originalCwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = fixture;
            check("cache location independent of working directory", ScanCacheStore.ForCurrentUser().DirectoryPath == ownStore.DirectoryPath);
        }
        finally { Environment.CurrentDirectory = originalCwd; }

        var originalDirectoryTime = Directory.GetLastWriteTimeUtc(renamed);
        var originalFileTime = File.GetLastWriteTimeUtc(one);
        Write(one, 250); File.SetLastWriteTimeUtc(one, originalFileTime); Directory.SetLastWriteTimeUtc(renamed, originalDirectoryTime);
        using (var offline = new CachedScanner(store, (_, _) => new FakeTracker()))
        {
            var verified = offline.Analyze(root, false, null, default);
            check("offline size change detected despite unchanged timestamps", verified.Root.Size == 320 && verified.ChangedItems > 0);
        }
        // Simulate a lost notification buffer: no individual paths are available.
        Write(one, 260); tracker.Invalidate();
        var overflow = service.Analyze(root, false, null, default);
        check("lost notifications trigger complete metadata validation", overflow.Origin == ScanOrigin.VerifiedCache && overflow.EnumeratedDirectories == 4 && overflow.Root.Size == 330);
        var forced = service.Analyze(root, true, null, default);
        check("forced analysis ignores cache", forced.Origin == ScanOrigin.FullScan && forced.EnumeratedDirectories == 4);
        ScanResult compacted = forced;
        for (int i = 1; i <= 33; i++)
        {
            Write(one, 260 + i); tracker.MarkChanged(one);
            compacted = service.Analyze(root, false, null, default);
        }
        check("delta journal compacts after 32 updates", Directory.GetFiles(store.DirectoryPath, "*.delta.gz").Length == 0 && store.Load(root, default)?.Root.Size == compacted.Root.Size);
        Write(one, 260); tracker.MarkChanged(one); service.Analyze(root, false, null, default);
        var brokenDelta = Directory.GetFiles(store.DirectoryPath, "*.delta.gz").Single();
        File.WriteAllBytes(brokenDelta, [3, 1, 4]);
        check("corrupt delta rejects the complete stale cache", store.Load(root, default) == null);
        using (var replayRepair = new CachedScanner(store, (_, _) => new FakeTracker()))
        {
            var rebuilt = replayRepair.Analyze(root, false, null, default);
            check("corrupt delta recovers with a fresh snapshot", rebuilt.Origin == ScanOrigin.FullScan && rebuilt.Root.Size == 330 && Directory.GetFiles(store.DirectoryPath, "*.delta.gz").Length == 0);
        }

        File.WriteAllBytes(store.GetCachePath(root), [1, 2, 3, 4]);
        check("corrupt cache is rejected", store.Load(root, default) == null);
        using (var corrupted = new CachedScanner(store, (_, _) => new FakeTracker()))
        {
            var rebuilt = corrupted.Analyze(root, false, null, default);
            check("corrupt cache falls back to full scan", rebuilt.Origin == ScanOrigin.FullScan && rebuilt.Root.Size == 330 && store.Load(root, default) != null);
        }
        using (var cancelled = new CancellationTokenSource())
        {
            var before = File.ReadAllBytes(store.GetCachePath(root)); cancelled.Cancel();
            try { store.Save(forced, cancelled.Token); } catch (OperationCanceledException) { }
            check("cancelled save leaves no partial cache", before.SequenceEqual(File.ReadAllBytes(store.GetCachePath(root))) && Directory.GetFiles(store.DirectoryPath, "*.tmp").Length == 0);
        }
        var invalidDirectory = Path.Combine(fixture, "not-a-directory"); File.WriteAllText(invalidDirectory, "fixture");
        using (var unableToSave = new CachedScanner(new ScanCacheStore(invalidDirectory), (_, _) => new FakeTracker()))
        {
            var stillUsable = unableToSave.Analyze(root, false, null, default);
            check("cache write failure does not discard valid scan", stillUsable.Root.Size == 330 && stillUsable.CacheWarning != null);
        }
        var inside = new ScanCacheStore(Path.Combine(root, "internal-cache"));
        Directory.CreateDirectory(inside.DirectoryPath); File.WriteAllText(Path.Combine(inside.DirectoryPath, "ignored.tmp"), "ignored");
        using (var excluded = new CachedScanner(inside, (_, _) => new FakeTracker()))
        {
            var excludingCache = excluded.Analyze(root, false, null, default);
            check("own cache excluded from scan", excludingCache.Root.Size == 330 && excludingCache.Root.Children.All(n => n.Name != "internal-cache"));
        }
        using (var realTracker = new DirectoryChangeTracker(root, inside.DirectoryPath))
        {
            realTracker.MarkChanged(Path.Combine(inside.DirectoryPath, "ignored.tmp"));
            check("own cache events do not trigger scans", !realTracker.HasChanges);
            Write(Path.Combine(b, "watcher-test.tmp"), 17);
            check("real Windows watcher reports file creation", SpinWait.SpinUntil(() => realTracker.HasChanges, TimeSpan.FromSeconds(5)));
            check("watcher preserves changes arriving after a drain", realTracker.Take().Paths.Count > 0 && !realTracker.HasChanges);
            realTracker.MarkChanged(one);
            check("next batch retains later event", realTracker.Take().Paths.Contains(one));
        }
        FakeTracker? replaceable = null;
        using (var recovery = new CachedScanner(store, (_, _) => replaceable = new FakeTracker()))
        {
            recovery.Analyze(root, false, null, default);
            replaceable!.Reliable = false;
            var retry = recovery.Analyze(root, false, null, default);
            check("failed watcher is replaced and cache verified", retry.Origin == ScanOrigin.VerifiedCache && recovery.IsWatching);
            check("recovered watcher resumes zero-IO cache hits", recovery.Analyze(root, false, null, default).Origin == ScanOrigin.MemoryCache);
        }
        var otherRoot = Directory.CreateDirectory(Path.Combine(fixture, "other-root")).FullName;
        var rootTrackers = new Dictionary<string, FakeTracker>(StringComparer.OrdinalIgnoreCase);
        using (var switching = new CachedScanner(store, (path, _) => rootTrackers[path] = new FakeTracker()))
        {
            switching.Analyze(root, false, null, default);
            switching.Analyze(otherRoot, false, null, default);
            var back = switching.Analyze(root, false, null, default);
            check("switching back to a watched root avoids directory IO", back.Origin == ScanOrigin.MemoryCache && back.EnumeratedDirectories == 0 && store.ReadLastRoot() == root);
            switching.Analyze(otherRoot, false, null, default);
            Write(one, 280); rootTrackers[root].MarkChanged(one);
            var updated = switching.Analyze(root, false, null, default);
            check("inactive root retains its pending changes", updated.Origin == ScanOrigin.Incremental && updated.Root.EnumerateFiles(true).First(f => f.Name == "one.dat").Size == 280);
        }
    }

    private static void Write(string path, long size) { using var file = File.Create(path); file.SetLength(size); }
    private sealed class FakeTracker : IChangeTracker
    {
        private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        private bool validate;
        public bool HasChanges => validate || paths.Count > 0;
        public bool Reliable { get; set; } = true;
        public bool IsReliable => Reliable;
        public DateTime LastChangeUtc { get; private set; }
        public void MarkChanged(string path) { paths.Add(path); LastChangeUtc = DateTime.UtcNow; }
        public void Invalidate() => validate = true;
        public ChangeBatch Take() { var result = new ChangeBatch(validate, paths.ToArray()); validate = false; paths.Clear(); return result; }
        public void Dispose() { }
    }
}
