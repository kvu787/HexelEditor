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

    [Fact]
    public void RoundingAlwaysLandsOnANearestCellIncludingAtTies()
    {
        // A fine grid of fractional coordinates, in twelfths so that it includes the exact halves and thirds
        // where a point sits on an edge between two cells or a corner between three. Wherever it is, the
        // cell chosen has to be as near as any cell is: at a tie any of the cells sharing the edge will do,
        // and nothing further away will. The rounding is free to change how it breaks ties, as it has, but
        // not this.
        for (var qTwelfths = -60; qTwelfths <= 60; qTwelfths++)
            for (var rTwelfths = -60; rTwelfths <= 60; rTwelfths++)
            {
                double q = qTwelfths / 12.0, r = rTwelfths / 12.0;
                var chosen = HexCoordinate.Round(q, r);
                var nearest = double.MaxValue;
                for (var candidateQ = (int)Math.Floor(q) - 1; candidateQ <= (int)Math.Ceiling(q) + 1; candidateQ++)
                    for (var candidateR = (int)Math.Floor(r) - 1; candidateR <= (int)Math.Ceiling(r) + 1; candidateR++)
                        nearest = Math.Min(nearest, Distance(new(candidateQ, candidateR), q, r));
                Assert.True(Distance(chosen, q, r) <= nearest + 1e-9,
                    $"Rounding ({q}, {r}) chose {chosen}, {Distance(chosen, q, r)} away, where a cell {nearest} away exists.");
            }

        // The hex distance of a cell from a fractional point: the cube coordinates' differences, halved.
        static double Distance(HexCoordinate cell, double q, double r) =>
            (Math.Abs(cell.Q - q) + Math.Abs(cell.R - r) + Math.Abs(cell.S - (-q - r))) / 2;
    }

    [Fact]
    public void RoundingBreaksTiesTheWayTheShaderDoes()
    {
        // The shader decides which cell a pixel is in and this decides which cell the pointer is in, and at a
        // tie only the rule decides, so the rule has to be the same one. The reference is the shader's
        // axialRound, in the single precision it runs in. Eighths, because they are exact in binary at both
        // precisions: a point exactly on an edge has to be exactly on it for both. Rounding to even failed
        // this at q = r = -7.5, among others: the pointer there was read as one cell and drawn as its
        // neighbour.
        for (var qEighths = -60; qEighths <= 60; qEighths++)
            for (var rEighths = -60; rEighths <= 60; rEighths++)
            {
                double q = qEighths / 8.0, r = rEighths / 8.0;
                Assert.Equal(ShaderRound((float)q, (float)r), HexCoordinate.Round(q, r));
            }

        static HexCoordinate ShaderRound(float x, float z)
        {
            var y = -x - z;
            float rx = MathF.Floor(x + 0.5f), ry = MathF.Floor(y + 0.5f), rz = MathF.Floor(z + 0.5f);
            float dx = MathF.Abs(rx - x), dy = MathF.Abs(ry - y), dz = MathF.Abs(rz - z);
            if (dx > dy && dx > dz) rx = -ry - rz;
            else if (dy > dz) ry = -rx - rz;
            else rz = -rx - ry;
            return new((int)rx, (int)rz);
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

    [Theory]
    [InlineData(HexOrientation.PointyTop)]
    [InlineData(HexOrientation.FlatTop)]
    public void OffsetNeighborsAreTheSixAxialNeighborsOnEveryLine(HexOrientation orientation)
    {
        // Both parities of both axes, well inside a canvas so that no neighbour falls off it.
        for (var column = 4; column < 8; column++)
            for (var row = 4; row < 8; row++)
            {
                var cell = HexGeometry.FromOffset(column, row, orientation);
                var expected = HexCoordinate.Directions.Select(direction => HexGeometry.ToOffset(cell + direction, orientation)).Order().ToArray();
                var line = orientation == HexOrientation.PointyTop ? row : column;
                var actual = new List<(int, int)>();
                foreach (var (deltaColumn, deltaRow) in HexGeometry.NeighborOffsets(orientation, line)) actual.Add((column + deltaColumn, row + deltaRow));
                Assert.Equal(expected, actual.Order().ToArray());
            }
    }

    [Fact]
    public void UndoingABrushStrokeIsNotReportedAsAChangeToTheArtworksLook()
    {
        var session = new EditorSession(new HexDocument(8, 8));
        var reported = EditorChange.Pixels;
        session.Changed += change => reported = change;
        session.Paint(new(1, 1), Red); session.EndStroke();
        session.Undo();
        Assert.True((reported & EditorChange.Pixels) != 0);
        Assert.True((reported & EditorChange.Artwork) == 0);
        session.Redo();
        Assert.True((reported & EditorChange.Artwork) == 0);
        Assert.Equal(Red, session.Document.Layers[0].Cells[session.Document.IndexOf(new(1, 1))]);
    }

    [Fact]
    public void OnlyChangesThatAlterHowCellsLookAreReportedAsArtwork()
    {
        var session = new EditorSession(new HexDocument(8, 8));
        var reported = EditorChange.Pixels;
        session.Changed += change => reported = change;
        void Expect(bool artwork, Action change)
        {
            change();
            Assert.Equal(artwork, (reported & EditorChange.Artwork) != 0);
            Assert.True((reported & EditorChange.Structure) != 0);
            session.Undo();
            Assert.Equal(artwork, (reported & EditorChange.Artwork) != 0);
            session.Redo();
            Assert.Equal(artwork, (reported & EditorChange.Artwork) != 0);
        }
        Expect(false, session.AddLayer);
        Expect(false, () => session.SetLayerName("Named"));
        Expect(false, () => session.SetLayerLocked(0, true));
        Expect(false, () => session.AddPaletteColor(0xFF123456));
        Expect(true, () => session.SetLayerVisible(0, false));
        Expect(true, () => session.SetLayerOpacity(100));
        Expect(true, () => session.SetBackgroundColor(0xFF654321));
        Expect(true, () => session.MoveLayer(-1));
        Expect(true, session.DeleteLayer);
        session.SelectLayer(0);
        Assert.True((reported & EditorChange.Artwork) == 0);
    }

    [Fact]
    public void AnEditingOperationReportsItselfOnce()
    {
        // Each report costs the window a header rebuild and a frame, and an operation that ends a stroke
        // used to send two: one for the stroke ending and one for itself. What it changed is said once.
        var document = new HexDocument(8, 8);
        var session = new EditorSession(document);
        var reports = new List<EditorChange>();
        session.Changed += reports.Add;
        void Expect(EditorChange expected, Action operation)
        {
            reports.Clear();
            operation();
            Assert.Equal([expected], reports);
        }
        const EditorChange edited = EditorChange.Pixels | EditorChange.History;
        Expect(edited, () => session.FloodFill(document.CoordinateAt(0), Red));
        Expect(edited, () => session.DrawLine(document.CoordinateAt(0), document.CoordinateAt(7), Blue, 0));
        Expect(edited, () => session.DrawRectangle(document.CoordinateAt(9), document.CoordinateAt(27), Blue, false));
        Expect(EditorChange.Selection, () => session.SelectRectangle(document.CoordinateAt(9), document.CoordinateAt(27)));
        Expect(edited | EditorChange.Selection, () => session.MoveSelection(1, 1));
        Expect(edited | EditorChange.Selection, () => session.RotateSelection(1));
        Expect(edited, session.ClearPixels);
        var clipboard = session.CopySelection();
        Expect(edited | EditorChange.Selection, () => session.Paste(clipboard, 0, 0));
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
        Assert.All(session.Document.Layers[0].Cells, color => Assert.Equal(0u, color)); Assert.False(session.CanUndo); Assert.False(session.IsDirty);
        session.Redo(); Assert.Equal(Blue, session.Document.Layers[0].Cells[session.Document.IndexOf(new(1, 1))]);
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
        Assert.Equal(1, document.Layers[0].Cells.Count(color => color == Blue));
        session.FloodFill(document.CoordinateAt(0), Blue);
        Assert.Equal(document.CellCount - 6, document.Layers[0].Cells.Count(color => color == Blue));
    }

    [Fact]
    public void SelectionConstrainsBrushAndFillAndClear()
    {
        var document = new HexDocument(8, 8); var session = new EditorSession(document);
        session.SelectRectangle(document.CoordinateAt(18), document.CoordinateAt(27));
        Assert.Equal(4, session.Selection.Count);
        session.FloodFill(document.CoordinateAt(18), Red); Assert.Equal(4, document.Layers[0].Cells.Count(color => color == Red));
        session.Paint(document.CoordinateAt(0), Blue, 8); session.EndStroke(); Assert.Equal(4, document.Layers[0].Cells.Count(color => color == Blue));
        session.ClearPixels(); Assert.All(document.Layers[0].Cells, color => Assert.Equal(0u, color));
    }

    [Fact]
    public void MovingAnOverlappingSelectionIsAtomicAndUndoable()
    {
        var session = new EditorSession(new HexDocument(8, 2)); var document = session.Document;
        session.Paint(document.CoordinateAt(0), Red); session.Paint(document.CoordinateAt(1), Blue); session.EndStroke();
        session.SelectRectangle(document.CoordinateAt(0), document.CoordinateAt(1)); session.MoveSelection(1, 0);
        Assert.Equal(new uint[] { 0, Red, Blue }, document.Layers[0].Cells.Take(3));
        session.Undo(); Assert.Equal(new uint[] { Red, Blue, 0 }, document.Layers[0].Cells.Take(3));
    }

    [Fact]
    public void ClipboardPreservesAlphaAndPastesWithClipping()
    {
        var session = new EditorSession(new HexDocument(3, 2));
        session.Paint(new(0, 0), 0x80223344); session.Paint(new(1, 0), Red); session.EndStroke();
        session.SelectRectangle(new(0, 0), new(1, 0)); var clipboard = session.CopySelection();
        session.Paste(clipboard, 2, 1);
        Assert.Equal(0x80223344u, session.Document.Layers[0].Cells[5]); Assert.Single(session.Selection);
    }

    [Fact]
    public void LayersRetainIndependentCellsThroughStructuralUndo()
    {
        var session = new EditorSession(new HexDocument(5, 5));
        session.Paint(new(0, 0), Red); session.EndStroke(); session.AddLayer();
        session.Paint(new(0, 0), Blue); session.EndStroke();
        Assert.Equal(Blue, session.Document.CompositeAt(0)); Assert.Equal(Red, session.Document.Layers[0].Cells[0]);
        session.Undo(); session.Undo(); Assert.Single(session.Document.Layers);
        session.Redo(); session.Redo(); Assert.Equal(Blue, session.Document.CompositeAt(0));
        session.MoveLayer(-1); Assert.Equal(Red, session.Document.CompositeAt(0)); session.Undo(); Assert.Equal(Blue, session.Document.CompositeAt(0));
    }

    [Fact]
    public void LockedLayersRejectAllPixelEditing()
    {
        var session = new EditorSession(new HexDocument(3, 3)); session.SetLayerLocked(0, true); session.MarkSaved();
        session.Paint(new(0, 0), Red); session.EndStroke(); session.FloodFill(new(0, 0), Red); session.DrawLine(new(0, 0), new(2, 0), Red, 0);
        session.Paste(new CellClipboard([new(0, 0, Blue)]), 0, 0); session.ClearPixels();
        Assert.All(session.Document.Layers[0].Cells, color => Assert.Equal(0u, color)); Assert.False(session.IsDirty);
    }

    [Fact]
    public void CompositeRespectsLayerVisibilityOpacityAndSourceAlpha()
    {
        var document = new HexDocument(1, 1); document.Layers[0].Cells[0] = Blue;
        document.Layers.Add(new HexLayer(1) { Opacity = 128 }); document.Layers[1].Cells[0] = Red;
        Assert.Equal(0xFF80007Fu, document.CompositeAt(0));
        document.Layers[1].Visible = false; Assert.Equal(Blue, document.CompositeAt(0));
        Assert.Equal(0x80223344u, ColorMath.Over(0x80223344, 0));
    }

    [Fact]
    public void TheLastLayerCannotBeDeleted()
    {
        var session = new EditorSession(new HexDocument(1, 1)); session.DeleteLayer();
        Assert.Single(session.Document.Layers); Assert.False(session.IsDirty);
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
        Assert.Equal(Red, document.Layers[0].Cells[document.IndexOf(destination)]); Assert.Equal(0u, document.Layers[0].Cells[document.IndexOf(source)]);
        session.RotateSelection(-1); Assert.Equal(Red, document.Layers[0].Cells[document.IndexOf(source)]);
        session.Undo(); Assert.Equal(Red, document.Layers[0].Cells[document.IndexOf(destination)]);
    }

    [Fact]
    public void ANewLayerIsBlankAndDeletingOneKeepsTheRemainingCells()
    {
        var session = new EditorSession(new HexDocument(4, 4)); session.Paint(new(0, 0), Red); session.EndStroke();
        session.AddLayer();
        Assert.All(session.Document.Layers[1].Cells, color => Assert.Equal(0u, color));
        session.Paint(new(0, 0), Blue); session.EndStroke(); session.DeleteLayer();
        Assert.Single(session.Document.Layers); Assert.Equal(Red, session.Document.Layers[0].Cells[0]);
        session.Undo(); Assert.Equal(2, session.Document.Layers.Count); Assert.Equal(Blue, session.Document.Layers[1].Cells[0]);
    }

    [Fact]
    public void RotatingAnAsymmetricSelectionDoesNotDriftAtRoundingTies()
    {
        var session = new EditorSession(new HexDocument(12, 12));
        session.Paint(new(1, 4), Red); session.Paint(new(1, 5), Blue); session.EndStroke();
        var original = (uint[])session.Document.Layers[0].Cells.Clone();
        session.Selection.UnionWith(new[] { session.Document.IndexOf(new(1, 4)), session.Document.IndexOf(new(1, 5)) });
        session.RotateSelection(1); session.RotateSelection(-1);
        Assert.Equal(original, session.Document.Layers[0].Cells);
        for (var turn = 0; turn < 6; turn++) session.RotateSelection(1);
        Assert.Equal(original, session.Document.Layers[0].Cells);
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
    public void PixelUndoActivatesTheLayerThatItChanges()
    {
        var session = new EditorSession(new HexDocument(3, 2));
        session.AddLayer(); session.Paint(new(0, 0), Blue); session.EndStroke();
        session.SelectLayer(0); session.Undo();
        Assert.Equal(1, session.ActiveLayer);
        Assert.Equal(0u, session.Document.Layers[1].Cells[0]);
    }

    [Fact]
    public void InvalidCanvasSizesAndTheLayerLimitAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HexDocument(0, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HexDocument(HexDocument.MaximumDimension + 1, 20));
        var session = new EditorSession(new HexDocument(2, 2));
        while (session.Document.Layers.Count < HexDocument.MaximumLayers) session.AddLayer();
        Assert.Throws<InvalidOperationException>(session.AddLayer);
        Assert.Equal(HexDocument.MaximumLayers, session.Document.Layers.Count);
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
        Assert.Equal("height", Assert.Throws<ArgumentOutOfRangeException>(() => new HexDocument(20, HexDocument.MaximumDimension + 1)).ParamName);
    }

    [Fact]
    public void StructuralUndoRestoresWhatItChangedWithoutLosingCells()
    {
        var session = new EditorSession(new HexDocument(8, 8));
        var cell = HexGeometry.FromOffset(3, 4, HexOrientation.PointyTop);
        var index = session.Document.IndexOf(cell);
        session.Paint(cell, 0xFF112233);
        session.EndStroke();

        session.SetBackgroundColor(0xFF445566);
        session.AddLayer();
        session.SetLayerName("Second");
        session.SetLayerOpacity(128);
        session.SetLayerVisible(0, false);
        Assert.Equal(2, session.Document.Layers.Count);

        session.Undo();
        Assert.True(session.Document.Layers[0].Visible);
        session.Undo();
        Assert.Equal(255, session.Document.Layers[1].Opacity);
        session.Undo();
        Assert.NotEqual("Second", session.Document.Layers[1].Name);
        session.Undo();
        Assert.Single(session.Document.Layers);
        session.Undo();
        Assert.Equal(HexDocument.DefaultBackgroundColor, session.Document.BackgroundColor);

        // The cells were shared rather than copied by every one of those records, and are still here.
        Assert.Equal(0xFF112233u, session.Document.Layers[0].Cells[index]);
        session.Undo();
        Assert.Equal(0u, session.Document.Layers[0].Cells[index]);
    }

    [Fact]
    public void PaintingBetweenStructuralChangesUndoesInOrder()
    {
        // The case sharing cells depends on: a structural record holds the same arrays the painting after it
        // goes on writing to, and is only ever reached once that painting has been undone.
        var session = new EditorSession(new HexDocument(8, 8));
        var cell = HexGeometry.FromOffset(2, 2, HexOrientation.PointyTop);
        var index = session.Document.IndexOf(cell);

        session.AddLayer();
        session.Paint(cell, 0xFFAABBCC);
        session.EndStroke();
        Assert.Equal(0xFFAABBCCu, session.Document.Layers[1].Cells[index]);

        session.Undo();
        Assert.Equal(0u, session.Document.Layers[1].Cells[index]);
        session.Undo();
        Assert.Single(session.Document.Layers);

        session.Redo();
        Assert.Equal(2, session.Document.Layers.Count);
        session.Redo();
        Assert.Equal(0xFFAABBCCu, session.Document.Layers[1].Cells[index]);
    }

    [Fact]
    public void DeletingALayerKeepsItsCellsForUndo()
    {
        var session = new EditorSession(new HexDocument(8, 8));
        session.AddLayer();
        var cell = HexGeometry.FromOffset(1, 1, HexOrientation.PointyTop);
        var index = session.Document.IndexOf(cell);
        session.Paint(cell, 0xFF778899);
        session.EndStroke();
        session.DeleteLayer();
        Assert.Single(session.Document.Layers);
        session.Undo();
        Assert.Equal(2, session.Document.Layers.Count);
        Assert.Equal(0xFF778899u, session.Document.Layers[1].Cells[index]);
    }

    [Fact]
    public void StructuralChangesOnALargeCanvasDoNotCrowdOutHistory()
    {
        // Recording a structural change used to copy every cell twice, so on a large canvas one entry ran to
        // tens of megabytes and the history budget held only a handful. Ten should all still be there.
        var session = new EditorSession(new HexDocument(2000, 1000));
        var colors = new uint[10];
        for (var change = 0; change < colors.Length; change++)
        {
            colors[change] = 0xFF000000u | (uint)(0x010203 * (change + 1));
            session.SetBackgroundColor(colors[change]);
        }
        Assert.Equal(colors[^1], session.Document.BackgroundColor);
        for (var change = colors.Length - 1; change > 0; change--)
        {
            session.Undo();
            Assert.Equal(colors[change - 1], session.Document.BackgroundColor);
        }
        session.Undo();
        Assert.Equal(HexDocument.DefaultBackgroundColor, session.Document.BackgroundColor);
    }

    [Fact]
    public void ACanvasOfTwoThousandByOneThousandIsAllowedAndOneTooLargeIsNot()
    {
        var document = new HexDocument(2000, 1000);
        Assert.Equal(2_000_000, document.CellCount);
        // Either side may be as long as the limit, but not both: it is the cell count that costs.
        Assert.Throws<ArgumentOutOfRangeException>(() => new HexDocument(HexDocument.MaximumDimension, HexDocument.MaximumDimension));
    }

    [Fact]
    public void TheLayerLimitFollowsTheCellBudgetOnALargeCanvas()
    {
        var small = new HexDocument(16, 16);
        Assert.Equal(HexDocument.MaximumLayers, small.MaximumLayersForThisCanvas);
        var large = new HexDocument(2000, 1000);
        Assert.InRange(large.MaximumLayersForThisCanvas, 1, HexDocument.MaximumLayers);
        Assert.True((long)large.MaximumLayersForThisCanvas * large.CellCount * sizeof(uint) <= HexDocument.MaximumCellBytes);
        large.ValidateLayerCount(large.MaximumLayersForThisCanvas);
        if (large.MaximumLayersForThisCanvas < HexDocument.MaximumLayers)
            Assert.Throws<InvalidOperationException>(() => large.ValidateLayerCount(large.MaximumLayersForThisCanvas + 1));
    }

    [Fact]
    public void SelectingEveryCellOfACanvasThatIsNotAWholeNumberOfWordsStopsAtItsLastCell()
    {
        // The selection keeps a bit a cell in 64-bit words, and the last word runs past the end of the
        // canvas. Seventy cells is one word and six bits: a bit set past the sixth would be counted and
        // enumerated as a cell that does not exist.
        var selection = new CellSelection(70);
        selection.SelectAll();
        Assert.Equal(70, selection.Count);
        Assert.Equal(Enumerable.Range(0, 70), selection.ToArray());
        Assert.False(selection.Contains(70));
    }

    [Fact]
    public void ARunOfCellsAcrossAWordBoundaryIsSelectedWhole()
    {
        // A rectangle is a run along each of its rows, filled a word at a time, so the runs that start in
        // one word and end in another are the ones the masks have to get right.
        var document = new HexDocument(100, 3);
        var session = new EditorSession(document);
        session.SelectRectangle(HexGeometry.FromOffset(60, 0, document.Orientation), HexGeometry.FromOffset(70, 2, document.Orientation));
        Assert.Equal(33, session.Selection.Count);
        var expected = new List<int>();
        for (var row = 0; row < 3; row++) for (var column = 60; column <= 70; column++) expected.Add(row * 100 + column);
        Assert.Equal(expected, session.Selection.ToArray());
        Assert.False(session.Selection.Contains(59));
        Assert.False(session.Selection.Contains(71));
        Assert.True(session.Selection.TryBounds(100, out var left, out var top));
        Assert.Equal((60, 0), (left, top));
    }

    [Fact]
    public void HistoryKeepsTheMostRecentTwoHundredCommands()
    {
        var document = new HexDocument(16, 16);
        var session = new EditorSession(document);
        for (var stroke = 0; stroke < 230; stroke++)
        {
            session.Paint(document.CoordinateAt(stroke % document.CellCount), 0xFF000000u | (uint)stroke);
            session.EndStroke();
        }
        var undone = 0;
        while (session.CanUndo) { session.Undo(); undone++; }
        Assert.Equal(200, undone);
        // The thirty oldest strokes are beyond recall, so their cells keep what those strokes left.
        Assert.Equal(0xFF00001Du, document.Layers[0].Cells[29]);
        Assert.Equal(0u, document.Layers[0].Cells[30]);
    }

    [Fact]
    public void PastingWritesEveryCellOfTheClipboardIncludingTheEmptyOnes()
    {
        // A rectangular selection is copied as a block, and the block lands as a block: a cell that held
        // nothing in the copy clears what it lands on. The move and the rotation work the same way, so this
        // is the behaviour to keep or to change for all three at once.
        var document = new HexDocument(4, 1);
        var session = new EditorSession(document);
        session.Paint(document.CoordinateAt(0), Red); session.Paint(document.CoordinateAt(2), Blue); session.Paint(document.CoordinateAt(3), Blue); session.EndStroke();
        session.SelectRectangle(document.CoordinateAt(0), document.CoordinateAt(1));
        var clipboard = session.CopySelection();
        session.Paste(clipboard, 2, 0);
        Assert.Equal(new uint[] { Red, 0, Red, 0 }, document.Layers[0].Cells);
    }

    [Fact]
    public void EditingReportsOnlyTheCellsItTouchedAsDirty()
    {
        var session = new EditorSession(new HexDocument(40, 32));
        Assert.Null(session.TakeDirtyCells());
        session.Paint(HexGeometry.FromOffset(5, 6, HexOrientation.PointyTop), 0xFF112233);
        session.EndStroke();
        var dirty = session.TakeDirtyCells();
        Assert.NotNull(dirty);
        Assert.Equal((5, 6, 5, 6), (dirty!.Value.MinColumn, dirty.Value.MinRow, dirty.Value.MaxColumn, dirty.Value.MaxRow));
        // Taking it forgets it, so the canvas is not told twice.
        Assert.Null(session.TakeDirtyCells());
        // Undo puts cells back, which is a change the canvas has to be told about as well.
        session.Undo();
        Assert.NotNull(session.TakeDirtyCells());
    }

    [Fact]
    public void AnOpaqueCellOnAnOpaqueLayerCompositesToItself()
    {
        // The fast path added for compositing large canvases has to agree with the arithmetic it skips.
        Assert.Equal(0xFF112233u, ColorMath.Over(0xFF112233u, 0xFF445566u));
        Assert.Equal(0xFF445566u, ColorMath.Over(0x00112233u, 0xFF445566u));
        Assert.Equal(ColorMath.Over(0x80112233u, 0xFF445566u, 255), ColorMath.Over(0x80112233u, 0xFF445566u));
    }

    [Fact]
    public void TheDefaultPaletteFillsAFullGridOfDistinctOpaqueColors()
    {
        Assert.Equal(256, HexDocument.DefaultPalette.Length);
        Assert.Equal(0, HexDocument.DefaultPalette.Length % HexDocument.PaletteColumns);
        Assert.Equal(HexDocument.DefaultPalette.Length, HexDocument.DefaultPalette.Distinct().Count());
        Assert.All(HexDocument.DefaultPalette, color => Assert.Equal(255u, color >> 24));
        Assert.Equal(HexDocument.DefaultPalette, new HexDocument(1, 1).Palette);
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
    public void ANewCanvasStartsOnOpaqueWhiteAndTheBackgroundStaysOpaque()
    {
        var session = new EditorSession(new HexDocument(3, 3));
        Assert.Equal(0xFFFFFFFFu, session.Document.BackgroundColor);
        session.SetBackgroundColor(0x40336699);
        Assert.Equal(0xFF336699u, session.Document.BackgroundColor);
        Assert.Equal("Background color", session.UndoLabel);
        session.Undo();
        Assert.Equal(0xFFFFFFFFu, session.Document.BackgroundColor);
        session.Redo();
        Assert.Equal(0xFF336699u, session.Document.BackgroundColor);
        // Setting the color it already holds is not an edit, so it leaves nothing on the history.
        session.MarkSaved();
        session.SetBackgroundColor(0xFF336699);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void AManifestWithoutABackgroundColorLoadsOnWhite()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".hexel");
        try
        {
            var document = new HexDocument(2, 2) { BackgroundColor = 0xFF102030 };
            ProjectFile.Save(document, path);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("Document.json")!;
                string manifest;
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) manifest = reader.ReadToEnd();
                var without = System.Text.RegularExpressions.Regex.Replace(manifest, @"\s*""BackgroundColor"": \d+,", "");
                Assert.DoesNotContain("BackgroundColor", without);
                entry.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("Document.json").Open(), Encoding.UTF8);
                writer.Write(without);
            }
            Assert.Equal(HexDocument.DefaultBackgroundColor, ProjectFile.Load(path).BackgroundColor);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ArchiveRoundTripPreservesEveryLayerAndItsMetadata()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".hexel");
        try
        {
            var session = new EditorSession(new HexDocument(7, 9, HexOrientation.FlatTop) { Name = "Test scene" });
            session.AddLayer(); session.SetLayerName("Highlights"); session.SetLayerOpacity(123); session.SetBackgroundColor(0xFF2D4F1E);
            session.Paint(session.Document.CoordinateAt(20), 0x80223344); session.EndStroke(); session.AddPaletteColor(0xAABBCCDD);
            ProjectFile.Save(session.Document, path); var loaded = ProjectFile.Load(path);
            Assert.Equal(session.Document.Name, loaded.Name); Assert.Equal(HexOrientation.FlatTop, loaded.Orientation);
            Assert.Equal("Highlights", loaded.Layers[1].Name); Assert.Equal(123, loaded.Layers[1].Opacity);
            Assert.Equal(session.Document.Palette, loaded.Palette);
            Assert.Equal(0xFF2D4F1Eu, loaded.BackgroundColor);
            for (var layer = 0; layer < 2; layer++) Assert.Equal(session.Document.Layers[layer].Cells, loaded.Layers[layer].Cells);
            // Saving over an existing file is also a complete atomic replacement.
            ProjectFile.Save(loaded, path); Assert.Equal(2, ProjectFile.Load(path).Layers.Count);
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
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update)) archive.GetEntry("Cells/0.bin")!.Delete();
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
    // The untampered manifest only fails on its absent cell buffers, which proves the tampered values below are what the reader rejects.
    [InlineData(ValidManifest)]
    [InlineData(ValidManifest, "\"Format\":1", "\"Format\":2")]
    [InlineData(ValidManifest, "\"Layers\":[{\"Name\":\"Layer 1\",\"Visible\":true,\"Locked\":false,\"Opacity\":255}]", "\"Layers\":[]")]
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
         "Palette":[4278190080]}
        """;
}
