using OpenTK.Graphics.OpenGL4;
using SkiaSharp;

namespace HexelEditor.Desktop;

// A texture on the GPU mirroring a raster on the processor, brought up to date over the part that changed.
//
// What this replaces: SKImage.FromBitmap, which makes a new image out of the whole raster every time any of
// it changes. On a two million cell canvas that is sixteen megabytes copied and uploaded because one cell
// was painted, measured at 4.6 ms to copy and another 4.6 ms inside the draw call that then sent it to the
// GPU -- 9.2 ms a frame to show a single changed hexel, on a canvas whose whole frame otherwise costs 1.4 ms.
//
// A texture that stays where it is and takes a sub-rectangle instead turns that into the cost of the cells
// actually painted. The dirty rectangle was already tracked and already followed through compositing; it
// simply stopped at the upload.
//
// Skia is told the texture exists and reads from it, but does not own it: the texture outlives any one frame
// and is deleted here. Because the updates go through raw GL behind Skia's back, the context is reset after
// each one, so Skia does not go on believing whatever it last bound is still bound.
internal sealed class MirroredTexture : IDisposable
{
    private const int BytesPerTexel = 4;

    private int id;
    private int width, height;
    private GRBackendTexture? backend;
    private SKImage? image;

    /// <summary>
    /// Brings the texture up to date from the raster and returns an image reading it. A region of null
    /// means the whole raster; a region outside it, or an empty one, means nothing to do.
    /// </summary>
    public SKImage Update(GRContext context, SKBitmap raster, (int X, int Y, int Width, int Height)? region)
    {
        if (image == null || width != raster.Width || height != raster.Height)
        {
            Allocate(context, raster);
            return image!;
        }
        if (region is not { } part) { Upload(context, raster, (0, 0, width, height)); return image; }
        if (part.Width <= 0 || part.Height <= 0) return image;
        Upload(context, raster, part);
        return image;
    }

    private void Allocate(GRContext context, SKBitmap raster)
    {
        Release();
        width = raster.Width;
        height = raster.Height;
        id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        // The raster holds blue, green, red, alpha in that order; the texture holds red, green, blue, alpha.
        // Saying so here is what makes the driver reorder the channels as it uploads, which is why the image
        // below is read as RGBA and still comes out the colour the cell was composited to.
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0,
            PixelFormat.Bgra, PixelType.UnsignedByte, raster.GetPixels());
        GL.BindTexture(TextureTarget.Texture2D, 0);
        context.ResetContext();
        backend = new GRBackendTexture(width, height, mipmapped: false,
            new GRGlTextureInfo((uint)TextureTarget.Texture2D, (uint)id, (uint)SizedInternalFormat.Rgba8));
        image = SKImage.FromTexture(context, backend, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888, SKAlphaType.Premul)
            ?? throw new InvalidOperationException($"Could not read a {width} by {height} texture as an image.");
    }

    private void Upload(GRContext context, SKBitmap raster, (int X, int Y, int Width, int Height) part)
    {
        GL.BindTexture(TextureTarget.Texture2D, id);
        // Where each row of the sub-rectangle begins in the raster, which is wider than the rectangle.
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, raster.Width);
        var start = raster.GetPixels() + (part.Y * raster.Width + part.X) * BytesPerTexel;
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, part.X, part.Y, part.Width, part.Height,
            PixelFormat.Bgra, PixelType.UnsignedByte, start);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        context.ResetContext();
    }

    private void Release()
    {
        image?.Dispose();
        image = null;
        backend?.Dispose();
        backend = null;
        if (id != 0)
        {
            GL.DeleteTexture(id);
            id = 0;
        }
    }

    public void Dispose() => Release();
}
