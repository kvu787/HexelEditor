using SkiaSharp;

namespace HexelEditor.Desktop;

// An image on the GPU mirroring a raster on the processor, brought up to date over the part that changed.
//
// What this replaces: SKImage.FromBitmap, which makes a new image out of the whole raster every time any of
// it changes. On a two million cell canvas that is sixteen megabytes copied and uploaded because one cell
// was painted, measured at 4.6 ms to copy and another 4.6 ms inside the draw call that then sent it to the
// GPU -- 9.2 ms a frame to show a single changed hexel, on a canvas whose whole frame otherwise costs
// 1.3 ms. Sending only the cells that changed costs what those cells cost.
//
// Why it is done with Skia's own surface rather than a texture of its own: the first version of this kept a
// GL texture and updated it with glTexSubImage2D, which is the direct way to say it. It worked in every
// headless check and left the real window blank -- not the canvas, the whole window, toolbar included --
// which is the same failure this renderer's own history records from owning GL on the wrong thread. Raw GL
// calls into a context Skia is also driving are not worth the trouble they cause, and a surface Skia owns
// gets the same result inside the rules Skia expects.
internal sealed class MirroredTexture : IDisposable
{
    private SKSurface? surface;
    private SKImage? snapshot;
    private int width, height;

    /// <summary>
    /// Brings the image up to date from the raster and returns it. A region of null means the whole raster,
    /// whether or not the image already has one of that size; an empty one means there is nothing to send.
    /// </summary>
    public SKImage Update(GRContext context, SKBitmap raster, (int X, int Y, int Width, int Height)? region)
    {
        if (surface == null || width != raster.Width || height != raster.Height)
        {
            Allocate(context, raster);
            return snapshot!;
        }
        // No region is the whole raster, and it has to be sent even though the surface is the right size
        // already: the caller asks for it when the raster is one this image has never mirrored, and a
        // raster of the same size as the last one is not the same raster. Left as "nothing to send", a
        // second canvas of the first one's dimensions drew the first one's cells.
        if (region is null) { Patch(raster, (0, 0, width, height)); return snapshot!; }
        if (region is { } part && part.Width > 0 && part.Height > 0) Patch(raster, part);
        return snapshot!;
    }

    private void Allocate(GRContext context, SKBitmap raster)
    {
        Release();
        width = raster.Width;
        height = raster.Height;
        surface = SKSurface.Create(context, budgeted: true,
            new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), sampleCount: 0, GRSurfaceOrigin.TopLeft)
            ?? throw new InvalidOperationException($"Could not create a {width} by {height} surface for the artwork.");
        using var pixels = raster.PeekPixels()
            ?? throw new InvalidOperationException("Could not read the artwork raster.");
        // An image over the raster's own pixels rather than a copy of them, for the reason given in Patch.
        using var whole = SKImage.FromPixels(pixels)
            ?? throw new InvalidOperationException("Could not read the artwork raster.");
        surface.Canvas.DrawImage(whole, 0, 0, Sampling, null);
        surface.Flush();
        snapshot = Snapshot();
    }

    private void Patch(SKBitmap raster, (int X, int Y, int Width, int Height) part)
    {
        // A view onto the raster's own pixels, and an image over that view rather than a copy of it, so the
        // changed rectangle travels from the raster to the GPU once. SKImage.FromBitmap copied it first: on a
        // whole-canvas rebuild that was sixteen megabytes copied for nothing, 4.6 ms, before the upload.
        using var pixels = raster.PeekPixels()
            ?? throw new InvalidOperationException("Could not read the artwork raster.");
        using var piece = pixels.ExtractSubset(SKRectI.Create(part.X, part.Y, part.Width, part.Height))
            ?? throw new InvalidOperationException("Could not take the changed part of the artwork raster.");
        using var image = SKImage.FromPixels(piece)
            ?? throw new InvalidOperationException("Could not read the changed part of the artwork raster.");
        // The old snapshot goes first. While one is alive the surface cannot be drawn into without copying
        // itself whole, which is the cost this exists to avoid.
        snapshot?.Dispose();
        snapshot = null;
        surface!.Canvas.DrawImage(image, part.X, part.Y, Sampling, null);
        // Flushed here because the image borrows the raster's memory, which the next recomposite will write
        // over: the upload has to have read it before this returns.
        surface.Flush();
        snapshot = Snapshot();
    }

    private SKImage Snapshot() =>
        surface!.Snapshot() ?? throw new InvalidOperationException("Could not read the artwork surface.");

    private static SKSamplingOptions Sampling => new(SKFilterMode.Nearest, SKMipmapMode.None);

    private void Release()
    {
        snapshot?.Dispose();
        snapshot = null;
        surface?.Dispose();
        surface = null;
    }

    public void Dispose() => Release();
}
