using System.Runtime.InteropServices;
using HexelEditor.Core;
using SkiaSharp;

namespace HexelEditor.Desktop;

// A raster of the composited artwork, one cell per two pixels, kept up to date as cells change.
//
// This is what GpuHexRenderer's shader reads: a cell's colour, at the cell's own place, in one texture. The
// shader works out which cell a pixel falls in and fetches that cell's colour from here, so the compositing
// of layers happens once when a cell changes rather than once per pixel per frame.
//
// Only the cells reported dirty are recomposited, which is why a brush stroke on a two million cell canvas
// costs the handful of cells it touched. Panning and zooming change no cell at all, so they cost nothing
// here and re-upload nothing.
//
// Two pixels per cell, not one, because alternate rows of a hex grid sit half a cell across. At one pixel per
// cell that offset falls between two pixels and cannot be stored, so the raster would describe a rectangular
// grid and the artwork would be read with its rows out of place. At two it is exactly one pixel, so the
// raster holds the real geometry and the shader can address it with whole texels.
public sealed class ArtworkCache : IDisposable
{
    private const int PixelsPerCell = 2;
    // Below this many rows, compositing them one after another is quicker than sharing them out.
    private const int RowsWorthSharing = 64;

    private SKBitmap? raster;
    private int[]? pixels;
    private GCHandle pinned;
    private int cellColumns, cellRows;
    private HexOrientation orientation;
    private bool everythingDirty;

    // Bumped whenever a cell's colour actually changed. The renderer holding a GPU copy of the
    // raster watches this so that panning and zooming, which change no cell, re-upload nothing.
    public int Version { get; private set; }

    /// <summary>
    /// The texels the last refresh wrote, or null when it wrote none. In texels rather than cells, because
    /// what reads this is uploading part of a texture and the two are not the same thing: a cell occupies
    /// two texels along the axis alternate lines are offset along, and which two depends on its parity.
    /// </summary>
    public (int X, int Y, int Width, int Height)? LastWritten { get; private set; }

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
        LastWritten = null;
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
        if (minColumn > maxColumn || minRow > maxRow) return;
        var stride = raster.Width;
        var background = document.BackgroundColor;
        var pointyTop = document.Orientation == HexOrientation.PointyTop;
        var target = pixels;
        void CompositeRow(int row)
        {
            var cellRow = row * document.Width;
            for (var column = minColumn; column <= maxColumn; column++)
            {
                var color = ColorMath.Over(document.CompositeAt(cellRow + column), background);
                // Two pixels per cell along the offset axis, shifted by one on alternate rows.
                if (pointyTop)
                {
                    var start = column * PixelsPerCell + (row & 1) + row * stride;
                    target[start] = unchecked((int)color);
                    target[start + 1] = unchecked((int)color);
                }
                else
                {
                    var start = column + (row * PixelsPerCell + (column & 1)) * stride;
                    target[start] = unchecked((int)color);
                    target[start + stride] = unchecked((int)color);
                }
            }
        }
        // A cell writes texels no other cell writes -- along the offset axis its two are picked out by its
        // own parity -- so the rows do not overlap and can be composited at the same time. Worth doing only
        // for the whole-canvas rebuilds a structural change forces: a brush dab is a handful of rows, and
        // handing those out costs more than doing them.
        if (maxRow - minRow + 1 >= RowsWorthSharing)
            System.Threading.Tasks.Parallel.For(minRow, maxRow + 1, CompositeRow);
        else
            for (var row = minRow; row <= maxRow; row++) CompositeRow(row);
        raster.NotifyPixelsChanged();
        // The cells written, as the texels they were written to. A cell's two texels run along the offset
        // axis and start one further on for an odd line, so the span covers one extra texel at each end
        // rather than working the parities out again.
        var texels = pointyTop
            ? (X: minColumn * PixelsPerCell, Y: minRow,
               Width: (maxColumn - minColumn + 1) * PixelsPerCell + 1, Height: maxRow - minRow + 1)
            : (X: minColumn, Y: minRow * PixelsPerCell,
               Width: maxColumn - minColumn + 1, Height: (maxRow - minRow + 1) * PixelsPerCell + 1);
        LastWritten = (texels.X, texels.Y,
            Math.Min(texels.Width, raster.Width - texels.X),
            Math.Min(texels.Height, raster.Height - texels.Y));
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
