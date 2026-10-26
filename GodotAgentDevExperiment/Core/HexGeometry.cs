namespace Experiment.Core;

/// <summary>A cell of a pointy-top hexagonal grid, addressed in "odd-r" offset coordinates: rows are
/// horizontal, and every odd row is shifted right by half a cell.</summary>
public readonly record struct HexCell(int Column, int Row);

/// <summary>Axial coordinates of the same grid. Rounding and neighbor arithmetic are simplest here, so
/// everything converts through this representation.</summary>
public readonly record struct AxialCell(int Q, int R);

/// <summary>Pointy-top hexagonal grid mathematics on the ground plane, with no engine dependency. The plane's
/// first axis runs right and its second axis runs toward the viewer, which is Godot's X and Z. A cell's
/// radius is the distance from its center to a corner. The shader that draws the ground follows the same
/// formulas, so a cell picked here is the cell drawn there.</summary>
public static class HexGeometry
{
    private static readonly double SquareRootOfThree = Math.Sqrt(3.0);

    public static AxialCell ToAxial(HexCell cell)
    {
        return new AxialCell(cell.Column - (cell.Row - (cell.Row & 1)) / 2, cell.Row);
    }

    public static HexCell ToOffset(AxialCell cell)
    {
        return new HexCell(cell.Q + (cell.R - (cell.R & 1)) / 2, cell.R);
    }

    /// <summary>The center of a cell on the ground plane.</summary>
    public static (double X, double Z) CellCenter(HexCell cell, double radius)
    {
        AxialCell axial = ToAxial(cell);
        return (radius * SquareRootOfThree * (axial.Q + axial.R / 2.0), radius * 1.5 * axial.R);
    }

    /// <summary>The cell containing a point on the ground plane. Points outside the grid still map to a
    /// cell, with negative or large coordinates, so a caller decides what the grid's bounds are.</summary>
    public static HexCell CellAt(double x, double z, double radius)
    {
        double q = (SquareRootOfThree / 3.0 * x - z / 3.0) / radius;
        double r = (2.0 / 3.0 * z) / radius;
        return ToOffset(RoundAxial(q, r));
    }

    /// <summary>Rounds fractional axial coordinates to the nearest cell, through cube coordinates so that the
    /// result is the nearest center rather than the nearest on each axis separately.</summary>
    public static AxialCell RoundAxial(double q, double r)
    {
        double s = -q - r;
        double roundedQ = Math.Round(q);
        double roundedR = Math.Round(r);
        double roundedS = Math.Round(s);
        double differenceQ = Math.Abs(roundedQ - q);
        double differenceR = Math.Abs(roundedR - r);
        double differenceS = Math.Abs(roundedS - s);
        if (differenceQ > differenceR && differenceQ > differenceS)
        {
            roundedQ = -roundedR - roundedS;
        }
        else if (differenceR > differenceS)
        {
            roundedR = -roundedQ - roundedS;
        }
        return new AxialCell((int)roundedQ, (int)roundedR);
    }

    private static readonly AxialCell[] AxialNeighborOffsets =
    [
        new(1, 0), new(1, -1), new(0, -1), new(-1, 0), new(-1, 1), new(0, 1),
    ];

    public static IEnumerable<HexCell> Neighbors(HexCell cell)
    {
        AxialCell axial = ToAxial(cell);
        foreach (AxialCell offset in AxialNeighborOffsets)
        {
            yield return ToOffset(new AxialCell(axial.Q + offset.Q, axial.R + offset.R));
        }
    }

    /// <summary>Rotates a footprint cell about the origin cell by sixty-degree steps, counterclockwise when
    /// seen from above with the second axis pointing toward the viewer.</summary>
    public static AxialCell Rotate(AxialCell cell, int sixtyDegreeSteps)
    {
        int steps = ((sixtyDegreeSteps % 6) + 6) % 6;
        int q = cell.Q;
        int r = cell.R;
        int s = -q - r;
        for (int i = 0; i < steps; i++)
        {
            (q, r, s) = (-r, -s, -q);
        }
        return new AxialCell(q, r);
    }
}
