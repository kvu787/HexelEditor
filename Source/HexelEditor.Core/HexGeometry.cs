namespace HexelEditor.Core;

public enum HexOrientation { PointyTop, FlatTop }

public readonly record struct HexCoordinate(int Q, int R)
{
    public int S => -Q - R;
    public static readonly HexCoordinate[] Directions = [new(1, 0), new(1, -1), new(0, -1), new(-1, 0), new(-1, 1), new(0, 1)];
    public static HexCoordinate operator +(HexCoordinate left, HexCoordinate right) => new(left.Q + right.Q, left.R + right.R);
    public int DistanceTo(HexCoordinate other) => (Math.Abs(Q - other.Q) + Math.Abs(R - other.R) + Math.Abs(S - other.S)) / 2;

    public static HexCoordinate Round(double q, double r)
    {
        var s = -q - r;
        var roundedQ = (int)Math.Round(q);
        var roundedR = (int)Math.Round(r);
        var roundedS = (int)Math.Round(s);
        var differenceQ = Math.Abs(roundedQ - q);
        var differenceR = Math.Abs(roundedR - r);
        var differenceS = Math.Abs(roundedS - s);
        if (differenceQ > differenceR && differenceQ > differenceS) roundedQ = -roundedR - roundedS;
        else if (differenceR > differenceS) roundedR = -roundedQ - roundedS;
        return new(roundedQ, roundedR);
    }

    public IEnumerable<HexCoordinate> LineTo(HexCoordinate end)
    {
        var distance = DistanceTo(end);
        for (var index = 0; index <= distance; index++)
        {
            var fraction = distance == 0 ? 0 : (double)index / distance;
            // A consistent tiny nudge resolves lines that run exactly along cell edges.
            yield return Round(Q + (end.Q - Q) * fraction + 0.000001, R + (end.R - R) * fraction + 0.000001);
        }
    }

    public IEnumerable<HexCoordinate> Disk(int radius)
    {
        for (var q = -radius; q <= radius; q++)
            for (var r = Math.Max(-radius, -q - radius); r <= Math.Min(radius, -q + radius); r++)
                yield return new(Q + q, R + r);
    }
}

public static class HexGeometry
{
    public static HexCoordinate FromOffset(int column, int row, HexOrientation orientation) => orientation == HexOrientation.PointyTop
        ? new(column - (row - (row & 1)) / 2, row)
        : new(column, row - (column - (column & 1)) / 2);

    public static (int Column, int Row) ToOffset(HexCoordinate coordinate, HexOrientation orientation) => orientation == HexOrientation.PointyTop
        ? (coordinate.Q + (coordinate.R - (coordinate.R & 1)) / 2, coordinate.R)
        : (coordinate.Q, coordinate.R + (coordinate.Q - (coordinate.Q & 1)) / 2);

    public static (double X, double Y) Center(HexCoordinate coordinate, HexOrientation orientation, double radius = 1) => orientation == HexOrientation.PointyTop
        ? (radius * Math.Sqrt(3) * (coordinate.Q + coordinate.R / 2.0), radius * 1.5 * coordinate.R)
        : (radius * 1.5 * coordinate.Q, radius * Math.Sqrt(3) * (coordinate.R + coordinate.Q / 2.0));

    public static HexCoordinate FromPoint(double x, double y, HexOrientation orientation, double radius = 1) => orientation == HexOrientation.PointyTop
        ? HexCoordinate.Round((Math.Sqrt(3) / 3 * x - y / 3) / radius, 2.0 / 3 * y / radius)
        : HexCoordinate.Round(2.0 / 3 * x / radius, (-x / 3 + Math.Sqrt(3) / 3 * y) / radius);
}
