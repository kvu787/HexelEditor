using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using HexelEditor.Core;

namespace HexelEditor.Desktop;

// The editor checking itself, driven by Verify.ps1 through App's verification mode.
//
// These are not unit tests and could not be: every one of them is about the real window, its real controls
// and the real renderer, in a hidden host on a desktop of its own. They live on MainWindow because that is
// what they drive, and in a file of their own because the window has enough to do without them.
public partial class MainWindow
{
    public void VerifyEditor(string folder)
    {
        var original = session.Document.CompositeAt(0);
        session.Paint(session.Document.CoordinateAt(0), 0xFF00FF00); session.EndStroke();
        if (!UndoButton.IsEnabled || UnsavedMarker.Visibility != Visibility.Visible) throw new InvalidOperationException("The interface did not reflect the edit.");
        UndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (session.IsDirty || session.Document.CompositeAt(0) != original) throw new InvalidOperationException("The Undo button did not restore the artwork.");
        toolButtons[DrawingTool.Eraser].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (EditorCanvas.Tool != DrawingTool.Eraser) throw new InvalidOperationException("The tool button did not select the eraser.");
        SetTool(DrawingTool.Pencil);
        AttachSession(new EditorSession(ExampleDocument.Create()));
        var path = System.IO.Path.Combine(folder, "Example.hexel"); ProjectFile.Save(session.Document, path);
        var roundTrip = ProjectFile.Load(path);
        if (roundTrip.Layers.Count != 3 || roundTrip.CompositeAt(0) != session.Document.CompositeAt(0)) throw new InvalidDataException("Project round trip failed.");
        var cells = VerifyArtworkRendering(roundTrip);
        var surfaceCells = VerifyCanvasSurface();
        EditorCanvas.Fit(); UpdateLayout();
        File.WriteAllText(System.IO.Path.Combine(folder, "Verification.txt"), $"WPF commands and saved-state indicator: passed\nProject save/load: passed\nArtwork rendering: all {cells} cell colors verified\nCanvas surface: background and seams verified across {surfaceCells} cells in both orientations\n");
    }

    // Draws a document the way the canvas draws it, one pixel per unit of artwork space, so a check can
    // sample what a cell actually put on screen. The pixel of margin keeps the outermost cells off the edge.
    private static SkiaSharp.SKBitmap RenderArtwork(HexDocument document, out SkiaSharp.SKRect bounds)
    {
        bounds = CanvasGeometry.Bounds(document);
        using var cache = new ArtworkCache();
        return RenderFrame(document, cache, (int)Math.Ceiling(bounds.Width) + 2, (int)Math.Ceiling(bounds.Height) + 2,
            zoom: 1, offsetX: 1 - bounds.Left, offsetY: 1 - bounds.Top);
    }

    // One frame from the editor's own renderer, as a bitmap the caller owns. This is the point of these
    // checks: the shader is the only thing that draws the canvas, so a check that drew the artwork any other
    // way would be verifying something nobody sees. The renderer hands back the buffer it reads every frame
    // into and goes on using it, so what a check keeps has to be a copy.
    private static SkiaSharp.SKBitmap RenderFrame(HexDocument document, ArtworkCache cache, int width, int height,
        float zoom, float offsetX, float offsetY)
    {
        var renderer = GpuHexRenderer.Shared(App.Log.Write)
            ?? throw new InvalidOperationException("There is no GPU renderer: " + GpuHexRenderer.Unavailable);
        var frame = renderer.Render(document, cache, width, height, zoom, offsetX, offsetY, grid: false);
        return frame.Copy() ?? throw new InvalidOperationException("Could not copy the rendered frame.");
    }

    // Every cell's composited color has to land at that cell's own center. This runs on the reloaded example,
    // which is three layers of real artwork, so a layer drawn out of order or geometry half a cell out of
    // place shows up as the wrong color at a known place rather than as a picture that merely looks hexagonal.
    private static int VerifyArtworkRendering(HexDocument document)
    {
        using var bitmap = RenderArtwork(document, out var bounds);
        for (var index = 0; index < document.CellCount; index++)
        {
            var (x, y) = HexGeometry.Center(document.CoordinateAt(index), document.Orientation, CanvasGeometry.CellRadius);
            var sample = (uint)bitmap.GetPixel((int)Math.Round(x + 1 - bounds.Left), (int)Math.Round(y + 1 - bounds.Top));
            var expected = ColorMath.Over(document.CompositeAt(index), document.BackgroundColor);
            if (sample != expected)
                throw new InvalidDataException($"Cell {index} drew {ColorMath.Format(sample)} where {ColorMath.Format(expected)} belongs.");
        }
        return document.CellCount;
    }

    // Two things about the surface under the artwork, on one canvas holding a single color everywhere but its
    // first cell. The background is one solid color rather than a pattern, so the cell holding no artwork
    // samples the background and every other cell samples its own color. And between two opaque neighbors
    // none of the background may show at all: a hairline there is what makes a canvas read as a grid when no
    // grid is drawn. The shader gives every pixel of the canvas the colour of whichever cell its centre
    // lands in, so the midpoint of two neighbours belongs to one of the two and there is no third thing it
    // could be; this holds the renderer to that.
    //
    // Both orientations, because the two lay their rows out along different axes, the shader takes a
    // different branch for each, and only one of them is what a new canvas starts as.
    private static int VerifyCanvasSurface()
    {
        const uint background = 0xFF336699;
        const uint artwork = 0xFFFF0000;
        var verified = 0;
        foreach (var orientation in new[] { HexOrientation.PointyTop, HexOrientation.FlatTop })
        {
            var document = new HexDocument(5, 4, orientation) { BackgroundColor = background };
            for (var index = 1; index < document.CellCount; index++) document.Layers[0].Cells[index] = artwork;
            using var bitmap = RenderArtwork(document, out var bounds);
            uint Sample(double x, double y) =>
                (uint)bitmap.GetPixel((int)Math.Round(x + 1 - bounds.Left), (int)Math.Round(y + 1 - bounds.Top));
            for (var index = 0; index < document.CellCount; index++)
            {
                var coordinate = document.CoordinateAt(index);
                var (x, y) = HexGeometry.Center(coordinate, orientation, CanvasGeometry.CellRadius);
                var expected = index == 0 ? background : artwork;
                var sample = Sample(x, y);
                if (sample != expected)
                    throw new InvalidDataException($"The {orientation} canvas drew {ColorMath.Format(sample)} at cell {index} where {ColorMath.Format(expected)} belongs.");
                if (index == 0) continue;
                foreach (var direction in HexCoordinate.Directions)
                {
                    // Cell zero holds no artwork, so it is not one of the opaque pairs this looks between.
                    var neighbor = document.IndexOf(coordinate + direction);
                    if (neighbor <= 0) continue;
                    var (neighborX, neighborY) = HexGeometry.Center(coordinate + direction, orientation, CanvasGeometry.CellRadius);
                    var between = Sample((x + neighborX) / 2, (y + neighborY) / 2);
                    if (between != artwork)
                        throw new InvalidDataException($"{ColorMath.Format(between)} shows between {orientation} cells {index} and {neighbor}.");
                }
            }
            verified += document.CellCount;
        }
        return verified;
    }

    // Opening a menu leaves it highlighted, so this runs after the editor screenshot rather than inside VerifyEditor.
    // The palette is the one panel laid out from the document at runtime, so the grid it builds, the color a
    // swatch sets, and the row an added color lands in are checked here rather than on a physical display.
    public void VerifyPalette(string folder)
    {
        UpdateLayout();
        if (PalettePanel.Columns != HexDocument.PaletteColumns)
            throw new InvalidOperationException("The palette is not laid out on its grid.");
        if (PalettePanel.Children.Count != session.Document.Palette.Count)
            throw new InvalidOperationException("The palette grid does not hold every color.");
        var side = Math.Floor(PalettePanel.ActualWidth / HexDocument.PaletteColumns);
        if (side < 1) throw new InvalidOperationException("The palette grid was given no width to lay swatches out in.");
        foreach (FrameworkElement swatch in PalettePanel.Children)
            if (swatch.Height != side) throw new InvalidOperationException("The palette swatches are not square.");

        // Choosing a color sets the foreground and moves the selection ring onto that swatch.
        var chosenIndex = HexDocument.PaletteColumns * 3 + 10;
        var chosen = session.Document.Palette[chosenIndex];
        ((Button)PalettePanel.Children[chosenIndex]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (EditorCanvas.ForegroundColor != chosen)
            throw new InvalidOperationException("A palette swatch did not set the foreground color.");
        if ((string?)((Button)PalettePanel.Children[chosenIndex]).Tag != "Active")
            throw new InvalidOperationException("The chosen palette swatch is not marked as active.");

        // Adding a color has to keep working now that the default palette fills the grid exactly.
        const uint added = 0xFF7F00FF;
        if (session.Document.Palette.Contains(added))
            throw new InvalidOperationException("The palette already holds the color this check adds.");
        var swatches = PalettePanel.Children.Count;
        SetColor(added);
        AddColorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (session.Document.Palette.Count != swatches + 1 || session.Document.Palette[^1] != added)
            throw new InvalidOperationException("Add current color did not append the color to the palette.");
        UpdateLayout();
        if (PalettePanel.Children.Count != swatches + 1)
            throw new InvalidOperationException("The added color did not reach the palette grid.");

        // Take it back out. Adding a color is an edit like any other, and leaving the session unsaved would
        // stop the window closing, which is how this verification host exits.
        session.Undo();
        if (PalettePanel.Children.Count != swatches || session.IsDirty)
            throw new InvalidOperationException("Undo did not take the added color back out of the palette.");
        File.AppendAllText(System.IO.Path.Combine(folder, "Verification.txt"),
            $"Palette grid: {swatches} swatches in {HexDocument.PaletteColumns} columns at {side} pixels square, choosing and adding a color verified\n");
    }

    // Everything else drives the editor by calling into it. This drives the pointer, which is how the editor
    // is actually used: window messages carrying real positions, through WPF's own input provider, so hit
    // testing decides what is hit, the canvas takes and releases real mouse capture, and the handlers see the
    // moves in the order a hand would make them. Painting is what this application is for, and none of it was
    // covered before: a wrong point-to-cell mapping, a stroke that dropped the cells between two moves, a
    // canvas that something had covered, or a pan fighting with drawing would all have passed.
    public void VerifyPointerInput(string folder)
    {
        // A blank canvas rather than the example, so that a cell holds the stroke or holds nothing. Erasing
        // clears the active layer, which on artwork with three layers would leave the ones underneath showing
        // and make "did the erase work" a question about compositing instead of about the gesture.
        AttachSession(new EditorSession(new HexDocument(40, 32)));
        SetTool(DrawingTool.Pencil);
        EditorCanvas.BrushRadius = 0;
        const uint stroke = 0xFF2040FF;
        SetColor(stroke);
        UpdateLayout();
        EditorCanvas.Fit();
        UpdateLayout();

        var document = session.Document;
        var orientation = document.Orientation;
        var row = document.Height / 2;
        var from = HexGeometry.FromOffset(4, row, orientation);
        var to = HexGeometry.FromOffset(12, row, orientation);
        var line = from.LineTo(to).ToArray();
        if (line.Length < 5) throw new InvalidOperationException("The pointer check needs a run of cells to drag across.");
        if (document.IndexOf(from) < 0 || document.IndexOf(to) < 0)
            throw new InvalidOperationException("The cells the pointer check drags between are outside the canvas.");

        // Hit testing, from the root, so that anything in front of the canvas would be found instead of it.
        var start = EditorCanvas.PointOf(from);
        var hit = PointerInput.HitTest(EditorCanvas, start);
        if (!ReferenceEquals(hit, EditorCanvas))
            throw new InvalidOperationException($"The pointer reaches {hit?.GetType().Name ?? "nothing"} at the canvas, not the canvas.");

        // A move on its own has to land on the cell the point was taken from, which is the mapping between
        // pointer positions and cells working in both directions.
        PointerInput.Move(EditorCanvas, start);
        if (EditorCanvas.HoverCoordinate != from)
            throw new InvalidOperationException($"A pointer move aimed at {start.X:F1},{start.Y:F1} for cell {from.Q},{from.R} was read as cell {EditorCanvas.HoverCoordinate?.ToString() ?? "none"}. {PointerInput.Describe(EditorCanvas)}");

        // The stroke. Capture is checked while the button is down, because losing it mid-gesture is what ends
        // a stroke early, and it has to be given back afterwards or the next click goes to the wrong place.
        PointerInput.Press(EditorCanvas, start, MouseButton.Left);
        if (!ReferenceEquals(Mouse.Captured, EditorCanvas))
            throw new InvalidOperationException("The canvas did not capture the mouse for a stroke.");
        var end = EditorCanvas.PointOf(to);
        for (var step = 1; step <= 8; step++)
        {
            var fraction = (double)step / 8;
            PointerInput.Move(EditorCanvas, new Point(start.X + (end.X - start.X) * fraction, start.Y + (end.Y - start.Y) * fraction), MouseButton.Left);
        }
        PointerInput.Release(EditorCanvas, end, MouseButton.Left);
        if (Mouse.Captured != null)
            throw new InvalidOperationException($"The canvas kept the mouse captured after the stroke, held by {Mouse.Captured}.");

        // Eight moves across nine cells, so the gesture only joins up if the cells between two reported
        // positions are filled in as well.
        foreach (var cell in line)
        {
            var index = document.IndexOf(cell);
            if (index < 0) throw new InvalidOperationException($"Cell {cell.Q},{cell.R} on the stroke is outside the canvas.");
            if (document.CompositeAt(index) != stroke)
                throw new InvalidOperationException($"A dragged stroke missed cell {cell.Q},{cell.R}, which holds {ColorMath.Format(document.CompositeAt(index))}.");
        }

        // One gesture is one undo, however many moves it was made of.
        if (!session.CanUndo) throw new InvalidOperationException("A dragged stroke left nothing to undo.");
        session.Undo();
        if (line.Any(cell => document.CompositeAt(document.IndexOf(cell)) != 0))
            throw new InvalidOperationException("Undoing the stroke did not take all of it back out, so the drag was recorded as more than one command.");

        // The right button erases with the same gesture. Paint first, then take it back out by dragging.
        PointerInput.Drag(EditorCanvas, start, end, MouseButton.Left);
        if (line.Any(cell => document.CompositeAt(document.IndexOf(cell)) != stroke))
            throw new InvalidOperationException("The drag that the erase check paints over did not fill every cell.");
        PointerInput.Drag(EditorCanvas, start, end, MouseButton.Right);
        foreach (var cell in line)
            if (document.CompositeAt(document.IndexOf(cell)) != 0)
                throw new InvalidOperationException($"A right-button drag did not erase cell {cell.Q},{cell.R}.");
        session.Undo();
        session.Undo();

        // Panning moves the canvas under the pointer and must not paint while doing it. The same point
        // reading as a different cell afterwards is the pan having happened.
        var edits = session.CanUndo;
        var before = EditorCanvas.HoverCoordinate;
        PointerInput.Drag(EditorCanvas, start, new Point(start.X + 120, start.Y + 40), MouseButton.Middle);
        PointerInput.Move(EditorCanvas, start);
        var after = EditorCanvas.HoverCoordinate;
        if (after == before)
            throw new InvalidOperationException($"A middle-button drag did not pan the canvas: {start.X},{start.Y} still reads as cell {after}.");
        if (session.CanUndo != edits) throw new InvalidOperationException("Panning the canvas changed the artwork.");

        // The host closes by shutting the application down, which a document with unsaved edits would stop.
        if (session.IsDirty) throw new InvalidOperationException("The pointer checks left the document unsaved.");
        File.AppendAllText(System.IO.Path.Combine(folder, "Verification.txt"),
            $"Pointer input: hit testing, mouse capture, a {line.Length}-cell drag, right-button erase and middle-button pan verified through real window messages\n");
    }

    // The shortcut table, struck rather than called. Keys are injected, so Control and Shift are really held
    // and OnKeyDown reads the modifier state it would read under a hand. Nothing here opens a dialog or
    // touches the clipboard: Ctrl+N, Ctrl+O and Ctrl+S would wait on a modal window that no one can answer,
    // and the clipboard belongs to the window station, which is shared with whoever is using the machine.
    public void VerifyKeyboardInput(string folder)
    {
        AttachSession(new EditorSession(new HexDocument(40, 32)));
        UpdateLayout();
        EditorCanvas.Fit();
        UpdateLayout();
        if (PresentationSource.FromVisual(EditorCanvas) is not HwndSource source)
            throw new InvalidOperationException("The canvas is not in a window, so keys cannot be sent to it.");
        KeyboardInput.Focus(EditorCanvas);

        // Every tool has a key, and picking the wrong tool is the kind of mistake nothing else would catch.
        var tools = new (Key Key, DrawingTool Tool)[]
        {
            (Key.E, DrawingTool.Eraser), (Key.G, DrawingTool.Fill), (Key.I, DrawingTool.Eyedropper),
            (Key.L, DrawingTool.Line), (Key.U, DrawingTool.Rectangle), (Key.M, DrawingTool.Selection),
            (Key.B, DrawingTool.Pencil)
        };
        foreach (var (key, tool) in tools)
        {
            KeyboardInput.Type(source, key);
            KeyboardInput.WaitUntil(() => EditorCanvas.Tool == tool, $"{key} did not select the {tool} tool");
        }

        // The brush keys, which also prove an unmodified punctuation key arrives as itself.
        var radius = EditorCanvas.BrushRadius;
        KeyboardInput.Type(source, Key.OemCloseBrackets);
        KeyboardInput.WaitUntil(() => EditorCanvas.BrushRadius == radius + 1, "] did not grow the brush");
        KeyboardInput.Type(source, Key.OemOpenBrackets);
        KeyboardInput.WaitUntil(() => EditorCanvas.BrushRadius == radius, "[ did not shrink the brush back");

        var grid = EditorCanvas.ShowGrid;
        KeyboardInput.Type(source, Key.H);
        KeyboardInput.WaitUntil(() => EditorCanvas.ShowGrid != grid, "H did not toggle the grid");
        KeyboardInput.Type(source, Key.H);
        KeyboardInput.WaitUntil(() => EditorCanvas.ShowGrid == grid, "H did not toggle the grid back");

        // Space is held rather than struck, and is the one thing that reaches OnKeyUp, so a key that is never
        // released would show up here as a canvas stuck in panning.
        KeyboardInput.Press(source, Key.Space);
        KeyboardInput.WaitUntil(() => EditorCanvas.SpacePressed, "holding Space did not arm panning");
        KeyboardInput.Release(source, Key.Space);
        KeyboardInput.WaitUntil(() => !EditorCanvas.SpacePressed, "releasing Space did not disarm panning");

        // Control, read from the real key state.
        KeyboardInput.Type(source, Key.A, ModifierKeys.Control);
        KeyboardInput.WaitUntil(() => session.Selection.Any(), "Ctrl+A did not select anything");
        KeyboardInput.Type(source, Key.D, ModifierKeys.Control);
        KeyboardInput.WaitUntil(() => !session.Selection.Any(), "Ctrl+D did not clear the selection");

        // Control and Shift together, against something undoable. The stroke is made through the session
        // rather than with the pointer: giving WPF a keyboard focus makes it work out afresh where the
        // pointer is by asking the system, and on a desktop that is not being displayed there is no cursor to
        // report, so it settles outside the window and stops accepting driven moves for good. The pointer
        // pipeline is covered on its own in VerifyPointerInput, which is why that runs before this, and what
        // these keys are here to prove is the shortcut, not the stroke.
        var document = session.Document;
        var cell = HexGeometry.FromOffset(6, document.Height / 2, document.Orientation);
        const uint stroke = 0xFF20C040;
        SetColor(stroke);
        session.BeginStroke();
        session.Paint(cell, stroke);
        session.EndStroke();
        var painted = document.IndexOf(cell);
        if (painted < 0 || document.CompositeAt(painted) != stroke)
            throw new InvalidOperationException($"The cell the keyboard checks undo was not painted: cell {cell.Q},{cell.R} is index {painted}.");
        KeyboardInput.Type(source, Key.Z, ModifierKeys.Control);
        KeyboardInput.WaitUntil(() => document.CompositeAt(painted) == 0, "Ctrl+Z did not undo the stroke");
        KeyboardInput.Type(source, Key.Z, ModifierKeys.Control | ModifierKeys.Shift);
        KeyboardInput.WaitUntil(() => document.CompositeAt(painted) == stroke, "Ctrl+Shift+Z did not redo the stroke");
        KeyboardInput.Type(source, Key.Z, ModifierKeys.Control);
        KeyboardInput.WaitUntil(() => document.CompositeAt(painted) == 0, "Ctrl+Z did not undo the redone stroke");

        // Modifiers must not be left down, or every later key would arrive as a shortcut.
        if (Keyboard.Modifiers != ModifierKeys.None)
            throw new InvalidOperationException($"The keyboard checks left {Keyboard.Modifiers} held down.");
        if (session.IsDirty) throw new InvalidOperationException("The keyboard checks left the document unsaved.");
        File.AppendAllText(System.IO.Path.Combine(folder, "Verification.txt"),
            $"Keyboard input: {tools.Length} tool keys, brush and grid keys, Space held and released, Ctrl+A, Ctrl+D, Ctrl+Z and Ctrl+Shift+Z verified through real key messages with the modifiers really held\n");
    }

    // The renderer at the sizes a cell is really drawn at, from a canvas far larger than the window to one
    // zoomed well in. A cell's colour has to land where that cell is, and two separate pieces of arithmetic
    // decide that: the shader's own inversion of the hex layout, and the raster's two texels a cell with the
    // half-cell offset of alternate lines. Either could put the artwork in the wrong place without anything
    // else noticing, and neither is exercised anywhere else.
    public void VerifyRenderScales(string folder)
    {
        const int width = 400, height = 200;
        var document = new HexDocument(width, height);
        var layer = document.Layers[0];
        // Four quadrants of flat colour, so where each one lands says whether the geometry is right.
        var quadrants = new uint[] { 0xFF1E89DA, 0xFF2FC866, 0xFFE4D114, 0xFFD5236C };
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
                layer.Cells[row * width + column] = quadrants[(row < height / 2 ? 0 : 2) + (column < width / 2 ? 0 : 1)];

        using var cache = new ArtworkCache();
        var checks = new List<string>();
        const int side = 600;
        foreach (var cellPixels in new[] { 1.5f, 5f, 10f, 20f })
        {
            var zoom = cellPixels / (float)(Math.Sqrt(3) * CanvasGeometry.CellRadius);
            // A point a quarter and three quarters of the way along each axis sits well inside one quadrant,
            // a hundred cells from any boundary. The view is placed to put that cell at the middle of the
            // frame, so this works at any zoom, including the ones where the canvas is far larger than the
            // window, which is the case that matters.
            for (var quadrant = 0; quadrant < 4; quadrant++)
            {
                var column = quadrant % 2 == 0 ? width / 4 : width * 3 / 4;
                var row = quadrant < 2 ? height / 4 : height * 3 / 4;
                var (x, y) = HexGeometry.Center(HexGeometry.FromOffset(column, row, document.Orientation), document.Orientation, CanvasGeometry.CellRadius);
                using var bitmap = RenderFrame(document, cache, side, side, zoom,
                    side / 2f - (float)x * zoom, side / 2f - (float)y * zoom);
                var sample = (uint)bitmap.GetPixel(side / 2, side / 2);
                var expected = quadrants[quadrant];
                // One sample a pixel, and the colour is fetched from a texel rather than computed, so the
                // pixel holds one cell's colour exactly instead of a blend of several. Nothing here needs a
                // tolerance, and not having one is what makes a half-cell error a failure.
                if (sample != expected)
                    throw new InvalidOperationException(
                        $"Drawing at {cellPixels} pixels a cell put {ColorMath.Format(sample)} at cell {column},{row} where {ColorMath.Format(expected)} belongs.");
            }
            checks.Add($"{cellPixels:0.#} px");
        }
        File.AppendAllText(System.IO.Path.Combine(folder, "Verification.txt"),
            $"Render scales: {string.Join(", ", checks)} a cell verified on a {width} x {height} canvas\n");
    }

    public void VerifyInterface(string folder)
    {
        var submenuPath = VerifyMenu();
        VerifyDialogs();
        File.AppendAllText(System.IO.Path.Combine(folder, "Verification.txt"), $"Menu bar and dialogs: passed, submenu opened {submenuPath}\n");
    }

    // Returns how the submenu was opened. A WPF menu captures the mouse so it can tell when to close itself,
    // and only a window on the input desktop can hold capture, so in a locked session the menu item refuses
    // to keep its submenu open. Opening the popup directly asks for no capture, which still builds, lays out
    // and measures the submenu. Which path was taken belongs in the report rather than being swallowed: the
    // fallback is expected while the session is locked and a defect while it is not, and only the caller
    // knows which of those it is looking at.
    private string VerifyMenu()
    {
        FileMenu.ApplyTemplate();
        // A menu item refuses to open its submenu before it has been loaded, so let pending Loaded events run first.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        if (FileMenu.Template.FindName("Submenu", FileMenu) is not Popup popup)
            throw new InvalidOperationException("The File menu has no submenu to open.");
        FileMenu.IsSubmenuOpen = true;
        UpdateLayout();
        var submenuPath = "through the menu item";
        var staysOpen = popup.StaysOpen;
        if (!popup.IsOpen)
        {
            popup.StaysOpen = true;
            popup.IsOpen = true;
            UpdateLayout();
            submenuPath = "directly, with no mouse capture";
        }
        if (!popup.IsOpen || popup.Child is not FrameworkElement submenu)
            throw new InvalidOperationException("The File menu did not open.");
        submenu.UpdateLayout();
        if (submenu.ActualWidth < 120 || submenu.ActualHeight < 120) throw new InvalidOperationException("The File menu rendered empty.");
        popup.IsOpen = false;
        // Restored only once the popup is closed, because a popup that is open when StaysOpen goes back to
        // false immediately reaches for the capture it was opened without.
        popup.StaysOpen = staysOpen;
        FileMenu.IsSubmenuOpen = false;
        return submenuPath;
    }

    // The dialogs build their visual tree in code from the application's styles, so measuring one proves those styles apply.
    private static void VerifyDialogs()
    {
        var dialogs = new (string Name, FrameworkElement Content)[]
        {
            ("Shortcuts", EditorDialogs.HelpContent()),
            ("New canvas", (FrameworkElement)new NewCanvasDialog().Content)
        };
        foreach (var (name, content) in dialogs)
        {
            content.Measure(new Size(560, 1000));
            if (content.DesiredSize.Width < 200 || content.DesiredSize.Height < 200) throw new InvalidOperationException($"The {name} dialog did not lay out.");
        }

        // Every box has to hold the longest value its own label offers. These drifted apart once:
        // the label read "Columns (1-2048)" while the box stopped accepting input after three
        // characters, so the largest canvas the editor supports could not be typed in. Nothing
        // about that is visible until someone tries it, which is why it is checked here.
        var digits = HexDocument.MaximumDimension.ToString().Length;
        foreach (var box in ((Panel)dialogs[1].Content).Children.OfType<TextBox>())
            if (box.MaxLength > 0 && box.MaxLength < digits)
                throw new InvalidOperationException(
                    $"A New canvas field accepts {box.MaxLength} characters, too few for {HexDocument.MaximumDimension}.");
    }

    public void VerifyCanvasRendered()
    {
        if (EditorCanvas.CanvasSize.Width <= 0 || EditorCanvas.CanvasSize.Height <= 0) throw new InvalidOperationException("The editor canvas did not render.");
    }
}
