using System.Globalization;
using System.IO;
using System.Text;
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

    public static void Draw(SKCanvas canvas, HexDocument document, bool paper = false, bool grid = false, float zoom = 1)
    {
        using var path = Hexagon(document.Orientation);
        using var fill = new SKPaint { IsAntialias = true };
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0, 0, 0, 58), StrokeWidth = 0.65f / zoom };
        var visible = canvas.LocalClipBounds;
        for (var index = 0; index < document.CellCount; index++)
        {
            var (x, y) = HexGeometry.Center(document.CoordinateAt(index), document.Orientation, CellRadius);
            if (!IsVisible(visible, x, y)) continue;
            canvas.Save(); canvas.Translate((float)x, (float)y);
            if (paper)
            {
                // Checkered paper marks the cells that hold no color, as a transparency checkerboard does.
                fill.Color = (index % document.Width + index / document.Width) % 2 == 0 ? new SKColor(255, 255, 255) : new SKColor(233, 233, 233);
                canvas.DrawPath(path, fill);
            }
            var color = document.CompositeAt(index);
            if ((color >> 24) > 0)
            {
                fill.Color = new SKColor(color);
                canvas.DrawPath(path, fill);
            }
            if (grid && zoom >= 0.3) canvas.DrawPath(path, outline);
            canvas.Restore();
        }
    }

    public static void ExportPng(HexDocument document, string path)
    {
        const float exportScale = 16f / CellRadius;
        var bounds = Bounds(document);
        var width = (int)Math.Ceiling(bounds.Width * exportScale) + 2;
        var height = (int)Math.Ceiling(bounds.Height * exportScale) + 2;
        if (width > 32768 || height > 32768 || (long)width * height > 64_000_000) throw new InvalidOperationException("This export is too large. Use a smaller canvas.");
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)) ?? throw new InvalidOperationException("Could not create the export image.");
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate(1 - bounds.Left * exportScale, 1 - bounds.Top * exportScale); surface.Canvas.Scale(exportScale);
        Draw(surface.Canvas, document);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path); data.SaveTo(output);
    }

    public static void ExportSvg(HexDocument document, string path)
    {
        var bounds = Bounds(document);
        static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var builder = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{Number(bounds.Left - 1)} {Number(bounds.Top - 1)} {Number(bounds.Width + 2)} {Number(bounds.Height + 2)}\">\n");
        for (var index = 0; index < document.CellCount; index++)
        {
            var color = document.CompositeAt(index);
            if ((color >> 24) == 0) continue;
            var (x, y) = HexGeometry.Center(document.CoordinateAt(index), document.Orientation, CellRadius);
            builder.Append("<polygon points=\"");
            for (var corner = 0; corner < 6; corner++)
            {
                var angle = Math.PI / 180 * (60 * corner + (document.Orientation == HexOrientation.PointyTop ? -30 : 0));
                builder.Append(Number(x + Math.Cos(angle) * CellRadius)).Append(',').Append(Number(y + Math.Sin(angle) * CellRadius)).Append(' ');
            }
            builder.Append($"\" fill=\"#{color & 0xFFFFFF:X6}\" fill-opacity=\"{Number((color >> 24) / 255.0)}\"/>\n");
        }
        builder.Append("</svg>\n"); File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }
}
