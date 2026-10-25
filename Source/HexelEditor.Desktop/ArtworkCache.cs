using System.Runtime.InteropServices;
using HexelEditor.Core;
using SkiaSharp;

namespace HexelEditor.Desktop;

// A raster of the composited artwork, one cell per two pixels, kept up to date as cells change.
//
// It exists for the zoomed-out case. Drawing two million hexagons into a window of under a million pixels is
// work with nothing to show for it, and it is what made a large canvas lag: the cost belonged to the document
// rather than to the view. Scaling a raster into place costs what the window costs, whatever the canvas is.
//
// Two pixels per cell, not one, because alternate rows of a hex grid sit half a cell across. At one pixel per
// cell that offset falls between two pixels and cannot be stored, so the raster would describe a rectangular
// grid and the artwork would be drawn with its rows out of place. At two it is exactly one pixel, so the
// raster holds the real geometry and can simply be scaled.
//
// A row is one pixel tall, so a cell's pixel is wider than it is tall by the ratio the grid itself has. The
// destination rectangle carries that, which is why the mapping back to the artwork's own coordinates lives
// here beside the layout rather than at the call.
public sealed class ArtworkCache : IDisposable
{
    private const int PixelsPerCell = 2;

    private SKBitmap? raster;
    private int[]? pixels;
    private GCHandle pinned;
    private int cellColumns, cellRows;
    private HexOrientation orientation;
    private bool everythingDirty;

    // The raster covers the whole document, so it is addressed in columns and rows and scaled on the way out.
    public SKBitmap? Raster => raster;

    // Bumped whenever a cell's colour actually changed. A renderer holding a GPU copy of the
    // raster watches this so that panning and zooming, which change no cell, re-upload nothing.
    public int Version { get; private set; }

    public void InvalidateEverything() => everythingDirty = true;

    // Dirt is accumulated rather than applied at once, because a frame drawn at a zoom that does not use the
    // raster must not lose what changed: the next frame that does use it has to know.
    private int pendingMinColumn = int.MaxValue, pendingMinRow = int.MaxValue, pendingMaxColumn = -1, pendingMaxRow = -1;

    public void MarkDirty((int MinColumn, int MinRow, int MaxColumn, int MaxRow)? region)
    {
        if (region is not { } dirty) return;
        if (dirty.MinColumn < pendingMinColumn) pendingMinColumn = dirty.MinColumn;
        if (dirty.MinRow < pendingMinRow) pendingMinRow = dirty.MinRow;
        if (dirty.MaxColumn > pendingMaxColumn) pendingMaxColumn = dirty.MaxColumn;
        if (dirty.MaxRow > pendingMaxRow) pendingMaxRow = dirty.MaxRow;
    }

    // Brings the raster up to date and returns it. Only the cells reported dirty are recomposited, so a brush
    // stroke costs the cells it touched; a new document or a change to a layer costs the whole canvas once.
    public SKBitmap Refresh(HexDocument document)
    {
        var rebuildEverything = everythingDirty;
        if (raster == null || cellColumns != document.Width || cellRows != document.Height || orientation != document.Orientation)
        {
            Allocate(document);
            rebuildEverything = true;
        }
        everythingDirty = false;
        if (rebuildEverything) Composite(document, 0, 0, document.Width - 1, document.Height - 1);
        else if (pendingMaxColumn >= 0 && pendingMaxRow >= 0)
            Composite(document, pendingMinColumn, pendingMinRow, pendingMaxColumn, pendingMaxRow);
        pendingMinColumn = int.MaxValue;
        pendingMinRow = int.MaxValue;
        pendingMaxColumn = -1;
        pendingMaxRow = -1;
        return raster!;
    }

    // Where the raster's pixels sit in the artwork's own coordinates, so it can be drawn with one scaled blit.
    // A cell spans a whole pitch across and a whole row pitch down, which is the band a row occupies rather
    // than the full height of a hexagon: neighbouring rows interlock, and the bands are what tile the plane.
    public SKRect DestinationFor(HexDocument document, float radius = CanvasGeometry.CellRadius)
    {
        var (across, down) = Pitches(document.Orientation, radius);
        return document.Orientation == HexOrientation.PointyTop
            ? new SKRect(-across / 2, -down / 2, across * document.Width, down * (document.Height - 0.5f))
            : new SKRect(-across / 2, -down / 2, across * (document.Width - 0.5f), down * document.Height);
    }

    // The pitch between cells along each axis. The row axis is the one alternate rows are offset along.
    private static (float Across, float Down) Pitches(HexOrientation orientation, float radius) =>
        orientation == HexOrientation.PointyTop
            ? ((float)(Math.Sqrt(3) * radius), 1.5f * radius)
            : (1.5f * radius, (float)(Math.Sqrt(3) * radius));

    private void Allocate(HexDocument document)
    {
        Release();
        cellColumns = document.Width;
        cellRows = document.Height;
        orientation = document.Orientation;
        // The extra pixel is where the odd rows' half-cell offset lands.
        var width = document.Orientation == HexOrientation.PointyTop ? cellColumns * PixelsPerCell + 1 : cellColumns;
        var height = document.Orientation == HexOrientation.PointyTop ? cellRows : cellRows * PixelsPerCell + 1;
        pixels = new int[width * height];
        pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        raster = new SKBitmap();
        // Installed over the pinned array rather than copied into: writing a cell writes the bitmap, so a
        // stroke costs no copy at all. Every cell is composited over the opaque canvas background, so the
        // result is opaque and premultiplied and straight alpha are the same thing here.
        if (!raster.InstallPixels(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
                pinned.AddrOfPinnedObject(), width * sizeof(int)))
        {
            Release();
            throw new InvalidOperationException("Could not create the artwork raster.");
        }
    }

    private void Composite(HexDocument document, int minColumn, int minRow, int maxColumn, int maxRow)
    {
        if (raster == null || pixels == null) return;
        minColumn = Math.Max(0, minColumn);
        minRow = Math.Max(0, minRow);
        maxColumn = Math.Min(document.Width - 1, maxColumn);
        maxRow = Math.Min(document.Height - 1, maxRow);
        var stride = raster.Width;
        var background = document.BackgroundColor;
        var pointyTop = document.Orientation == HexOrientation.PointyTop;
        for (var row = minRow; row <= maxRow; row++)
        {
            var cellRow = row * document.Width;
            for (var column = minColumn; column <= maxColumn; column++)
            {
                var color = ColorMath.Over(document.CompositeAt(cellRow + column), background);
                // Two pixels per cell along the offset axis, shifted by one on alternate rows.
                if (pointyTop)
                {
                    var start = column * PixelsPerCell + (row & 1) + row * stride;
                    pixels[start] = unchecked((int)color);
                    pixels[start + 1] = unchecked((int)color);
                }
                else
                {
                    var start = column + (row * PixelsPerCell + (column & 1)) * stride;
                    pixels[start] = unchecked((int)color);
                    pixels[start + stride] = unchecked((int)color);
                }
            }
        }
        raster.NotifyPixelsChanged();
        Version++;
    }

    private void Release()
    {
        raster?.Dispose();
        raster = null;
        if (pinned.IsAllocated) pinned.Free();
        pixels = null;
    }

    public void Dispose() => Release();
}
