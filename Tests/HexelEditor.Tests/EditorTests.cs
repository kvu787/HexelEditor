using System.IO.Compression;
using System.Text;
using HexelEditor.Core;
using Xunit;

namespace HexelEditor.Tests;

public sealed class EditorTests
{
    private const uint Red = 0xFFFF0000;
    private const uint Blue = 0xFF0000FF;

    [Theory]
    [InlineData(HexOrientation.PointyTop)]
    [InlineData(HexOrientation.FlatTop)]
    public void CoordinatesRoundTripAcrossNegativeAndPositiveGrid(HexOrientation orientation)
    {
        for (var column = -20; column < 20; column++)
            for (var row = -20; row < 20; row++)
            {
                var coordinate = HexGeometry.FromOffset(column, row, orientation);
                Assert.Equal((column, row), HexGeometry.ToOffset(coordinate, orientation));
                var (x, y) = HexGeometry.Center(coordinate, orientation, 19.3);
                Assert.Equal(coordinate, HexGeometry.FromPoint(x, y, orientation, 19.3));
            }
    }

    [Theory]
    [InlineData(0, 0, 20, -9)]
    [InlineData(-8, 12, 8, -4)]
    [InlineData(0, 0, 0, 0)]
    public void LinesHaveAdjacentStepsAndIncludeBothEndpoints(int q, int r, int endQ, int endR)
    {
        var start = new HexCoordinate(q, r); var end = new HexCoordinate(endQ, endR);
        var line = start.LineTo(end).ToArray();
        Assert.Equal(start, line[0]); Assert.Equal(end, line[^1]); Assert.Equal(start.DistanceTo(end) + 1, line.Length);
        for (var index = 1; index < line.Length; index++) Assert.Equal(1, line[index - 1].DistanceTo(line[index]));
    }

    [Fact]
    public void BrushDiskContainsTheSixHexNeighbors()
    {
        var origin = new HexCoordinate(0, 0);
        Assert.Equal(7, origin.Disk(1).Distinct().Count());
        Assert.All(HexCoordinate.Directions, direction => Assert.Contains(direction, origin.Disk(1)));
        Assert.Equal(19, origin.Disk(2).Count());
    }

    [Fact]
    public void OneStrokeIsOneUndoAndRestoresRepeatedlyPaintedCells()
    {
        var session = new EditorSession(new HexDocument(10, 10));
        session.BeginStroke(); session.Paint(new(1, 1), Red); session.Paint(new(2, 1), Red); session.Paint(new(1, 1), Blue); session.EndStroke();
        Assert.True(session.IsDirty); session.Undo();
        Assert.All(session.Document.Frames[0].Cells[0], color => Assert.Equal(0u, color)); Assert.False(session.CanUndo); Assert.False(session.IsDirty);
        session.Redo(); Assert.Equal(Blue, session.Document.Frames[0].Cells[0][session.Document.IndexOf(new(1, 1))]);
    }

    [Fact]
    public void NoOpStrokeDoesNotCreateHistoryOrDirtyState()
    {
        var session = new EditorSession(new HexDocument(3, 3));
        session.Paint(new(0, 0), Red); session.Paint(new(0, 0), 0); session.EndStroke();
        Assert.False(session.CanUndo); Assert.False(session.IsDirty);
    }

    [Fact]
    public void SavedRevisionAndDivergentRedoAreTracked()
    {
        var session = new EditorSession(new HexDocument(3, 3));
        session.Paint(new(0, 0), Red); session.EndStroke(); session.MarkSaved();
        session.Paint(new(1, 0), Red); session.EndStroke(); Assert.True(session.IsDirty);
        session.Undo(); Assert.False(session.IsDirty); Assert.True(session.CanRedo);
        session.Paint(new(2, 0), Blue); session.EndStroke(); Assert.False(session.CanRedo); Assert.True(session.IsDirty);
        session.Undo(); Assert.False(session.IsDirty);
    }

    [Theory]
    [InlineData(HexOrientation.PointyTop)]
    [InlineData(HexOrientation.FlatTop)]
    public void FillUsesSixNeighborsAndCannotCrossAClosedRing(HexOrientation orientation)
    {
        var document = new HexDocument(12, 12, orientation); var session = new EditorSession(document);
        var center = document.CoordinateAt(6 * document.Width + 6);
        foreach (var direction in HexCoordinate.Directions) session.Paint(center + direction, Red);
        session.EndStroke(); session.FloodFill(center, Blue);
        Assert.Equal(1, document.Frames[0].Cells[0].Count(color => color == Blue));
        session.FloodFill(document.CoordinateAt(0), Blue);
        Assert.Equal(document.CellCount - 6, document.Frames[0].Cells[0].Count(color => color == Blue));
    }

    [Fact]
    public void SelectionConstrainsBrushAndFillAndClear()
    {
        var document = new HexDocument(8, 8); var session = new EditorSession(document);
        session.SelectRectangle(document.CoordinateAt(18), document.CoordinateAt(27));
        Assert.Equal(4, session.Selection.Count);
        session.FloodFill(document.CoordinateAt(18), Red); Assert.Equal(4, document.Frames[0].Cells[0].Count(color => color == Red));
        session.Paint(document.CoordinateAt(0), Blue, 8); session.EndStroke(); Assert.Equal(4, document.Frames[0].Cells[0].Count(color => color == Blue));
        session.ClearPixels(); Assert.All(document.Frames[0].Cells[0], color => Assert.Equal(0u, color));
    }

    [Fact]
    public void MovingAnOverlappingSelectionIsAtomicAndUndoable()
    {
        var session = new EditorSession(new HexDocument(8, 2)); var document = session.Document;
        session.Paint(document.CoordinateAt(0), Red); session.Paint(document.CoordinateAt(1), Blue); session.EndStroke();
        session.SelectRectangle(document.CoordinateAt(0), document.CoordinateAt(1)); session.MoveSelection(1, 0);
        Assert.Equal(new uint[] { 0, Red, Blue }, document.Frames[0].Cells[0].Take(3));
        session.Undo(); Assert.Equal(new uint[] { Red, Blue, 0 }, document.Frames[0].Cells[0].Take(3));
    }

    [Fact]
    public void ClipboardPreservesAlphaAndPastesWithClipping()
    {
        var session = new EditorSession(new HexDocument(3, 2));
        session.Paint(new(0, 0), 0x80223344); session.Paint(new(1, 0), Red); session.EndStroke();
        session.SelectRectangle(new(0, 0), new(1, 0)); var clipboard = session.CopySelection();
        session.Paste(clipboard, 2, 1);
        Assert.Equal(0x80223344u, session.Document.Frames[0].Cells[0][5]); Assert.Single(session.Selection);
    }

    [Fact]
    public void LayersAndFramesRetainIndependentCelsThroughStructuralUndo()
    {
        var session = new EditorSession(new HexDocument(5, 5));
        session.Paint(new(0, 0), Red); session.EndStroke(); session.AddFrame(true); session.AddLayer();
        session.Paint(new(0, 0), Blue); session.EndStroke();
        Assert.Equal(Blue, session.Document.CompositeAt(0, 1)); Assert.Equal(Red, session.Document.CompositeAt(0, 0));
        session.Undo(); session.Undo(); Assert.Single(session.Document.Layers); Assert.Equal(2, session.Document.Frames.Count);
        session.Redo(); session.Redo(); Assert.Equal(Blue, session.Document.CompositeAt(0, 1));
        session.MoveLayer(-1); Assert.Equal(Red, session.Document.CompositeAt(0, 1)); session.Undo(); Assert.Equal(Blue, session.Document.CompositeAt(0, 1));
    }

    [Fact]
    public void LockedLayersRejectAllPixelEditing()
    {
        var session = new EditorSession(new HexDocument(3, 3)); session.SetLayerLocked(0, true); session.MarkSaved();
        session.Paint(new(0, 0), Red); session.EndStroke(); session.FloodFill(new(0, 0), Red); session.DrawLine(new(0, 0), new(2, 0), Red, 0);
        session.Paste(new CellClipboard([new(0, 0, Blue)]), 0, 0); session.ClearPixels();
        Assert.All(session.Document.Frames[0].Cells[0], color => Assert.Equal(0u, color)); Assert.False(session.IsDirty);
    }

    [Fact]
    public void CompositeRespectsLayerVisibilityOpacityAndSourceAlpha()
    {
        var document = new HexDocument(1, 1); document.Frames[0].Cells[0][0] = Blue;
        document.Layers.Add(new HexLayer { Opacity = 128 }); document.Frames[0].Cells.Add([Red]);
        Assert.Equal(0xFF80007Fu, document.CompositeAt(0, 0));
        document.Layers[1].Visible = false; Assert.Equal(Blue, document.CompositeAt(0, 0));
        Assert.Equal(0x80223344u, ColorMath.Over(0x80223344, 0));
    }

    [Fact]
    public void LastLayerAndFrameCannotBeDeleted()
    {
        var session = new EditorSession(new HexDocument(1, 1)); session.DeleteLayer(); session.DeleteFrame();
        Assert.Single(session.Document.Layers); Assert.Single(session.Document.Frames); Assert.False(session.IsDirty);
    }

    [Theory]
    [InlineData(HexOrientation.PointyTop)]
    [InlineData(HexOrientation.FlatTop)]
    public void SelectionRotatesExactlySixtyDegreesAndBack(HexOrientation orientation)
    {
        var document = new HexDocument(12, 12, orientation); var session = new EditorSession(document);
        var center = document.CoordinateAt(6 * document.Width + 6);
        var source = center + new HexCoordinate(1, 0); var destination = center + new HexCoordinate(0, 1);
        session.Paint(source, Red); session.EndStroke(); session.Selection.UnionWith(center.Disk(1).Select(document.IndexOf));
        session.RotateSelection(1);
        Assert.Equal(Red, document.Frames[0].Cells[0][document.IndexOf(destination)]); Assert.Equal(0u, document.Frames[0].Cells[0][document.IndexOf(source)]);
        session.RotateSelection(-1); Assert.Equal(Red, document.Frames[0].Cells[0][document.IndexOf(source)]);
        session.Undo(); Assert.Equal(Red, document.Frames[0].Cells[0][document.IndexOf(destination)]);
    }

    [Fact]
    public void BlankFramesAndDeletedLayersKeepAllFrameBuffersAligned()
    {
        var session = new EditorSession(new HexDocument(4, 4)); session.Paint(new(0, 0), Red); session.EndStroke();
        session.AddLayer(); session.Paint(new(0, 0), Blue); session.EndStroke(); session.AddFrame(false);
        Assert.All(session.Document.Frames[1].Cells.SelectMany(cells => cells), color => Assert.Equal(0u, color));
        session.DeleteLayer(); Assert.All(session.Document.Frames, frame => Assert.Single(frame.Cells));
        Assert.Equal(Red, session.Document.Frames[0].Cells[0][0]); session.Undo(); Assert.All(session.Document.Frames, frame => Assert.Equal(2, frame.Cells.Count));
    }

    [Fact]
    public void RotatingAnAsymmetricSelectionDoesNotDriftAtRoundingTies()
    {
        var session = new EditorSession(new HexDocument(12, 12));
        session.Paint(new(1, 4), Red); session.Paint(new(1, 5), Blue); session.EndStroke();
        var original = (uint[])session.Document.Frames[0].Cells[0].Clone();
        session.Selection.UnionWith(new[] { session.Document.IndexOf(new(1, 4)), session.Document.IndexOf(new(1, 5)) });
        session.RotateSelection(1); session.RotateSelection(-1);
        Assert.Equal(original, session.Document.Frames[0].Cells[0]);
        for (var turn = 0; turn < 6; turn++) session.RotateSelection(1);
        Assert.Equal(original, session.Document.Frames[0].Cells[0]);
    }

    [Fact]
    public void UndoRestoresTheSelectionMaskAfterAClippedMove()
    {
        var session = new EditorSession(new HexDocument(3, 2));
        session.Paint(new(0, 0), Red); session.Paint(new(1, 0), Blue); session.EndStroke();
        session.SelectRectangle(new(0, 0), new(1, 0)); session.MoveSelection(2, 0);
        Assert.Equal(new[] { 2 }, session.Selection.Order());
        session.Undo(); Assert.Equal(new[] { 0, 1 }, session.Selection.Order());
        session.Redo(); Assert.Equal(new[] { 2 }, session.Selection.Order());
    }

    [Fact]
    public void PixelUndoActivatesTheFrameAndLayerThatItChanges()
    {
        var session = new EditorSession(new HexDocument(3, 2));
        session.AddFrame(true); session.AddLayer(); session.Paint(new(0, 0), Blue); session.EndStroke();
        session.SelectFrame(0); session.SelectLayer(0); session.Undo();
        Assert.Equal(1, session.ActiveFrame); Assert.Equal(1, session.ActiveLayer);
        Assert.Equal(0u, session.Document.Frames[1].Cells[1][0]);
    }

    [Fact]
    public void InvalidCanvasAndDurationAndCapacityAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HexDocument(0, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HexDocument(257, 20));
        var session = new EditorSession(new HexDocument(256, 256));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetFrameDuration(0));
        Assert.Throws<InvalidOperationException>(() => session.Document.ValidateCapacity(32, 128));
    }

    [Theory]
    [InlineData("#5CE4C2", 0xFF5CE4C2u)]
    [InlineData("80223344", 0x80223344u)]
    public void ColorParsingPreservesTransparency(string text, uint expected) => Assert.Equal(expected, ColorMath.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#5CE4C2FF00")]
    [InlineData("#5CE4CZ")]
    [InlineData("#5C E4C2")]
    public void MalformedColorTextIsRejected(string text) => Assert.Throws<FormatException>(() => ColorMath.Parse(text));

    [Theory]
    [InlineData(0xFF5CE4C2u)]
    [InlineData(0x80223344u)]
    [InlineData(0x00000000u)]
    public void FormattedColorsParseBackToTheSameValue(uint color) => Assert.Equal(color, ColorMath.Parse(ColorMath.Format(color)));

    [Fact]
    public void CanvasDimensionErrorsNameTheDimensionThatIsOutOfRange()
    {
        Assert.Equal("width", Assert.Throws<ArgumentOutOfRangeException>(() => new HexDocument(0, 20)).ParamName);
        Assert.Equal("height", Assert.Throws<ArgumentOutOfRangeException>(() => new HexDocument(20, 257)).ParamName);
    }

    [Fact]
    public void FrameDurationsAreAcceptedOnlyWithinTheDocumentedRange()
    {
        var session = new EditorSession(new HexDocument(2, 2));
        session.SetFrameDuration(HexDocument.MinimumFrameDuration);
        Assert.Equal(HexDocument.MinimumFrameDuration, session.Document.Frames[0].DurationMilliseconds);
        session.SetFrameDuration(HexDocument.MaximumFrameDuration);
        Assert.Equal(HexDocument.MaximumFrameDuration, session.Document.Frames[0].DurationMilliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetFrameDuration(HexDocument.MinimumFrameDuration - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetFrameDuration(HexDocument.MaximumFrameDuration + 1));
    }

    [Fact]
    public void ThePaletteRejectsDuplicatesAndStopsAtItsLimit()
    {
        var session = new EditorSession(new HexDocument(1, 1));
        var starting = session.Document.Palette.Count;
        Assert.True(session.AddPaletteColor(0xAABBCCDD));
        Assert.False(session.AddPaletteColor(0xAABBCCDD));
        Assert.Equal(starting + 1, session.Document.Palette.Count);
        for (var color = 0u; session.Document.Palette.Count < HexDocument.MaximumPaletteColors; color++) session.AddPaletteColor(0xFF000000 | color);
        Assert.False(session.AddPaletteColor(0xFFFFFFFE));
        Assert.Equal(HexDocument.MaximumPaletteColors, session.Document.Palette.Count);
    }

    [Fact]
    public void ArchiveRoundTripPreservesEveryCelAndMetadata()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".hexel");
        try
        {
            var session = new EditorSession(new HexDocument(7, 9, HexOrientation.FlatTop) { Name = "Test scene" });
            session.AddLayer(); session.SetLayerName("Highlights"); session.SetLayerOpacity(123); session.AddFrame(true); session.SetFrameDuration(333);
            session.Paint(session.Document.CoordinateAt(20), 0x80223344); session.EndStroke(); session.AddPaletteColor(0xAABBCCDD);
            ProjectFile.Save(session.Document, path); var loaded = ProjectFile.Load(path);
            Assert.Equal(session.Document.Name, loaded.Name); Assert.Equal(HexOrientation.FlatTop, loaded.Orientation); Assert.Equal(123, loaded.Layers[1].Opacity);
            Assert.Equal(333, loaded.Frames[1].DurationMilliseconds); Assert.Equal(session.Document.Palette, loaded.Palette);
            for (var frame = 0; frame < 2; frame++) for (var layer = 0; layer < 2; layer++) Assert.Equal(session.Document.Frames[frame].Cells[layer], loaded.Frames[frame].Cells[layer]);
            // Saving over an existing file is also a complete atomic replacement.
            ProjectFile.Save(loaded, path); Assert.Equal(2, ProjectFile.Load(path).Frames.Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MissingCellBuffersAreRejected()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".hexel");
        try
        {
            ProjectFile.Save(new HexDocument(2, 2), path);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update)) archive.GetEntry("Cells/0/0.bin")!.Delete();
            Assert.Throws<InvalidDataException>(() => ProjectFile.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnOversizedManifestIsRejected()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".hexel");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("Document.json", CompressionLevel.Optimal).Open(), Encoding.UTF8))
                writer.Write(new string(' ', 4 * 1024 * 1024));
            Assert.Throws<InvalidDataException>(() => ProjectFile.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Format\":1,\"Width\":9999999,\"Height\":256}")]
    [InlineData("{broken")]
    // The untampered manifest only fails on its absent cels, which proves the tampered values below are what the reader rejects.
    [InlineData(ValidManifest)]
    [InlineData(ValidManifest, "\"Format\":1", "\"Format\":2")]
    [InlineData(ValidManifest, "\"Durations\":[150]", "\"Durations\":[19]")]
    [InlineData(ValidManifest, "\"Durations\":[150]", "\"Durations\":[10001]")]
    [InlineData(ValidManifest, "\"Palette\":[4278190080]", "\"Palette\":[]")]
    [InlineData(ValidManifest, "\"Name\":\"Tampered\"", "\"Name\":null")]
    [InlineData(ValidManifest, "\"Name\":\"Layer 1\"", "\"Name\":\" \"")]
    public void MalformedArchivesAreRejectedBeforeAllocatingCellBuffers(string manifest, string? search = null, string? replacement = null)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".hexel");
        var reachesCells = manifest == ValidManifest && search == null;
        if (search != null) { Assert.Contains(search, manifest); manifest = manifest.Replace(search, replacement); }
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("Document.json").Open(), Encoding.UTF8)) writer.Write(manifest);
            var failure = Assert.Throws<InvalidDataException>(() => ProjectFile.Load(path));
            Assert.Equal(reachesCells, failure.Message.Contains("missing cell data"));
        }
        finally { File.Delete(path); }
    }

    private const string ValidManifest = """
        {"Format":1,"Name":"Tampered","Width":2,"Height":2,"Orientation":0,
         "Layers":[{"Name":"Layer 1","Visible":true,"Locked":false,"Opacity":255}],
         "Durations":[150],"Palette":[4278190080]}
        """;
}
