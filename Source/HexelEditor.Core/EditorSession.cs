namespace HexelEditor.Core;

[Flags]
public enum EditorChange { Pixels = 1, Structure = 2, Selection = 4, History = 8, All = 15 }

public sealed class EditorSession
{
    public const int MaximumBrushRadius = 8;
    private const long HistoryBudget = 64 * 1024 * 1024;
    private readonly List<HistoryItem> undo = [];
    private readonly List<HistoryItem> redo = [];
    private Dictionary<int, uint>? stroke;
    private int strokeLayer;
    private SelectionState? strokeSelection;
    private HexCoordinate? selectionPivot;
    private long revision;
    private long nextRevision;
    private long savedRevision;
    private long historyBytes;
    public HexDocument Document { get; private set; }
    public int ActiveLayer { get; private set; }
    public HashSet<int> Selection { get; } = [];
    public bool IsDirty => revision != savedRevision || stroke?.Count > 0;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool CanPaint => !Document.Layers[ActiveLayer].Locked;
    public string UndoLabel => undo.Count > 0 ? undo[^1].Label : "";
    public string RedoLabel => redo.Count > 0 ? redo[^1].Label : "";
    public event Action<EditorChange>? Changed;

    public EditorSession(HexDocument document) => Document = document;
    public void Notify(EditorChange change) => Changed?.Invoke(change);
    public void MarkSaved() { savedRevision = revision; Notify(EditorChange.History); }
    public void SelectLayer(int index) { EndStroke(); ActiveLayer = Math.Clamp(index, 0, Document.Layers.Count - 1); Notify(EditorChange.Structure); }

    public void BeginStroke(bool captureSelection = false)
    {
        if (stroke != null) return;
        stroke = [];
        strokeLayer = ActiveLayer;
        strokeSelection = captureSelection ? CaptureSelection() : null;
    }

    public void Paint(HexCoordinate center, uint color, int radius = 0)
    {
        if (!CanPaint) return;
        BeginStroke();
        foreach (var coordinate in center.Disk(Math.Clamp(radius, 0, MaximumBrushRadius))) SetPixel(Document.IndexOf(coordinate), color);
        Notify(EditorChange.Pixels);
    }

    private void SetPixel(int index, uint color)
    {
        if (index < 0 || index >= Document.CellCount || (Selection.Count > 0 && !Selection.Contains(index))) return;
        var cells = Document.Layers[strokeLayer].Cells;
        if (cells[index] == color) return;
        stroke!.TryAdd(index, cells[index]);
        cells[index] = color;
        MarkCellDirty(index);
    }

    // The smallest rectangle of cells, in columns and rows, that has changed since a reader last took it.
    // The canvas keeps a raster of the artwork and recomposites only this much of it, so a brush stroke costs
    // the handful of cells it touched rather than every cell in the document. On a canvas of two million
    // cells that is the difference between a stroke that keeps up and one that does not.
    private int dirtyMinColumn = int.MaxValue, dirtyMinRow = int.MaxValue, dirtyMaxColumn = -1, dirtyMaxRow = -1;

    public void MarkCellDirty(int index)
    {
        if (index < 0 || index >= Document.CellCount) return;
        var column = index % Document.Width;
        var row = index / Document.Width;
        if (column < dirtyMinColumn) dirtyMinColumn = column;
        if (column > dirtyMaxColumn) dirtyMaxColumn = column;
        if (row < dirtyMinRow) dirtyMinRow = row;
        if (row > dirtyMaxRow) dirtyMaxRow = row;
    }

    public void MarkAllCellsDirty()
    {
        dirtyMinColumn = 0;
        dirtyMinRow = 0;
        dirtyMaxColumn = Document.Width - 1;
        dirtyMaxRow = Document.Height - 1;
    }

    // Returns what has changed and forgets it, so two readers would not both be served. The canvas is the
    // only reader.
    public (int MinColumn, int MinRow, int MaxColumn, int MaxRow)? TakeDirtyCells()
    {
        if (dirtyMaxColumn < 0 || dirtyMaxRow < 0) return null;
        var region = (dirtyMinColumn, dirtyMinRow, dirtyMaxColumn, dirtyMaxRow);
        dirtyMinColumn = int.MaxValue;
        dirtyMinRow = int.MaxValue;
        dirtyMaxColumn = -1;
        dirtyMaxRow = -1;
        return region;
    }

    public void EndStroke(string label = "Brush stroke")
    {
        if (stroke == null) return;
        var cells = Document.Layers[strokeLayer].Cells;
        var edits = stroke.Where(pair => pair.Value != cells[pair.Key]).Select(pair => new PixelDelta(pair.Key, pair.Value, cells[pair.Key])).ToArray();
        stroke = null;
        if (edits.Length > 0) { Commit(label, new PixelChange(strokeLayer, edits, strokeSelection, strokeSelection == null ? null : CaptureSelection())); Notify(EditorChange.Pixels | EditorChange.History); }
        else Notify(EditorChange.History);
        strokeSelection = null;
    }

    public void FloodFill(HexCoordinate coordinate, uint color)
    {
        EndStroke();
        var start = Document.IndexOf(coordinate);
        if (!CanPaint || start < 0 || (Selection.Count > 0 && !Selection.Contains(start))) return;
        var cells = Document.Layers[ActiveLayer].Cells;
        var target = cells[start];
        if (target == color) return;
        BeginStroke();
        var visited = new HashSet<int> { start };
        var pending = new Queue<int>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out var index))
        {
            SetPixel(index, color);
            foreach (var direction in HexCoordinate.Directions)
            {
                var neighbor = Document.IndexOf(Document.CoordinateAt(index) + direction);
                if (neighbor >= 0 && cells[neighbor] == target && (Selection.Count == 0 || Selection.Contains(neighbor)) && visited.Add(neighbor)) pending.Enqueue(neighbor);
            }
        }
        EndStroke("Flood fill");
        Notify(EditorChange.Pixels);
    }

    public void DrawLine(HexCoordinate start, HexCoordinate end, uint color, int radius)
    {
        if (!CanPaint) return;
        EndStroke();
        BeginStroke();
        foreach (var point in start.LineTo(end))
            foreach (var cell in point.Disk(radius)) SetPixel(Document.IndexOf(cell), color);
        EndStroke("Line");
        Notify(EditorChange.Pixels);
    }

    public void DrawRectangle(HexCoordinate start, HexCoordinate end, uint color, bool filled)
    {
        if (!CanPaint) return;
        EndStroke();
        BeginStroke();
        var (columnA, rowA) = HexGeometry.ToOffset(start, Document.Orientation);
        var (columnB, rowB) = HexGeometry.ToOffset(end, Document.Orientation);
        var left = Math.Max(0, Math.Min(columnA, columnB));
        var right = Math.Min(Document.Width - 1, Math.Max(columnA, columnB));
        var top = Math.Max(0, Math.Min(rowA, rowB));
        var bottom = Math.Min(Document.Height - 1, Math.Max(rowA, rowB));
        for (var row = top; row <= bottom; row++)
            for (var column = left; column <= right; column++)
                if (filled || row == top || row == bottom || column == left || column == right) SetPixel(row * Document.Width + column, color);
        EndStroke("Rectangle");
        Notify(EditorChange.Pixels);
    }

    public void SelectRectangle(HexCoordinate start, HexCoordinate end)
    {
        var (columnA, rowA) = HexGeometry.ToOffset(start, Document.Orientation);
        var (columnB, rowB) = HexGeometry.ToOffset(end, Document.Orientation);
        Selection.Clear();
        selectionPivot = null;
        var left = Math.Max(0, Math.Min(columnA, columnB));
        var right = Math.Min(Document.Width - 1, Math.Max(columnA, columnB));
        var top = Math.Max(0, Math.Min(rowA, rowB));
        var bottom = Math.Min(Document.Height - 1, Math.Max(rowA, rowB));
        for (var row = top; row <= bottom; row++)
            for (var column = left; column <= right; column++) Selection.Add(row * Document.Width + column);
        Notify(EditorChange.Selection);
    }

    public void SelectAll()
    {
        EndStroke();
        Selection.Clear();
        selectionPivot = null;
        Selection.EnsureCapacity(Document.CellCount);
        for (var index = 0; index < Document.CellCount; index++) Selection.Add(index);
        Notify(EditorChange.Selection);
    }
    public void ClearSelection() { EndStroke(); Selection.Clear(); selectionPivot = null; Notify(EditorChange.Selection); }

    public void ClearPixels()
    {
        if (!CanPaint) return;
        EndStroke();
        BeginStroke();
        foreach (var index in Selection.Count > 0 ? Selection.ToArray() : Enumerable.Range(0, Document.CellCount)) SetPixel(index, 0);
        EndStroke("Clear cells");
        Notify(EditorChange.Pixels);
    }

    public CellClipboard CopySelection()
    {
        var indices = Selection.Count > 0 ? Selection.ToArray() : Enumerable.Range(0, Document.CellCount).ToArray();
        var left = indices.Min(index => index % Document.Width);
        var top = indices.Min(index => index / Document.Width);
        var cells = Document.Layers[ActiveLayer].Cells;
        return new CellClipboard(indices.Select(index => new ClipboardCell(index % Document.Width - left, index / Document.Width - top, cells[index])).ToArray());
    }

    public void Paste(CellClipboard clipboard, int column, int row)
    {
        if (!CanPaint) return;
        EndStroke();
        BeginStroke(captureSelection: true);
        Selection.Clear();
        selectionPivot = null;
        PaintThenSelect(CellsLandingAt(clipboard, column, row));
        EndStroke("Paste");
        Notify(EditorChange.Pixels | EditorChange.Selection);
    }

    public void MoveSelection(int columns, int rows)
    {
        if (!CanPaint || Selection.Count == 0) return;
        var clipboard = CopySelection();
        var left = Selection.Min(index => index % Document.Width);
        var top = Selection.Min(index => index / Document.Width);
        EndStroke();
        BeginStroke(captureSelection: true);
        foreach (var index in Selection) SetPixel(index, 0);
        Selection.Clear();
        selectionPivot = null;
        PaintThenSelect(CellsLandingAt(clipboard, left + columns, top + rows));
        EndStroke("Move selection");
        Notify(EditorChange.Pixels | EditorChange.Selection);
    }

    // Where a clipboard's cells land with its top left corner at this column and row, dropping whatever
    // would fall outside the canvas.
    private (int Index, uint Color)[] CellsLandingAt(CellClipboard clipboard, int column, int row) =>
        clipboard.Cells
            .Select(cell => (Column: column + cell.Column, Row: row + cell.Row, cell.Color))
            .Where(cell => cell.Column >= 0 && cell.Column < Document.Width && cell.Row >= 0 && cell.Row < Document.Height)
            .Select(cell => (cell.Row * Document.Width + cell.Column, cell.Color))
            .ToArray();

    // Paints the cells, then selects them, and never in one pass: SetPixel refuses a cell outside a
    // selection that holds anything, so a selection grown as the painting went along would turn away every
    // cell it did not hold yet and the paste would come out as its first cell alone.
    private void PaintThenSelect((int Index, uint Color)[] cells)
    {
        foreach (var (index, color) in cells) SetPixel(index, color);
        foreach (var (index, _) in cells) Selection.Add(index);
    }

    public void AddLayer()
    {
        Document.ValidateLayerCount(Document.Layers.Count + 1);
        ChangeDocument("Add layer", () =>
        {
            var index = ActiveLayer + 1;
            Document.Layers.Insert(index, new HexLayer(Document.CellCount) { Name = $"Layer {Document.Layers.Count + 1}" });
            ActiveLayer = index;
        });
    }

    public void RotateSelection(int steps)
    {
        if (!CanPaint || Selection.Count == 0) return;
        EndStroke();
        var selected = Selection.Select(index => (Coordinate: Document.CoordinateAt(index), Color: Document.Layers[ActiveLayer].Cells[index])).ToArray();
        var pivot = selectionPivot ?? HexCoordinate.Round(selected.Average(cell => cell.Coordinate.Q), selected.Average(cell => cell.Coordinate.R));
        var turns = (steps % 6 + 6) % 6;
        if (turns == 0) return;
        BeginStroke(captureSelection: true);
        foreach (var index in Selection) SetPixel(index, 0);
        Selection.Clear();
        var destination = new HashSet<int>();
        foreach (var cell in selected)
        {
            var relative = new HexCoordinate(cell.Coordinate.Q - pivot.Q, cell.Coordinate.R - pivot.R);
            for (var turn = 0; turn < turns; turn++) relative = new(-relative.R, relative.Q + relative.R);
            var index = Document.IndexOf(relative + pivot);
            if (index >= 0) { SetPixel(index, cell.Color); destination.Add(index); }
        }
        Selection.UnionWith(destination);
        selectionPivot = pivot;
        EndStroke("Rotate selection");
        Notify(EditorChange.Pixels | EditorChange.Selection);
    }

    public void DeleteLayer()
    {
        if (Document.Layers.Count == 1) return;
        ChangeDocument("Delete layer", () =>
        {
            Document.Layers.RemoveAt(ActiveLayer);
            ActiveLayer = Math.Min(ActiveLayer, Document.Layers.Count - 1);
        });
    }

    public void MoveLayer(int offset)
    {
        var target = ActiveLayer + offset;
        if (target < 0 || target >= Document.Layers.Count) return;
        ChangeDocument("Reorder layer", () =>
        {
            (Document.Layers[target], Document.Layers[ActiveLayer]) = (Document.Layers[ActiveLayer], Document.Layers[target]);
            ActiveLayer = target;
        });
    }

    public void SetLayerName(string name) { if (!string.IsNullOrWhiteSpace(name) && Document.Layers[ActiveLayer].Name != name.Trim()) ChangeDocument("Rename layer", () => Document.Layers[ActiveLayer].Name = name.Trim()); }
    public void SetLayerVisible(int index, bool visible) { if (Document.Layers[index].Visible != visible) ChangeDocument("Layer visibility", () => Document.Layers[index].Visible = visible); }
    public void SetLayerLocked(int index, bool locked) { if (Document.Layers[index].Locked != locked) ChangeDocument("Layer lock", () => Document.Layers[index].Locked = locked); }
    public void SetLayerOpacity(byte opacity) { if (Document.Layers[ActiveLayer].Opacity != opacity) ChangeDocument("Layer opacity", () => Document.Layers[ActiveLayer].Opacity = opacity); }
    public void SetBackgroundColor(uint color) { var opaque = color | 0xFF000000; if (Document.BackgroundColor != opaque) ChangeDocument("Background color", () => Document.BackgroundColor = opaque); }
    public bool AddPaletteColor(uint color)
    {
        if (Document.Palette.Contains(color) || Document.Palette.Count >= HexDocument.MaximumPaletteColors) return false;
        ChangeDocument("Add palette color", () => Document.Palette.Add(color));
        return true;
    }

    private void ChangeDocument(string label, Action change)
    {
        EndStroke();
        var before = new EditorState(Document.CloneStructure(), ActiveLayer);
        change();
        var after = new EditorState(Document.CloneStructure(), ActiveLayer);
        Commit(label, new DocumentChange(before, after));
        Notify(EditorChange.All);
    }

    private void Commit(string label, IChange change)
    {
        foreach (var item in redo) historyBytes -= item.Change.Bytes;
        redo.Clear();
        var afterRevision = ++nextRevision;
        undo.Add(new(label, change, revision, afterRevision));
        revision = afterRevision;
        historyBytes += change.Bytes;
        while (undo.Count > 1 && (historyBytes > HistoryBudget || undo.Count > 200)) { historyBytes -= undo[0].Change.Bytes; undo.RemoveAt(0); }
    }

    public void Undo()
    {
        EndStroke();
        if (undo.Count == 0) return;
        var item = undo[^1]; undo.RemoveAt(undo.Count - 1);
        item.Change.Apply(this, false); revision = item.BeforeRevision; redo.Add(item);
        Notify(EditorChange.All);
    }

    public void Redo()
    {
        EndStroke();
        if (redo.Count == 0) return;
        var item = redo[^1]; redo.RemoveAt(redo.Count - 1);
        item.Change.Apply(this, true); revision = item.AfterRevision; undo.Add(item);
        Notify(EditorChange.All);
    }

    private interface IChange { long Bytes { get; } void Apply(EditorSession session, bool forward); }
    private sealed record HistoryItem(string Label, IChange Change, long BeforeRevision, long AfterRevision);
    private sealed record EditorState(HexDocument Document, int Layer);
    private sealed record SelectionState(int[] Indices, HexCoordinate? Pivot);
    private SelectionState CaptureSelection() => new(Selection.ToArray(), selectionPivot);
    private readonly record struct PixelDelta(int Index, uint Before, uint After);
    private sealed record PixelChange(int Layer, PixelDelta[] Edits, SelectionState? BeforeSelection, SelectionState? AfterSelection) : IChange
    {
        public long Bytes => 12L * Edits.Length + 4L * ((BeforeSelection?.Indices.Length ?? 0) + (AfterSelection?.Indices.Length ?? 0));
        public void Apply(EditorSession session, bool forward)
        {
            session.ActiveLayer = Layer;
            foreach (var edit in Edits)
            {
                session.Document.Layers[Layer].Cells[edit.Index] = forward ? edit.After : edit.Before;
                session.MarkCellDirty(edit.Index);
            }
            var selection = forward ? AfterSelection : BeforeSelection;
            if (selection != null) { session.Selection.Clear(); session.Selection.UnionWith(selection.Indices); session.selectionPivot = selection.Pivot; }
        }
    }
    private sealed record DocumentChange(EditorState Before, EditorState After) : IChange
    {
        // Structure only: a layer's properties and the palette. The cells are shared, not copied, so they are
        // not this record's to account for.
        public long Bytes => StructureBytes(Before) + StructureBytes(After);

        private static long StructureBytes(EditorState state) =>
            128L + 64L * state.Document.Layers.Count + 4L * state.Document.Palette.Count;

        public void Apply(EditorSession session, bool forward)
        {
            var state = forward ? After : Before;
            session.Document = state.Document.CloneStructure(); session.ActiveLayer = state.Layer;
            session.Selection.Clear(); session.selectionPivot = null;
            // The cells are the same arrays, but which layers there are, and how they look, is not.
            session.MarkAllCellsDirty();
        }
    }
}

public sealed record CellClipboard(ClipboardCell[] Cells);
public readonly record struct ClipboardCell(int Column, int Row, uint Color);
