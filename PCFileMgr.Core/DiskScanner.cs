using System.Diagnostics;
using System.Security;

namespace PCFileMgr.Core;

public sealed class DiskScanner
{
    // A null dirty set verifies all metadata after an offline interval. A non-null set enumerates
    // only those directories. Never mutate the previous tree, which may still be displayed by WPF.
    public ScanResult Scan(string path, IProgress<ScanProgress>? progress, CancellationToken token,
        ScanResult? previous = null, IReadOnlyCollection<string>? dirtyDirectories = null, string? excludedPath = null, bool shareUnchanged = false)
    {
        var rootPath = PathPolicy.ValidateLocalDirectory(path);
        PathPolicy.EnsureNoReparseAncestors(rootPath);
        if (previous != null && !Same(previous.Root.FullPath, rootPath)) throw new ArgumentException("캐시의 분석 범위가 다릅니다.");
        var started = DateTime.UtcNow;
        var timer = Stopwatch.StartNew();
        var dirty = dirtyDirectories == null ? null : new HashSet<string>(dirtyDirectories, StringComparer.OrdinalIgnoreCase);
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (dirty != null)
        {
            foreach (var item in dirty)
            {
                var ancestor = item;
                while (Same(ancestor, rootPath) || PathPolicy.IsWithin(rootPath, ancestor))
                {
                    affected.Add(ancestor);
                    if (Same(ancestor, rootPath)) break;
                    ancestor = Path.GetDirectoryName(ancestor)!;
                }
            }
        }
        var root = NewDirectory(new DirectoryInfo(rootPath), null);
        var pending = new Stack<(FileNode Node, FileNode? Old, bool Aggregate)>();
        pending.Push((root, previous?.Root, false));
        long files = 0, folders = 0, bytes = 0, skipped = 0, lastReport = -200, enumerated = 0, reused = 0, changed = 0;
        int savedIssues = 0;
        var enumeratedNodes = new List<FileNode>();
        var parentRepairs = new List<(FileNode Child, FileNode Parent)>();

        void Report(string location)
        {
            if (timer.ElapsedMilliseconds - lastReport < 150) return;
            progress?.Report(new(files, folders, bytes, skipped, location)); lastReport = timer.ElapsedMilliseconds;
        }
        void Skip(FileNode node, string location, string reason, bool readFailed = false)
        {
            node.OwnSkippedCount++; skipped++; node.ReadFailed |= readFailed;
            if (savedIssues++ < 500) node.OwnIssues.Add(new(location, reason));
        }
        void AddFile(FileNode node, FileNode file) { node.Children.Add(file); files++; bytes += file.Size; }

        while (pending.TryPop(out var work))
        {
            token.ThrowIfCancellationRequested();
            var (node, old, aggregate) = work;
            if (aggregate) { Aggregate(node); continue; }
            pending.Push((node, old, true));
            if (old != null && dirty != null && !dirty.Contains(node.FullPath))
            {
                // Unchanged ancestors need no directory IO. Their descendants may still be dirty.
                node.OwnSkippedCount = old.OwnSkippedCount; node.ReadFailed = old.ReadFailed;
                node.OwnIssues.AddRange(old.OwnIssues); skipped += old.OwnSkippedCount; reused++;
                foreach (var child in old.Children)
                {
                    token.ThrowIfCancellationRequested();
                    if (!child.IsDirectory) AddFile(node, Copy(child, node));
                    else if (affected.Contains(child.FullPath))
                    {
                        var copy = NewDirectory(new DirectoryInfo(child.FullPath), node); node.Children.Add(copy); folders++;
                        pending.Push((copy, child, false));
                    }
                    else
                    {
                        var copy = shareUnchanged ? child : CloneBranch(child, node, token); node.Children.Add(copy);
                        if (shareUnchanged) parentRepairs.Add((child, node));
                        folders += copy.FolderCount + 1; files += copy.FileCount; bytes += copy.Size;
                        reused += copy.FolderCount + 1; skipped += copy.SubtreeSkippedCount;
                    }
                }
                Report(node.FullPath); continue;
            }
            enumerated++;
            enumeratedNodes.Add(node);
            var oldChildren = old?.Children.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, FileNode>(StringComparer.OrdinalIgnoreCase);
            if (old == null || !SameMetadata(node, old)) changed++;
            try
            {
                PathPolicy.EnsureNoReparseAncestors(node.FullPath);
                foreach (var entry in new DirectoryInfo(node.FullPath).EnumerateFileSystemInfos("*", new EnumerationOptions
                { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (excludedPath != null && (Same(entry.FullName, excludedPath) || PathPolicy.IsWithin(excludedPath, entry.FullName))) continue;
                        var attributes = entry.Attributes;
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        { Skip(node, entry.FullName, "연결 지점 / 심볼릭 링크 / 일부 클라우드 파일 제외"); continue; }
                        if (entry.Name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                        { Skip(node, entry.FullName, "휴지통 / 볼륨 관리 폴더 제외"); continue; }
                        oldChildren.Remove(entry.Name, out var prior);
                        if (entry is DirectoryInfo directory)
                        {
                            var child = NewDirectory(directory, node);
                            // A replaced directory must not inherit its previous contents.
                            var reusable = prior is { IsDirectory: true } && prior.CreationUtc == child.CreationUtc ? prior : null;
                            if (prior != null && reusable == null) changed += prior.FileCount + prior.FolderCount + 1;
                            if (reusable != null && dirty != null && !SameMetadata(child, reusable))
                            { dirty.Add(child.FullPath); affected.Add(child.FullPath); }
                            if (reusable != null && dirty != null && !affected.Contains(child.FullPath) && shareUnchanged)
                            {
                                node.Children.Add(reusable); parentRepairs.Add((reusable, node));
                                folders += reusable.FolderCount + 1; files += reusable.FileCount; bytes += reusable.Size;
                                reused += reusable.FolderCount + 1; skipped += reusable.SubtreeSkippedCount;
                                continue;
                            }
                            node.Children.Add(child); folders++;
                            pending.Push((child, reusable, false));
                        }
                        else if (entry is FileInfo file)
                        {
                            var child = new FileNode { FullPath = file.FullName, Parent = node, Size = file.Length,
                                LastWriteUtc = file.LastWriteTimeUtc, CreationUtc = file.CreationTimeUtc, Attributes = attributes, FileCount = 1 };
                            AddFile(node, child);
                            if (prior == null || prior.IsDirectory || !SameMetadata(child, prior) || child.Size != prior.Size) changed++;
                        }
                    }
                    catch (Exception ex) when (IsFileSystemError(ex)) { Skip(node, entry.FullName, ex.Message, true); }
                    Report(node.FullPath);
                }
            }
            catch (Exception ex) when (IsFileSystemError(ex)) { Skip(node, node.FullPath, ex.Message, true); }
            changed += oldChildren.Values.Sum(c => c.IsDirectory ? c.FileCount + c.FolderCount + 1 : 1);
            if (old != null && (old.OwnSkippedCount != node.OwnSkippedCount || old.ReadFailed != node.ReadFailed)) changed++;
        }
        var issues = CollectIssues(root, token);
        progress?.Report(new(files, folders, bytes, skipped, rootPath));
        return new(root, issues, skipped, timer.Elapsed)
        {
            AnalyzedAtUtc = previous != null && changed == 0 ? previous.AnalyzedAtUtc : started, VerifiedAtUtc = DateTime.UtcNow,
            Origin = previous == null ? ScanOrigin.FullScan : dirty == null ? ScanOrigin.VerifiedCache : ScanOrigin.Incremental,
            EnumeratedDirectories = enumerated, ReusedDirectories = reused, ChangedItems = changed,
            EnumeratedNodes = enumeratedNodes, ParentRepairs = parentRepairs,
            CacheGeneration = previous?.CacheGeneration ?? Guid.NewGuid()
        };
    }

    internal static bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
    // Directory index timestamps may lag their entries on NTFS; they are not a content-change signal.
    private static bool SameMetadata(FileNode a, FileNode b) => (a.IsDirectory || a.LastWriteUtc == b.LastWriteUtc) &&
        a.CreationUtc == b.CreationUtc && a.Attributes == b.Attributes && a.FullPath == b.FullPath;
    private static FileNode NewDirectory(DirectoryInfo info, FileNode? parent) => new()
    { FullPath = info.FullName, IsDirectory = true, Parent = parent, LastWriteUtc = info.LastWriteTimeUtc, CreationUtc = info.CreationTimeUtc, Attributes = info.Attributes };
    internal static FileNode Copy(FileNode old, FileNode? parent) => new()
    { FullPath = old.FullPath, IsDirectory = old.IsDirectory, Parent = parent, LastWriteUtc = old.LastWriteUtc,
        CreationUtc = old.CreationUtc, Attributes = old.Attributes, Size = old.IsDirectory ? 0 : old.Size, FileCount = old.IsDirectory ? 0 : 1 };

    private static FileNode CloneBranch(FileNode old, FileNode parent, CancellationToken token)
    {
        var root = Copy(old, parent);
        var queue = new Stack<(FileNode Old, FileNode New)>(); queue.Push((old, root));
        while (queue.TryPop(out var pair))
        {
            token.ThrowIfCancellationRequested();
            pair.New.Size = pair.Old.Size; pair.New.FileCount = pair.Old.FileCount; pair.New.FolderCount = pair.Old.FolderCount;
            pair.New.IsPartial = pair.Old.IsPartial; pair.New.OwnSkippedCount = pair.Old.OwnSkippedCount;
            pair.New.ReadFailed = pair.Old.ReadFailed; pair.New.OwnIssues.AddRange(pair.Old.OwnIssues);
            pair.New.SubtreeSkippedCount = pair.Old.SubtreeSkippedCount; pair.New.HasReadFailures = pair.Old.HasReadFailures;
            foreach (var child in pair.Old.Children)
            {
                token.ThrowIfCancellationRequested();
                var copy = Copy(child, pair.New); pair.New.Children.Add(copy);
                if (child.IsDirectory) queue.Push((child, copy));
            }
        }
        return root;
    }
    internal static void Aggregate(FileNode node)
    {
        node.Size = node.FileCount = node.FolderCount = 0; node.IsPartial = node.OwnSkippedCount > 0;
        node.SubtreeSkippedCount = node.OwnSkippedCount; node.HasReadFailures = node.ReadFailed;
        foreach (var child in node.Children)
        {
            node.Size += child.Size; node.FileCount += child.FileCount;
            node.FolderCount += child.FolderCount + (child.IsDirectory ? 1 : 0); node.IsPartial |= child.IsPartial;
            node.SubtreeSkippedCount += child.SubtreeSkippedCount; node.HasReadFailures |= child.HasReadFailures;
        }
    }
    internal static IReadOnlyList<ScanIssue> CollectIssues(FileNode root, CancellationToken token)
    {
        var issues = new List<ScanIssue>();
        var pending = new Stack<FileNode>(); pending.Push(root);
        while (pending.TryPop(out var node) && issues.Count < 500)
        {
            token.ThrowIfCancellationRequested();
            issues.AddRange(node.OwnIssues.Take(500 - issues.Count));
            if (!node.IsPartial) continue;
            foreach (var child in node.Children) if (child.IsDirectory && child.IsPartial) pending.Push(child);
        }
        return issues;
    }
    internal static IEnumerable<string> FailedDirectories(FileNode root, CancellationToken token)
    {
        var pending = new Stack<FileNode>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if (node.ReadFailed) yield return node.FullPath;
            if (!node.HasReadFailures) continue;
            foreach (var child in node.Children) if (child.IsDirectory && child.HasReadFailures) pending.Push(child);
        }
    }
    internal static FileNode? FindDirectory(FileNode root, string path)
    {
        if (Same(root.FullPath, path)) return root;
        if (!PathPolicy.IsWithin(root.FullPath, path)) return null;
        var node = root;
        foreach (var part in Path.GetRelativePath(root.FullPath, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Children.FirstOrDefault(n => n.IsDirectory && string.Equals(n.Name, part, StringComparison.OrdinalIgnoreCase));
            if (node == null) return null;
        }
        return node;
    }
    internal static IEnumerable<FileNode> Directories(FileNode root, CancellationToken token)
    {
        var queue = new Stack<FileNode>(); queue.Push(root);
        while (queue.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested(); yield return node;
            foreach (var child in node.Children) if (child.IsDirectory) queue.Push(child);
        }
    }
    private static bool IsFileSystemError(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException;
}
