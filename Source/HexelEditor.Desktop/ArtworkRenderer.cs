using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
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

    public static void Draw(SKCanvas canvas, HexDocument document, int frameIndex, bool paper = false, bool grid = false, float zoom = 1, bool onion = false)
    {
        using var path = Hexagon(document.Orientation);
        using var fill = new SKPaint { IsAntialias = true };
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(13, 19, 27, 110), StrokeWidth = 0.65f / zoom };
        var visible = canvas.LocalClipBounds;
        for (var index = 0; index < document.CellCount; index++)
        {
            var (x, y) = HexGeometry.Center(document.CoordinateAt(index), document.Orientation, CellRadius);
            if (x + CellRadius < visible.Left || x - CellRadius > visible.Right || y + CellRadius < visible.Top || y - CellRadius > visible.Bottom) continue;
            canvas.Save(); canvas.Translate((float)x, (float)y);
            if (paper)
            {
                fill.Color = (index % document.Width + index / document.Width) % 2 == 0 ? new SKColor(44, 51, 62) : new SKColor(50, 57, 68);
                canvas.DrawPath(path, fill);
            }
            if (onion && frameIndex > 0)
            {
                var previous = document.CompositeAt(index, frameIndex - 1);
                fill.Color = new SKColor((byte)(previous >> 16), (byte)(previous >> 8), (byte)previous, (byte)((previous >> 24) * 0.25));
                canvas.DrawPath(path, fill);
            }
            var color = document.CompositeAt(index, frameIndex);
            if ((color >> 24) > 0)
            {
                fill.Color = new SKColor(color);
                canvas.DrawPath(path, fill);
            }
            if (grid && zoom >= 0.3) canvas.DrawPath(path, outline);
            canvas.Restore();
        }
    }

    public static BitmapSource Thumbnail(HexDocument document, int frameIndex)
    {
        using var surface = SKSurface.Create(new SKImageInfo(92, 62));
        surface.Canvas.Clear(new SKColor(16, 19, 25));
        var bounds = Bounds(document);
        var scale = Math.Min(88 / bounds.Width, 58 / bounds.Height);
        surface.Canvas.Translate((92 - bounds.Width * scale) / 2 - bounds.Left * scale, (62 - bounds.Height * scale) / 2 - bounds.Top * scale);
        surface.Canvas.Scale(scale);
        Draw(surface.Canvas, document, frameIndex);
        using var snapshot = surface.Snapshot(); using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }

    public static void ExportPng(HexDocument document, int activeFrame, string path, bool spriteSheet)
    {
        const float exportScale = 16f / CellRadius;
        var bounds = Bounds(document);
        var frameWidth = (int)Math.Ceiling(bounds.Width * exportScale) + 2;
        var height = (int)Math.Ceiling(bounds.Height * exportScale) + 2;
        var count = spriteSheet ? document.Frames.Count : 1;
        var width = checked(frameWidth * count);
        if (width > 32768 || height > 32768 || (long)width * height > 64_000_000) throw new InvalidOperationException("This export is too large. Export individual frames or use a smaller canvas.");
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)) ?? throw new InvalidOperationException("Could not create the export image.");
        surface.Canvas.Clear(SKColors.Transparent);
        for (var frame = 0; frame < count; frame++)
        {
            surface.Canvas.Save(); surface.Canvas.Translate(frame * frameWidth + 1 - bounds.Left * exportScale, 1 - bounds.Top * exportScale); surface.Canvas.Scale(exportScale);
            Draw(surface.Canvas, document, spriteSheet ? frame : activeFrame);
            surface.Canvas.Restore();
        }
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path); data.SaveTo(output);
    }

    public static void ExportSvg(HexDocument document, int frameIndex, string path)
    {
        var bounds = Bounds(document);
        static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var builder = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{Number(bounds.Left - 1)} {Number(bounds.Top - 1)} {Number(bounds.Width + 2)} {Number(bounds.Height + 2)}\">\n");
        for (var index = 0; index < document.CellCount; index++)
        {
            var color = document.CompositeAt(index, frameIndex);
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
