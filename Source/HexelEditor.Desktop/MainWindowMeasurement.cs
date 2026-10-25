using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using HexelEditor.Core;
using SkiaSharp;

namespace HexelEditor.Desktop;

// The editor measuring itself, driven by Measure.ps1 through App's measurement mode.
//
// Why it lives here rather than in a benchmark project of its own: the figures worth having are the ones the
// real control, the real session and the real renderer produce. A separate project could reach the document
// and the renderer, but not the canvas or the panels, which need this window, its resources and a
// presentation source. So this measures the objects the editor actually uses, in the same process, and the
// only thing about it that is not real is the host the window sits in -- which is what verification already
// does for the same reason.
//
// Everything here runs on a desktop of its own and shows nothing. See Measure.ps1.
public partial class MainWindow
{
    private sealed record Timing(string Group, string Name, string Detail, double Median, double Minimum, double Maximum, int Runs);

    private readonly List<Timing> timings = [];

    // Runs an action enough times to have a middle value, after one unrecorded run to settle allocation and
    // the just-in-time compiler. The median is reported rather than the mean, because one stall from the
    // garbage collector or from the machine's other work should not be what a figure describes. The warmup
    // is skipped for the operations measured in seconds, where it buys little and costs a lot.
    private void Time(string group, string name, string detail, int runs, Action action, Action? before = null, bool warmup = true)
    {
        if (warmup) { before?.Invoke(); action(); }
        var samples = new double[runs];
        for (var run = 0; run < runs; run++)
        {
            before?.Invoke();
            var started = Stopwatch.GetTimestamp();
            action();
            samples[run] = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        }
        Array.Sort(samples);
        timings.Add(new(group, name, detail, samples[runs / 2], samples[0], samples[^1], runs));
        App.Log.Write($"Measured {group} / {name} ({detail}): {samples[runs / 2]:0.000} ms");
    }

    private static double Middle(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private void Add(string group, string name, string detail, IReadOnlyList<double> samples) =>
        timings.Add(new(group, name, detail, Middle(samples), samples.Min(), samples.Max(), samples.Count));

    private static HexDocument LargeDocument(int layers = 3)
    {
        // The canvas the editor is built to handle, filled with real colour rather than left empty: an empty
        // cell takes the cheap path through ColorMath.Over, so measuring on an empty one would flatter every
        // figure that touches compositing.
        var document = new HexDocument(2000, 1000) { Name = "Measurement" };
        for (var layer = 0; layer < layers; layer++)
        {
            if (layer > 0) document.Layers.Add(new HexLayer(document.CellCount) { Name = $"Layer {layer + 1}" });
            var cells = document.Layers[layer].Cells;
            for (var index = 0; index < cells.Length; index++)
                cells[index] = HexDocument.DefaultPalette[(index + layer * 7) % HexDocument.DefaultPalette.Length];
        }
        return document;
    }

    private static float ZoomForCellPixels(float cellPixels) => cellPixels / (float)(Math.Sqrt(3) * CanvasGeometry.CellRadius);

    /// <summary>
    /// Measures the editor and writes its report into the folder. The root and its size come from the host
    /// the window was put in, because in this mode the window itself was never shown and has neither.
    /// </summary>
    public void MeasureEditor(string folder)
    {
        var startup = DateTime.Now - Process.GetCurrentProcess().StartTime;
        App.Log.Write($"Measurement started. Process start to here: {startup.TotalMilliseconds:0} ms.");
        // Each section is run on its own account. A measurement run is long, and a section that throws
        // partway through should cost its own figures rather than every section that came before it. The run
        // still fails at the end, because a measurement nobody can see failed is worse than no measurement.
        var failures = new List<string>();
        // A suite this long is awkward to iterate on, so one section can be asked for by name.
        var wanted = (Environment.GetEnvironmentVariable("HEXEL_MEASURE_SECTIONS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted.Length > 0) App.Log.Write($"Measuring only: {string.Join(", ", wanted)}.");
        Section("Renderer frames", MeasureRendererFrames);
        Section("Compositing", MeasureCompositing);
        Section("Canvas paints", MeasureCanvasPaints);
        Section("Interactions", MeasureInteractions);
        Section("Interface", MeasureInterface);
        Section("Editing", MeasureEditing);
        Section("Documents", () => MeasureDocuments(folder));
        Report(folder, startup, failures);
        App.Log.Write("Measurement finished.");
        if (failures.Count > 0)
            throw new InvalidOperationException("Measurement sections failed: " + string.Join("; ", failures));

        void Section(string name, Action section)
        {
            if (wanted.Length > 0 && !wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
            try { section(); }
            catch (Exception error)
            {
                failures.Add($"{name}: {error.Message}");
                App.Log.Write($"Measurement section '{name}' failed: {error}");
            }
        }
    }

    // The renderer's frame, split into its parts. This is the only section that turns on the GPU sync inside
    // the frame, because it is the only one asking what the GPU's own share of it is.
    private void MeasureRendererFrames()
    {
        var renderer = Renderer();
        var example = ExampleDocument.Create();
        var large = LargeDocument();
        GpuHexRenderer.MeasureFrames = true;
        try
        {
            // 2456 x 1270 is the frame the earlier work measured, kept so these figures can be set beside
            // it. 3840 x 2400 is the validation display, which is the size the editor really draws here.
            // 1.7 pixels a cell is what the minimum zoom allows on a 2000 x 1000 canvas, so it is the most
            // cells that can be on screen at once.
            Frame("Example canvas", example, 2456, 1270, 12f, dirty: false);
            Frame("Large canvas zoomed out", large, 2456, 1270, 1.7f, dirty: false);
            Frame("Large canvas zoomed in", large, 2456, 1270, 20f, dirty: false);
            Frame("Large canvas on the 4K display", large, 3840, 2400, 1.7f, dirty: false);
            Frame("Large canvas, one cell changed a frame", large, 2456, 1270, 1.7f, dirty: true);
            Frame("Large canvas on 4K, one cell changed a frame", large, 3840, 2400, 1.7f, dirty: true);
            Frame("Example canvas, one cell changed a frame", example, 2456, 1270, 12f, dirty: true);
        }
        finally { GpuHexRenderer.MeasureFrames = false; }

        void Frame(string name, HexDocument document, int width, int height, float cellPixels, bool dirty)
        {
            using var cache = new ArtworkCache();
            // A GPU surface of the frame's size stands in for the window's framebuffer: the frame is drawn
            // into it and left there, as the window's is, with nothing read back.
            using var surface = renderer.CreateSurface(width, height);
            var zoom = ZoomForCellPixels(cellPixels);
            const int runs = 20;
            List<double> composite = [], upload = [], submit = [], gpu = [], whole = [];
            var cells = document.Layers[0].Cells;
            for (var run = -1; run < runs; run++)
            {
                if (dirty)
                {
                    // One cell, the way a brush dab dirties one. The smallest change there is, so what the
                    // frame costs with it is what having changed anything at all costs.
                    var index = (run + 1) % document.CellCount;
                    cells[index] = HexDocument.DefaultPalette[(run + 2) % HexDocument.DefaultPalette.Length];
                    cache.MarkDirty((index % document.Width, index / document.Width, index % document.Width, index / document.Width));
                }
                renderer.Draw(surface.Canvas, width, height, document, cache, zoom, width / 2f, height / 2f, grid: true);
                if (run < 0) continue;
                var frame = renderer.LastFrameTimings;
                composite.Add(frame.Composite); upload.Add(frame.Upload); submit.Add(frame.Submit);
                gpu.Add(frame.Gpu); whole.Add(frame.Total);
            }
            var group = $"Frame: {name}";
            var detail = $"{document.Width}x{document.Height} cells, {width}x{height} px, {cellPixels:0.#} px a cell";
            Add(group, "Composite changed cells", detail, composite);
            Add(group, "Upload colour texture", detail, upload);
            Add(group, "Record the draw", detail, submit);
            Add(group, "GPU runs the shader", detail, gpu);
            Add(group, "Whole frame", detail, whole);
            App.Log.Write($"Frame {name}: {detail}, {Middle(whole):0.000} ms");
        }
    }

    // What it costs to keep the colour raster current, which is the work a brush stroke actually adds.
    private void MeasureCompositing()
    {
        foreach (var layers in new[] { 1, 3 })
        {
            var document = LargeDocument(layers);
            using var cache = new ArtworkCache();
            cache.Refresh(document);
            var detail = $"2000x1000, {layers} layer{(layers == 1 ? "" : "s")}";
            Time("Compositing", "Whole canvas", detail, 5,
                () => cache.Refresh(document), () => cache.InvalidateEverything());
            Time("Compositing", "One cell", detail, 50,
                () => cache.Refresh(document), () => cache.MarkDirty((100, 100, 100, 100)));
            Time("Compositing", "A 17 cell wide dab", detail, 50,
                () => cache.Refresh(document), () => cache.MarkDirty((100, 100, 116, 116)));
        }
        var composite = LargeDocument();
        Time("Compositing", "CompositeAt over every cell", "2000x1000, 3 layers, no raster involved", 3, () =>
        {
            uint total = 0;
            for (var index = 0; index < composite.CellCount; index++) total += composite.CompositeAt(index);
            GC.KeepAlive(total);
        });
    }

    private static GpuHexRenderer Renderer() => GpuHexRenderer.Shared
        ?? throw new InvalidOperationException("There is no GPU renderer: " + GpuHexRenderer.Unavailable);

    // The real control painting, in the real window, with the real renderer behind it. The parts come from
    // HexCanvas itself; what this does is set up the cases and force the frames. The frames go into a GPU
    // surface of the renderer's, which is what the window's own framebuffer is to the paint.
    private void MeasureCanvasPaints()
    {
        // Shared by every case, so no two cases paint the same cell the same colour. One that did would
        // change nothing, and would measure a frame with no edit in it while claiming otherwise.
        var dabbed = 0;
        HexCanvas.MeasurePaints = true;
        try
        {
            Case("Example canvas", ExampleDocument.Create(), fit: true, selectAll: false, cellPixels: null, dab: false);
            var large = LargeDocument();
            Case("Large canvas zoomed out", large, fit: true, selectAll: false, cellPixels: null, dab: false);
            Case("Large canvas zoomed in", large, fit: false, selectAll: false, cellPixels: 20f, dab: false);
            Case("Large canvas, everything selected", large, fit: true, selectAll: true, cellPixels: null, dab: false);
            Case("Large canvas, painting", large, fit: true, selectAll: false, cellPixels: null, dab: true);
            // The validation display is 3840 x 2400, and WPF scales it, so the canvas holds far more device
            // pixels than it does of its own units. Everything per-pixel follows the larger number.
            Case("Large canvas on a 4K display", large, fit: true, selectAll: false, cellPixels: null, dab: false, deviceScale: 1.82);
            Case("Large canvas on 4K, painting", large, fit: true, selectAll: false, cellPixels: null, dab: true, deviceScale: 1.82);
        }
        finally { HexCanvas.MeasurePaints = false; }

        void Case(string name, HexDocument document, bool fit, bool selectAll, float? cellPixels, bool dab, double deviceScale = 1)
        {
            var measured = new EditorSession(document);
            AttachSession(measured);
            if (fit) EditorCanvas.Fit();
            if (cellPixels.HasValue) EditorCanvas.ChangeZoom(ZoomForCellPixels(cellPixels.Value) / EditorCanvas.Zoom);
            if (selectAll) measured.SelectAll();
            var info = new SKImageInfo(
                (int)Math.Round(EditorCanvas.ActualWidth * deviceScale),
                (int)Math.Round(EditorCanvas.ActualHeight * deviceScale),
                SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = Renderer().CreateSurface(info.Width, info.Height);
            const int runs = 12;
            List<double> artwork = [], overlays = [], gpu = [], whole = [];
            for (var run = -1; run < runs; run++)
            {
                // A dab before the frame, so the frame is one a stroke would really ask for: cells changed
                // since the last one, which is what makes the raster stale. A cell and a colour of its own
                // each time, counted across every case, because painting a cell the colour it already holds
                // changes nothing and would measure a frame with no edit in it.
                if (dab) measured.Paint(HexGeometry.FromOffset(dabbed % document.Width, 10, document.Orientation),
                    0xFF000000u | (uint)(0x2FC866 + dabbed++ * 0x131));
                EditorCanvas.PaintOneFrame(surface, info.Width, info.Height);
                if (run < 0) continue;
                var paint = EditorCanvas.LastPaintTimings;
                artwork.Add(paint.Artwork); overlays.Add(paint.Overlays); gpu.Add(paint.Gpu); whole.Add(paint.Total);
            }
            var group = $"Paint: {name}";
            var detail = $"{document.Width}x{document.Height} cells, {info.Width}x{info.Height} px, "
                + $"{EditorCanvas.Zoom * deviceScale * Math.Sqrt(3) * CanvasGeometry.CellRadius:0.#} px a cell, "
                + $"{measured.Selection.Count:N0} selected, overlays walked {EditorCanvas.LastOverlayCells:N0} cells "
                + $"in a clip of {EditorCanvas.LastVisibleBounds.Width:0}x{EditorCanvas.LastVisibleBounds.Height:0}";
            Add(group, "Artwork, recorded and handed over", detail, artwork);
            Add(group, "Selection, previews and cursor", detail, overlays);
            Add(group, "GPU finishes the frame", detail, gpu);
            Add(group, "Whole paint", detail, whole);
        }
    }

    // The panels, which are rebuilt rather than updated. Nothing here touches the canvas.
    private void MeasureInterface()
    {
        AttachSession(new EditorSession(ExampleDocument.Create()));
        Time("Interface", "Rebuild the palette", $"{session.Document.Palette.Count} swatches", 10, RefreshPalette);
        Time("Interface", "Rebuild the layer list", $"{session.Document.Layers.Count} layers", 10, RefreshLayers);
        Time("Interface", "Rebuild every panel", "palette, layers and background", 10, RefreshPanels);
        Time("Interface", "Choose a colour", "sets the colour and rebuilds the palette", 10, () => SetColor(0xFF2FC866));
        Time("Interface", "Update the header", "titles, menu items and status", 20, UpdateHeader);
        var many = new HexDocument(40, 32);
        for (var layer = 1; layer < 12; layer++) many.Layers.Add(new HexLayer(many.CellCount) { Name = $"Layer {layer + 1}" });
        AttachSession(new EditorSession(many));
        Time("Interface", "Rebuild the layer list", "12 layers", 10, RefreshLayers);
        Time("Interface", "Build a second window", "every control, panel and icon", 3, () => GC.KeepAlive(new MainWindow()));
    }

    // The editing operations, on the canvas size the editor is built for. These are what a keystroke or a
    // click actually runs, and none of them is on a background thread.
    private void MeasureEditing()
    {
        var document = LargeDocument();
        var editing = new EditorSession(document);
        const string large = "2000x1000, 3 layers";

        Time("Editing", "Paint one cell", large, 50, () =>
        {
            editing.Paint(HexGeometry.FromOffset(Random.Shared.Next(2000), Random.Shared.Next(1000), document.Orientation), 0xFF2FC866);
            editing.EndStroke();
        });
        Time("Editing", "Paint a 17 cell wide dab", large, 50, () =>
        {
            editing.Paint(HexGeometry.FromOffset(Random.Shared.Next(2000), Random.Shared.Next(1000), document.Orientation), 0xFF2FC866, 8);
            editing.EndStroke();
        });
        Time("Editing", "Select everything", large, 3, editing.SelectAll, editing.ClearSelection);
        Time("Editing", "Clear the selection", large, 3, editing.ClearSelection, editing.SelectAll);
        Time("Editing", "Drag a selection over the whole canvas", large, 3,
            () => editing.SelectRectangle(document.CoordinateAt(0), document.CoordinateAt(document.CellCount - 1)),
            editing.ClearSelection);
        Time("Editing", "Copy everything", large, 2, () => GC.KeepAlive(editing.CopySelection()), editing.SelectAll, warmup: false);
        Time("Editing", "Clear every cell", large, 2, editing.ClearPixels,
            () => { editing.ClearSelection(); Fill(0xFF1E89DA); }, warmup: false);
        Time("Editing", "Move the whole selection", large, 2, () => editing.MoveSelection(1, 0), editing.SelectAll, warmup: false);
        Time("Editing", "Rotate the whole selection", large, 2, () => editing.RotateSelection(1), editing.SelectAll, warmup: false);
        Time("Editing", "Flood fill the whole canvas", large, 2,
            () => editing.FloodFill(document.CoordinateAt(0), 0xFF1E89DA),
            () => { editing.ClearSelection(); Fill(0xFFE4D114); }, warmup: false);
        Time("Editing", "Undo a whole-canvas change", large, 2, editing.Undo,
            () => { editing.ClearSelection(); Fill(0xFF2FC866); }, warmup: false);
        Time("Editing", "Hide a layer", $"{large}, a structural change", 5,
            () => editing.SetLayerVisible(0, !document.Layers[0].Visible));
        Time("Editing", "Add a layer", large, 3, editing.AddLayer, () =>
        {
            while (document.Layers.Count > 3) document.Layers.RemoveAt(3);
            // The session decides where a new layer goes from the active one, so the layer list and the
            // session have to be put back together.
            editing.SelectLayer(0);
        });

        Time("Editing", "Create the document", "2000x1000, one layer", 5, () => GC.KeepAlive(new HexDocument(2000, 1000)));
        var cloning = LargeDocument();
        Time("Editing", "Copy the document with its cells", large, 3, () => GC.KeepAlive(cloning.Clone()));
        Time("Editing", "Copy the document sharing its cells", large, 20, () => GC.KeepAlive(cloning.CloneStructure()));
        Time("Editing", "Build the example canvas", "40x32, 3 layers", 10, () => GC.KeepAlive(ExampleDocument.Create()));

        // Puts a colour into every cell of the active layer through the session, so that the operation being
        // set up for has a whole canvas of real work to do and one undo entry to undo.
        void Fill(uint color)
        {
            editing.BeginStroke();
            for (var index = 0; index < document.CellCount; index++) editing.Paint(document.CoordinateAt(index), color);
            editing.EndStroke("Fill");
        }
    }

    private void MeasureDocuments(string folder)
    {
        var document = LargeDocument();
        var path = Path.Combine(folder, "Measurement.hexel");
        // One run each, and no warmup: these touch the disk, and a figure taken from a file the file system
        // has just finished writing is not the figure a person waiting for a save would see.
        Time("Documents", "Save", "2000x1000, 3 layers", 1, () => ProjectFile.Save(document, path), warmup: false);
        Time("Documents", "Load", "2000x1000, 3 layers", 1, () => GC.KeepAlive(ProjectFile.Load(path)), warmup: false);
        timings.Add(new("Documents", "Saved file size", $"{new FileInfo(path).Length / 1024.0 / 1024:0.0} MB for 2000x1000, 3 layers", 0, 0, 0, 0));
        var small = Path.Combine(folder, "Example.hexel");
        var example = ExampleDocument.Create();
        Time("Documents", "Save", "40x32, 3 layers", 3, () => ProjectFile.Save(example, small));
        Time("Documents", "Load", "40x32, 3 layers", 3, () => GC.KeepAlive(ProjectFile.Load(small)));
    }

    // What a thing the editor does actually costs, from the click to the frame that shows it. The parts are
    // reported separately because they are fixed in different places: the operation is the session's work,
    // the panels are the interface rebuilding itself, and the paint is the renderer.
    //
    // The frame is painted into a GPU surface of the renderer's through the canvas's own paint, and what is
    // reported is the paint's own figure, out of HexCanvas, with the GPU's share waited for at the end.
    private void MeasureInteractions()
    {
        var document = LargeDocument();
        var interacting = new EditorSession(document);
        AttachSession(interacting);
        EditorCanvas.Fit();
        int width = (int)EditorCanvas.ActualWidth, height = (int)EditorCanvas.ActualHeight;
        using var surface = Renderer().CreateSurface(width, height);
        HexCanvas.MeasurePaints = true;
        try
        {
            Interaction("Paint one cell", 10, () =>
            {
                interacting.Paint(HexGeometry.FromOffset(Random.Shared.Next(2000), Random.Shared.Next(1000), document.Orientation), 0xFF2FC866);
                interacting.EndStroke();
            });
            // What one mouse move of a drag really asks for: the cells between the last position and this
            // one, each painted in turn. Every one of them tells the window something changed, so this is
            // where that chain is paid for, if it costs anything.
            var stroke = 0;
            Interaction("Drag the brush over 60 cells", 10, () =>
            {
                var row = 20 + stroke++ % 500;
                foreach (var cell in HexGeometry.FromOffset(10, row, document.Orientation)
                    .LineTo(HexGeometry.FromOffset(70, row, document.Orientation)))
                    interacting.Paint(cell, 0xFFD5236C);
                interacting.EndStroke();
            });
            Interaction("Pan the canvas", 10, () => EditorCanvas.ChangeZoom(1f));
            Interaction("Hide or show a layer", 6, () => interacting.SetLayerVisible(0, !document.Layers[0].Visible));
            // A flood fill rather than a painted fill: both change every cell and leave one undo entry, but
            // the fill goes through the session one cell at a time, and on a session the window is listening
            // to that is two million notifications nothing in the editor would ever send.
            var shade = 0u;
            Interaction("Undo a whole-canvas change", 3, interacting.Undo, () =>
            {
                interacting.FloodFill(document.CoordinateAt(0), 0xFF000000 | (shade += 0x010305));
                Frame();
            });
        }
        finally { HexCanvas.MeasurePaints = false; }

        void Frame() => EditorCanvas.PaintOneFrame(surface, width, height);

        void Interaction(string name, int runs, Action act, Action? before = null)
        {
            before?.Invoke(); act(); Frame();
            List<double> operation = [], paint = [], together = [];
            for (var run = 0; run < runs; run++)
            {
                before?.Invoke();
                var started = Stopwatch.GetTimestamp();
                act();
                var acted = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                Frame();
                var painted = EditorCanvas.LastPaintTimings.Total;
                operation.Add(acted); paint.Add(painted); together.Add(acted + painted);
            }
            var detail = $"2000x1000, 3 layers, {width}x{height} px, zoomed to fit";
            Add($"Interaction: {name}", "The operation itself", detail, operation);
            Add($"Interaction: {name}", "The frame that follows", detail, paint);
            Add($"Interaction: {name}", "Both, which is what is waited for", detail, together);
        }
    }

    private void Report(string folder, TimeSpan startup, List<string> failures)
    {
        var text = new StringBuilder();
        text.AppendLine($"Hexel Editor measurement, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Process start to measurement: {startup.TotalMilliseconds:0} ms");
        foreach (var failure in failures) text.AppendLine($"FAILED SECTION: {failure}");
        text.AppendLine();
        var nameWidth = Math.Max(timings.Max(timing => timing.Name.Length), 4);
        foreach (var group in timings.GroupBy(timing => timing.Group))
        {
            text.AppendLine(group.Key);
            text.AppendLine($"  {"What".PadRight(nameWidth)}  {"Median",10}  {"Lowest",10}  {"Highest",10}  {"Runs",5}  Detail");
            foreach (var timing in group)
                text.AppendLine($"  {timing.Name.PadRight(nameWidth)}  {timing.Median,10:0.000}  {timing.Minimum,10:0.000}  {timing.Maximum,10:0.000}  {timing.Runs,5}  {timing.Detail}");
            text.AppendLine();
        }
        File.WriteAllText(Path.Combine(folder, "Measurement.txt"), text.ToString());
        File.WriteAllText(Path.Combine(folder, "Measurement.json"), JsonSerializer.Serialize(
            new { StartupMilliseconds = startup.TotalMilliseconds, Failures = failures, Timings = timings },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
