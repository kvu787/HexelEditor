using HexelEditor.Core;
using SkiaSharp;

namespace HexelEditor.Desktop;

public static class ArtworkRenderer
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

    // Two antialiased fills that merely touch each cover a part of the pixels along their shared edge, and
    // neither part ever reaches the whole, so whatever lies behind them shows through as a hairline. A canvas
    // of those hairlines reads as a grid even when no grid is drawn. Cells therefore overlap by enough that
    // one side or the other covers every edge pixel whole: three quarters of a pixel past each edge covers
    // any pixel a hexagon's edge angles can straddle, and still reads as the same shape in the same place.
    private const float SeamOverlap = 0.75f;

    // An edge sits closer to the center than a corner does, so growing the radius moves it by cos(30 degrees)
    // of that growth. Far enough out, a whole cell is thinner than a pixel, and the overlap stops growing.
    private static float Bleed(float scale) =>
        Math.Min(scale > 0.01f ? SeamOverlap * 2 / (float)Math.Sqrt(3) / scale : CellRadius, CellRadius / 8);

    private static float CanvasScale(SKCanvas canvas)
    {
        var matrix = canvas.TotalMatrix;
        return (float)Math.Sqrt(Math.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY));
    }

    // How much of the document a viewport can actually show, in columns and rows. Asking each of two million
    // cells whether it is on screen costs the same whether nine or nine hundred are: the work belongs to the
    // document rather than the view, which is the shape of problem a large canvas makes expensive. Inverting
    // the layout instead gives the span directly, and the cost then follows the window.
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

    // Below this many pixels across, a cell is too small for its shape to read and the raster stands in for
    // it. Above the second, cells are few enough and large enough that the exact antialiased outline is both
    // affordable and worth having. Between them a flat mesh carries the colour without the per-cell cost.
    private const float RasterBelowPixels = 3f;
    private const float ExactAbovePixels = 14f;

    // Within the mesh, a cell too small for its corners to read is drawn as the band it occupies: two
    // triangles rather than four. Setting a triangle up costs the same whatever its size, and that setup is
    // what the mesh spends its time on, so halving the count halves the frame. The cells still sit exactly
    // where they belong, including the half-cell offset of alternate rows, which is what separates this from
    // the raster: no scaling, no resampling, only a simpler outline.
    private const float HexagonAbovePixels = 7f;
    private const int MeshBatchCells = 4096;

    public enum CellDetail { Raster, Mesh, Exact }

    public static CellDetail DetailFor(float cellPixels, bool rasterAvailable) =>
        !rasterAvailable || cellPixels >= ExactAbovePixels ? CellDetail.Exact
        : cellPixels < RasterBelowPixels ? CellDetail.Raster
        : CellDetail.Mesh;

    public static void Draw(SKCanvas canvas, HexDocument document, bool background = false, bool grid = false,
        float zoom = 1, ArtworkCache? cache = null)
    {
        var cellPixels = (float)(Math.Sqrt(3) * CellRadius * CanvasScale(canvas));
        switch (DetailFor(cellPixels, cache != null))
        {
            case CellDetail.Raster:
                DrawRaster(canvas, document, cache!);
                break;
            case CellDetail.Mesh:
                DrawMesh(canvas, document, background);
                break;
            default:
                DrawExact(canvas, document, background, grid, zoom);
                return;
        }
        // A grid of lines closer together than a pixel is only a grey wash, which is why it stops down there.
        if (grid && zoom >= 0.3) DrawGrid(canvas, document, zoom);
    }

    // One scaled blit of the raster, which costs what the window costs however large the canvas is.
    private static void DrawRaster(SKCanvas canvas, HexDocument document, ArtworkCache cache)
    {
        var raster = cache.Refresh(document);
        using var image = SKImage.FromBitmap(raster);
        // Linear filtering, because several cells fall in a pixel at this size and taking just one of them
        // would make the artwork shimmer as the view moves.
        canvas.DrawImage(image, new SKRect(0, 0, raster.Width, raster.Height), cache.DestinationFor(document),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
    }

    // Four triangles a cell, every visible cell in one call per batch, flat colours and no antialiasing.
    // Cells meet exactly here instead of overlapping, so there is no seam to hide and nothing blends twice.
    private static void DrawMesh(SKCanvas canvas, HexDocument document, bool background)
    {
        var viewport = canvas.LocalClipBounds;
        var (minColumn, minRow, maxColumn, maxRow) = VisibleCells(document, viewport);
        if (minColumn > maxColumn || minRow > maxRow) return;
        if (background)
        {
            using var backgroundPaint = new SKPaint { Color = new SKColor(document.BackgroundColor) };
            canvas.DrawRect(viewport, backgroundPaint);
        }
        // Every cell is the same shape in a different place, so its corner offsets are worked out once here
        // rather than from sine and cosine per cell.
        var cellPixels = (float)(Math.Sqrt(3) * CellRadius * CanvasScale(canvas));
        var fan = cellPixels >= HexagonAbovePixels ? MeshFan(document.Orientation) : BandFan(document.Orientation);
        var fanLength = fan.Length;
        var vertices = new SKPoint[MeshBatchCells * fanLength];
        var colors = new SKColor[MeshBatchCells * fanLength];
        var filled = 0;
        // Skia blends the vertex colours against the paint, so the paint has to be white and the blend a
        // modulation for a cell to come out the colour it actually is. A default paint is black, which makes
        // every cell black.
        using var paint = new SKPaint { Color = SKColors.White };
        void Flush()
        {
            if (filled == 0) return;
            // Skia takes the vertex count from the array's length, so a part-filled batch is copied to size.
            // That is one allocation for the last batch of a frame rather than one per cell.
            var points = filled == vertices.Length ? vertices : vertices[..filled];
            var tints = filled == colors.Length ? colors : colors[..filled];
            canvas.DrawVertices(SKVertexMode.Triangles, points, null, tints, SKBlendMode.Modulate, null, paint);
            filled = 0;
        }
        for (var row = minRow; row <= maxRow; row++)
        {
            var rowStart = row * document.Width;
            for (var column = minColumn; column <= maxColumn; column++)
            {
                var index = rowStart + column;
                var color = document.CompositeAt(index);
                if ((color >> 24) == 0) continue;
                var (x, y) = HexGeometry.Center(document.CoordinateAt(index), document.Orientation, CellRadius);
                var tint = new SKColor(color);
                for (var corner = 0; corner < fanLength; corner++)
                {
                    vertices[filled + corner] = new SKPoint((float)x + fan[corner].X, (float)y + fan[corner].Y);
                    colors[filled + corner] = tint;
                }
                filled += fanLength;
                if (filled == vertices.Length) Flush();
            }
        }
        Flush();
    }

    // The twelve corners of a hexagon drawn as four triangles fanned from its first corner, as offsets from
    // the cell's centre.
    private static SKPoint[] MeshFan(HexOrientation orientation)
    {
        var corners = new SKPoint[6];
        for (var corner = 0; corner < 6; corner++)
        {
            var angle = Math.PI / 180 * (60 * corner + (orientation == HexOrientation.PointyTop ? -30 : 0));
            corners[corner] = new SKPoint((float)(Math.Cos(angle) * CellRadius), (float)(Math.Sin(angle) * CellRadius));
        }
        var fan = new SKPoint[12];
        for (var triangle = 0; triangle < 4; triangle++)
        {
            fan[triangle * 3] = corners[0];
            fan[triangle * 3 + 1] = corners[triangle + 1];
            fan[triangle * 3 + 2] = corners[triangle + 2];
        }
        return fan;
    }

    // The six corners of the band a cell occupies, drawn as two triangles: a cell's pitch across and its row
    // pitch down, which is what tiles the plane.
    private static SKPoint[] BandFan(HexOrientation orientation)
    {
        var across = (float)(Math.Sqrt(3) * CellRadius);
        var down = 1.5f * CellRadius;
        if (orientation == HexOrientation.FlatTop) (across, down) = (down, across);
        var left = -across / 2;
        var right = across / 2;
        var top = -down / 2;
        var bottom = down / 2;
        return
        [
            new SKPoint(left, top), new SKPoint(right, top), new SKPoint(right, bottom),
            new SKPoint(left, top), new SKPoint(right, bottom), new SKPoint(left, bottom)
        ];
    }

    // Every visible cell's outline in one path, stroked once. Saving the canvas, translating it and stroking
    // a path per cell costs about as much again as filling the cells did, and at a zoom showing twenty
    // thousand of them that was the slowest thing left in a frame.
    private static void DrawGrid(SKCanvas canvas, HexDocument document, float zoom)
    {
        var (minColumn, minRow, maxColumn, maxRow) = VisibleCells(document, canvas.LocalClipBounds);
        if (minColumn > maxColumn || minRow > maxRow) return;
        using var cell = Hexagon(document.Orientation);
        using var outlines = new SKPathBuilder();
        for (var row = minRow; row <= maxRow; row++)
            for (var column = minColumn; column <= maxColumn; column++)
            {
                var (x, y) = HexGeometry.Center(document.CoordinateAt(row * document.Width + column), document.Orientation, CellRadius);
                outlines.AddPath(cell, (float)x, (float)y);
            }
        using var grid = outlines.Detach();
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0, 0, 0, 58), StrokeWidth = 0.65f / zoom };
        canvas.DrawPath(grid, outline);
    }

    private static void DrawExact(SKCanvas canvas, HexDocument document, bool background, bool grid, float zoom)
    {
        var visible = canvas.LocalClipBounds;
        var (minColumn, minRow, maxColumn, maxRow) = VisibleCells(document, visible);
        using var cell = Hexagon(document.Orientation, CellRadius + Bleed(CanvasScale(canvas)));
        // Cells of one color also share a path, so that the fill covers them as the single shape they look like.
        using var backgroundCells = new SKPathBuilder { FillType = SKPathFillType.Winding };
        var colors = new List<uint>();
        var groups = new Dictionary<uint, SKPathBuilder>();
        var seeThrough = false;
        for (var row = minRow; row <= maxRow; row++)
        for (var column = minColumn; column <= maxColumn; column++)
        {
            var index = row * document.Width + column;
            var (x, y) = HexGeometry.Center(document.CoordinateAt(index), document.Orientation, CellRadius);
            if (!IsVisible(visible, x, y)) continue;
            // The background layer lies under every cell, so a cell that holds no color shows it instead.
            if (background) backgroundCells.AddPath(cell, (float)x, (float)y);
            var color = document.CompositeAt(index);
            if ((color >> 24) == 0) continue;
            if (!groups.TryGetValue(color, out var group))
            {
                groups.Add(color, group = new SKPathBuilder { FillType = SKPathFillType.Winding });
                colors.Add(color);
                seeThrough |= (color >> 24) < 255;
            }
            group.AddPath(cell, (float)x, (float)y);
        }
        using var fill = new SKPaint { IsAntialias = true };
        void FillCells(SKPathBuilder group, uint color)
        {
            using var filled = group.Detach();
            fill.Color = new SKColor(color);
            canvas.DrawPath(filled, fill);
        }
        // All of the background goes down first, so that no cell's overlap paints over the artwork of its neighbor.
        if (background) FillCells(backgroundCells, document.BackgroundColor);
        // Cells one can see through would blend twice where they overlap and draw their own seam, a darker one.
        // Collecting them in a layer, where each cell replaces what it covers, leaves one cell's color per pixel.
        if (seeThrough) { canvas.SaveLayer(); fill.BlendMode = SKBlendMode.Src; }
        foreach (var color in colors) { using var group = groups[color]; FillCells(group, color); }
        if (seeThrough) canvas.Restore();
        if (!grid || zoom < 0.3) return;
        DrawGrid(canvas, document, zoom);
    }
}
