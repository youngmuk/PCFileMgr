namespace PCFileMgr.Core;

public static class PathPolicy
{
    public static string ValidateLocalDirectory(string path)
    {
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
            throw new IOException("네트워크 경로는 지원하지 않습니다. 로컬 드라이브를 선택하세요.");
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("로컬 고정 드라이브 또는 이동식 드라이브를 선택하세요.");
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("폴더가 존재하지 않거나 접근할 수 없습니다.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    public static bool IsWithin(string root, string path)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureNoReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("연결 지점 또는 심볼릭 링크를 포함하는 경로는 사용할 수 없습니다.");
            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }
    }

    public static void ValidateDeletion(FileNode file, string scope)
    {
        if (file.IsDirectory) throw new IOException("폴더는 삭제 대상이 아닙니다.");
        if (!IsWithin(scope, file.FullPath)) throw new IOException("선택한 폴더 범위 밖의 파일입니다.");
        EnsureNoReparseAncestors(file.FullPath);
        var current = new FileInfo(file.FullPath);
        if (!current.Exists) throw new FileNotFoundException("파일이 이미 이동되거나 삭제되었습니다.");
        if ((current.Attributes & (FileAttributes.System | FileAttributes.ReadOnly)) != 0)
            throw new IOException("시스템 / 읽기 전용 파일은 정리 대상에서 제외됩니다.");
        if (current.Length != file.Size || current.LastWriteTimeUtc != file.LastWriteUtc ||
            (file.CreationUtc != default && current.CreationTimeUtc != file.CreationUtc))
            throw new IOException("분석 후 변경된 파일입니다. 다시 분석한 뒤 선택하세요.");
    }
}
