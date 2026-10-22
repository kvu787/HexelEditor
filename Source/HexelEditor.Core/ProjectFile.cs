using System.IO.Compression;
using System.Text.Json;

namespace HexelEditor.Core;

public static class ProjectFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(HexDocument document, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".temporary";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var manifest = new Manifest(1, document.Name, document.Width, document.Height, document.Orientation,
                    document.Layers.Select(layer => new LayerMetadata(layer.Name, layer.Visible, layer.Locked, layer.Opacity)).ToArray(),
                    document.Frames.Select(frame => frame.DurationMilliseconds).ToArray(), document.Palette.ToArray());
                using (var stream = archive.CreateEntry("Document.json").Open()) JsonSerializer.Serialize(stream, manifest, Options);
                for (var frame = 0; frame < document.Frames.Count; frame++)
                    for (var layer = 0; layer < document.Layers.Count; layer++)
                    {
                        using var stream = archive.CreateEntry($"Cells/{frame}/{layer}.bin", CompressionLevel.Optimal).Open();
                        using var writer = new BinaryWriter(stream);
                        foreach (var color in document.Frames[frame].Cells[layer]) writer.Write(color);
                    }
            }
            File.Move(temporary, fullPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static HexDocument Load(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("Document.json") ?? throw new InvalidDataException("The project has no document manifest.");
        if (entry.Length > 128 * 1024) throw new InvalidDataException("The document manifest is too large.");
        Manifest metadata;
        try
        {
            using var stream = entry.Open();
            metadata = JsonSerializer.Deserialize<Manifest>(stream) ?? throw new InvalidDataException("The document manifest is empty.");
        }
        catch (JsonException exception) { throw new InvalidDataException("The document manifest is invalid.", exception); }
        if (metadata.Format != 1 || metadata.Width is < 1 or > HexDocument.MaximumDimension || metadata.Height is < 1 or > HexDocument.MaximumDimension || !Enum.IsDefined(metadata.Orientation))
            throw new InvalidDataException("The project format or canvas dimensions are invalid.");
        if (metadata.Layers == null || metadata.Durations == null || metadata.Palette == null || metadata.Name == null || metadata.Name.Length > 256 ||
            metadata.Layers.Length is < 1 or > HexDocument.MaximumLayers || metadata.Durations.Length is < 1 or > HexDocument.MaximumFrames ||
            (long)metadata.Width * metadata.Height * metadata.Layers.Length * metadata.Durations.Length > HexDocument.MaximumTotalCells ||
            metadata.Palette.Length is < 1 or > 256 || metadata.Durations.Any(duration => duration is < 20 or > 10000) ||
            metadata.Layers.Any(layer => layer == null || string.IsNullOrWhiteSpace(layer.Name) || layer.Name.Length > 256))
            throw new InvalidDataException("The project contains invalid layers, frames, or palette data.");
        var document = new HexDocument(metadata.Width, metadata.Height, metadata.Orientation) { Name = metadata.Name };
        document.Palette.Clear(); document.Palette.AddRange(metadata.Palette);
        document.Layers.Clear();
        document.Layers.AddRange(metadata.Layers.Select(layer => new HexLayer { Name = layer.Name, Visible = layer.Visible, Locked = layer.Locked, Opacity = layer.Opacity }));
        document.Frames.Clear();
        for (var frameIndex = 0; frameIndex < metadata.Durations.Length; frameIndex++)
        {
            var frame = new HexFrame { DurationMilliseconds = metadata.Durations[frameIndex] };
            for (var layer = 0; layer < metadata.Layers.Length; layer++)
            {
                var cellsEntry = archive.GetEntry($"Cells/{frameIndex}/{layer}.bin") ?? throw new InvalidDataException("The project is missing cell data.");
                if (cellsEntry.Length != document.CellCount * 4L) throw new InvalidDataException("The project contains an invalid cell buffer.");
                var cells = new uint[document.CellCount];
                using var stream = cellsEntry.Open();
                using var reader = new BinaryReader(stream);
                for (var index = 0; index < cells.Length; index++) cells[index] = reader.ReadUInt32();
                frame.Cells.Add(cells);
            }
            document.Frames.Add(frame);
        }
        return document;
    }

    private sealed record LayerMetadata(string Name, bool Visible, bool Locked, byte Opacity);
    private sealed record Manifest(int Format, string Name, int Width, int Height, HexOrientation Orientation, LayerMetadata[] Layers, int[] Durations, uint[] Palette);
}
