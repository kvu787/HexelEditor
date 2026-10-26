using Experiment.Core;
using Xunit;

namespace Experiment.Core.Tests;

public class HexGeometryTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 3)]
    [InlineData(7, 4)]
    [InlineData(-3, -5)]
    [InlineData(40, 39)]
    public void OffsetAndAxialRoundTrip(int column, int row)
    {
        HexCell cell = new(column, row);
        Assert.Equal(cell, HexGeometry.ToOffset(HexGeometry.ToAxial(cell)));
    }

    [Theory]
    [InlineData(0, 0, 1.0)]
    [InlineData(5, 3, 1.0)]
    [InlineData(2, 7, 0.5)]
    [InlineData(-4, 2, 2.0)]
    public void CellCenterIsInsideItsOwnCell(int column, int row, double radius)
    {
        HexCell cell = new(column, row);
        (double x, double z) = HexGeometry.CellCenter(cell, radius);
        Assert.Equal(cell, HexGeometry.CellAt(x, z, radius));
    }

    [Fact]
    public void PointsNearTheEdgeStillRoundToTheNearestCenter()
    {
        // Just inside the right edge of cell (3, 2): the center plus most of the inradius along X.
        (double x, double z) = HexGeometry.CellCenter(new HexCell(3, 2), 1.0);
        double inradius = Math.Sqrt(3.0) / 2.0;
        Assert.Equal(new HexCell(3, 2), HexGeometry.CellAt(x + inradius * 0.95, z, 1.0));
        Assert.Equal(new HexCell(4, 2), HexGeometry.CellAt(x + inradius * 1.05, z, 1.0));
    }

    [Fact]
    public void OddRowsAreShiftedRight()
    {
        (double evenX, _) = HexGeometry.CellCenter(new HexCell(2, 2), 1.0);
        (double oddX, double oddZ) = HexGeometry.CellCenter(new HexCell(2, 3), 1.0);
        Assert.Equal(Math.Sqrt(3.0) / 2.0, oddX - evenX, 9);
        Assert.Equal(4.5, oddZ, 9);
    }

    [Fact]
    public void EveryCellHasSixDistinctNeighborsAtOneRadiusOfSpacing()
    {
        HexCell cell = new(5, 5);
        HexCell[] neighbors = HexGeometry.Neighbors(cell).ToArray();
        Assert.Equal(6, neighbors.Distinct().Count());
        (double x, double z) = HexGeometry.CellCenter(cell, 1.0);
        foreach (HexCell neighbor in neighbors)
        {
            (double nx, double nz) = HexGeometry.CellCenter(neighbor, 1.0);
            Assert.Equal(Math.Sqrt(3.0), Math.Sqrt((nx - x) * (nx - x) + (nz - z) * (nz - z)), 9);
        }
    }

    [Fact]
    public void SixRotationsReturnToTheStart()
    {
        AxialCell cell = new(2, -1);
        Assert.Equal(cell, HexGeometry.Rotate(cell, 6));
        Assert.Equal(cell, HexGeometry.Rotate(HexGeometry.Rotate(cell, 2), -2));
        Assert.NotEqual(cell, HexGeometry.Rotate(cell, 1));
    }
}
