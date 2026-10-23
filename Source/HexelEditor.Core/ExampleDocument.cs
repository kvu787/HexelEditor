namespace HexelEditor.Core;

public static class ExampleDocument
{
    public static HexDocument Create()
    {
        var document = new HexDocument(40, 32) { Name = "Neon tide" };
        document.Layers[0].Name = "Dusk sky";
        document.Layers.Add(new HexLayer(document.CellCount) { Name = "Distant peaks" });
        document.Layers.Add(new HexLayer(document.CellCount) { Name = "Tidal light" });
        var skyCells = document.Layers[0].Cells;
        var peakCells = document.Layers[1].Cells;
        var lightCells = document.Layers[2].Cells;
        for (var row = 0; row < document.Height; row++)
            for (var column = 0; column < document.Width; column++)
            {
                var index = row * document.Width + column;
                var x = column + (row % 2) * 0.5;
                uint sky = row < 6 ? 0xFF202338 : row < 11 ? 0xFF302744 : row < 16 ? 0xFF4D3156 : 0xFF754366;
                if (Math.Pow((x - 24) / 7.6, 2) + Math.Pow((row - 11) / 7.5, 2) < 1)
                    sky = row < 8 ? 0xFFFFE6A3 : row < 11 ? 0xFFFFB56B : row < 14 ? 0xFFFF8D61 : 0xFFE55B57;
                if (row < 9 && ((column * 31 + row * 73) % 137 == 0 || (column == 8 && row == 4))) sky = 0xFFCED5E5;
                skyCells[index] = sky;
                var ridge = Math.Min(22 - 1.25 * Math.Max(0, 9 - Math.Abs(x - 8)), 23 - 1.1 * Math.Max(0, 9 - Math.Abs(x - 34)));
                if (row >= ridge && row < 24) peakCells[index] = x < 20 ? (row < ridge + 2 ? 0xFF626389 : 0xFF41456A) : (row < ridge + 2 ? 0xFF63306D : 0xFF391C4B);
                if (row >= 22)
                {
                    uint sea = row < 25 ? 0xFF166477 : row < 29 ? 0xFF103C48 : 0xFF151923;
                    var spread = 2 + (row - 22) * 0.8;
                    if (Math.Abs(x - 24) < spread && (column + row) % 4 < 2) sea = row < 25 ? 0xFFF29BA8 : row < 28 ? 0xFF38B5A5 : 0xFF238F98;
                    if ((column * 7 + row * 11) % 41 == 0) sea = 0xFF5CE4C2;
                    lightCells[index] = sea;
                }
                if (row >= 27 && column < (row - 26) * 2) lightCells[index] = 0xFF151923;
            }
        return document;
    }
}
