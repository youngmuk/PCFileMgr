namespace PCFileMgr.Core;

public sealed class FileNode
{
    public required string FullPath { get; init; }
    public string Name => Path.GetFileName(FullPath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : FullPath;
    public bool IsDirectory { get; init; }
    public bool? TemporaryOverride { get; init; }
    public bool IsTemporary => TemporaryOverride ?? TemporaryClassifier.IsTemporary(FullPath, IsDirectory);
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; init; }
    public DateTime CreationUtc { get; init; }
    public FileAttributes Attributes { get; init; }
    public FileNode? Parent { get; set; }
    public List<FileNode> Children { get; } = [];
    public long FileCount { get; set; }
    public long FolderCount { get; set; }
    public bool IsPartial { get; set; }
    public long OwnSkippedCount { get; set; }
    public bool ReadFailed { get; set; }
    public long SubtreeSkippedCount { get; set; }
    public bool HasReadFailures { get; set; }
    public List<ScanIssue> OwnIssues { get; } = [];
    public string Extension => IsDirectory ? "폴더" : Path.GetExtension(FullPath).ToLowerInvariant();
    public string SizeText => SizeFormat.Format(Size);
    public string KindText => (IsTemporary ? "임시 · " : "") + (IsDirectory ? (IsPartial ? "폴더 · 일부 제외" : "폴더") : (Extension.Length == 0 ? "확장자 없음" : Extension));
    public string Symbol => IsDirectory ? "▰" : "▪";
    public string ModifiedText => LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public double Percent => Parent is { Size: > 0 } ? (double)Size / Parent.Size * 100 : 0;
    public string PercentText => $"{Percent:0.0}%";

    public IEnumerable<FileNode> EnumerateFiles(bool recursive, CancellationToken token = default)
    {
        var pending = new Stack<FileNode>();
        pending.Push(this);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (var child in directory.Children)
            {
                token.ThrowIfCancellationRequested();
                if (!child.IsDirectory) yield return child;
                else if (recursive) pending.Push(child);
            }
        }
    }
}

public static class SizeFormat
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{size:N0} B" : $"{size:0.##} {units[unit]}";
    }
}

public sealed record ScanProgress(long Files, long Folders, long Bytes, long Skipped, string CurrentPath);
public sealed record ScanIssue(string Path, string Reason);
public enum ScanOrigin { FullScan, VerifiedCache, Incremental, MemoryCache }

public sealed record ScanResult(FileNode Root, IReadOnlyList<ScanIssue> Issues, long SkippedCount, TimeSpan Duration)
{
    public DateTime AnalyzedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime VerifiedAtUtc { get; init; } = DateTime.UtcNow;
    public ScanOrigin Origin { get; init; }
    public long EnumeratedDirectories { get; init; }
    public long ReusedDirectories { get; init; }
    public long ChangedItems { get; init; }
    public string? CacheWarning { get; init; }
    internal Guid CacheGeneration { get; init; } = Guid.NewGuid();
    internal IReadOnlyList<FileNode> EnumeratedNodes { get; init; } = [];
    internal IReadOnlyList<(FileNode Child, FileNode Parent)> ParentRepairs { get; init; } = [];
    internal void CommitParentLinks() { foreach (var (child, parent) in ParentRepairs) child.Parent = parent; }
}
