using HexelEditor.Core;
using SkiaSharp;

namespace HexelEditor.Desktop;

// Where a cell sits on the canvas, how large it is, and which cells a viewport can reach: the axial
// arithmetic of HexGeometry in the canvas's own units, in Skia's types.
//
// The artwork is drawn by GpuHexRenderer, which does all of this per pixel in a shader instead. What is
// here serves the things drawn over the artwork -- the selection, the shape previews and the cursor ring,
// which are still Skia paths on the CPU -- and the bounds the view is fitted to.
public static class CanvasGeometry
{
    public const float CellRadius = 12;

    public static SKRect Bounds(HexDocument document, float radius = CellRadius)
    {
        var halfWidth = (float)(Math.Sqrt(3) * radius / 2);
        return document.Orientation == HexOrientation.PointyTop
            ? new(-halfWidth, -radius, (float)(Math.Sqrt(3) * radius * (document.Width - 1 + (document.Height > 1 ? 0.5 : 0))) + halfWidth, (document.Height - 1) * 1.5f * radius + radius)
            : new(-radius, -halfWidth, (document.Width - 1) * 1.5f * radius + radius, (float)(Math.Sqrt(3) * radius * (document.Height - 1 + (document.Width > 1 ? 0.5 : 0))) + halfWidth);
    }

    public static bool IsVisible(SKRect viewport, double x, double y, float radius = CellRadius) =>
        x + radius >= viewport.Left && x - radius <= viewport.Right && y + radius >= viewport.Top && y - radius <= viewport.Bottom;

    public static SKPath Hexagon(HexOrientation orientation, float radius = CellRadius)
    {
        using var path = new SKPathBuilder();
        for (var corner = 0; corner < 6; corner++)
        {
            var angle = Math.PI / 180 * (60 * corner + (orientation == HexOrientation.PointyTop ? -30 : 0));
            var x = (float)Math.Cos(angle) * radius;
            var y = (float)Math.Sin(angle) * radius;
            if (corner == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        path.Close(); return path.Detach();
    }

    // How much of the document a viewport can actually show, in columns and rows. Asking each of two million
    // cells whether it is on screen costs the same whether nine or nine hundred are: the work belongs to the
    // document rather than the view, which is the shape of problem a large canvas makes expensive. Inverting
    // the layout instead gives the span directly, and the cost then follows the window. A whole-canvas
    // selection is what this is for now: its highlights are drawn one cell at a time.
    //
    // The span is deliberately generous by a cell on each side: cells overlap their neighbours' bands, and
    // alternate rows sit half a cell across, so the arithmetic is kept simple rather than exact.
    public static (int MinColumn, int MinRow, int MaxColumn, int MaxRow) VisibleCells(
        HexDocument document, SKRect viewport, float radius = CellRadius)
    {
        var across = (float)(Math.Sqrt(3) * radius);
        var down = 1.5f * radius;
        if (document.Orientation == HexOrientation.FlatTop) (across, down) = (down, across);
        var minColumn = (int)Math.Floor(viewport.Left / across) - 1;
        var maxColumn = (int)Math.Ceiling(viewport.Right / across) + 1;
        var minRow = (int)Math.Floor(viewport.Top / down) - 1;
        var maxRow = (int)Math.Ceiling(viewport.Bottom / down) + 1;
        return (Math.Max(0, minColumn), Math.Max(0, minRow),
            Math.Min(document.Width - 1, maxColumn), Math.Min(document.Height - 1, maxRow));
    }
}
