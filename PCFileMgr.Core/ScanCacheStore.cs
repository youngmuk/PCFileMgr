using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PCFileMgr.Core;

public sealed class ScanCacheStore(string directory, string? legacyDirectory = null)
{
    private const int Version = 2;
    private readonly ScanCacheStore? legacy = legacyDirectory == null || DiskScanner.Same(Path.GetFullPath(directory), Path.GetFullPath(legacyDirectory))
        ? null : new ScanCacheStore(legacyDirectory);
    public static ScanCacheStore ForCurrentUser()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(documents)) throw new DirectoryNotFoundException("Windows 문서 폴더 경로를 찾을 수 없습니다.");
        return new(Path.Combine(documents, "PCFileMgr", "Cache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCFileMgr", "Cache"));
    }
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string GetCachePath(string root) => Path.Combine(DirectoryPath,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant()))) + ".scan.gz");

    public string? ReadLastRoot()
    {
        try { var path = File.ReadAllText(Path.Combine(DirectoryPath, "last-root.txt")); return Directory.Exists(path) ? path : legacy?.ReadLastRoot(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return legacy?.ReadLastRoot(); }
    }
    public void RememberRoot(string path) { Directory.CreateDirectory(DirectoryPath); File.WriteAllText(Path.Combine(DirectoryPath, "last-root.txt"), path); }

    public ScanResult? Load(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Import on demand, including legacy deltas. Save publishes a complete snapshot in
        // the new location after validation; the old cache is never modified or deleted.
        if (!File.Exists(GetCachePath(path))) return legacy?.Load(path, token);
        try
        {
            using var stream = File.OpenRead(GetCachePath(path));
            using var compressed = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new BinaryReader(compressed, Encoding.UTF8);
            var header = ReadHeader(reader, path, "snapshot");
            int count = ReadCount(reader, 10_000_000);
            if (count == 0) return null;
            var nodes = new List<FileNode>(Math.Min(count, 65536));
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                int parentIndex = reader.ReadInt32();
                if (i == 0 ? parentIndex != -1 : parentIndex < 0 || parentIndex >= i || !nodes[parentIndex].IsDirectory) return null;
                var parent = i == 0 ? null : nodes[parentIndex];
                var node = ReadNode(reader, parent, i == 0 ? path : null);
                if (i == 0 && !node.IsDirectory) return null;
                parent?.Children.Add(node); nodes.Add(node);
            }
            if (compressed.ReadByte() != -1) return null;
            var root = nodes[0];
            foreach (var delta in DeltaPaths(path, header.Generation))
            {
                token.ThrowIfCancellationRequested();
                using var deltaStream = File.OpenRead(delta);
                using var deltaCompressed = new GZipStream(deltaStream, CompressionMode.Decompress);
                using var deltaReader = new BinaryReader(deltaCompressed, Encoding.UTF8);
                var next = ReadHeader(deltaReader, path, "delta");
                if (next.Generation != header.Generation) return null;
                int directoryCount = ReadCount(deltaReader, 10_000_000);
                for (int i = 0; i < directoryCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var directoryPath = deltaReader.ReadString();
                    if (!DiskScanner.Same(directoryPath, path) && !PathPolicy.IsWithin(path, directoryPath)) return null;
                    var old = DiskScanner.FindDirectory(root, directoryPath);
                    if (old == null) return null;
                    // Parent is looked up from the live tree, never from a possibly superseded parent link.
                    var parent = DiskScanner.Same(directoryPath, path) ? null : DiskScanner.FindDirectory(root, Path.GetDirectoryName(directoryPath)!);
                    if (parent == null && !DiskScanner.Same(directoryPath, path)) return null;
                    var replacement = ReadNode(deltaReader, parent, parent == null ? path : null);
                    if (!replacement.IsDirectory || !DiskScanner.Same(replacement.FullPath, directoryPath)) return null;
                    var oldChildren = old.Children.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
                    int children = ReadCount(deltaReader, 10_000_000);
                    for (int j = 0; j < children; j++)
                    {
                        token.ThrowIfCancellationRequested();
                        var child = ReadNode(deltaReader, replacement, null);
                        if (child.IsDirectory && oldChildren.TryGetValue(child.Name, out var prior) && prior.IsDirectory && prior.CreationUtc == child.CreationUtc)
                            child.Children.AddRange(prior.Children);
                        replacement.Children.Add(child);
                    }
                    if (parent == null) root = replacement;
                    else parent.Children[parent.Children.IndexOf(old)] = replacement;
                }
                if (deltaCompressed.ReadByte() != -1) return null;
                header = next;
            }
            // Recompute totals and restore parent links once after replay, without trusting stored aggregates.
            nodes.Clear(); nodes.Add(root);
            for (int i = 0; i < nodes.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                foreach (var child in nodes[i].Children) { child.Parent = nodes[i]; nodes.Add(child); }
            }
            for (int i = nodes.Count - 1; i >= 0; i--) if (nodes[i].IsDirectory) DiskScanner.Aggregate(nodes[i]);
            return new(root, DiskScanner.CollectIssues(root, token), root.SubtreeSkippedCount, TimeSpan.Zero)
            { AnalyzedAtUtc = header.Analyzed, VerifiedAtUtc = header.Verified, CacheGeneration = header.Generation, Origin = ScanOrigin.VerifiedCache };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidOperationException)
        { return null; }
    }

    public ScanResult Save(ScanResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Directory.CreateDirectory(DirectoryPath);
        var destination = GetCachePath(result.Root.FullPath);
        // Cold validation still reads metadata, but an unchanged result needs no full
        // tree serialization/compression. A legacy-only cache must still be imported.
        if (result.Origin == ScanOrigin.VerifiedCache && result.ChangedItems == 0 &&
            ReadGeneration(destination, result.Root.FullPath) == result.CacheGeneration)
        {
            RememberRoot(result.Root.FullPath);
            return result;
        }
        var deltas = DeltaPaths(result.Root.FullPath, result.CacheGeneration).ToArray();
        bool canAppend = result.Origin == ScanOrigin.Incremental && File.Exists(destination) && deltas.Length < 32 &&
            ReadGeneration(destination, result.Root.FullPath) == result.CacheGeneration;
        if (canAppend && result.ChangedItems == 0) return result;
        if (canAppend)
        {
            var sequence = deltas.Length == 0 ? 1 : long.Parse(Path.GetFileName(deltas[^1]).Split('.')[4]) + 1;
            destination += $".{result.CacheGeneration:N}.{sequence:D19}.delta.gz";
            AtomicWrite(destination, writer =>
            {
                WriteHeader(writer, result, "delta"); writer.Write(result.EnumeratedNodes.Count);
                foreach (var node in result.EnumeratedNodes)
                {
                    token.ThrowIfCancellationRequested(); writer.Write(node.FullPath);
                    WriteNode(writer, node, node.Parent == null); writer.Write(node.Children.Count);
                    foreach (var child in node.Children) { token.ThrowIfCancellationRequested(); WriteNode(writer, child, false); }
                }
            }, token);
        }
        else
        {
            result = result with { CacheGeneration = Guid.NewGuid() };
            var nodes = new List<(FileNode Node, int Parent)> { (result.Root, -1) };
            for (int i = 0; i < nodes.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                foreach (var child in nodes[i].Node.Children) nodes.Add((child, i));
            }
            AtomicWrite(destination, writer =>
            {
                WriteHeader(writer, result, "snapshot"); writer.Write(nodes.Count);
                foreach (var (node, parent) in nodes)
                { token.ThrowIfCancellationRequested(); writer.Write(parent); WriteNode(writer, node, parent < 0); }
            }, token);
            // Old generations are ignored during load even if cleanup is interrupted.
            foreach (var oldDelta in Directory.EnumerateFiles(DirectoryPath, Path.GetFileName(destination) + ".*.delta.gz"))
            { try { File.Delete(oldDelta); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        RememberRoot(result.Root.FullPath);
        return result;
    }

    private IEnumerable<string> DeltaPaths(string root, Guid generation) => Directory.Exists(DirectoryPath)
        ? Directory.GetFiles(DirectoryPath, Path.GetFileName(GetCachePath(root)) + $".{generation:N}.*.delta.gz").Order(StringComparer.Ordinal)
        : [];

    private static Guid? ReadGeneration(string path, string root)
    {
        try
        {
            using var stream = File.OpenRead(path); using var zip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new BinaryReader(zip); return ReadHeader(reader, root, "snapshot").Generation;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { return null; }
    }

    private static void AtomicWrite(string path, Action<BinaryWriter> write, CancellationToken token)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
                using (var writer = new BinaryWriter(zip, Encoding.UTF8)) write(writer);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested(); File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private sealed record Header(Guid Generation, DateTime Analyzed, DateTime Verified);
    private static Header ReadHeader(BinaryReader reader, string root, string kind)
    {
        if (reader.ReadString() != "PCFileMgr" || reader.ReadInt32() != Version || reader.ReadString() != kind || !DiskScanner.Same(reader.ReadString(), root))
            throw new InvalidDataException("캐시 형식 또는 분석 경로가 다릅니다.");
        return new(new Guid(reader.ReadBytes(16)), new DateTime(reader.ReadInt64(), DateTimeKind.Utc), new DateTime(reader.ReadInt64(), DateTimeKind.Utc));
    }
    private static void WriteHeader(BinaryWriter writer, ScanResult result, string kind)
    {
        writer.Write("PCFileMgr"); writer.Write(Version); writer.Write(kind); writer.Write(result.Root.FullPath);
        writer.Write(result.CacheGeneration.ToByteArray()); writer.Write(result.AnalyzedAtUtc.Ticks); writer.Write(result.VerifiedAtUtc.Ticks);
    }
    private static int ReadCount(BinaryReader reader, int maximum)
    { int count = reader.ReadInt32(); if (count < 0 || count > maximum) throw new InvalidDataException("잘못된 캐시 항목 수입니다."); return count; }
    private static FileNode ReadNode(BinaryReader reader, FileNode? parent, string? root)
    {
        var name = reader.ReadString();
        if (root != null ? name != "" : name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new InvalidDataException("잘못된 캐시 파일명입니다.");
        var isDirectory = reader.ReadBoolean(); var size = reader.ReadInt64();
        var modified = new DateTime(reader.ReadInt64(), DateTimeKind.Utc); var created = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        var attributes = (FileAttributes)reader.ReadInt32(); long skipped = reader.ReadInt64(); bool failed = reader.ReadBoolean();
        if (size < 0 || skipped < 0 || (attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("잘못된 캐시 파일 속성입니다.");
        var node = new FileNode { FullPath = root ?? Path.Combine(parent!.FullPath, name), Parent = parent, IsDirectory = isDirectory,
            Size = size, LastWriteUtc = modified, CreationUtc = created, Attributes = attributes, FileCount = isDirectory ? 0 : 1,
            OwnSkippedCount = skipped, ReadFailed = failed };
        int issues = ReadCount(reader, 500);
        for (int i = 0; i < issues; i++) node.OwnIssues.Add(new(reader.ReadString(), reader.ReadString()));
        return node;
    }
    private static void WriteNode(BinaryWriter writer, FileNode node, bool root)
    {
        writer.Write(root ? "" : node.Name); writer.Write(node.IsDirectory); writer.Write(node.Size);
        writer.Write(node.LastWriteUtc.Ticks); writer.Write(node.CreationUtc.Ticks); writer.Write((int)node.Attributes);
        writer.Write(node.OwnSkippedCount); writer.Write(node.ReadFailed); writer.Write(node.OwnIssues.Count);
        foreach (var issue in node.OwnIssues) { writer.Write(issue.Path); writer.Write(issue.Reason); }
    }
}
