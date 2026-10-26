using HexelEditor.Core;
using SkiaSharp;

namespace HexelEditor.Desktop;

// Where a cell sits on the canvas and how large it is: the axial arithmetic of HexGeometry in the canvas's
// own units, in Skia's types.
//
// The artwork is drawn by GpuHexRenderer, which does all of this per pixel in a shader instead, and so are
// the selection and the rectangle being dragged out, from a mask and a rectangle the shader is given. What
// is here serves the two things still drawn over the artwork as Skia paths on the CPU -- the line preview
// and the cursor ring, both bounded by a brush or a diagonal rather than by the canvas -- and the bounds the
// view is fitted to.
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
}
