using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using HexelEditor.Core;
using OpenTK.Wpf;
using SkiaSharp;

namespace HexelEditor.Desktop;

public enum DrawingTool { Pencil, Eraser, Fill, Eyedropper, Line, Rectangle, Selection }

// The canvas: a GL framebuffer WPF composites as a Direct3D surface, painted with Skia through the GPU
// renderer's context.
//
// Why GLWpfControl rather than the bitmap element this used to be: that element hands WPF a bitmap on the
// processor, so every frame drawn on the GPU had to be read back across the bus to reach it, and WPF then
// uploaded it again to show it. At a 4K display's size the readback alone was 3.7 ms a frame, the whole of
// the frame once the rest had been taken out of it. GLWpfControl shares a Direct3D 9 surface with GL
// through the NV_DX_interop extension and shows that surface through a D3DImage, so the frame is drawn
// where it is shown and nothing crosses the bus.
//
// Why this is the element that extends GLWpfControl rather than SkiaSharp's SKGLElement: SKGLElement
// builds a GRContext of its own on first paint, and the renderer's textures live in a GRContext, so the two
// have to be the same one. The renderer's is built here, as soon as the element has started and its GL
// context is current, and the element's framebuffer is wrapped as a surface of that context each frame.
public sealed class HexCanvas : GLWpfControl
{
    private const float MinimumZoom = 0.08f;
    private const float MaximumZoom = 30f;
    // The dotted paper behind the canvas, in this control's own units.
    private const float DotSpacing = 24f;
    private const float DotOffset = 18f;
    private const float DotRadius = 0.7f;
    private static readonly SKColor Paper = new(243, 243, 243);
    // GL_RGBA8, the sized format the element's framebuffer is declared to Skia as. The Direct3D surface
    // behind it is X8R8G8B8, and the driver maps the one onto the other.
    private const uint GlRgba8 = 0x8058;
    // The settings every canvas starts with. GLWpfControl shares one GL context between every element in the
    // process and refuses a second element whose settings would call for a different one, so they are fixed
    // here. 3.3 core is what the renderer was written against; RenderContinuously is off because the canvas
    // repaints when something changed, through InvalidateVisual, and not on every composition tick.
    private static GLWpfControlSettings ElementSettings => new()
    {
        MajorVersion = 3,
        MinorVersion = 3,
        GraphicsProfile = OpenTK.Windowing.Common.ContextProfile.Core,
        RenderContinuously = false,
        UseDeviceDpi = true,
        TransparentBackground = false,
    };
    private EditorSession? session;
    private readonly ArtworkCache artwork = new();
    private readonly SelectionMask selectionMask = new();
    // Null when the GPU could not be opened, which the application refuses to start on before anything is
    // drawn. GpuHexRenderer.Unavailable says why.
    private readonly GpuHexRenderer? gpu;
    // The element's framebuffer as a Skia surface. Wrapped once and kept while the framebuffer stays the
    // same, because GLWpfControl keeps the same one until the element changes size.
    private GRBackendRenderTarget? frameTarget;
    private SKSurface? frame;
    private int frameWidth, frameHeight, frameHandle;
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

    /// <summary>The size in device pixels of the last frame the element drew, or zero before the first.</summary>
    public SKSize CanvasSize { get; private set; }

    /// <summary>
    /// What one paint spent, in milliseconds: the artwork (the renderer's whole frame, recorded and handed
    /// to the GPU), the selection, previews and cursor ring drawn over it, and then the wait for the GPU to
    /// finish the frame, which is only waited for while measuring. Only filled while
    /// <see cref="MeasurePaints"/> is set.
    /// </summary>
    public readonly record struct PaintTimings(double Artwork, double Overlays, double Gpu)
    {
        public double Total => Artwork + Overlays + Gpu;
    }

    /// <summary>
    /// Times each paint's parts. Off unless something is measuring, because attributing the GPU's own share
    /// costs a sync at the end of the frame.
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
        // GLWpfControl would otherwise register class handlers that raise every key event in the window a
        // second time on this element. The window handles the keyboard itself.
        RegisterToEventsDirectly = false;
        // Starting the element creates the process's GL context, or joins it, and leaves it current on this
        // thread; the renderer is built right after, over that context.
        gpu = GpuHexRenderer.Open(() => Start(ElementSettings), App.Log.Write);
        Render += OnFrame;
        MouseDown += PointerDown; MouseMove += PointerMoved; MouseUp += PointerUp;
        MouseWheel += Wheel;
        MouseLeave += (_, _) => { if (!drawing) { hover = null; InvalidateVisual(); } };
        LostMouseCapture += (_, _) => FinishGesture();
        SizeChanged += (_, _) => { if (needsFit && ActualWidth > 0 && ActualHeight > 0) Fit(); else InvalidateVisual(); };
        // The raster holds its pixels in a pinned array, so it is worth releasing deliberately
        // rather than leaving to the finalizer. ShutdownStarted fires once, unlike Unloaded,
        // which also fires when a control is merely reparented. The frame surface goes with them, while the
        // context it belongs to is still current.
        Dispatcher.ShutdownStarted += (_, _) => { artwork.Dispose(); selectionMask.Dispose(); ReleaseFrame(); };
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

    // The element's own frame. GLWpfControl has locked the shared surface, bound its framebuffer and set the
    // viewport by the time this runs, and unlocks the surface once it returns, so everything drawn into the
    // framebuffer has to be drawn and flushed in here.
    private void OnFrame(TimeSpan elapsed)
    {
        if (gpu == null) return;
        int width = FrameBufferWidth, height = FrameBufferHeight, handle = Framebuffer;
        if (width <= 0 || height <= 0) return;
        // GLWpfControl's own GL calls, binding the framebuffer and the viewport, happened behind Skia's back.
        // Skia caches the GL state it set last, so it is told to assume nothing about it before drawing.
        gpu.Context.ResetContext();
        if (frame == null || frameWidth != width || frameHeight != height || frameHandle != handle)
        {
            ReleaseFrame();
            // Eight stencil bits and no multisampling, which is the renderbuffer GLWpfControl attaches. The
            // origin is GL's: the shared surface is read by Direct3D the other way up, and GLWpfControl flips
            // it back when it draws the image, so a frame drawn the GL way round comes out upright.
            frameTarget = new GRBackendRenderTarget(width, height, sampleCount: 0, stencilBits: 8,
                new GRGlFramebufferInfo((uint)handle, GlRgba8));
            frame = SKSurface.Create(gpu.Context, frameTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888)
                ?? throw new InvalidOperationException($"Could not wrap the {width} by {height} framebuffer as a surface.");
            frameWidth = width; frameHeight = height; frameHandle = handle;
        }
        CanvasSize = new SKSize(width, height);
        Paint(frame, width, height);
        if (compareNextFrame)
        {
            compareNextFrame = false;
            FrameComparison = CompareFrame(frame, width, height);
        }
    }

    private void ReleaseFrame()
    {
        frame?.Dispose();
        frame = null;
        frameTarget?.Dispose();
        frameTarget = null;
    }

    /// <summary>
    /// Paints one frame into a surface the caller owns, through the same code a real paint runs. The surface
    /// has to be one of the renderer's context, which <see cref="GpuHexRenderer.CreateSurface"/> makes.
    /// </summary>
    /// <remarks>
    /// For measurement, and it exists because asking WPF for a frame is not a way to get one: a control
    /// repaints when WPF decides to run its render pass, and until then a visual is drawn from what it drew
    /// last time. Rendering the tree into a bitmap reuses that same content, so it produces a picture
    /// without producing a paint.
    /// </remarks>
    public void PaintOneFrame(SKSurface surface, int width, int height) => Paint(surface, width, height);

    // The view the renderer is given for a frame of this many device pixels: the pan and zoom in device
    // pixels, and where the paper's dots fall. Device pixels per unit of the canvas's own coordinates come
    // from the frame's size against the element's, and the renderer is given the horizontal one for the zoom
    // and the pan, which takes the pixels to be square; WPF scales a display uniformly, so the two agree on
    // every real one.
    private (float Zoom, float OffsetX, float OffsetY, float ScaleX, float ScaleY, BackdropDots Backdrop) ViewFor(int width, int height)
    {
        var scaleX = (float)(width / ActualWidth);
        var scaleY = (float)(height / ActualHeight);
        var backdrop = new BackdropDots(DotSpacing * scaleX, DotSpacing * scaleY,
            DotOffset * scaleX, DotOffset * scaleY, DotRadius * scaleX);
        return (Zoom * scaleX, offsetX * scaleX, offsetY * scaleX, scaleX, scaleY, backdrop);
    }

    private void Paint(SKSurface surface, int width, int height)
    {
        var canvas = surface.Canvas;
        var measuring = MeasurePaints;
        var paintStarted = measuring ? Stopwatch.GetTimestamp() : 0;
        if (session == null || ActualWidth <= 0)
        {
            // Nothing to draw, so nothing has written over whatever the surface last held.
            canvas.Clear(Paper);
            return;
        }
        // The shader is the only thing that draws artwork, and the application refuses to start without it,
        // so there is nothing to fall back to here and no case where carrying on would show anything.
        var renderer = gpu ?? throw new InvalidOperationException("The canvas cannot draw: " + GpuHexRenderer.Unavailable);
        var view = ViewFor(width, height);

        // Drained every frame whatever the zoom, so the raster never misses an edit made while it was not
        // the thing being drawn.
        artwork.MarkDirty(session.TakeDirtyCells());

        // The selection and the rectangle being dragged out are drawn by the same shader as the artwork,
        // from a mask of their own. Both cover areas rather than outlines, and an area on this canvas can be
        // two million cells, which is more than any per-cell drawing can keep up with.
        selectionMask.Refresh(session.Document, session.Selection);

        // One shaded rectangle covering the surface, drawn at device resolution so it stays crisp on a
        // scaled display, and covering every pixel: the shader draws the paper as well as the canvas, so
        // the surface needs no clearing and nothing is left over from the frame before.
        renderer.Draw(canvas, width, height, session.Document, artwork,
            view.Zoom, view.OffsetX, view.OffsetY, ShowGrid, selectionMask, RectangleBeingDragged(), view.Backdrop);
        var afterArtwork = measuring ? Stopwatch.GetTimestamp() : 0;

        // The overlays are drawn in the canvas's own coordinates, and the transform is put back afterwards so
        // that a surface painted twice does not inherit the first paint's scale.
        var saved = canvas.Save();
        canvas.Scale(view.ScaleX, view.ScaleY);
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
        canvas.RestoreToCount(saved);
        // Handed to the GPU here, because for the element's own frame nothing after this point may touch the
        // framebuffer: GLWpfControl gives the surface back to Direct3D as soon as the frame returns.
        renderer.Context.Flush();
        var afterOverlays = measuring ? Stopwatch.GetTimestamp() : 0;
        if (measuring)
        {
            // Waiting for the GPU is what makes the figure a frame's cost rather than the cost of asking for
            // one. The window never waits: the compositor picks the frame up when it is done.
            renderer.Finish();
            LastPaintTimings = new PaintTimings(
                Milliseconds(paintStarted, afterArtwork),
                Milliseconds(afterArtwork, afterOverlays),
                Milliseconds(afterOverlays, Stopwatch.GetTimestamp()));
        }
    }

    private bool compareNextFrame;

    /// <summary>
    /// Asks for the next frame the element draws into its own framebuffer to be read back and compared, pixel
    /// for pixel, with the renderer's offscreen frame of the same view. The result lands in
    /// <see cref="FrameComparison"/>. For the checks, which otherwise verify the shader only through the
    /// offscreen surface and would not notice the window's own surface disagreeing with it.
    /// </summary>
    public void CompareNextFrame() => compareNextFrame = true;

    /// <summary>
    /// How the window's last compared frame stood against the renderer's: the pixels counted, how many
    /// differed, and the first few differences. Null until a frame has been compared.
    /// </summary>
    public (long Pixels, long Differing, IReadOnlyList<string> Differences)? FrameComparison { get; private set; }

    /// <summary>The two frames of the last comparison, for a check that wants to keep a picture of them.</summary>
    public (SKBitmap Window, SKBitmap Renderer)? ComparedFrames { get; private set; }

    // Reads the frame the element drew, while the framebuffer is still the element's to read, and renders the
    // same view through the renderer's offscreen path. The two are drawn by one shader with one set of
    // uniforms into surfaces of two different formats and origins, so the comparison is of the surfaces,
    // which is the part nothing else checks. The cursor ring and the other overlays are left out of the
    // window's frame by comparing only when none of them is being drawn, which is the caller's to arrange.
    private (long Pixels, long Differing, IReadOnlyList<string> Differences) CompareFrame(SKSurface surface, int width, int height)
    {
        if (session == null || gpu == null) throw new InvalidOperationException("There is no frame to compare.");
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var window = new SKBitmap(info);
        if (!surface.ReadPixels(info, window.GetPixels(), window.RowBytes, 0, 0))
            throw new InvalidOperationException("Could not read the window's frame back from its framebuffer.");
        var view = ViewFor(width, height);
        var rendered = gpu.Render(session.Document, artwork, width, height,
            view.Zoom, view.OffsetX, view.OffsetY, ShowGrid, selectionMask, null, view.Backdrop);
        var differing = 0L;
        var differences = new List<string>();
        var windowPixels = window.GetPixelSpan();
        var renderedPixels = rendered.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            var windowRow = windowPixels.Slice(y * window.RowBytes, width * 4);
            var renderedRow = renderedPixels.Slice(y * rendered.RowBytes, width * 4);
            if (windowRow.SequenceEqual(renderedRow)) continue;
            for (var x = 0; x < width; x++)
            {
                var a = windowRow.Slice(x * 4, 4);
                var b = renderedRow.Slice(x * 4, 4);
                if (a.SequenceEqual(b)) continue;
                differing++;
                if (differences.Count < 20)
                    differences.Add($"at {x},{y} the window drew {ColorMath.Format(Bgra(a))} where the renderer drew {ColorMath.Format(Bgra(b))}");
            }
        }
        ComparedFrames?.Window.Dispose();
        ComparedFrames?.Renderer.Dispose();
        ComparedFrames = (window, rendered.Copy() ?? throw new InvalidOperationException("Could not keep the renderer's frame."));
        return ((long)width * height, differing, differences);

        static uint Bgra(ReadOnlySpan<byte> pixel) => (uint)(pixel[3] << 24 | pixel[2] << 16 | pixel[1] << 8 | pixel[0]);
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
