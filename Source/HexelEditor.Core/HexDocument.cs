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
    public const int PaletteColumns = 16;
    // The default palette already fills a 16 by 16 grid, so the limit leaves room for colors the artist adds.
    public const int MaximumPaletteColors = 512;
    public const uint DefaultBackgroundColor = 0xFFFFFFFF;
    private uint backgroundColor = DefaultBackgroundColor;
    public string Name { get; set; } = "Untitled";
    public int Width { get; }
    public int Height { get; }
    public HexOrientation Orientation { get; }
    public int CellCount => Width * Height;

    // The canvas always sits on one opaque color, so a cell that holds nothing shows that color rather than
    // whatever is behind the canvas. Alpha here would let the view behind it through and stop it being solid.
    public uint BackgroundColor
    {
        get => backgroundColor;
        set => backgroundColor = value | 0xFF000000;
    }

    public List<HexLayer> Layers { get; } = [];
    public List<uint> Palette { get; } = [];

    // A 16 by 16 working palette. The first row is a neutral grey ramp from black to white; each row below it
    // is one hue shaded from dark on the left to light on the right. Shadows lean toward violet and highlights
    // toward yellow, the way hand-mixed ramps do, so a row reads as light falling on one material.
    public static readonly uint[] DefaultPalette =
    [
        0xFF000000, 0xFF111111, 0xFF222222, 0xFF333333, 0xFF444444, 0xFF555555, 0xFF666666, 0xFF777777, 0xFF888888, 0xFF999999, 0xFFAAAAAA, 0xFFBBBBBB, 0xFFCCCCCC, 0xFFDDDDDD, 0xFFEEEEEE, 0xFFFFFFFF,
        0xFF3A0D16, 0xFF50111B, 0xFF67131F, 0xFF7E1521, 0xFF961622, 0xFFAD1821, 0xFFC51920, 0xFFDC1B1E, 0xFFE52D2C, 0xFFE74643, 0xFFE85F5B, 0xFFEA7872, 0xFFEC908A, 0xFFEEA7A1, 0xFFF0BDB7, 0xFFF3D1CD,
        0xFF3B0F0C, 0xFF52150F, 0xFF691C11, 0xFF812312, 0xFF992C13, 0xFFB13714, 0xFFC94215, 0xFFE15016, 0xFFEA6227, 0xFFEB753F, 0xFFEC8757, 0xFFED996F, 0xFFEEAA87, 0xFFF0BB9F, 0xFFF2CBB6, 0xFFF4DBCC,
        0xFF3C190B, 0xFF53230E, 0xFF6A2F0F, 0xFF833C10, 0xFF9B4B11, 0xFFB45C11, 0xFFCD6E12, 0xFFE58213, 0xFFED9324, 0xFFEE9F3C, 0xFFEFAB54, 0xFFEFB76D, 0xFFF0C285, 0xFFF1CE9D, 0xFFF3D9B5, 0xFFF5E4CC,
        0xFF3C280C, 0xFF523A0E, 0xFF6A4E10, 0xFF826411, 0xFF9B7C11, 0xFFB39612, 0xFFCCB313, 0xFFE4D114, 0xFFECDF25, 0xFFEDE13D, 0xFFEEE355, 0xFFEFE56E, 0xFFEFE886, 0xFFF1EB9E, 0xFFF2EEB5, 0xFFF5F2CC,
        0xFF38320F, 0xFF4E4813, 0xFF636016, 0xFF797A19, 0xFF8A911B, 0xFF99A81E, 0xFFA6BE20, 0xFFB0D523, 0xFFB6DE33, 0xFFBFE149, 0xFFC6E360, 0xFFCEE577, 0xFFD5E88E, 0xFFDCEBA4, 0xFFE4EEBA, 0xFFEBF2CF,
        0xFF2E3611, 0xFF3C4A16, 0xFF485F1B, 0xFF52751E, 0xFF5A8A22, 0xFF60A025, 0xFF64B629, 0xFF66CB2D, 0xFF70D43C, 0xFF81D852, 0xFF92DC67, 0xFFA2DF7D, 0xFFB2E393, 0xFFC1E7A8, 0xFFD0EBBD, 0xFFDEF0D1,
        0xFF12352A, 0xFF174937, 0xFF1C5E43, 0xFF20734D, 0xFF238956, 0xFF279E5D, 0xFF2BB362, 0xFF2FC866, 0xFF3FD26E, 0xFF54D67B, 0xFF69DA88, 0xFF7EDE96, 0xFF94E2A5, 0xFFA9E6B5, 0xFFBDEAC5, 0xFFD1F0D6,
        0xFF113437, 0xFF154A4B, 0xFF19605F, 0xFF1D7671, 0xFF208C81, 0xFF23A291, 0xFF27B89E, 0xFF2ACDAA, 0xFF3AD7B0, 0xFF50DAB4, 0xFF65DEB8, 0xFF7BE1BE, 0xFF91E4C5, 0xFFA7E8CE, 0xFFBCECD7, 0xFFD0F1E2,
        0xFF0F2938, 0xFF133A4E, 0xFF164C63, 0xFF195F7A, 0xFF1B7491, 0xFF1E8AA8, 0xFF20A2BE, 0xFF23BAD5, 0xFF33CADE, 0xFF49D5E1, 0xFF60DDE3, 0xFF77E5E5, 0xFF8EE8E5, 0xFFA4EBE6, 0xFFBAEEE9, 0xFFCFF2ED,
        0xFF0E1F39, 0xFF112B4F, 0xFF143866, 0xFF16467D, 0xFF185594, 0xFF1A65AC, 0xFF1C77C3, 0xFF1E89DA, 0xFF2E9BE2, 0xFF45ACE5, 0xFF5CBBE7, 0xFF74C8E8, 0xFF8BD4EA, 0xFFA2DEED, 0xFFB8E7EF, 0xFFCEEFF3,
        0xFF0F1439, 0xFF121B4E, 0xFF152265, 0xFF18297B, 0xFF1A3292, 0xFF1C3BAA, 0xFF1E44C1, 0xFF204FD7, 0xFF3163E0, 0xFF477BE3, 0xFF5E92E5, 0xFF75A6E7, 0xFF8CB8E9, 0xFFA3C9EC, 0xFFB9D7EF, 0xFFCEE4F3,
        0xFF1B1037, 0xFF23154C, 0xFF2B1861, 0xFF321C77, 0xFF391F8E, 0xFF3F21A4, 0xFF4524BA, 0xFF4A28D0, 0xFF5B38D9, 0xFF734DDC, 0xFF8A64DF, 0xFF9E7AE2, 0xFFB190E5, 0xFFC3A6E9, 0xFFD3BBED, 0xFFE1D0F1,
        0xFF2A1235, 0xFF391749, 0xFF481C5E, 0xFF582073, 0xFF672389, 0xFF77279E, 0xFF862BB3, 0xFF952FC8, 0xFFA33FD2, 0xFFB254D6, 0xFFBE69DA, 0xFFCA7EDE, 0xFFD494E2, 0xFFDDA9E6, 0xFFE6BDEA, 0xFFEDD1F0,
        0xFF371133, 0xFF4B1545, 0xFF601958, 0xFF761D6B, 0xFF8C207D, 0xFFA22390, 0xFFB827A2, 0xFFCD2AB3, 0xFFD73ABA, 0xFFDA50BD, 0xFFDE65C1, 0xFFE17BC6, 0xFFE491CC, 0xFFE8A7D3, 0xFFECBCDB, 0xFFF1D0E5,
        0xFF380F25, 0xFF4E1331, 0xFF63163D, 0xFF7A1948, 0xFF911B52, 0xFFA81E5B, 0xFFBE2064, 0xFFD5236C, 0xFFDE3376, 0xFFE14981, 0xFFE3608E, 0xFFE5779B, 0xFFE88EA9, 0xFFEBA4B8, 0xFFEEBAC8, 0xFFF2CFD8
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
        var clone = new HexDocument(Width, Height, Orientation) { Name = Name, BackgroundColor = BackgroundColor };
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
