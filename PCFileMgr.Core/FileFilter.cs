using System.Globalization;

namespace PCFileMgr.Core;

public sealed record FileFilter(HashSet<string> Extensions, long? Minimum, long? Maximum, string NameContains, bool Recursive)
{
    public static FileFilter Parse(string extensions, string minimum, string maximum, int unit, string name, bool recursive)
    {
        if (unit is < 0 or > 4) throw new ArgumentException("올바른 크기 단위를 선택하세요.");
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in extensions.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var value = raw.Trim().ToLowerInvariant();
            if (value == "*") continue;
            if (value == "<없음>") { values.Add(""); continue; }
            if (value.StartsWith("*.")) value = value[1..];
            if (!value.StartsWith('.')) value = "." + value;
            if (value.Length < 2 || value[1..].Contains('.') || value.IndexOfAny(['*', '?', '/', '\\', ':']) >= 0)
                throw new ArgumentException("확장자는 .zip, .mp4처럼 입력하세요. 확장자 없는 파일은 <없음>입니다.");
            values.Add(value);
        }
        var min = ParseSize(minimum, unit);
        var max = ParseSize(maximum, unit, upper: true);
        if (min > max) throw new ArgumentException("최소 크기는 최대 크기보다 클 수 없습니다.");
        return new(values, min, max, name.Trim(), recursive);
    }

    private static long? ParseSize(string text, int unit, bool upper = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out var number) &&
            !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number))
            throw new ArgumentException("파일 크기에 0 이상의 숫자를 입력하세요.");
        try
        {
            var result = number * (decimal)Math.Pow(1024, unit);
            if (result < 0 || result > long.MaxValue) throw new OverflowException();
            return (long)(upper ? decimal.Floor(result) : decimal.Ceiling(result));
        }
        catch (OverflowException) { throw new ArgumentException("파일 크기가 허용 범위를 벗어났습니다."); }
    }

    public bool Matches(FileNode file) => !file.IsDirectory &&
        (file.Attributes & (FileAttributes.System | FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == 0 &&
        (Extensions.Count == 0 || Extensions.Contains(file.Extension)) &&
        (!Minimum.HasValue || file.Size >= Minimum.Value) &&
        (!Maximum.HasValue || file.Size <= Maximum.Value) &&
        (NameContains.Length == 0 || file.Name.Contains(NameContains, StringComparison.OrdinalIgnoreCase));
}
