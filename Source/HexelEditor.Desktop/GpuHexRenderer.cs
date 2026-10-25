using HexelEditor.Core;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SkiaSharp;

namespace HexelEditor.Desktop;

// Draws the whole canvas as one rectangle, with a shader that works out which cell each pixel
// falls in rather than drawing any cells at all.
//
// The tiered renderer in ArtworkRenderer costs what the artwork costs: it walks visible cells,
// builds paths or vertex arrays for them, and rasterises those on the CPU. Measured on a two
// million cell canvas at a 2456 by 1270 view, that is 9 ms zoomed right in and 133 ms at the zoom
// where the most cells are visible, and the three detail tiers exist to claw some of it back. The
// jaggies below 67% zoom are what they cost.
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
// and for the headless checks, and 1.3 ms is affordable where 133 ms was not.
//
// Why everything GL happens on a thread of its own: the first version created the context on
// WPF's UI thread, and the real window then never painted at all -- not just the canvas, the
// whole window, toolbar included -- while the headless checks still passed. Owning a GL context
// and a GLFW window on the thread running WPF's dispatcher is not worth diagnosing when it can
// simply be kept away from it. The UI thread blocks for the millisecond or two a frame takes,
// which is far less than the renderer it replaces.
public sealed class GpuHexRenderer : IDisposable
{
    // Cells smaller than this cannot show their own shape, and the grid becomes a grey wash.
    private const float GridAbovePixels = 4f;
    private const float GridLinePixels = 0.9f;

    private readonly Thread thread;
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
    private SKImage? colours;
    private int colourVersion = -1;

    // The one request in flight. The caller blocks until it is answered, so a single slot is all
    // that is ever needed, and the two semaphores are all the synchronisation there has to be.
    private HexDocument? askDocument;
    private ArtworkCache? askCache;
    private int askWidth, askHeight;
    private float askZoom, askOffsetX, askOffsetY;
    private bool askGrid;
    private SKBitmap? answer;

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
    /// only create windows on the Glfw main thread" and silently drops the editor onto the CPU
    /// path for the rest of the session. That is a sharp edge to leave lying about, so the single
    /// instance is the public way in and the constructor is not. Call from the UI thread.
    /// </remarks>
    public static GpuHexRenderer? Shared(Action<string>? log = null)
    {
        if (attempted) return shared;
        attempted = true;
        shared = TryCreate(log);
        return shared;
    }

    /// <summary>Releases the process's renderer. For application shutdown.</summary>
    public static void DisposeShared()
    {
        shared?.Dispose();
        shared = null;
    }

    private static GpuHexRenderer? TryCreate(Action<string>? log)
    {
        var renderer = new GpuHexRenderer();
        renderer.thread.Start();
        // Creating the context is the slow part. A machine without a GPU usually fails quickly;
        // one that hangs here falls back rather than holding the editor up behind it.
        if (!renderer.ready.Wait(TimeSpan.FromSeconds(10)))
        {
            log?.Invoke("GPU renderer timed out starting up.");
            renderer.running = false;
            return null;
        }
        if (renderer.startupFailure != null) { log?.Invoke(renderer.startupFailure); return null; }
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
            try { answer = RenderCore(); }
            catch { answer = null; }
            finished.Release();
        }
        Release();
    }

    /// <summary>
    /// Renders the artwork at device scale and returns a bitmap the caller draws at 1:1, or null
    /// when the GPU could not produce one. Cells outside the canvas come back transparent.
    /// </summary>
    public SKBitmap? Render(HexDocument document, ArtworkCache cache, int width, int height,
        float zoom, float offsetX, float offsetY, bool grid)
    {
        if (width <= 0 || height <= 0 || !running) return null;
        askDocument = document; askCache = cache;
        askWidth = width; askHeight = height;
        askZoom = zoom; askOffsetX = offsetX; askOffsetY = offsetY; askGrid = grid;
        wanted.Release();
        // A frame is a millisecond or two. If one ever fails to come back, give up on the GPU
        // rather than freezing the editor waiting for it.
        if (!finished.Wait(TimeSpan.FromSeconds(2))) { running = false; return null; }
        return answer;
    }

    private SKBitmap? RenderCore()
    {
        var document = askDocument!;
        var cache = askCache!;
        int width = askWidth, height = askHeight;
        float zoom = askZoom, offsetX = askOffsetX, offsetY = askOffsetY;
        var grid = askGrid;
        {
            if (surface == null || surfaceWidth != width || surfaceHeight != height)
            {
                surface?.Dispose();
                readback?.Dispose();
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                surface = SKSurface.Create(context!, budgeted: true, info, sampleCount: 0, GRSurfaceOrigin.TopLeft);
                if (surface == null) return null;
                readback = new SKBitmap(info);
                surfaceWidth = width;
                surfaceHeight = height;
            }

            // The colour texture is ArtworkCache's raster: two texels a cell across, so the
            // half-cell offset of alternate rows lands on exactly one texel. It is only re-uploaded
            // when a cell actually changed, so panning and zooming upload nothing.
            var raster = cache.Refresh(document);
            if (colours == null || colourVersion != cache.Version)
            {
                colours?.Dispose();
                colours = SKImage.FromBitmap(raster);
                colourVersion = cache.Version;
            }

            var cellPixels = (float)(Math.Sqrt(3) * ArtworkRenderer.CellRadius * zoom);
            var pointyTop = document.Orientation == HexOrientation.PointyTop;
            using var child = colours.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal,
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
            using var uniforms = new SKRuntimeEffectUniforms(effect!)
            {
                { "invScale", new[] { 1f / zoom, 1f / zoom } },
                { "pan", new[] { offsetX, offsetY } },
                { "grid", new[] { (float)document.Width, (float)document.Height } },
                { "radius", ArtworkRenderer.CellRadius },
                { "pointy", pointyTop ? 1f : 0f },
                // The grid is drawn analytically from the distance to the nearest cell edge, which
                // is exact at every zoom and costs nothing per cell. Below a few pixels a cell it
                // would only be a grey wash, so it stops there as the old renderer did.
                { "gridStrength", grid && cellPixels >= GridAbovePixels ? 0.23f : 0f },
                { "gridWidth", GridLinePixels / zoom },
            };
            using var children = new SKRuntimeEffectChildren(effect!) { { "colours", child } };
            using var shader = effect!.ToShader(uniforms, children);
            using var paint = new SKPaint { Shader = shader };

            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawRect(new SKRect(0, 0, width, height), paint);
            context!.Flush();
            if (!surface!.ReadPixels(readback!.Info, readback.GetPixels(), readback.RowBytes, 0, 0)) return null;
            return readback;
        }
    }

    private void Release()
    {
        colours?.Dispose();
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
uniform float2 invScale;
uniform float2 pan;
uniform float2 grid;
uniform float  radius;
uniform float  pointy;
uniform float  gridStrength;
uniform float  gridWidth;

const float SQRT3 = 1.7320508;
const float HALF_SQRT3 = 0.8660254;

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

    if (gridStrength > 0.0) {
        // Distance from the cell's centre out to its nearest edge. A hexagon is the points whose
        // projection onto all three edge normals stays inside the apothem, so the smallest slack
        // across those three is the distance to the boundary.
        float2 centre = pointy > 0.5
            ? radius * float2(SQRT3 * (q + r * 0.5), 1.5 * r)
            : radius * float2(1.5 * q, SQRT3 * (r + q * 0.5));
        float2 p = w - centre;
        if (pointy < 0.5) { p = float2(p.y, p.x); }
        float reach = max(abs(p.x), max(abs(0.5 * p.x + HALF_SQRT3 * p.y), abs(-0.5 * p.x + HALF_SQRT3 * p.y)));
        float edge = 1.0 - step(radius * HALF_SQRT3 - gridWidth, reach);
        colour = mix(half4(half3(0.0), colour.a), colour, half(edge));
    }
    return colour;
}

half4 main(float2 coord) {
    // One sample a pixel: a pixel takes the colour of whichever cell its centre lands in, and
    // cell edges come out hard. Nothing here softens them, by design.
    return sampleOne((coord - pan) * invScale);
}
""";
}
