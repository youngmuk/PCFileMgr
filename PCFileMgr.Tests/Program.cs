using System.IO;
using PCFileMgr;
using PCFileMgr.Core;

internal static class Program
{
    private static int passed;
    [STAThread]
    private static int Main(string[] args)
    {
        var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.test-data"));
        var fixture = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(fixture, "nested")).FullName;
            WriteFile(Path.Combine(fixture, "big.ZIP"), 8192);
            WriteFile(Path.Combine(fixture, "empty"), 0);
            WriteFile(Path.Combine(nested, "small.log"), 1024);
            WriteFile(Path.Combine(nested, "movie.mp4"), 4096);
            Directory.CreateDirectory(Path.Combine(fixture, "empty-directory"));
            var result = new DiskScanner().Scan(fixture, null, default);
            var root = result.Root;
            Check("nested byte totals", root.Size == 13312);
            Check("file/folder counts", root.FileCount == 4 && root.FolderCount == 2);
            Check("no unexpected skips", result.SkippedCount == 0 && !root.IsPartial);
            Check("direct vs recursive", root.EnumerateFiles(false).Count() == 2 && root.EnumerateFiles(true).Count() == 4);
            var filter = FileFilter.Parse("*.zip, LOG", "1", "8", 1, "", true);
            Check("extensions + inclusive size range", root.EnumerateFiles(true).Count(filter.Matches) == 2);
            Check("extensionless filter", root.EnumerateFiles(true).Count(FileFilter.Parse("<없음>", "", "", 0, "", true).Matches) == 1);
            Check("case insensitive name", root.EnumerateFiles(true).Count(FileFilter.Parse("", "", "", 0, "BIG", true).Matches) == 1);
            Throws<ArgumentException>("invalid extension", () => FileFilter.Parse("*.tar.gz", "", "", 0, "", true));
            Throws<ArgumentException>("negative size", () => FileFilter.Parse("", "-1", "", 0, "", true));
            Throws<ArgumentException>("reversed size bounds", () => FileFilter.Parse("", "10", "1", 0, "", true));
            Throws<ArgumentException>("size overflow", () => FileFilter.Parse("", "99999999999999999999999999", "", 4, "", true));
            var cts = new CancellationTokenSource(); cts.Cancel();
            Throws<OperationCanceledException>("scan cancellation", () => new DiskScanner().Scan(fixture, null, cts.Token));
            Check("scope boundary", !PathPolicy.IsWithin(fixture, fixture + "-outside\\x.zip"));
            Check("drive root scope", PathPolicy.IsWithin(Path.GetPathRoot(fixture)!, fixture));
            Check("fractional maximum rounds down", FileFilter.Parse("", "", "0.5", 0, "", true).Maximum == 0);
            var file = root.EnumerateFiles(true).First(n => n.Name == "big.ZIP");
            PathPolicy.ValidateDeletion(file, fixture);
            Check("unchanged file validated", true);
            Throws<IOException>("folder deletion rejected", () => PathPolicy.ValidateDeletion(root, fixture));
            Throws<IOException>("outside scope rejected", () => PathPolicy.ValidateDeletion(file, nested));
            var fake = new FakeRecycler();
            var outcomes = new DeletionService(fake).Delete([file, file], fixture, default);
            Check("duplicate targets processed once", fake.Paths.Count == 1 && outcomes.Count == 1 && outcomes[0].Success);
            File.AppendAllText(file.FullPath, "changed");
            outcomes = new DeletionService(fake).Delete([file], fixture, default);
            Check("changed file rejected before recycle", outcomes.Count == 1 && !outcomes[0].Success && fake.Paths.Count == 1);
            var readOnly = root.EnumerateFiles(true).First(n => n.Name == "small.log");
            File.SetAttributes(readOnly.FullPath, FileAttributes.ReadOnly);
            Throws<IOException>("read-only target rejected", () => PathPolicy.ValidateDeletion(readOnly, fixture));
            File.SetAttributes(readOnly.FullPath, FileAttributes.Normal);
            var modifiedAttributes = new FileNode { FullPath = readOnly.FullPath, Attributes = FileAttributes.System };
            Check("system files excluded by filter", !FileFilter.Parse("", "", "", 0, "", true).Matches(modifiedAttributes));
            Check("cancelled deletion does no work", new DeletionService(fake).Delete([readOnly], fixture, cts.Token).Count == 0);
            TestMap();
            var veto = new RecycleOnlySink();
            Check("permanent deletion veto", veto.PreDeleteItem(0, IntPtr.Zero) < 0 && veto.PermanentDeleteBlocked);
            Check("recycling permitted", new RecycleOnlySink().PreDeleteItem(0x80, IntPtr.Zero) == 0);
            CacheTests.Run(Path.Combine(fixture, "cache-tests"), Check);
            Check("temporary extension ignores case", TemporaryClassifier.IsTemporary(@"D:\Data\save.TMP", false));
            Check("temporary parent colors contained files", TemporaryClassifier.IsTemporary(@"C:\Users\person\AppData\Local\Temp\report.pdf", false));
            Check("cache folder colors descendants", TemporaryClassifier.IsTemporary(@"D:\Work\.cache\nested", true));
            Check("temporary directory suffix", TemporaryClassifier.IsTemporary(@"D:\Work\build.temp", true));
            Check("partial download recognized", TemporaryClassifier.IsTemporary(@"D:\Downloads\archive.zip.crdownload", false));
            Check("office lock file recognized", TemporaryClassifier.IsTemporary(@"D:\Documents\~$report.docx", false));
            Check("similar folder names are not temporary", !TemporaryClassifier.IsTemporary(@"D:\Templates\report.docx", false));
            Check("backup files not marked disposable", !TemporaryClassifier.IsTemporary(@"D:\Backups\database.bak", false));
            if (args.Contains("--recycle-smoke"))
            {
                var scratch = Path.Combine(fixture, "PCFileMgr-recycle-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(scratch, "Disposable PCFileMgr integration-test file.");
                new WindowsRecycler(IntPtr.Zero).Recycle(scratch);
                Check("native Windows recycle operation", !File.Exists(scratch));
            }
            Console.WriteLine($"PASS: {passed} checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            var resolved = Path.GetFullPath(fixture);
            if (resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static void TestMap()
    {
        var random = new Random(42);
        foreach (var count in new[] { 1, 2, 30, 400, 2000 })
        {
            var sizes = Enumerable.Range(0, count).Select(_ => random.NextInt64(1, 1000000)).OrderDescending().ToArray();
            var rects = TreemapLayout.Build(sizes, 800, 360);
            var sum = sizes.Sum();
            Check($"treemap {count} area conservation", Math.Abs(rects.Sum(r => r.Width * r.Height) - 800 * 360) < 0.01);
            Check($"treemap {count} proportional area", rects.All(r => Math.Abs(r.Width * r.Height / (800 * 360) - (double)sizes[r.Index] / sum) < 0.0000001));
            Check($"treemap {count} bounds", rects.All(r => r.X >= -0.001 && r.Y >= -0.001 && r.X + r.Width <= 800.001 && r.Y + r.Height <= 360.001));
            Check($"treemap {count} no overlap", !rects.Where((r, i) => rects.Skip(i + 1).Any(s => Math.Min(r.X + r.Width, s.X + s.Width) - Math.Max(r.X, s.X) > 0.001 && Math.Min(r.Y + r.Height, s.Y + s.Height) - Math.Max(r.Y, s.Y) > 0.001)).Any());
        }
        Check("zero sizes have no area", TreemapLayout.Build([0, 0], 800, 300).Count == 0);
    }
    private static void WriteFile(string path, int bytes) { using var stream = File.Create(path); stream.SetLength(bytes); }
    private static void Check(string label, bool ok) { if (!ok) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS " + label); }
    private static void Throws<T>(string label, Action action) where T : Exception { try { action(); } catch (T) { Check(label, true); return; } throw new Exception("FAIL: " + label); }
    private sealed class FakeRecycler : IFileRecycler { public List<string> Paths { get; } = []; public void Recycle(string path) => Paths.Add(path); }
}
