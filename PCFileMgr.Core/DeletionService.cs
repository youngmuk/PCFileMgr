namespace PCFileMgr.Core;

public interface IFileRecycler { void Recycle(string path); }
public sealed record DeleteOutcome(FileNode File, bool Success, string? Error);

public sealed class DeletionService(IFileRecycler recycler)
{
    public IReadOnlyList<DeleteOutcome> Delete(IEnumerable<FileNode> files, string scope, CancellationToken token,
        IProgress<(int Done, int Total)>? progress = null)
    {
        var targets = files.DistinctBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var outcomes = new List<DeleteOutcome>();
        foreach (var file in targets)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                PathPolicy.ValidateDeletion(file, scope);
                recycler.Recycle(file.FullPath);
                outcomes.Add(new(file, true, null));
            }
            catch (OperationCanceledException) { outcomes.Add(new(file, false, "사용자가 취소했습니다.")); break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            { outcomes.Add(new(file, false, ex.Message)); }
            progress?.Report((outcomes.Count, targets.Length));
        }
        return outcomes;
    }
}
