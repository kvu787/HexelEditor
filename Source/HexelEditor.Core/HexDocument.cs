namespace HexelEditor.Core;

public sealed class HexLayer
{
    public HexLayer(int cellCount) => Cells = new uint[cellCount];
    public string Name { get; set; } = "Layer";
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public byte Opacity { get; set; } = 255;
    public uint[] Cells { get; private set; }
    public HexLayer Clone() { var clone = (HexLayer)MemberwiseClone(); clone.Cells = (uint[])Cells.Clone(); return clone; }
}

public sealed class HexDocument
{
    public const int MaximumDimension = 256;
    public const int MaximumLayers = 32;
    public const int MaximumPaletteColors = 256;
    public string Name { get; set; } = "Untitled";
    public int Width { get; }
    public int Height { get; }
    public HexOrientation Orientation { get; }
    public int CellCount => Width * Height;
    public List<HexLayer> Layers { get; } = [];
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
        if (width is < 1 or > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(width), width, $"Canvas width must be between 1 and {MaximumDimension}.");
        if (height is < 1 or > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(height), height, $"Canvas height must be between 1 and {MaximumDimension}.");
        if (!Enum.IsDefined(orientation)) throw new ArgumentOutOfRangeException(nameof(orientation));
        Width = width;
        Height = height;
        Orientation = orientation;
        Layers.Add(new HexLayer(CellCount) { Name = "Layer 1" });
        Palette.AddRange(DefaultPalette);
    }

    public int IndexOf(HexCoordinate coordinate)
    {
        var (column, row) = HexGeometry.ToOffset(coordinate, Orientation);
        return column >= 0 && column < Width && row >= 0 && row < Height ? row * Width + column : -1;
    }

    public HexCoordinate CoordinateAt(int index) => HexGeometry.FromOffset(index % Width, index / Width, Orientation);

    public uint CompositeAt(int index)
    {
        uint result = 0;
        foreach (var layer in Layers)
            if (layer.Visible) result = ColorMath.Over(layer.Cells[index], result, layer.Opacity);
        return result;
    }

    public void ValidateLayerCount(int layers)
    {
        if (layers is < 1 or > MaximumLayers) throw new InvalidOperationException($"The document limit is {MaximumLayers} layers.");
    }

    public HexDocument Clone()
    {
        var clone = new HexDocument(Width, Height, Orientation) { Name = Name };
        clone.Layers.Clear();
        clone.Layers.AddRange(Layers.Select(layer => layer.Clone()));
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
