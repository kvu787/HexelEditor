namespace HexelEditor.Core;

public sealed class HexLayer
{
    public string Name { get; set; } = "Layer";
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public byte Opacity { get; set; } = 255;
    public HexLayer Clone() => (HexLayer)MemberwiseClone();
}

public sealed class HexFrame
{
    public int DurationMilliseconds { get; set; } = 150;
    public List<uint[]> Cells { get; } = [];
    public HexFrame Clone()
    {
        var clone = new HexFrame { DurationMilliseconds = DurationMilliseconds };
        clone.Cells.AddRange(Cells.Select(cells => (uint[])cells.Clone()));
        return clone;
    }
}

public sealed class HexDocument
{
    public const int MaximumDimension = 256;
    public const int MaximumLayers = 32;
    public const int MaximumFrames = 128;
    public const long MaximumTotalCells = 16_777_216;
    public string Name { get; set; } = "Untitled";
    public int Width { get; }
    public int Height { get; }
    public HexOrientation Orientation { get; }
    public int CellCount => Width * Height;
    public List<HexLayer> Layers { get; } = [];
    public List<HexFrame> Frames { get; } = [];
    public List<uint> Palette { get; } = [];

    public static readonly uint[] DefaultPalette =
    [
        0xFF151923, 0xFF272B40, 0xFF41456A, 0xFF626389, 0xFF9295B2, 0xFFCED5E5, 0xFFF3F6F1, 0xFFFFFFFF,
        0xFF391C4B, 0xFF63306D, 0xFF994A8C, 0xFFD8679C, 0xFFF29BA8, 0xFFFFD2B2, 0xFFFFE6A3, 0xFFFFF3CD,
        0xFF6B243E, 0xFFAE3B50, 0xFFE55B57, 0xFFFF8D61, 0xFFFFB56B, 0xFFEDCE75, 0xFFB7C77B, 0xFF7EAC81,
        0xFF103C48, 0xFF166477, 0xFF238F98, 0xFF38B5A5, 0xFF5CE4C2, 0xFF9AF4CF, 0xFF4B81C3, 0xFF5CA8DD
    ];

    public HexDocument(int width, int height, HexOrientation orientation = HexOrientation.PointyTop)
    {
        if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(width), "Canvas dimensions must be between 1 and 256.");
        if (!Enum.IsDefined(orientation)) throw new ArgumentOutOfRangeException(nameof(orientation));
        Width = width;
        Height = height;
        Orientation = orientation;
        Layers.Add(new HexLayer { Name = "Layer 1" });
        var frame = new HexFrame();
        frame.Cells.Add(new uint[CellCount]);
        Frames.Add(frame);
        Palette.AddRange(DefaultPalette);
    }

    public int IndexOf(HexCoordinate coordinate)
    {
        var (column, row) = HexGeometry.ToOffset(coordinate, Orientation);
        return column >= 0 && column < Width && row >= 0 && row < Height ? row * Width + column : -1;
    }

    public HexCoordinate CoordinateAt(int index) => HexGeometry.FromOffset(index % Width, index / Width, Orientation);

    public uint CompositeAt(int index, int frameIndex)
    {
        uint result = 0;
        for (var layer = 0; layer < Layers.Count; layer++)
            if (Layers[layer].Visible) result = ColorMath.Over(Frames[frameIndex].Cells[layer][index], result, Layers[layer].Opacity);
        return result;
    }

    public void ValidateCapacity(int layers, int frames)
    {
        if (layers is < 1 or > MaximumLayers || frames is < 1 or > MaximumFrames || (long)CellCount * layers * frames > MaximumTotalCells)
            throw new InvalidOperationException("The document limit is 32 layers, 128 frames, and 16 million total cells.");
    }

    public HexDocument Clone()
    {
        var clone = new HexDocument(Width, Height, Orientation) { Name = Name };
        clone.Layers.Clear();
        clone.Layers.AddRange(Layers.Select(layer => layer.Clone()));
        clone.Frames.Clear();
        clone.Frames.AddRange(Frames.Select(frame => frame.Clone()));
        clone.Palette.Clear();
        clone.Palette.AddRange(Palette);
        return clone;
    }
}

public static class ColorMath
{
    public static uint Over(uint source, uint destination, byte opacity = 255)
    {
        var sourceAlpha = (source >> 24) / 255.0 * opacity / 255.0;
        if (sourceAlpha == 0) return destination;
        var destinationAlpha = (destination >> 24) / 255.0;
        var alpha = sourceAlpha + destinationAlpha * (1 - sourceAlpha);
        uint Channel(int shift) => (uint)Math.Clamp(Math.Round((((source >> shift) & 255) * sourceAlpha + ((destination >> shift) & 255) * destinationAlpha * (1 - sourceAlpha)) / alpha), 0, 255);
        return (uint)Math.Round(alpha * 255) << 24 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    public static uint Parse(string value)
    {
        var text = value.Trim().TrimStart('#');
        if (text.Length != 6 && text.Length != 8) throw new FormatException("Use #RRGGBB or #AARRGGBB.");
        var result = uint.Parse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        return text.Length == 6 ? result | 0xFF000000 : result;
    }

    public static string Format(uint color) => (color >> 24) == 255 ? $"#{color & 0xFFFFFF:X6}" : $"#{color:X8}";
}
