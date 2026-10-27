using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PCFileMgr.Core;

namespace PCFileMgr;

public sealed class TreemapView : FrameworkElement
{
    private IReadOnlyList<FileNode> nodes = [];
    private IReadOnlyList<MapRect> rectangles = [];
    private FileNode? hovered;
    private FileNode? selected;
    private static readonly Brush[] Palette = [Brush("#286E67"), Brush("#345D8C"), Brush("#68598C"), Brush("#4D6E89"), Brush("#3D7483"), Brush("#577B52"), Brush("#885770"), Brush("#4F6398")];
    private static readonly Brush TemporaryFill = Brush("#965B29");
    private static readonly Pen TemporaryBorder = new(Brush("#E8AD68"), 1);
    public event Action<FileNode>? Selected;
    public event Action<FileNode>? Navigated;
    public int HiddenCount { get; private set; }

    public TreemapView()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        MouseMove += (_, e) =>
        {
            var node = Hit(e.GetPosition(this));
            if (node == hovered) return;
            hovered = node;
            ToolTip = node is null ? null : $"{node.Name}\n{node.SizeText} · {node.PercentText}\n{node.FullPath}\n{(node.IsTemporary ? "임시 / 캐시 항목 (이름·위치 기준)\n" : "")}{(node.IsDirectory ? "더블클릭하여 폴더 열기" : "클릭하여 목록에서 선택")}";
            InvalidateVisual();
        };
        MouseLeave += (_, _) => { hovered = null; InvalidateVisual(); };
        MouseLeftButtonDown += (_, e) =>
        {
            var node = Hit(e.GetPosition(this));
            if (node == null) return;
            selected = node; InvalidateVisual(); Selected?.Invoke(node);
            if (e.ClickCount == 2 && node.IsDirectory) Navigated?.Invoke(node);
        };
    }

    public void SetFolder(FileNode? node)
    {
        var all = node?.Children.Where(n => n.Size > 0).OrderByDescending(n => n.Size).ToArray() ?? [];
        // Keep the map responsive for huge folders, with an honest aggregate for the tail.
        HiddenCount = all.Length > 400 ? all.Length - 398 : 0;
        if (HiddenCount > 0)
        {
            var tail = all.Skip(398).GroupBy(n => n.IsTemporary).Select(group => new FileNode
            { FullPath = $"{(group.Key ? "임시 항목" : "기타")} {group.Count():N0}개 (아래 목록에서 확인)",
                Size = group.Sum(x => x.Size), Parent = node, TemporaryOverride = group.Key });
            nodes = all.Take(398).Concat(tail).OrderByDescending(n => n.Size).ToArray();
        }
        else nodes = all;
        hovered = selected = null; InvalidateVisual();
    }

    public void Select(FileNode? node) { selected = node; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        rectangles = TreemapLayout.Build(nodes.Select(n => n.Size).ToArray(), ActualWidth, ActualHeight);
        for (int i = 0; i < rectangles.Count; i++)
        {
            var box = rectangles[i];
            var node = nodes[box.Index];
            var gap = Math.Min(3, Math.Min(box.Width, box.Height) / 5);
            var rect = new Rect(box.X + gap / 2, box.Y + gap / 2, Math.Max(0, box.Width - gap), Math.Max(0, box.Height - gap));
            if (rect.Width < 0.3 || rect.Height < 0.3) continue;
            var pen = node == selected ? new Pen(Brushes.White, 2) : node == hovered ? new Pen(Brush("#A7F4DE"), 1.5) : node.IsTemporary ? TemporaryBorder : null;
            dc.DrawRoundedRectangle(node.IsTemporary ? TemporaryFill : Palette[i % Palette.Length], pen, rect, 4, 4);
            if (rect.Width < 48 || rect.Height < 30) continue;
            dc.PushClip(new RectangleGeometry(rect));
            DrawText(dc, (node.IsTemporary ? "[임시] " : "") + node.Name, rect.X + 10, rect.Y + 9, Math.Max(1, rect.Width - 20), 12, Brushes.White);
            if (rect.Height >= 55) DrawText(dc, node.SizeText, rect.X + 10, rect.Y + 31, Math.Max(1, rect.Width - 20), rect.Width > 140 && rect.Height > 90 ? 22 : 13, Brush("#DBEAF5"));
            if (rect.Height >= 92 && rect.Width > 100) DrawText(dc, node.PercentText, rect.X + 10, rect.Bottom - 27, rect.Width - 20, 11, Brush("#CBDCE9"));
            dc.Pop();
        }
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, double width, double size, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Malgun Gothic"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, new Point(x, y));
    }

    private FileNode? Hit(Point point)
    {
        foreach (var r in rectangles)
            if (new Rect(r.X, r.Y, r.Width, r.Height).Contains(point)) return nodes[r.Index];
        return null;
    }
    private static Brush Brush(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
}
