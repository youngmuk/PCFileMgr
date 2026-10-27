namespace PCFileMgr.Core;

// Visual classification only. It never automatically selects files or changes deletion eligibility.
public static class TemporaryClassifier
{
    private static readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase)
    { "temp", "tmp", "cache", "caches", ".cache", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".tmp", ".temp", ".cache", ".part", ".partial", ".crdownload", ".download", ".swp", ".swo" };

    public static bool IsTemporary(string path, bool isDirectory)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - (isDirectory ? 0 : 1); i++)
            if (Folders.Contains(parts[i]) || parts[i].EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || parts[i].EndsWith(".temp", StringComparison.OrdinalIgnoreCase)) return true;
        if (isDirectory || parts.Length == 0) return false;
        var name = parts[^1];
        return name.StartsWith("~$", StringComparison.Ordinal) || name.EndsWith('~') || Extensions.Contains(Path.GetExtension(name));
    }
}
