using System.Diagnostics;
using HexelEditor.Core;
using OpenTK.Graphics.OpenGL4;
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
//
// Where the frame goes: straight into the framebuffer the canvas element shows, which WPF composites
// as a Direct3D surface. There is no reading back. The first GPU renderer drew into a surface of its
// own and read every frame back to the processor for a bitmap element to show, which cost 1.14 ms at
// a window's size and 3.70 ms at a 4K display's -- the whole of a frame once everything else had been
// taken out of it. The checks that sample pixels still need a bitmap, and get one from Render, which
// draws through the same Draw into an offscreen surface of this context and reads that back. The
// shader, its textures and its uniforms are the same either way; only the surface differs.
//
// Whose GL context this is: the canvas element's, on the interface thread. GLWpfControl creates one
// GLFW window for the process and keeps its context current on that thread, and GLFW allows windows
// only from the thread that first made one, so a renderer with a thread and a context of its own
// cannot coexist with the element -- which is what the attempt to adopt the element found. So this
// owns no context and no thread: it is built by the first canvas once the element's context exists,
// and everything here runs on the thread that calls it, with that context current. Nothing else in
// the process uses GL, so nothing ever makes a different context current behind it.
public sealed class GpuHexRenderer : IDisposable
{
    // Cells smaller than this cannot show their own shape, and the grid becomes a grey wash.
    private const float GridAbovePixels = 4f;
    private const float GridLinePixels = 0.9f;
    // What the selection used to be drawn with on the processor: a fill at 40 of 255 and a 1.4 pixel
    // outline, both in the interface's blue.
    private const float SelectionFillAlpha = 40f / 255f;
    private const float SelectionLinePixels = 1.4f;

    private readonly GRGlInterface glInterface;
    private readonly GRContext context;
    private readonly SKRuntimeEffect effect;
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
    // The surface Render draws into and the bitmap it reads that into, kept across calls at one size.
    private SKSurface? offscreen;
    private int offscreenWidth, offscreenHeight;
    private SKBitmap? readback;

    /// <summary>
    /// What one frame spent, in milliseconds, split into the parts that behave differently: recompositing the
    /// cells that changed, uploading the colour texture, recording the draw, and the GPU running it. Only
    /// filled while <see cref="MeasureFrames"/> is set.
    /// </summary>
    public readonly record struct FrameTimings(double Composite, double Upload, double Submit, double Gpu)
    {
        public double Total => Composite + Upload + Submit + Gpu;
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

    /// <summary>
    /// The renderer for this process, once a canvas has built it, or null before that and when there is no
    /// usable GPU. The checks and the measurements draw through it; the canvas that built it draws through it
    /// too.
    /// </summary>
    /// <remarks>
    /// There is one because there is one GL context: GLWpfControl shares a single context between every
    /// element in the process, and a GRContext belongs to the GL context it was made on, so a second renderer
    /// would be a second GRContext over the same GL state for no gain. Use from the interface thread only,
    /// which is the thread the context is current on.
    /// </remarks>
    public static GpuHexRenderer? Shared { get; private set; }

    /// <summary>Why there is no renderer, once <see cref="Open"/> has returned null.</summary>
    public static string? Unavailable { get; private set; }

    /// <summary>The GPU context frames are drawn with. For surfaces a caller wants to paint a frame into.</summary>
    public GRContext Context => context;

    /// <summary>
    /// Opens the element's GL context and returns the process's renderer over it, building the renderer the
    /// first time. Null, with <see cref="Unavailable"/> set, when either part fails the first time; a later
    /// failure, with a renderer already standing, is the caller's to deal with.
    /// </summary>
    /// <param name="openContext">
    /// Starts the canvas element, which is what creates the GL context or joins the shared one. Called first,
    /// so that the renderer is built with a context current.
    /// </param>
    internal static GpuHexRenderer? Open(Action openContext, Action<string> log)
    {
        if (Unavailable != null) return null;
        try
        {
            openContext();
            return Shared ??= new GpuHexRenderer(log);
        }
        catch (Exception error) when (Shared == null)
        {
            Unavailable = "GPU renderer unavailable: " + error.Message;
            log(Unavailable);
            return null;
        }
    }

    /// <summary>Releases the process's renderer. For application shutdown, on the interface thread.</summary>
    public static void DisposeShared()
    {
        Shared?.Dispose();
        Shared = null;
    }

    private GpuHexRenderer(Action<string> log)
    {
        glInterface = GRGlInterface.Create() ?? throw new InvalidOperationException("No GL interface.");
        context = GRContext.CreateGl(glInterface) ?? throw new InvalidOperationException("No GL context.");
        effect = SKRuntimeEffect.CreateShader(Sksl, out var errors)
            ?? throw new InvalidOperationException("Shader failed to compile: " + errors);
        // Which device the context landed on is worth having in the log. A machine with both an integrated
        // and a discrete GPU gives the driver the choice, and the two differ by more than speed.
        log($"GPU renderer device: {GL.GetString(StringName.Renderer)} / {GL.GetString(StringName.Vendor)} / OpenGL {GL.GetString(StringName.Version)}.");
        log("GPU renderer ready.");
    }

    /// <summary>
    /// A GPU surface of this context at the given size, for a caller that wants to paint whole frames
    /// somewhere other than the window. Measurement paints into one; the window paints into its own.
    /// </summary>
    /// <remarks>
    /// The origin is GL's, bottom left, because that is the origin of the window's framebuffer, and the two
    /// have to be drawn the same way for a frame drawn into one to match a frame drawn into the other. The
    /// shader is the same either way, but Skia plumbs the pixel's coordinates into it differently for the
    /// two origins, and the driver then rounds the arithmetic differently: with one origin each, ten pixels
    /// in eight hundred thousand landed on the other side of the grid line's edge. Reading pixels out of a
    /// bottom-left surface still gives them the right way up.
    /// </remarks>
    public SKSurface CreateSurface(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"A surface of {width} by {height} has no pixels.");
        return SKSurface.Create(context, budgeted: true,
            new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), sampleCount: 0, GRSurfaceOrigin.BottomLeft)
            ?? throw new InvalidOperationException($"Could not create a {width} by {height} GPU surface.");
    }

    /// <summary>
    /// Renders the artwork at device scale into a bitmap the caller reads and does not keep: the next call
    /// writes over it. Cells outside the canvas come back transparent unless a backdrop is asked for.
    /// </summary>
    /// <remarks>
    /// This is the frame as pixels, for the checks that sample them. It is <see cref="Draw"/> into an
    /// offscreen surface of this context, read back once it is drawn. Reading back is the cost the window
    /// no longer pays, and it is paid here only by a caller that wants pixels on the processor.
    /// </remarks>
    public SKBitmap Render(HexDocument document, ArtworkCache cache, int width, int height,
        float zoom, float offsetX, float offsetY, bool grid,
        SelectionMask? selection = null, RectanglePreview? preview = null, BackdropDots? backdrop = null)
    {
        if (offscreen == null || offscreenWidth != width || offscreenHeight != height)
        {
            offscreen?.Dispose();
            readback?.Dispose();
            readback = null;
            offscreen = CreateSurface(width, height);
            offscreenWidth = width;
            offscreenHeight = height;
        }
        Draw(offscreen.Canvas, width, height, document, cache, zoom, offsetX, offsetY, grid, selection, preview, backdrop);
        readback ??= new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!offscreen.ReadPixels(readback.Info, readback.GetPixels(), readback.RowBytes, 0, 0))
            throw new InvalidOperationException("Could not read the rendered frame back from the GPU.");
        return readback;
    }

    /// <summary>
    /// Draws one frame over the whole of a canvas of this context: the artwork, the grid, the selection and
    /// the rectangle being dragged out, and the paper behind them when a backdrop is given. Every pixel of
    /// the frame is written, so nothing needs clearing first. The work is flushed to the GPU before this
    /// returns; it is not waited for.
    /// </summary>
    /// <remarks>
    /// There is no other renderer, so a frame that cannot be produced throws instead of drawing nothing.
    /// Drawing nothing would leave the editor showing a canvas with no artwork in it and no account of why,
    /// which is a worse answer than stopping.
    /// </remarks>
    public void Draw(SKCanvas canvas, int width, int height, HexDocument document, ArtworkCache cache,
        float zoom, float offsetX, float offsetY, bool grid,
        SelectionMask? selection = null, RectanglePreview? preview = null, BackdropDots? backdrop = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"A frame of {width} by {height} has no pixels to draw.");

        // Measured as four parts rather than one, because they answer to different things: compositing
        // follows the cells that changed, uploading follows the canvas, recording the draw follows little,
        // and the GPU's share follows the window. One number for the frame hides which of them is the one
        // worth going after.
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
            colourImage = colours.Update(context, raster, null);
            colourSource = cache;
            colourVersion = cache.Version;
        }
        else if (colourVersion != cache.Version)
        {
            colourImage = colours.Update(context, raster, cache.LastWritten);
            colourVersion = cache.Version;
        }
        colourImage ??= colours.Update(context, raster, null);
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
        using var uniforms = new SKRuntimeEffectUniforms(effect)
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
            // The paper the canvas sits on. Drawn here so the frame has no transparent pixels left in it
            // and nothing underneath it is needed. Off for a caller that wants the artwork alone, which is
            // what the checks ask for.
            { "backdropStrength", backdrop.HasValue ? 1f : 0f },
            { "dotSpacing", new[] { backdrop?.SpacingX ?? 1f, backdrop?.SpacingY ?? 1f } },
            { "dotOffset", new[] { backdrop?.OffsetX ?? 0f, backdrop?.OffsetY ?? 0f } },
            { "dotRadius", backdrop?.Radius ?? 0f },
        };
        using var children = new SKRuntimeEffectChildren(effect)
        {
            { "colours", child },
            { "selection", selectionChild },
        };
        using var shader = effect.ToShader(uniforms, children);
        // Written over whatever the surface held rather than blended onto it, which is what lets the frame
        // skip clearing the surface first: outside the canvas the shader leaves transparent pixels when no
        // backdrop is asked for, and those have to land as transparent, not as the previous frame.
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };

        canvas.DrawRect(new SKRect(0, 0, width, height), paint);
        var afterSubmit = measuring ? Stopwatch.GetTimestamp() : 0;
        context.Flush();
        // Flushing hands the work over without waiting for it, so the time spent recording the draw says
        // nothing about what the GPU then does with it. Waiting here is the only way to tell the two apart,
        // and it is why measuring is not something to leave switched on.
        if (measuring)
        {
            GL.Finish();
            LastFrameTimings = new FrameTimings(
                Milliseconds(frameStarted, afterComposite),
                Milliseconds(afterComposite, afterUpload),
                Milliseconds(afterUpload, afterSubmit),
                Milliseconds(afterSubmit, Stopwatch.GetTimestamp()));
        }
    }

    /// <summary>
    /// Flushes everything drawn on this context and waits for the GPU to finish it. For measurement, which
    /// wants a frame's cost to include the GPU's share rather than only the time it took to hand over.
    /// </summary>
    public void Finish()
    {
        context.Flush();
        GL.Finish();
    }

    private static SKImage OneTexel()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Gray8, SKAlphaType.Opaque));
        bitmap.SetPixel(0, 0, SKColors.Black);
        return SKImage.FromBitmap(bitmap) ?? throw new InvalidOperationException("Could not create the empty selection texture.");
    }

    public void Dispose()
    {
        colours?.Dispose();
        colours = null;
        colourImage = null;
        colourSource = null;
        selected?.Dispose();
        selected = null;
        selectedSource = null;
        noSelection?.Dispose();
        noSelection = null;
        readback?.Dispose();
        readback = null;
        offscreen?.Dispose();
        offscreen = null;
        effect.Dispose();
        context.Dispose();
        glInterface.Dispose();
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
