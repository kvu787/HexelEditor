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
    private EditorSession? session;
    private readonly ArtworkCache artwork = new();
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
        var bounds = ArtworkRenderer.Bounds(session.Document);
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

    private HexCoordinate Coordinate(Point point) => HexGeometry.FromPoint((point.X - offsetX) / Zoom, (point.Y - offsetY) / Zoom, session!.Document.Orientation, ArtworkRenderer.CellRadius);

    // The inverse of Coordinate: where a cell's center sits in this canvas's own coordinates. Validation aims
    // real pointer input at a cell with it, so the pan and zoom it reads are the ones the pointer handlers use.
    public Point PointOf(HexCoordinate coordinate)
    {
        var (x, y) = HexGeometry.Center(coordinate, session!.Document.Orientation, ArtworkRenderer.CellRadius);
        return new Point(x * Zoom + offsetX, y * Zoom + offsetY);
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(new SKColor(243, 243, 243));
        if (session == null || ActualWidth <= 0) return;
        canvas.Scale((float)(args.Info.Width / ActualWidth), (float)(args.Info.Height / ActualHeight));
        using (var dot = new SKPaint { Color = new SKColor(222, 222, 222), IsAntialias = true })
            for (var y = 18; y < ActualHeight; y += 24)
                for (var x = 18; x < ActualWidth; x += 24) canvas.DrawCircle(x, y, 0.7f, dot);
        canvas.Translate(offsetX, offsetY); canvas.Scale(Zoom);
        // Drained every frame whatever the zoom, so the raster never misses an edit made while it was not
        // the thing being drawn.
        artwork.MarkDirty(session.TakeDirtyCells());
        ArtworkRenderer.Draw(canvas, session.Document, true, ShowGrid, Zoom, artwork);
        using var path = ArtworkRenderer.Hexagon(session.Document.Orientation, ArtworkRenderer.CellRadius * 0.96f);
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f / Zoom, Color = new SKColor(0, 95, 184) };
        using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0, 95, 184, 40) };
        // The cursor ring carries a light halo so that it stays visible over both pale and dark artwork.
        using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.4f / Zoom, Color = new SKColor(255, 255, 255, 205) };
        var visible = canvas.LocalClipBounds;
        void Highlight(HexCoordinate coordinate, bool filled, bool cursor = false)
        {
            if (session.Document.IndexOf(coordinate) < 0) return;
            var (x, y) = HexGeometry.Center(coordinate, session.Document.Orientation, ArtworkRenderer.CellRadius);
            // A whole-canvas selection is mostly off screen once zoomed in, so the artwork's culling applies here too.
            if (!ArtworkRenderer.IsVisible(visible, x, y)) return;
            canvas.Save(); canvas.Translate((float)x, (float)y);
            if (filled) canvas.DrawPath(path, fill);
            if (cursor) canvas.DrawPath(path, halo);
            canvas.DrawPath(path, outline); canvas.Restore();
        }
        var (minColumn, minRow, maxColumn, maxRow) = ArtworkRenderer.VisibleCells(session.Document, visible);
        if (session.Selection.Count > 0)
            for (var row = minRow; row <= maxRow; row++)
                for (var column = minColumn; column <= maxColumn; column++)
                {
                    var index = row * session.Document.Width + column;
                    if (session.Selection.Contains(index)) Highlight(session.Document.CoordinateAt(index), true);
                }
        if (drawing && start.HasValue && hover.HasValue && Tool == DrawingTool.Line)
            foreach (var cell in start.Value.LineTo(hover.Value)) Highlight(cell, true);
        if (drawing && start.HasValue && hover.HasValue && Tool == DrawingTool.Rectangle)
        {
            var (columnA, rowA) = HexGeometry.ToOffset(start.Value, session.Document.Orientation);
            var (columnB, rowB) = HexGeometry.ToOffset(hover.Value, session.Document.Orientation);
            var left = Math.Max(minColumn, Math.Min(columnA, columnB)); var right = Math.Min(maxColumn, Math.Max(columnA, columnB));
            var top = Math.Max(minRow, Math.Min(rowA, rowB)); var bottom = Math.Min(maxRow, Math.Max(rowA, rowB));
            for (var row = top; row <= bottom; row++)
                for (var column = left; column <= right; column++)
                    if (FilledShapes || row == top || row == bottom || column == left || column == right) Highlight(HexGeometry.FromOffset(column, row, session.Document.Orientation), true);
        }
        if (hover.HasValue && !panning)
        {
            outline.Color = session.CanPaint ? new SKColor(31, 31, 31) : new SKColor(196, 43, 28);
            foreach (var cell in hover.Value.Disk(Tool is DrawingTool.Pencil or DrawingTool.Eraser ? BrushRadius : 0)) Highlight(cell, false, true);
        }
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
