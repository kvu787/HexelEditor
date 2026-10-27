using System.Text.Json;
using Experiment.Core;
using Godot;

namespace GodotAgentDevExperiment;

/// <summary>Drives the real interface from inside the process and writes what happened. Each check reports
/// passed, failed or skipped, and the launcher requires every one of them to have reported, so a run that
/// dies halfway cannot pass by silence. Nothing here depends on a display: the same checks run headless,
/// on a desktop of their own, and on the validation display, and the report says which it was.</summary>
public static class Checks
{
    private sealed class Report
    {
        public List<string> Lines { get; } = [];
        public bool Failed { get; private set; }

        public void Add(string name, string outcome, string detail)
        {
            Lines.Add($"{name}: {outcome} - {detail}");
            if (outcome == "failed")
            {
                Failed = true;
            }
        }
    }

    public static async Task Run(Main main)
    {
        Report report = new();
        string folder = main.Log.Folder;
        bool headless = DisplayServer.GetName() == "headless";
        try
        {
            // The engine merges consecutive mouse motion events within one frame into the last of them, which
            // is right for a mouse and wrong for a stroke driven in one go, so it is turned off for the run.
            Input.UseAccumulatedInput = false;
            await Frames(main, 3);
            // A launcher that shows the window where nobody can see it first shows it tiny, and gives it its
            // real size a moment later. The interface is laid out for that size, so the checks wait for it.
            ulong sizeDeadline = Time.GetTicksMsec() + 15000;
            while (main.GetWindow().Size.X < 640 && Time.GetTicksMsec() < sizeDeadline)
            {
                await Frames(main, 1);
            }
            if (main.GetWindow().Size.X < 640)
            {
                report.Add("Environment", "failed", $"the window is still {main.GetWindow().Size} after 15 s; the launcher never gave it its size");
                throw new MeasurementOnly();
            }
            await Frames(main, 2);
            report.Add("Environment", "passed",
                $"display server {DisplayServer.GetName()}; {RenderingServer.GetCurrentRenderingMethod()} on {RenderingServer.GetCurrentRenderingDriverName()}; adapter '{RenderingServer.GetVideoAdapterName()}'; " +
                $"window {DisplayServer.WindowGetSize()} at {DisplayServer.WindowGetPosition()} on screen {DisplayServer.WindowGetCurrentScreen()} of {DisplayServer.GetScreenCount()}; grid {main.Ground.Columns} x {main.Ground.Rows}");

            if (main.Arguments.Measure)
            {
                await Measure(main, report, headless, Path.Combine(folder, "Measurement.json"));
                throw new MeasurementOnly();
            }
            await CheckToolButton(main, report);
            await CheckMousePositionSources(main, report);
            await CheckClickPaints(main, report, headless);
            await CheckDragStroke(main, report);
            await CheckKeys(main, report);
            await CheckMenuAndDialog(main, report);
            await CheckMenuEscape(main, report);
            await CheckFileDialog(main, report);
            CheckPiece(main, report);
            await CheckTopDown(main, report);
            await CheckCapture(main, report, headless, Path.Combine(folder, "Viewport.png"));
            await Measure(main, report, headless, Path.Combine(folder, "Measurement.json"));
        }
        catch (MeasurementOnly)
        {
        }
        catch (Exception exception)
        {
            report.Add("Checks", "failed", exception.ToString());
        }

        foreach (string line in report.Lines)
        {
            main.Log.Write(line);
        }
        File.WriteAllLines(Path.Combine(folder, "Checks.txt"), report.Lines);
        main.Log.Write(report.Failed ? "Checks failed." : "Checks passed.");
        if (main.Arguments.HoldSeconds > 0.0)
        {
            main.Log.Write($"Holding the window for {main.Arguments.HoldSeconds} s.");
            ulong until = Time.GetTicksMsec() + (ulong)(main.Arguments.HoldSeconds * 1000.0);
            while (Time.GetTicksMsec() < until)
            {
                await Frames(main, 1);
            }
        }
        main.Quit(report.Failed ? 1 : 0);
    }

    private sealed class MeasurementOnly : Exception;

    private static async Task Frames(Main main, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static Vector2 Center(Control control)
    {
        return control.GetGlobalRect().GetCenter();
    }

    /// <summary>Colors compared to within a byte, since one of them has been through an 8-bit image, which
    /// truncates each channel rather than rounding it.</summary>
    private static bool SameColor(Color a, Color b)
    {
        const float tolerance = 1.5f / 255.0f;
        return Mathf.Abs(a.R - b.R) <= tolerance && Mathf.Abs(a.G - b.G) <= tolerance && Mathf.Abs(a.B - b.B) <= tolerance;
    }

    /// <summary>A point over the ground and away from every control.</summary>
    private static Vector2 OverGround(Main main, float x = 0.55f, float y = 0.6f)
    {
        Vector2 size = main.GetViewport().GetVisibleRect().Size;
        return new Vector2(size.X * x, size.Y * y);
    }

    private static async Task CheckToolButton(Main main, Report report)
    {
        int before = main.ToolPresses;
        DrivenInput.Click(Center(main.EraseButton));
        await Frames(main, 1);
        bool pressed = main.ToolPresses == before + 1 && main.Tool == "erase";
        report.Add("Button click", pressed ? "passed" : "failed", $"driven click at {Center(main.EraseButton)} selected '{main.Tool}' ({main.ToolPresses - before} press)");
        DrivenInput.Click(Center(main.PaintButton));
        await Frames(main, 1);
    }

    private static async Task CheckMousePositionSources(Main main, Report report)
    {
        Vector2 driven = OverGround(main);
        DrivenInput.Move(driven);
        await Frames(main, 1);
        Vector2 fromViewport = main.GetViewport().GetMousePosition();
        Vector2 realCursor = DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition();
        bool viewportMatches = fromViewport.DistanceTo(driven) < 0.5f;
        bool hoverMatches = main.HoverCell is HexCell hover && HexGround.Pick(main.Camera, driven) == hover;
        // The event's own position is what the application acts on, and the hover cell proves it arrived.
        // Viewport.GetMousePosition asks the display server, which on Windows asks the operating system for
        // the real cursor. Code that reads it sees the user's pointer rather than the driven one, so an
        // application that is to be driven this way must take positions from events alone.
        report.Add("Mouse position sources", hoverMatches ? "passed" : "failed",
            $"driven {driven} reached the application (hover cell {main.HoverCell}); Viewport.GetMousePosition {fromViewport} ({(viewportMatches ? "matches" : "reads the real cursor instead")}); the real cursor is at {realCursor} in window coordinates");
    }

    private static async Task CheckClickPaints(Main main, Report report, bool headless)
    {
        Vector2 at = OverGround(main);
        HexCell? expected = HexGround.Pick(main.Camera, at);
        int before = main.PaintedCells;
        DrivenInput.Click(at);
        await Frames(main, 1);
        bool painted = expected is HexCell cell && main.PaintedCells == before + 1 && main.LastPaintedCell == cell && SameColor(main.Ground.CellColor(cell), Main.PaintColor);
        report.Add("Click paints the cell under the pointer", painted ? "passed" : "failed",
            $"click at {at} picked {expected}, painted {main.LastPaintedCell}, upload {main.LastUploadMilliseconds:0.000} ms");

        if (headless)
        {
            report.Add("Painted cell is drawn", "skipped", "no renderer in headless mode");
            return;
        }
        await main.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = main.GetViewport().GetTexture().GetImage();
        if (image.IsEmpty())
        {
            report.Add("Painted cell is drawn", "failed", "the viewport texture came back empty");
            return;
        }
        Color pixel = image.GetPixel((int)at.X, (int)at.Y);
        // The frame is read back as the sRGB bytes that were presented, so the painted color should come
        // back within a byte or two of what was painted.
        float difference = Mathf.Max(Mathf.Abs(pixel.R - Main.PaintColor.R), Mathf.Max(Mathf.Abs(pixel.G - Main.PaintColor.G), Mathf.Abs(pixel.B - Main.PaintColor.B)));
        report.Add("Painted cell is drawn", difference <= 2.5f / 255.0f ? "passed" : "failed", $"viewport pixel at {at} is {pixel}, painted {Main.PaintColor}, largest channel difference {difference * 255.0f:0.0} of 255, in a {image.GetWidth()} x {image.GetHeight()} frame");
    }

    private static async Task CheckDragStroke(Main main, Report report)
    {
        Vector2 from = OverGround(main, 0.45f, 0.5f);
        Vector2 to = OverGround(main, 0.75f, 0.52f);
        int before = main.PaintedCells;
        DrivenInput.Press(from);
        for (int step = 1; step <= 12; step++)
        {
            DrivenInput.Move(from.Lerp(to, step / 12.0f));
        }
        DrivenInput.Release(to);
        await Frames(main, 1);
        int painted = main.PaintedCells - before;
        report.Add("Drag paints a stroke", painted >= 3 ? "passed" : "failed", $"{painted} cells painted between {from} and {to}");
    }

    private static async Task CheckKeys(Main main, Report report)
    {
        HexCell? last = main.LastPaintedCell;
        int undos = main.UndoRequests;
        DrivenInput.Key(Key.Z, control: true);
        await Frames(main, 1);
        bool undone = main.UndoRequests == undos + 1 && last is HexCell cell && SameColor(main.Ground.CellColor(cell), Main.PaperColor);
        report.Add("Ctrl+Z undoes the last cell", undone ? "passed" : "failed", $"{main.UndoRequests - undos} undo, cell {last} is {(last is HexCell c ? main.Ground.CellColor(c).ToString() : "none")}");

        DrivenInput.Key(Key.E);
        await Frames(main, 1);
        bool toolKey = main.Tool == "erase";
        DrivenInput.Key(Key.B);
        await Frames(main, 1);
        report.Add("Tool shortcut keys", toolKey && main.Tool == "paint" ? "passed" : "failed", $"E selected '{(toolKey ? "erase" : main.Tool)}', then B selected '{main.Tool}'");
    }

    private static Vector2 FileMenuTitle(Main main)
    {
        Rect2 bar = main.MenuBar.GetGlobalRect();
        return new Vector2(bar.Position.X + 18.0f, bar.GetCenter().Y);
    }

    private static async Task CheckMenuAndDialog(Main main, Report report)
    {
        DrivenInput.Click(FileMenuTitle(main));
        await Frames(main, 2);
        bool opened = main.FileMenu.Visible;
        string how = $"menu {(opened ? "opened" : "did not open")} at {main.FileMenu.Position} size {main.FileMenu.Size}, embedded {main.FileMenu.IsEmbedded()}";
        if (!opened)
        {
            report.Add("Menu, item and dialog", "failed", how);
            return;
        }
        int requests = main.NewMapRequests;
        DrivenInput.Key(Key.Down);
        await Frames(main, 1);
        DrivenInput.Key(Key.Enter);
        await Frames(main, 2);
        bool dialogShown = main.NewMapRequests == requests + 1 && main.NewMapDialog.Visible;
        if (!dialogShown)
        {
            report.Add("Menu, item and dialog", "failed", how + $"; Down and Enter gave {main.NewMapRequests - requests} request and dialog visible {main.NewMapDialog.Visible}");
            return;
        }
        // Paint something first so that confirming the dialog has something to clear.
        Vector2 ok = Center(main.NewMapDialog.GetOkButton()) + main.NewMapDialog.Position;
        int confirmations = main.NewMapConfirmations;
        DrivenInput.Click(ok);
        await Frames(main, 2);
        bool confirmed = main.NewMapConfirmations == confirmations + 1 && !main.NewMapDialog.Visible;
        report.Add("Menu, item and dialog", confirmed ? "passed" : "failed",
            how + $"; dialog at {main.NewMapDialog.Position} size {main.NewMapDialog.Size}, embedded {main.NewMapDialog.IsEmbedded()}; OK clicked at {ok} gave {main.NewMapConfirmations - confirmations} confirmation, dialog visible {main.NewMapDialog.Visible}");
    }

    private static async Task CheckMenuEscape(Main main, Report report)
    {
        DrivenInput.Click(FileMenuTitle(main));
        await Frames(main, 2);
        bool opened = main.FileMenu.Visible;
        DrivenInput.Key(Key.Escape);
        await Frames(main, 2);
        report.Add("Escape closes the menu", opened && !main.FileMenu.Visible ? "passed" : "failed", $"opened {opened}, visible after Escape {main.FileMenu.Visible}");
    }

    private static async Task CheckFileDialog(Main main, Report report)
    {
        DrivenInput.Click(FileMenuTitle(main));
        await Frames(main, 2);
        DrivenInput.Key(Key.Down);
        await Frames(main, 1);
        DrivenInput.Key(Key.Down);
        await Frames(main, 1);
        DrivenInput.Key(Key.Enter);
        await Frames(main, 2);
        bool shown = main.OpenPieceRequests == 1 && main.OpenPieceDialog.Visible;
        string detail = $"dialog visible {main.OpenPieceDialog.Visible}, embedded {main.OpenPieceDialog.IsEmbedded()}, native {main.OpenPieceDialog.UseNativeDialog}, at {main.OpenPieceDialog.Position} size {main.OpenPieceDialog.Size}";
        if (shown)
        {
            Vector2 cancel = Center(main.OpenPieceDialog.GetCancelButton()) + main.OpenPieceDialog.Position;
            DrivenInput.Click(cancel);
            await Frames(main, 2);
            detail += $"; Cancel clicked at {cancel}, visible after {main.OpenPieceDialog.Visible}";
        }
        report.Add("File dialog opens embedded and closes", shown && !main.OpenPieceDialog.Visible ? "passed" : "failed", detail);
    }

    private static void CheckPiece(Main main, Report report)
    {
        if (main.Piece is null)
        {
            report.Add("glTF piece under SimplePaint", "failed", "the piece did not load");
            return;
        }
        bool painted = main.Piece.Surfaces > 0 && main.Piece.PaintedSurfaces == main.Piece.Surfaces;
        report.Add("glTF piece under SimplePaint", painted ? "passed" : "failed",
            $"{main.Piece.Surfaces} surfaces, {main.Piece.PaintedSurfaces} with parameters read from Blender's custom properties; " + string.Join("; ", main.Piece.Notes));
    }

    private static async Task CheckTopDown(Main main, Report report)
    {
        int toggles = main.PitchToggles;
        DrivenInput.Key(Key.T);
        await Frames(main, 1);
        Vector3 looking = -main.Camera.GlobalBasis.Z;
        bool down = main.PitchToggles == toggles + 1 && looking.Dot(Vector3.Down) > 0.999f;
        report.Add("T turns the camera top-down", down ? "passed" : "failed", $"{main.PitchToggles - toggles} toggle, camera looks along {looking}");
        DrivenInput.Key(Key.T);
        await Frames(main, 1);
    }

    private static async Task CheckCapture(Main main, Report report, bool headless, string path)
    {
        if (headless)
        {
            report.Add("Viewport capture", "skipped", "no renderer in headless mode");
            return;
        }
        // A few cells painted so the picture shows something besides the empty grid.
        Vector2 from = OverGround(main, 0.3f, 0.35f);
        Vector2 to = OverGround(main, 0.6f, 0.75f);
        DrivenInput.Press(from);
        for (int step = 1; step <= 20; step++)
        {
            DrivenInput.Move(from.Lerp(to, step / 20.0f));
        }
        DrivenInput.Release(to);
        await Frames(main, 1);
        await main.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = main.GetViewport().GetTexture().GetImage();
        if (image.IsEmpty())
        {
            report.Add("Viewport capture", "failed", "the viewport texture came back empty");
            return;
        }
        Error saved = image.SavePng(path);
        HashSet<uint> colors = [];
        for (int y = 0; y < image.GetHeight(); y += 8)
        {
            for (int x = 0; x < image.GetWidth(); x += 8)
            {
                colors.Add(image.GetPixel(x, y).ToRgba32());
            }
        }
        report.Add("Viewport capture", saved == Error.Ok && colors.Count >= 8 ? "passed" : "failed", $"{image.GetWidth()} x {image.GetHeight()}, {colors.Count} distinct sampled colors, saved {saved} to {Path.GetFileName(path)}");
    }

    // Performance.Monitor.TimeProcess is not a per-frame figure: it holds the longest process time seen in the
    // current second, so it is not sampled here. The frame interval, the renderer's own CPU and GPU times
    // and the texture upload are.
    private sealed record FrameSample(double IntervalMs, double RenderCpuMs, double RenderGpuMs, double UploadMs);

    private static async Task Measure(Main main, Report report, bool headless, string path)
    {
        int frames = main.Arguments.Frames;
        Rid viewport = main.GetViewport().GetViewportRid();
        if (!headless)
        {
            RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
        }
        Vector2 from = OverGround(main, 0.2f, 0.4f);
        Vector2 to = OverGround(main, 0.9f, 0.9f);
        List<FrameSample> samples = new(frames);
        ulong previous = Time.GetTicksUsec();
        DrivenInput.Press(from);
        for (int frame = 0; frame < frames; frame++)
        {
            // Back and forth along the stroke, so that nearly every frame paints a cell and uploads the texture.
            float t = (frame % 60) / 59.0f;
            DrivenInput.Move(from.Lerp(to, (frame / 60) % 2 == 0 ? t : 1.0f - t));
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            ulong now = Time.GetTicksUsec();
            samples.Add(new FrameSample(
                (now - previous) / 1000.0,
                headless ? 0.0 : RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport),
                headless ? 0.0 : RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport),
                main.LastUploadMilliseconds));
            previous = now;
        }
        DrivenInput.Release(to);
        await Frames(main, 1);

        static double Median(IEnumerable<double> values)
        {
            double[] sorted = values.Order().ToArray();
            return sorted.Length == 0 ? 0.0 : sorted[sorted.Length / 2];
        }
        static double Percentile95(IEnumerable<double> values)
        {
            double[] sorted = values.Order().ToArray();
            return sorted.Length == 0 ? 0.0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))];
        }
        var summary = new
        {
            Frames = frames,
            Grid = new { main.Ground.Columns, main.Ground.Rows },
            TextureUploads = main.Ground.Uploads,
            Renderer = new { Method = RenderingServer.GetCurrentRenderingMethod(), Driver = RenderingServer.GetCurrentRenderingDriverName(), Adapter = RenderingServer.GetVideoAdapterName(), DisplayServer = DisplayServer.GetName() },
            FrameIntervalMs = new { Median = Median(samples.Select(s => s.IntervalMs)), Percentile95 = Percentile95(samples.Select(s => s.IntervalMs)) },
            RenderCpuMs = new { Median = Median(samples.Select(s => s.RenderCpuMs)), Percentile95 = Percentile95(samples.Select(s => s.RenderCpuMs)) },
            RenderGpuMs = new { Median = Median(samples.Select(s => s.RenderGpuMs)), Percentile95 = Percentile95(samples.Select(s => s.RenderGpuMs)) },
            UploadMs = new { Median = Median(samples.Select(s => s.UploadMs)), Percentile95 = Percentile95(samples.Select(s => s.UploadMs)) },
            Samples = samples,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        report.Add("Frame timing", "passed",
            $"{frames} frames on {main.Ground.Columns} x {main.Ground.Rows}: interval median {summary.FrameIntervalMs.Median:0.00} ms (p95 {summary.FrameIntervalMs.Percentile95:0.00}), " +
            $"render cpu {summary.RenderCpuMs.Median:0.00} ms, render gpu {summary.RenderGpuMs.Median:0.00} ms, texture upload {summary.UploadMs.Median:0.000} ms (p95 {summary.UploadMs.Percentile95:0.000}); see Measurement.json");
    }
}
