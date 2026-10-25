using System.Runtime.InteropServices;
using HexelEditor.Core;
using SkiaSharp;

namespace HexelEditor.Desktop;

// Which cells are selected, as one byte a cell, for the shader to read.
//
// The selection used to be drawn on the processor: a hexagon path per selected cell, over every cell the
// viewport's bounding box covered. On a two million cell canvas with everything selected that walked
// 1,149,148 cells and drew a path for each one, every frame, and a frame is what every mouse move asks for.
// It cost 2.6 seconds a frame, which is not a slow editor but a stopped one.
//
// Handing the selection to the shader instead makes it cost what the window costs, like the artwork: a texel
// fetch for each pixel actually on screen. This is the texture that fetch reads. It is rebuilt only when the
// selection itself changes, so panning, zooming and painting inside a selection rebuild nothing.
//
// One byte a cell rather than one bit, because a texture is addressed in texels and the shader has the cell's
// column and row in hand by the time it asks. Two megabytes for the largest canvas the editor allows.
public sealed class SelectionMask : IDisposable
{
    private SKBitmap? mask;
    private byte[]? pixels;
    private GCHandle pinned;
    private int columns, rows;
    private int builtVersion = -1;
    private CellSelection? builtFrom;

    /// <summary>Bumped whenever the mask's contents changed, so a holder of a copy knows to take a new one.</summary>
    public int Version { get; private set; }

    /// <summary>Whether anything is selected at all, which is what decides if the shader looks here.</summary>
    public bool Any { get; private set; }

    /// <summary>The mask as it stands, or null before anything has been built.</summary>
    public SKBitmap? Current => mask;

    /// <summary>
    /// Brings the mask up to date and returns it. Rebuilt only when the selection has changed since last
    /// time, or when it is a different selection, or when the canvas is a different shape.
    /// </summary>
    public SKBitmap Refresh(HexDocument document, CellSelection selection)
    {
        if (mask == null || columns != document.Width || rows != document.Height)
        {
            Allocate(document);
            builtVersion = -1;
            builtFrom = null;
        }
        // A version counts changes within one selection, so it says nothing across two of them, exactly as
        // the artwork raster's version does.
        if (builtVersion == selection.Version && ReferenceEquals(builtFrom, selection)) return mask!;
        Array.Clear(pixels!);
        // Walking the selection rather than the canvas: a selection of nine cells costs nine writes, and
        // the bitset skips its empty words whole.
        foreach (var index in selection) pixels![index] = 255;
        mask!.NotifyPixelsChanged();
        builtVersion = selection.Version;
        builtFrom = selection;
        Any = selection.Count > 0;
        Version++;
        return mask;
    }

    private void Allocate(HexDocument document)
    {
        Release();
        columns = document.Width;
        rows = document.Height;
        pixels = new byte[columns * rows];
        pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        mask = new SKBitmap();
        if (!mask.InstallPixels(new SKImageInfo(columns, rows, SKColorType.Gray8, SKAlphaType.Opaque),
                pinned.AddrOfPinnedObject(), columns))
        {
            Release();
            throw new InvalidOperationException("Could not create the selection mask.");
        }
    }

    private void Release()
    {
        mask?.Dispose();
        mask = null;
        if (pinned.IsAllocated) pinned.Free();
        pixels = null;
        Any = false;
    }

    public void Dispose() => Release();
}

/// <summary>
/// The rectangle a drag is sketching out, in columns and rows, drawn by the shader rather than by a path a
/// cell. A filled rectangle over a whole canvas is as many cells as a whole-canvas selection, so it had the
/// same problem and wants the same answer.
/// </summary>
public readonly record struct RectanglePreview(int Left, int Top, int Right, int Bottom, bool Filled)
{
    /// <summary>No rectangle, for the frames where nothing is being dragged out.</summary>
    public static readonly RectanglePreview Nothing = new(0, 0, -1, -1, false);

    public bool IsEmpty => Right < Left || Bottom < Top;
}
