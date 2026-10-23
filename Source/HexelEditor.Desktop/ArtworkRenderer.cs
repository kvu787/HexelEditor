using System.Globalization;
using System.IO;
using System.Text;
using HexelEditor.Core;
using SkiaSharp;

namespace HexelEditor.Desktop;

public static class ArtworkRenderer
{
    public const float CellRadius = 12;
    private const float ExportScale = 16f / CellRadius;

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

    public static void Draw(SKCanvas canvas, HexDocument document, bool background = false, bool grid = false, float zoom = 1)
    {
        var visible = canvas.LocalClipBounds;
        using var cell = Hexagon(document.Orientation, CellRadius + Bleed(CanvasScale(canvas)));
        // Cells of one color also share a path, so that the fill covers them as the single shape they look like.
        using var backgroundCells = new SKPathBuilder { FillType = SKPathFillType.Winding };
        var colors = new List<uint>();
        var groups = new Dictionary<uint, SKPathBuilder>();
        var seeThrough = false;
        for (var index = 0; index < document.CellCount; index++)
        {
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
        using var path = Hexagon(document.Orientation);
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0, 0, 0, 58), StrokeWidth = 0.65f / zoom };
        for (var index = 0; index < document.CellCount; index++)
        {
            var (x, y) = HexGeometry.Center(document.CoordinateAt(index), document.Orientation, CellRadius);
            if (!IsVisible(visible, x, y)) continue;
            canvas.Save(); canvas.Translate((float)x, (float)y);
            canvas.DrawPath(path, outline);
            canvas.Restore();
        }
    }

    public static void ExportPng(HexDocument document, string path)
    {
        var bounds = Bounds(document);
        var width = (int)Math.Ceiling(bounds.Width * ExportScale) + 2;
        var height = (int)Math.Ceiling(bounds.Height * ExportScale) + 2;
        if (width > 32768 || height > 32768 || (long)width * height > 64_000_000) throw new InvalidOperationException("This export is too large. Use a smaller canvas.");
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)) ?? throw new InvalidOperationException("Could not create the export image.");
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate(1 - bounds.Left * ExportScale, 1 - bounds.Top * ExportScale); surface.Canvas.Scale(ExportScale);
        Draw(surface.Canvas, document);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path); data.SaveTo(output);
    }

    public static void ExportSvg(HexDocument document, string path)
    {
        var bounds = Bounds(document);
        static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var builder = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{Number(bounds.Left - 1)} {Number(bounds.Top - 1)} {Number(bounds.Width + 2)} {Number(bounds.Height + 2)}\">\n");
        // Opaque polygons carry the same overlap as the other outputs, sized for the scale the PNG export uses.
        // Nothing here can hold a layer the way a canvas can, so cells one can see through stay exact instead:
        // overlapping those would blend twice and draw a seam of its own, which no viewer's scale would hide.
        var overlapped = CellRadius + Bleed(ExportScale);
        for (var index = 0; index < document.CellCount; index++)
        {
            var color = document.CompositeAt(index);
            if ((color >> 24) == 0) continue;
            var radius = (color >> 24) == 255 ? overlapped : CellRadius;
            var (x, y) = HexGeometry.Center(document.CoordinateAt(index), document.Orientation, CellRadius);
            builder.Append("<polygon points=\"");
            for (var corner = 0; corner < 6; corner++)
            {
                var angle = Math.PI / 180 * (60 * corner + (document.Orientation == HexOrientation.PointyTop ? -30 : 0));
                builder.Append(Number(x + Math.Cos(angle) * radius)).Append(',').Append(Number(y + Math.Sin(angle) * radius)).Append(' ');
            }
            builder.Append($"\" fill=\"#{color & 0xFFFFFF:X6}\" fill-opacity=\"{Number((color >> 24) / 255.0)}\"/>\n");
        }
        builder.Append("</svg>\n"); File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }
}
