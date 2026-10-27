namespace PCFileMgr.Core;

public readonly record struct MapRect(int Index, double X, double Y, double Width, double Height);

public static class TreemapLayout
{
    // Squarified treemap. Input values are ordered largest first; zero-sized entries have no area.
    public static IReadOnlyList<MapRect> Build(IReadOnlyList<long> sizes, double width, double height)
    {
        var output = new List<MapRect>();
        var total = sizes.Sum(x => (double)Math.Max(0, x));
        if (total <= 0 || width <= 0 || height <= 0) return output;
        var areas = sizes.Select(x => Math.Max(0, x) / total * width * height).ToArray();
        double x = 0, y = 0, w = width, h = height;
        int start = 0;
        while (start < areas.Length && w > 0 && h > 0)
        {
            if (areas[start] <= 0) { start++; continue; }
            int end = start + 1;
            double sum = areas[start], min = areas[start], max = areas[start];
            var side = Math.Min(w, h);
            var worst = Worst(sum, min, max, side);
            while (end < areas.Length && areas[end] > 0)
            {
                var next = areas[end];
                var candidate = Worst(sum + next, Math.Min(min, next), Math.Max(max, next), side);
                if (candidate > worst) break;
                sum += next; min = Math.Min(min, next); max = Math.Max(max, next); worst = candidate; end++;
            }
            if (w >= h)
            {
                var strip = Math.Min(w, sum / h);
                double offset = y;
                for (int i = start; i < end; i++)
                {
                    var length = i == end - 1 ? y + h - offset : areas[i] / strip;
                    output.Add(new(i, x, offset, strip, Math.Max(0, length))); offset += length;
                }
                x += strip; w -= strip;
            }
            else
            {
                var strip = Math.Min(h, sum / w);
                double offset = x;
                for (int i = start; i < end; i++)
                {
                    var length = i == end - 1 ? x + w - offset : areas[i] / strip;
                    output.Add(new(i, offset, y, Math.Max(0, length), strip)); offset += length;
                }
                y += strip; h -= strip;
            }
            start = end;
        }
        return output;
    }

    private static double Worst(double sum, double min, double max, double side) =>
        Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
}
