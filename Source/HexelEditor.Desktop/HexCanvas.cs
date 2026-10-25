using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using HexelEditor.Core;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace HexelEditor.Desktop;

public enum DrawingTool { Pencil, Eraser, Fill, Eyedropper, Line, Rectangle, Selection }

public sealed class HexCanvas : SKElement
{
    private const float MinimumZoom = 0.08f;
    private const float MaximumZoom = 30f;
    // The dotted paper behind the canvas, in this control's own units.
    private const float DotSpacing = 24f;
    private const float DotOffset = 18f;
    private const float DotRadius = 0.7f;
    private static readonly SKColor Paper = new(243, 243, 243);
    private EditorSession? session;
    private readonly ArtworkCache artwork = new();
    private readonly SelectionMask selectionMask = new();
    private GpuHexRenderer? gpu;
    private HexCoordinate? hover;
    private HexCoordinate? start;
    private HexCoordinate? previous;
    private Point panPoint;
    private bool panning;
    private bool drawing;
    private bool eraseGesture;
    private bool needsFit = true;
    private float offsetX;
    private float offsetY;
    public float Zoom { get; private set; } = 1;
    public DrawingTool Tool { get; set; }
    public uint ForegroundColor { get; set; } = 0xFF000000;
    public int BrushRadius { get; set; }
    public bool ShowGrid { get; set; } = true;
    public bool FilledShapes { get; set; }
    public bool SpacePressed { get; set; }
    public HexCoordinate? HoverCoordinate => hover;
    /// <summary>
    /// What one paint spent, in milliseconds: the dotted backdrop, the artwork (the renderer's whole frame,
    /// including the wait on its thread), copying that frame onto this surface, and the selection, previews
    /// and cursor ring drawn over it. Only filled while <see cref="MeasurePaints"/> is set.
    /// </summary>
    public readonly record struct PaintTimings(double Backdrop, double Artwork, double Blit, double Overlays)
    {
        public double Total => Backdrop + Artwork + Blit + Overlays;
    }

    /// <summary>
    /// Times each paint's parts. Off unless something is measuring, because attributing the GPU's own share
    /// costs a sync inside the frame.
    /// </summary>
    public static bool MeasurePaints;

    /// <summary>The parts of the last paint drawn while <see cref="MeasurePaints"/> was set.</summary>
    public PaintTimings LastPaintTimings { get; private set; }

    /// <summary>
    /// What the overlays were culled against on the last measured paint, and how many cells they therefore
    /// walked. Both are here because the figure for the overlays is only meaningful next to the amount of
    /// canvas they covered, and that is decided by the surface's clip rather than by anything visible.
    /// </summary>
    public SKRect LastVisibleBounds { get; private set; }

    /// <summary>How many cells the last measured paint's overlay pass walked.</summary>
    public long LastOverlayCells { get; private set; }

    private static double Milliseconds(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    public event Action<string>? StatusChanged;
    public event Action<uint>? ColorPicked;
    public event Action? ZoomChanged;

    public HexCanvas()
    {
        PaintSurface += Paint;
        MouseDown += PointerDown; MouseMove += PointerMoved; MouseUp += PointerUp;
        MouseWheel += Wheel;
        MouseLeave += (_, _) => { if (!drawing) { hover = null; InvalidateVisual(); } };
        LostMouseCapture += (_, _) => FinishGesture();
        SizeChanged += (_, _) => { if (needsFit && ActualWidth > 0 && ActualHeight > 0) Fit(); else InvalidateVisual(); };
        // The raster holds its pixels in a pinned array, so it is worth releasing deliberately
        // rather than leaving to the finalizer. ShutdownStarted fires once, unlike Unloaded,
        // which also fires when a control is merely reparented.
        Dispatcher.ShutdownStarted += (_, _) => { artwork.Dispose(); selectionMask.Dispose(); };
        Cursor = Cursors.Cross;
    }

    // Anything that changes what a cell looks like without changing the cell: a different document, a layer
    // hidden or reordered, a new canvas background. The raster holds composited colour, so it is all stale.
    public void InvalidateArtwork() => artwork.InvalidateEverything();

    public void Attach(EditorSession value)
    {
        FinishGesture(); session = value; hover = null; needsFit = true;
        artwork.InvalidateEverything();
        if (ActualWidth > 0 && ActualHeight > 0) Fit();
        InvalidateVisual();
    }

    public void Fit()
    {
        if (session == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var bounds = CanvasGeometry.Bounds(session.Document);
        Zoom = Math.Clamp((float)Math.Min((ActualWidth - 100) / bounds.Width, (ActualHeight - 90) / bounds.Height), MinimumZoom, MaximumZoom);
        offsetX = ((float)ActualWidth - bounds.Width * Zoom) / 2 - bounds.Left * Zoom;
        offsetY = ((float)ActualHeight - bounds.Height * Zoom) / 2 - bounds.Top * Zoom;
        needsFit = false; InvalidateVisual(); ZoomChanged?.Invoke();
    }

    public void ChangeZoom(float factor, Point? anchor = null)
    {
        var center = anchor ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var next = Math.Clamp(Zoom * factor, MinimumZoom, MaximumZoom);
        offsetX = (float)(center.X - (center.X - offsetX) * next / Zoom);
        offsetY = (float)(center.Y - (center.Y - offsetY) * next / Zoom);
        Zoom = next; InvalidateVisual(); ZoomChanged?.Invoke();
    }

    private HexCoordinate Coordinate(Point point) => HexGeometry.FromPoint((point.X - offsetX) / Zoom, (point.Y - offsetY) / Zoom, session!.Document.Orientation, CanvasGeometry.CellRadius);

    // The inverse of Coordinate: where a cell's center sits in this canvas's own coordinates. Validation aims
    // real pointer input at a cell with it, so the pan and zoom it reads are the ones the pointer handlers use.
    public Point PointOf(HexCoordinate coordinate)
    {
        var (x, y) = HexGeometry.Center(coordinate, session!.Document.Orientation, CanvasGeometry.CellRadius);
        return new Point(x * Zoom + offsetX, y * Zoom + offsetY);
    }

    /// <summary>
    /// Paints one frame into a surface the caller owns, through the same code a real paint runs.
    /// </summary>
    /// <remarks>
    /// For measurement, and it exists because asking WPF for a frame is not a way to get one: a control
    /// repaints when WPF decides to run its render pass, and until then a visual is drawn from what it drew
    /// last time. Rendering the tree into a bitmap reuses that same content, so it produces a picture
    /// without producing a paint. The surface stands in for the one SKElement hands over, which is a raster
    /// surface of this kind over a bitmap's pixels.
    /// </remarks>
    public void PaintOneFrame(SKSurface surface, SKImageInfo info)
    {
        // SKElement builds a surface for each paint, so Paint is written for a canvas that arrives with no
        // transform of its own and leaves its last one in place. A caller reusing one surface has to put it
        // back into that state, or every paint inherits the scale of the one before it.
        surface.Canvas.ResetMatrix();
        Paint(this, new SKPaintSurfaceEventArgs(surface, info));
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var measuring = MeasurePaints;
        var paintStarted = measuring ? Stopwatch.GetTimestamp() : 0;
        if (session == null || ActualWidth <= 0)
        {
            // Nothing to draw, so nothing has written over whatever the surface last held.
            canvas.Clear(Paper);
            return;
        }
        // Device pixels per unit of the canvas's own coordinates. The renderer is given the horizontal one
        // for the zoom and the pan, which takes the pixels to be square; WPF scales a display uniformly, so
        // the two agree on every real one.
        var scaleX = (float)(args.Info.Width / ActualWidth);
        var scaleY = (float)(args.Info.Height / ActualHeight);

        // The dotted paper behind the canvas is drawn by the shader, as part of the one frame it draws.
        // Four thousand little antialiased circles, one draw call each, cost 3.0 ms a frame at a window's
        // size and 5.3 ms at a 4K display's, which was more than the artwork they sat behind.
        var backdrop = new BackdropDots(DotSpacing * scaleX, DotSpacing * scaleY,
            DotOffset * scaleX, DotOffset * scaleY, DotRadius * scaleX);
        var afterBackdrop = measuring ? Stopwatch.GetTimestamp() : 0;

        // Drained every frame whatever the zoom, so the raster never misses an edit made while it was not
        // the thing being drawn.
        artwork.MarkDirty(session.TakeDirtyCells());

        // The shader is the only thing that draws artwork, and the application refuses to start without it,
        // so there is nothing to fall back to here and no case where carrying on would show anything. The
        // renderer is already built by the time the first frame arrives; this is only how the canvas reaches
        // it, and it throws rather than leaving an empty canvas behind if it ever does not.
        gpu ??= GpuHexRenderer.Shared(App.Log.Write)
            ?? throw new InvalidOperationException("The canvas cannot draw: " + GpuHexRenderer.Unavailable);

        // The selection and the rectangle being dragged out are drawn by the same shader as the artwork,
        // from a mask of their own. Both cover areas rather than outlines, and an area on this canvas can be
        // two million cells, which is more than any per-cell drawing can keep up with.
        selectionMask.Refresh(session.Document, session.Selection);

        // One shaded rectangle covering the window, drawn at device resolution so it stays crisp on a
        // scaled display, and read straight into this surface's own pixels. Straight into them because the
        // frame covers every pixel: the shader draws the paper as well as the canvas, so there is nothing
        // underneath for a blend to reveal. Drawing it as a bitmap instead copied the frame a second time,
        // which cost more than everything else in the frame put together on a large display.
        using var pixels = args.Surface.PeekPixels();
        var intoSurface = pixels != null && pixels.Width == args.Info.Width && pixels.Height == args.Info.Height;
        if (intoSurface)
            gpu.RenderInto(pixels!, session.Document, artwork,
                Zoom * scaleX, offsetX * scaleX, offsetY * scaleX, ShowGrid, selectionMask, RectangleBeingDragged(), backdrop);
        var afterArtwork = measuring ? Stopwatch.GetTimestamp() : 0;
        if (!intoSurface)
        {
            // A surface that will not lend out its pixels, which no surface the editor draws into does.
            // Kept because the alternative is a canvas with nothing in it.
            var rendered = gpu.Render(session.Document, artwork, args.Info.Width, args.Info.Height,
                Zoom * scaleX, offsetX * scaleX, offsetY * scaleX, ShowGrid, selectionMask, RectangleBeingDragged(), backdrop);
            canvas.DrawBitmap(rendered, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), null);
        }
        var afterBlit = measuring ? Stopwatch.GetTimestamp() : 0;

        canvas.Scale(scaleX, scaleY);
        canvas.Translate(offsetX, offsetY); canvas.Scale(Zoom);
        using var path = CanvasGeometry.Hexagon(session.Document.Orientation, CanvasGeometry.CellRadius * 0.96f);
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f / Zoom, Color = new SKColor(0, 95, 184) };
        using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0, 95, 184, 40) };
        // The cursor ring carries a light halo so that it stays visible over both pale and dark artwork.
        using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.4f / Zoom, Color = new SKColor(255, 255, 255, 205) };
        var visible = canvas.LocalClipBounds;
        void Highlight(HexCoordinate coordinate, bool filled, bool cursor = false)
        {
            if (session.Document.IndexOf(coordinate) < 0) return;
            var (x, y) = HexGeometry.Center(coordinate, session.Document.Orientation, CanvasGeometry.CellRadius);
            // A whole-canvas selection is mostly off screen once zoomed in, so the artwork's culling applies here too.
            if (!CanvasGeometry.IsVisible(visible, x, y)) return;
            canvas.Save(); canvas.Translate((float)x, (float)y);
            if (filled) canvas.DrawPath(path, fill);
            if (cursor) canvas.DrawPath(path, halo);
            canvas.DrawPath(path, outline); canvas.Restore();
        }
        var lineCells = 0L;
        // A line is bounded by the cells it crosses, which is at most a canvas diagonal, so it stays here
        // rather than going into the shader with the areas.
        if (drawing && start.HasValue && hover.HasValue && Tool == DrawingTool.Line)
            foreach (var cell in start.Value.LineTo(hover.Value)) { Highlight(cell, true); lineCells++; }
        if (measuring)
        {
            LastVisibleBounds = visible;
            LastOverlayCells = lineCells;
        }
        if (hover.HasValue && !panning)
        {
            outline.Color = session.CanPaint ? new SKColor(31, 31, 31) : new SKColor(196, 43, 28);
            foreach (var cell in hover.Value.Disk(Tool is DrawingTool.Pencil or DrawingTool.Eraser ? BrushRadius : 0)) Highlight(cell, false, true);
        }
        if (measuring)
            LastPaintTimings = new PaintTimings(
                Milliseconds(paintStarted, afterBackdrop),
                Milliseconds(afterBackdrop, afterArtwork),
                Milliseconds(afterArtwork, afterBlit),
                Milliseconds(afterBlit, Stopwatch.GetTimestamp()));
    }

    // The rectangle a drag is sketching out, for the shader to fill. Unclamped: the shader rejects cells
    // outside the canvas anyway, and clamping it to what is on screen was only ever there to keep the
    // per-cell loop down.
    private RectanglePreview? RectangleBeingDragged()
    {
        if (!drawing || Tool != DrawingTool.Rectangle || !start.HasValue || !hover.HasValue || session == null) return null;
        var (columnA, rowA) = HexGeometry.ToOffset(start.Value, session.Document.Orientation);
        var (columnB, rowB) = HexGeometry.ToOffset(hover.Value, session.Document.Orientation);
        return new RectanglePreview(
            Math.Min(columnA, columnB), Math.Min(rowA, rowB),
            Math.Max(columnA, columnB), Math.Max(rowA, rowB), FilledShapes);
    }

    private void PointerDown(object sender, MouseButtonEventArgs args)
    {
        if (session == null) return;
        if (!App.KeepFocusAway) Focus();
        var point = args.GetPosition(this);
        if (args.ChangedButton == MouseButton.Middle || (SpacePressed && args.ChangedButton == MouseButton.Left))
        {
            panning = true; panPoint = point; Cursor = Cursors.ScrollAll; CaptureMouse(); args.Handled = true; return;
        }
        if (args.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        hover = Coordinate(point);
        if (session.Document.IndexOf(hover.Value) < 0) return;
        eraseGesture = args.ChangedButton == MouseButton.Right;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 || Tool == DrawingTool.Eyedropper)
        {
            ColorPicked?.Invoke(session.Document.CompositeAt(session.Document.IndexOf(hover.Value))); return;
        }
        if (Tool == DrawingTool.Fill) { session.FloodFill(hover.Value, eraseGesture ? 0 : ForegroundColor); return; }
        drawing = true; start = previous = hover; CaptureMouse();
        if (Tool is DrawingTool.Pencil or DrawingTool.Eraser)
        {
            session.BeginStroke(); session.Paint(hover.Value, eraseGesture || Tool == DrawingTool.Eraser ? 0 : ForegroundColor, BrushRadius);
        }
        else if (Tool == DrawingTool.Selection) session.SelectRectangle(start.Value, hover.Value);
        InvalidateVisual(); args.Handled = true;
    }

    private void PointerMoved(object sender, MouseEventArgs args)
    {
        if (session == null) return;
        var point = args.GetPosition(this);
        if (panning)
        {
            offsetX += (float)(point.X - panPoint.X); offsetY += (float)(point.Y - panPoint.Y); panPoint = point; InvalidateVisual(); return;
        }
        hover = Coordinate(point);
        var (column, row) = HexGeometry.ToOffset(hover.Value, session.Document.Orientation);
        StatusChanged?.Invoke(session.Document.IndexOf(hover.Value) >= 0 ? $"Cell {column}, {row}    |    Axial {hover.Value.Q}, {hover.Value.R}" : "Outside canvas    |    Space + drag to pan");
        if (drawing && previous.HasValue)
        {
            if (Tool is DrawingTool.Pencil or DrawingTool.Eraser)
                foreach (var cell in previous.Value.LineTo(hover.Value)) session.Paint(cell, eraseGesture || Tool == DrawingTool.Eraser ? 0 : ForegroundColor, BrushRadius);
            else if (Tool == DrawingTool.Selection && start.HasValue) session.SelectRectangle(start.Value, hover.Value);
            previous = hover;
        }
        InvalidateVisual();
    }

    private void PointerUp(object sender, MouseButtonEventArgs args)
    {
        if (!panning && drawing && session != null && start.HasValue && hover.HasValue)
        {
            if (Tool == DrawingTool.Line) session.DrawLine(start.Value, hover.Value, eraseGesture ? 0 : ForegroundColor, BrushRadius);
            else if (Tool == DrawingTool.Rectangle) session.DrawRectangle(start.Value, hover.Value, eraseGesture ? 0 : ForegroundColor, FilledShapes);
        }
        FinishGesture(); ReleaseMouseCapture(); args.Handled = true;
    }

    public void FinishGesture()
    {
        session?.EndStroke(Tool == DrawingTool.Eraser || eraseGesture ? "Erase" : "Brush stroke");
        drawing = false; panning = false; previous = start = null; Cursor = Cursors.Cross; InvalidateVisual();
    }

    private void Wheel(object sender, MouseWheelEventArgs args) { ChangeZoom(args.Delta > 0 ? 1.15f : 1 / 1.15f, args.GetPosition(this)); args.Handled = true; }
}
