using System.Diagnostics;
using HexelEditor.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SkiaSharp;

namespace HexelEditor.Desktop;

// Draws the whole canvas as one rectangle, with a shader that works out which cell each pixel
// falls in rather than drawing any cells at all.
//
// This is the only renderer. What came before it drew the cells themselves on the CPU, which costs
// what the artwork costs: it walked the visible cells, built paths or vertex arrays for them, and
// rasterised those. Measured on a two million cell canvas at a 2456 by 1270 view, that was 9 ms
// zoomed right in and 133 ms at the zoom where the most cells are visible, and it carried three
// tiers of falling detail to claw some of that back, at the price of jaggies below 67% zoom.
//
// Every cell edge is hard: one sample a pixel, no antialiasing anywhere in this file.
//
// A hex grid is a regular tiling, so "which cell is this point in" is closed-form arithmetic --
// HexGeometry.FromPoint, written as SkSL. That turns the frame into one rectangle and a per-pixel
// calculation, which costs what the window costs and nothing at all for the canvas behind it.
// Measured the same way: 0.04 ms to draw and 1.3 ms to read back, flat at every zoom.
//
// Why read back rather than present the GPU surface directly: SKElement hands WPF a bitmap, and
// the verification harness captures the window with RenderTargetBitmap, which cannot see GPU
// content at all. Reading the pixels into a bitmap keeps one rendering path for the real window
// and for the headless checks, and 1.3 ms is affordable where 133 ms was not. It is also what
// lets the checks sample a frame of the real thing rather than of some stand-in.
//
// Why everything GL happens on a thread of its own: the first version created the context on
// WPF's UI thread, and the real window then never painted at all -- not just the canvas, the
// whole window, toolbar included -- while the headless checks still passed. Owning a GL context
// and a GLFW window on the thread running WPF's dispatcher is not worth diagnosing when it can
// simply be kept away from it. The UI thread blocks for the millisecond or two a frame takes,
// which is far less than the renderer this replaced.
public sealed class GpuHexRenderer : IDisposable
{
    // Cells smaller than this cannot show their own shape, and the grid becomes a grey wash.
    private const float GridAbovePixels = 4f;
    private const float GridLinePixels = 0.9f;
    // What the selection used to be drawn with on the processor: a fill at 40 of 255 and a 1.4 pixel
    // outline, both in the interface's blue.
    private const float SelectionFillAlpha = 40f / 255f;
    private const float SelectionLinePixels = 1.4f;

    private readonly Thread thread;
    // Which device the context landed on is worth having in the log. A machine with both an integrated and
    // a discrete GPU gives the driver the choice, and the two differ by more than speed: reading a frame
    // back from a discrete GPU crosses the bus, and from an integrated one it does not.
    private Action<string>? log;
    private readonly SemaphoreSlim wanted = new(0);
    private readonly SemaphoreSlim finished = new(0);
    private readonly SemaphoreSlim ready = new(0);
    private volatile bool running = true;
    private string? startupFailure;

    // Owned by the render thread.
    private NativeWindow? window;
    private GRGlInterface? glInterface;
    private GRContext? context;
    private SKRuntimeEffect? effect;
    private SKSurface? surface;
    private int surfaceWidth, surfaceHeight;
    private SKBitmap? readback;
    private MirroredTexture? colours;
    private ArtworkCache? colourSource;
    private int colourVersion = -1;
    // The image reading the colour texture. It survives the texture being written to, so it is kept
    // rather than remade, and only a reallocation replaces it.
    private SKImage? colourImage;
    private SKImage? selected;
    private SelectionMask? selectedSource;
    private int selectedVersion = -1;
    // Bound when nothing is selected. A runtime effect's child shader has to be bound whether the frame
    // reads from it or not, and one texel is the cheapest thing to bind.
    private SKImage? noSelection;

    /// <summary>
    /// What one frame spent, in milliseconds, split into the parts that behave differently: recompositing the
    /// cells that changed, uploading the colour texture, recording the draw, the GPU running it, and reading
    /// the result back. Only filled while <see cref="MeasureFrames"/> is set.
    /// </summary>
    public readonly record struct FrameTimings(double Composite, double Upload, double Submit, double Gpu, double Readback)
    {
        public double Total => Composite + Upload + Submit + Gpu + Readback;
    }

    /// <summary>
    /// Times each frame's parts, which costs a full GPU sync inside the frame to attribute the GPU's own
    /// share, so it is off unless something is measuring. Set it before the frame, read
    /// <see cref="LastFrameTimings"/> after.
    /// </summary>
    public static bool MeasureFrames;

    /// <summary>The parts of the last frame drawn while <see cref="MeasureFrames"/> was set.</summary>
    public FrameTimings LastFrameTimings { get; private set; }

    private static double Milliseconds(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    // The one request in flight. The caller blocks until it is answered, so a single slot is all
    // that is ever needed, and the two semaphores are all the synchronisation there has to be.
    private HexDocument? askDocument;
    private ArtworkCache? askCache;
    private SelectionMask? askSelection;
    private RectanglePreview? askPreview;
    private BackdropDots? askBackdrop;
    private (SKImageInfo Info, IntPtr Pixels, int RowBytes)? askDestination;
    private int askWidth, askHeight;
    private float askZoom, askOffsetX, askOffsetY;
    private bool askGrid;
    private SKBitmap? answer;
    private Exception? failure;

    private GpuHexRenderer()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "HexelGpuRenderer" };
    }

    private static GpuHexRenderer? shared;
    private static bool attempted;

    /// <summary>
    /// The renderer for this process, built on first use, or null when there is no usable GPU.
    /// </summary>
    /// <remarks>
    /// There can only ever be one. GLFW binds to the thread that first creates a window in the
    /// process, and every renderer wants a thread of its own, so a second one fails with "Can
    /// only create windows on the Glfw main thread" and takes the editor's only way of drawing
    /// with it. That is a sharp edge to leave lying about, so the single instance is the public
    /// way in and the constructor is not. Call from the UI thread.
    /// </remarks>
    public static GpuHexRenderer? Shared(Action<string>? log = null)
    {
        if (attempted) return shared;
        attempted = true;
        shared = TryCreate(log);
        return shared;
    }

    /// <summary>Why there is no renderer, once <see cref="Shared"/> has returned null.</summary>
    public static string? Unavailable { get; private set; }

    /// <summary>Releases the process's renderer. For application shutdown.</summary>
    public static void DisposeShared()
    {
        shared?.Dispose();
        shared = null;
    }

    private static GpuHexRenderer? TryCreate(Action<string>? log)
    {
        var renderer = new GpuHexRenderer { log = log };
        renderer.thread.Start();
        // Creating the context is the slow part. A machine without a GPU usually fails quickly; one that
        // hangs here is given up on rather than left to hold the editor behind it.
        if (!renderer.ready.Wait(TimeSpan.FromSeconds(10)))
        {
            Unavailable = "The GPU renderer timed out starting up.";
            log?.Invoke(Unavailable);
            renderer.running = false;
            return null;
        }
        if (renderer.startupFailure != null) { Unavailable = renderer.startupFailure; log?.Invoke(Unavailable); return null; }
        log?.Invoke("GPU renderer ready.");
        return renderer;
    }

    private void Loop()
    {
        try
        {
            // Hidden, unfocused and never pumped: it exists only to own a GL context.
            window = new NativeWindow(new NativeWindowSettings
            {
                Size = new OpenTK.Mathematics.Vector2i(16, 16),
                StartVisible = false,
                StartFocused = false,
                WindowBorder = WindowBorder.Hidden,
                APIVersion = new Version(3, 3),
                Profile = ContextProfile.Core,
            });
            window.Context.MakeCurrent();
            glInterface = GRGlInterface.Create() ?? throw new InvalidOperationException("No GL interface.");
            context = GRContext.CreateGl(glInterface) ?? throw new InvalidOperationException("No GL context.");
            effect = SKRuntimeEffect.CreateShader(Sksl, out var errors)
                ?? throw new InvalidOperationException("Shader failed to compile: " + errors);
            log?.Invoke($"GPU renderer device: {GL.GetString(StringName.Renderer)} / {GL.GetString(StringName.Vendor)} / OpenGL {GL.GetString(StringName.Version)}.");
        }
        catch (Exception error)
        {
            startupFailure = "GPU renderer unavailable: " + error.Message;
            Release();
            ready.Release();
            return;
        }
        ready.Release();

        while (true)
        {
            wanted.Wait();
            if (!running) break;
            // Carried back rather than swallowed: whatever went wrong on this thread is the only account of
            // it there is, and the caller has no second renderer to turn to.
            try { answer = RenderCore(); failure = null; }
            catch (Exception error) { answer = null; failure = error; }
            finished.Release();
        }
        Release();
    }

    /// <summary>
    /// Renders the artwork at device scale and returns a bitmap the caller draws at 1:1. Cells outside the
    /// canvas come back transparent.
    /// </summary>
    /// <remarks>
    /// There is no other renderer, so a frame that cannot be produced throws instead of coming back empty.
    /// Returning nothing would leave the editor showing a canvas with no artwork in it and no account of
    /// why, which is a worse answer than stopping.
    /// </remarks>
    public SKBitmap Render(HexDocument document, ArtworkCache cache, int width, int height,
        float zoom, float offsetX, float offsetY, bool grid,
        SelectionMask? selection = null, RectanglePreview? preview = null, BackdropDots? backdrop = null)
    {
        Ask(document, cache, width, height, zoom, offsetX, offsetY, grid, selection, preview, backdrop, null);
        return answer ?? throw new InvalidOperationException("The GPU renderer produced no frame.");
    }

    /// <summary>
    /// Draws a frame straight into pixels the caller owns, which is how the window takes one.
    /// </summary>
    /// <remarks>
    /// The frame used to be read into a bitmap of the renderer's own and then drawn onto the window's
    /// surface, which copied every pixel a second time: 4.4 ms at a window's size and 14.7 ms at a 4K
    /// display's, around a draw costing 0.16 ms. Reading it where it is wanted removes that copy. It works
    /// because the frame is opaque everywhere once the shader draws the backdrop too, so there is nothing
    /// underneath it left to show through.
    /// </remarks>
    public void RenderInto(SKPixmap destination, HexDocument document, ArtworkCache cache,
        float zoom, float offsetX, float offsetY, bool grid,
        SelectionMask? selection = null, RectanglePreview? preview = null, BackdropDots? backdrop = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Ask(document, cache, destination.Width, destination.Height, zoom, offsetX, offsetY, grid,
            selection, preview, backdrop, (destination.Info, destination.GetPixels(), destination.RowBytes));
    }

    private void Ask(HexDocument document, ArtworkCache cache, int width, int height,
        float zoom, float offsetX, float offsetY, bool grid,
        SelectionMask? selection, RectanglePreview? preview, BackdropDots? backdrop,
        (SKImageInfo Info, IntPtr Pixels, int RowBytes)? destination)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"A frame of {width} by {height} has no pixels to draw.");
        if (!running) throw new InvalidOperationException("The GPU renderer has stopped.");
        askDocument = document; askCache = cache;
        askSelection = selection; askPreview = preview; askBackdrop = backdrop;
        askDestination = destination;
        askWidth = width; askHeight = height;
        askZoom = zoom; askOffsetX = offsetX; askOffsetY = offsetY; askGrid = grid;
        wanted.Release();
        // A frame is a millisecond or two. One that never comes back has left the GL thread somewhere this
        // side cannot see into, so the renderer is finished rather than waited on again.
        if (!finished.Wait(TimeSpan.FromSeconds(2)))
        {
            running = false;
            throw new TimeoutException("A GPU frame did not come back within two seconds.");
        }
        if (failure != null)
            throw new InvalidOperationException("The GPU renderer could not draw the canvas: " + failure.Message, failure);
    }

    private SKBitmap? RenderCore()
    {
        var document = askDocument!;
        var cache = askCache!;
        var selection = askSelection;
        var preview = askPreview;
        var backdrop = askBackdrop;
        var destination = askDestination;
        int width = askWidth, height = askHeight;
        float zoom = askZoom, offsetX = askOffsetX, offsetY = askOffsetY;
        var grid = askGrid;
        if (surface == null || surfaceWidth != width || surfaceHeight != height)
        {
            surface?.Dispose();
            readback?.Dispose();
            readback = null;
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            surface = SKSurface.Create(context!, budgeted: true, info, sampleCount: 0, GRSurfaceOrigin.TopLeft)
                ?? throw new InvalidOperationException($"Could not create a {width} by {height} GPU surface.");
            surfaceWidth = width;
            surfaceHeight = height;
        }

        // Measured as five parts rather than one, because they answer to different things: compositing
        // follows the cells that changed, uploading follows the canvas, recording the draw follows little,
        // and the GPU's share and the read back both follow the window. One number for the frame hides
        // which of them is the one worth going after.
        var measuring = MeasureFrames;
        var frameStarted = measuring ? Stopwatch.GetTimestamp() : 0;

        // The colour texture is ArtworkCache's raster: two texels a cell across, so the
        // half-cell offset of alternate rows lands on exactly one texel. It is only re-uploaded
        // when a cell actually changed, so panning and zooming upload nothing.
        var raster = cache.Refresh(document);
        var afterComposite = measuring ? Stopwatch.GetTimestamp() : 0;
        // A version counts the changes within one cache, so it says nothing across two of them: a second
        // cache reaching a version this one has already seen would leave the first cache's texture in
        // place. Which cache the texture came from is therefore part of whether it is still current.
        colours ??= new MirroredTexture();
        // Which part of the texture to send. A cache this frame has not seen before has to go up whole,
        // because nothing is known about what in it differs from the texture; after that, only the texels
        // the compositing step actually wrote.
        if (!ReferenceEquals(colourSource, cache))
        {
            colourImage = colours.Update(context!, raster, null);
            colourSource = cache;
            colourVersion = cache.Version;
        }
        else if (colourVersion != cache.Version)
        {
            colourImage = colours.Update(context!, raster, cache.LastWritten);
            colourVersion = cache.Version;
        }
        colourImage ??= colours.Update(context!, raster, null);
        noSelection ??= OneTexel();
        var selectionRaster = selection?.Current;
        if (selectionRaster != null
            && (selected == null || selectedVersion != selection!.Version || !ReferenceEquals(selectedSource, selection)))
        {
            selected?.Dispose();
            selected = SKImage.FromBitmap(selectionRaster);
            selectedSource = selection;
            selectedVersion = selection!.Version;
        }
        var afterUpload = measuring ? Stopwatch.GetTimestamp() : 0;

        var cellPixels = (float)(Math.Sqrt(3) * CanvasGeometry.CellRadius * zoom);
        var pointyTop = document.Orientation == HexOrientation.PointyTop;
        using var child = colourImage.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal,
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        var showSelection = selection is { Any: true } && selected != null;
        using var selectionChild = (showSelection ? selected! : noSelection!).ToShader(
            SKShaderTileMode.Decal, SKShaderTileMode.Decal,
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        var shownPreview = preview is { IsEmpty: false } ? preview.Value : RectanglePreview.Nothing;
        using var uniforms = new SKRuntimeEffectUniforms(effect!)
        {
            { "invScale", new[] { 1f / zoom, 1f / zoom } },
            { "pan", new[] { offsetX, offsetY } },
            { "grid", new[] { (float)document.Width, (float)document.Height } },
            { "radius", CanvasGeometry.CellRadius },
            { "pointy", pointyTop ? 1f : 0f },
            // The grid is drawn analytically from the distance to the nearest cell edge, which
            // is exact at every zoom and costs nothing per cell. Below a few pixels a cell it
            // would only be a grey wash, so it stops there as the old renderer did.
            { "gridStrength", grid && cellPixels >= GridAbovePixels ? 0.23f : 0f },
            { "gridWidth", GridLinePixels / zoom },
            // The same fill and outline the per-cell paths used to draw, and the same outline width, so
            // that moving this into the shader is a change of cost rather than of appearance.
            { "selectStrength", showSelection ? SelectionFillAlpha : 0f },
            { "selectWidth", SelectionLinePixels / zoom },
            { "previewStrength", shownPreview.IsEmpty ? 0f : SelectionFillAlpha },
            { "previewBounds", new[] { (float)shownPreview.Left, shownPreview.Top, shownPreview.Right, (float)shownPreview.Bottom } },
            { "previewFilled", shownPreview.Filled ? 1f : 0f },
            // The paper the canvas sits on. Drawn here so the frame has no transparent pixels left in it,
            // which is what lets the window read it rather than blend it. Off for a caller that wants the
            // artwork alone, which is what the checks ask for.
            { "backdropStrength", backdrop.HasValue ? 1f : 0f },
            { "dotSpacing", new[] { backdrop?.SpacingX ?? 1f, backdrop?.SpacingY ?? 1f } },
            { "dotOffset", new[] { backdrop?.OffsetX ?? 0f, backdrop?.OffsetY ?? 0f } },
            { "dotRadius", backdrop?.Radius ?? 0f },
        };
        using var children = new SKRuntimeEffectChildren(effect!)
        {
            { "colours", child },
            { "selection", selectionChild },
        };
        using var shader = effect!.ToShader(uniforms, children);
        using var paint = new SKPaint { Shader = shader };

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawRect(new SKRect(0, 0, width, height), paint);
        var afterSubmit = measuring ? Stopwatch.GetTimestamp() : 0;
        context!.Flush();
        // Flushing hands the work over without waiting for it, so the time spent recording the draw says
        // nothing about what the GPU then does with it. Waiting here is the only way to tell the two apart,
        // and it is why measuring is not something to leave switched on.
        if (measuring) GL.Finish();
        var afterGpu = measuring ? Stopwatch.GetTimestamp() : 0;
        if (destination is { } into)
        {
            if (!surface!.ReadPixels(into.Info, into.Pixels, into.RowBytes, 0, 0))
                throw new InvalidOperationException("Could not read the rendered frame into the window's surface.");
        }
        else
        {
            readback ??= new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (!surface!.ReadPixels(readback.Info, readback.GetPixels(), readback.RowBytes, 0, 0))
                throw new InvalidOperationException("Could not read the rendered frame back from the GPU.");
        }
        if (measuring)
            LastFrameTimings = new FrameTimings(
                Milliseconds(frameStarted, afterComposite),
                Milliseconds(afterComposite, afterUpload),
                Milliseconds(afterUpload, afterSubmit),
                Milliseconds(afterSubmit, afterGpu),
                Milliseconds(afterGpu, Stopwatch.GetTimestamp()));
        return destination == null ? readback : null;
    }

    private static SKImage OneTexel()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Gray8, SKAlphaType.Opaque));
        bitmap.SetPixel(0, 0, SKColors.Black);
        return SKImage.FromBitmap(bitmap) ?? throw new InvalidOperationException("Could not create the empty selection texture.");
    }

    private void Release()
    {
        colours?.Dispose();
        colours = null;
        colourImage = null;
        colourSource = null;
        selected?.Dispose();
        selectedSource = null;
        noSelection?.Dispose();
        readback?.Dispose();
        surface?.Dispose();
        effect?.Dispose();
        context?.Dispose();
        glInterface?.Dispose();
        window?.Dispose();
    }

    public void Dispose()
    {
        if (!running) return;
        running = false;
        wanted.Release();
        thread.Join(TimeSpan.FromSeconds(2));
    }

    // HexGeometry.FromPoint and HexGeometry.Center, written for the GPU. Both orientations are
    // here because the document chooses, and a branch on a uniform costs nothing: every pixel in
    // a frame takes the same side of it.
    private const string Sksl = """
uniform shader colours;
uniform shader selection;
uniform float2 invScale;
uniform float2 pan;
uniform float2 grid;
uniform float  radius;
uniform float  pointy;
uniform float  gridStrength;
uniform float  gridWidth;
uniform float  selectStrength;
uniform float  selectWidth;
uniform float  previewStrength;
uniform float4 previewBounds;
uniform float  previewFilled;
uniform float  backdropStrength;
uniform float2 dotSpacing;
uniform float2 dotOffset;
uniform float  dotRadius;

const float SQRT3 = 1.7320508;
const float HALF_SQRT3 = 0.8660254;
// The interface's blue, which the selection is filled and outlined with.
const half3 SELECTION = half3(0.0, 0.372549, 0.721569);
// The paper the canvas sits on, and the dots ruled across it.
const half3 PAPER = half3(0.952941, 0.952941, 0.952941);
const half3 DOTS = half3(0.870588, 0.870588, 0.870588);

// Cube rounding: the nearest hex to a fractional axial coordinate. The three cube coordinates sum
// to zero, so the one that moved furthest is recomputed from the other two.
float2 axialRound(float2 ax) {
    float x = ax.x, z = ax.y, y = -x - z;
    float rx = floor(x + 0.5), ry = floor(y + 0.5), rz = floor(z + 0.5);
    float dx = abs(rx - x), dy = abs(ry - y), dz = abs(rz - z);
    if (dx > dy && dx > dz)      { rx = -ry - rz; }
    else if (dy > dz)            { ry = -rx - rz; }
    else                         { rz = -rx - ry; }
    return float2(rx, rz);
}

half4 sampleOne(float2 w) {
    float2 ax = pointy > 0.5
        ? float2((SQRT3 / 3.0 * w.x - w.y / 3.0) / radius, (2.0 / 3.0 * w.y) / radius)
        : float2((2.0 / 3.0 * w.x) / radius, (-w.x / 3.0 + SQRT3 / 3.0 * w.y) / radius);
    float2 qr = axialRound(ax);
    float q = qr.x, r = qr.y;

    // Axial to offset, matching HexGeometry.ToOffset for each orientation.
    float column = pointy > 0.5 ? q + floor(r * 0.5) : q;
    float row    = pointy > 0.5 ? r : r + floor(q * 0.5);
    if (column < 0.0 || row < 0.0 || column >= grid.x || row >= grid.y) { return half4(0.0); }

    // The raster carries two texels a cell along the axis alternate lines are offset along.
    float2 texel = pointy > 0.5
        ? float2(column * 2.0 + (r - 2.0 * floor(r * 0.5)) + 0.5, row + 0.5)
        : float2(column + 0.5, row * 2.0 + (q - 2.0 * floor(q * 0.5)) + 0.5);
    half4 colour = colours.eval(texel);

    // Distance from the cell's centre out to its nearest edge. A hexagon is the points whose projection
    // onto all three edge normals stays inside the apothem, so the smallest slack across those three is
    // the distance to the boundary. Both the grid and a selected cell's outline are drawn from it.
    float2 centre = pointy > 0.5
        ? radius * float2(SQRT3 * (q + r * 0.5), 1.5 * r)
        : radius * float2(1.5 * q, SQRT3 * (r + q * 0.5));
    float2 p = w - centre;
    if (pointy < 0.5) { p = float2(p.y, p.x); }
    float reach = max(abs(p.x), max(abs(0.5 * p.x + HALF_SQRT3 * p.y), abs(-0.5 * p.x + HALF_SQRT3 * p.y)));
    float apothem = radius * HALF_SQRT3;

    if (gridStrength > 0.0) {
        float edge = 1.0 - step(apothem - gridWidth, reach);
        colour = mix(half4(half3(0.0), colour.a), colour, half(edge));
    }

    // Selected, or inside the rectangle being dragged out: filled with the interface's blue and outlined
    // in it, which is what the per-cell paths drew before this moved into the shader.
    float marked = 0.0;
    if (selectStrength > 0.0 && selection.eval(float2(column + 0.5, row + 0.5)).r > 0.5) {
        marked = selectStrength;
    }
    if (previewStrength > 0.0
        && column >= previewBounds.x && column <= previewBounds.z
        && row >= previewBounds.y && row <= previewBounds.w
        && (previewFilled > 0.5
            || column == previewBounds.x || column == previewBounds.z
            || row == previewBounds.y || row == previewBounds.w)) {
        marked = previewStrength;
    }
    if (marked > 0.0) {
        half4 tinted = mix(colour, half4(SELECTION, colour.a), half(marked));
        float edge = 1.0 - step(apothem - selectWidth, reach);
        colour = mix(half4(SELECTION, colour.a), tinted, half(edge));
    }
    return colour;
}

half4 main(float2 coord) {
    // One sample a pixel: a pixel takes the colour of whichever cell its centre lands in, and
    // cell edges come out hard. Nothing here softens them, by design.
    half4 cell = sampleOne((coord - pan) * invScale);
    if (cell.a > 0.0 || backdropStrength < 0.5) { return cell; }

    // Off the canvas: the dotted paper it sits on. Here rather than drawn underneath because a frame with
    // nothing transparent in it can be read straight into the window's surface, and because a dot pattern
    // worked out per pixel costs nothing next to four thousand little circles drawn one at a time.
    float2 toNearestDot = abs(fract((coord - dotOffset) / dotSpacing + 0.5) - 0.5) * dotSpacing;
    float ink = 1.0 - smoothstep(dotRadius - 0.5, dotRadius + 0.5, length(toNearestDot));
    return half4(mix(PAPER, DOTS, half(ink)), 1.0);
}
""";
}

/// <summary>
/// The dotted paper the canvas sits on, in device pixels: how far apart the dots are, where the first
/// one sits, and how large it is.
/// </summary>
public readonly record struct BackdropDots(float SpacingX, float SpacingY, float OffsetX, float OffsetY, float Radius);
