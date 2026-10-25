using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HexelEditor.Core;

public static class ProjectFile
{
    private const int ManifestByteLimit = 128 * 1024;
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
                var manifest = new Manifest(1, document.Name, document.Width, document.Height, document.Orientation, document.BackgroundColor,
                    document.Layers.Select(layer => new LayerMetadata(layer.Name, layer.Visible, layer.Locked, layer.Opacity)).ToArray(),
                    document.Palette.ToArray());
                using (var stream = archive.CreateEntry("Document.json").Open()) JsonSerializer.Serialize(stream, manifest, Options);
                for (var layer = 0; layer < document.Layers.Count; layer++)
                {
                    using var stream = archive.CreateEntry($"Cells/{layer}.bin", CompressionLevel.Optimal).Open();
                    // The whole layer in one write. A BinaryWriter call a cell wrote the same bytes -- this
                    // is x64, so a uint is already little-endian in memory, which is the order it wrote --
                    // but it made two million calls a layer to do it, and that was most of the 478 ms a
                    // large canvas took to save.
                    stream.Write(MemoryMarshal.AsBytes(document.Layers[layer].Cells.AsSpan()));
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
        if (entry.Length > ManifestByteLimit) throw new InvalidDataException("The document manifest is too large.");
        Manifest metadata;
        try
        {
            // An archive states its own entry sizes, so the read is bounded rather than trusting the declared length.
            using var stream = entry.Open();
            var manifestBytes = new byte[ManifestByteLimit + 1];
            var manifestLength = stream.ReadAtLeast(manifestBytes, manifestBytes.Length, throwOnEndOfStream: false);
            if (manifestLength > ManifestByteLimit) throw new InvalidDataException("The document manifest is too large.");
            var manifestText = manifestBytes.AsSpan(0, manifestLength);
            // Reading bytes rather than a stream means the byte order mark, which editors add, has to be skipped here.
            if (manifestText.StartsWith(Encoding.UTF8.Preamble)) manifestText = manifestText[Encoding.UTF8.Preamble.Length..];
            metadata = JsonSerializer.Deserialize<Manifest>(manifestText) ?? throw new InvalidDataException("The document manifest is empty.");
        }
        catch (JsonException exception) { throw new InvalidDataException("The document manifest is invalid.", exception); }
        if (metadata.Format != 1 || metadata.Width is < 1 or > HexDocument.MaximumDimension || metadata.Height is < 1 or > HexDocument.MaximumDimension || !Enum.IsDefined(metadata.Orientation))
            throw new InvalidDataException("The project format or canvas dimensions are invalid.");
        if (metadata.Layers == null || metadata.Palette == null || metadata.Name == null || metadata.Name.Length > 256 ||
            metadata.Layers.Length is < 1 or > HexDocument.MaximumLayers ||
            metadata.Palette.Length is < 1 or > HexDocument.MaximumPaletteColors ||
            metadata.Layers.Any(layer => layer == null || string.IsNullOrWhiteSpace(layer.Name) || layer.Name.Length > 256))
            throw new InvalidDataException("The project contains invalid layers or palette data.");
        // A manifest written before the canvas had a background color of its own leaves that canvas on the default white.
        var document = new HexDocument(metadata.Width, metadata.Height, metadata.Orientation)
        { Name = metadata.Name, BackgroundColor = metadata.BackgroundColor ?? HexDocument.DefaultBackgroundColor };
        document.Palette.Clear(); document.Palette.AddRange(metadata.Palette);
        document.Layers.Clear();
        for (var layerIndex = 0; layerIndex < metadata.Layers.Length; layerIndex++)
        {
            var cellsEntry = archive.GetEntry($"Cells/{layerIndex}.bin") ?? throw new InvalidDataException("The project is missing cell data.");
            if (cellsEntry.Length != document.CellCount * 4L) throw new InvalidDataException("The project contains an invalid cell buffer.");
            var metadataLayer = metadata.Layers[layerIndex];
            var layer = new HexLayer(document.CellCount) { Name = metadataLayer.Name, Visible = metadataLayer.Visible, Locked = metadataLayer.Locked, Opacity = metadataLayer.Opacity };
            using var stream = cellsEntry.Open();
            // Straight into the layer's own memory, and exactly as many bytes as it holds: a short entry
            // throws rather than leaving the rest of the layer as whatever it was.
            stream.ReadExactly(MemoryMarshal.AsBytes(layer.Cells.AsSpan()));
            document.Layers.Add(layer);
        }
        return document;
    }

    private sealed record LayerMetadata(string Name, bool Visible, bool Locked, byte Opacity);
    private sealed record Manifest(int Format, string Name, int Width, int Height, HexOrientation Orientation, uint? BackgroundColor, LayerMetadata[] Layers, uint[] Palette);
}
