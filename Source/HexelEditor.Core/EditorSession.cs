namespace HexelEditor.Core;

// What a change touched, so a listener does only the work that change calls for. Pixels: cells were
// written. Structure: the layer list, the active layer, the palette or the background changed, so the panels
// that show them are stale. Selection and History say what they say. Artwork: the composited look of cells
// changed without the cells themselves changing -- a layer hidden, reordered or made see-through, a new
// background -- so a cache of composited colour is stale from end to end. Structure without Artwork is the
// common case: choosing a layer, renaming one, locking one or adding a palette colour changes what the panels
// show and nothing about what the canvas shows, and used to cost a whole-canvas recomposite and re-upload.
[Flags]
public enum EditorChange { Pixels = 1, Structure = 2, Selection = 4, History = 8, Artwork = 16, All = 31 }

public sealed class EditorSession
{
    public const int MaximumBrushRadius = 8;
    private const long HistoryBudget = 64 * 1024 * 1024;
    private readonly List<HistoryItem> undo = [];
    private readonly List<HistoryItem> redo = [];
    private StrokeRecord? strokeRecord;
    private bool stroking;
    private int strokeLayer;
    private SelectionState? strokeSelection;
    private HexCoordinate? selectionPivot;
    private long revision;
    private long nextRevision;
    private long savedRevision;
    private long historyBytes;
    public HexDocument Document { get; private set; }
    public int ActiveLayer { get; private set; }
    public CellSelection Selection { get; }
    public bool IsDirty => revision != savedRevision || (stroking && strokeRecord!.Count > 0);
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool CanPaint => !Document.Layers[ActiveLayer].Locked;
    public string UndoLabel => undo.Count > 0 ? undo[^1].Label : "";
    public string RedoLabel => redo.Count > 0 ? redo[^1].Label : "";
    public event Action<EditorChange>? Changed;

    public EditorSession(HexDocument document)
    {
        Document = document;
        Selection = new CellSelection(document.CellCount);
    }
    public void Notify(EditorChange change) => Changed?.Invoke(change);
    public void MarkSaved() { savedRevision = revision; Notify(EditorChange.History); }
    public void SelectLayer(int index) { EndStroke(); ActiveLayer = Math.Clamp(index, 0, Document.Layers.Count - 1); Notify(EditorChange.Structure); }

    public void BeginStroke(bool captureSelection = false)
    {
        if (stroking) return;
        (strokeRecord ??= new StrokeRecord(Document.CellCount)).Reset();
        stroking = true;
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
        strokeRecord!.Record(index, cells[index]);
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

    // Ends the stroke and reports it, together with whatever else the operation ending it changed, so that
    // an operation is reported once. A flood fill used to report itself twice, once as the stroke ending and
    // once as pixels, and every report is a header rebuild and a frame for the window.
    public void EndStroke(string label = "Brush stroke", EditorChange alongside = 0)
    {
        if (!stroking) return;
        var cells = Document.Layers[strokeLayer].Cells;
        var edits = strokeRecord!.Edits(cells);
        stroking = false;
        if (edits.Length > 0) { Commit(label, new PixelChange(strokeLayer, edits, strokeSelection, strokeSelection == null ? null : CaptureSelection())); Notify(EditorChange.Pixels | EditorChange.History | alongside); }
        else Notify(EditorChange.History | alongside);
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
        // A bit a cell for where the fill has already been, and the cell's coordinate worked out once
        // rather than once per neighbour: six conversions a cell became one, and on a full canvas that is
        // twelve million of them saved.
        var visited = new CellSelection(Document.CellCount);
        visited.Add(start);
        var pending = new Queue<int>();
        pending.Enqueue(start);
        int width = Document.Width, height = Document.Height;
        var pointyTop = Document.Orientation == HexOrientation.PointyTop;
        var selecting = Selection.Count > 0;
        while (pending.TryDequeue(out var index))
        {
            SetPixel(index, color);
            // The neighbours in columns and rows directly, from the parity of the line the cell is on,
            // rather than through axial coordinates and back. The round trip was most of what a fill cost.
            var column = index % width;
            var row = index / width;
            foreach (var (deltaColumn, deltaRow) in HexGeometry.NeighborOffsets(Document.Orientation, pointyTop ? row : column))
            {
                var neighborColumn = column + deltaColumn;
                var neighborRow = row + deltaRow;
                if ((uint)neighborColumn >= (uint)width || (uint)neighborRow >= (uint)height) continue;
                var neighbor = neighborRow * width + neighborColumn;
                if (cells[neighbor] == target && (!selecting || Selection.Contains(neighbor)) && visited.Add(neighbor)) pending.Enqueue(neighbor);
            }
        }
        EndStroke("Flood fill");
    }

    public void DrawLine(HexCoordinate start, HexCoordinate end, uint color, int radius)
    {
        if (!CanPaint) return;
        EndStroke();
        BeginStroke();
        foreach (var point in start.LineTo(end))
            foreach (var cell in point.Disk(radius)) SetPixel(Document.IndexOf(cell), color);
        EndStroke("Line");
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
        for (var row = top; row <= bottom; row++) Selection.AddRange(row * Document.Width + left, row * Document.Width + right);
        Notify(EditorChange.Selection);
    }

    public void SelectAll()
    {
        EndStroke();
        selectionPivot = null;
        Selection.SelectAll();
        Notify(EditorChange.Selection);
    }
    public void ClearSelection() { EndStroke(); Selection.Clear(); selectionPivot = null; Notify(EditorChange.Selection); }

    public void ClearPixels()
    {
        if (!CanPaint) return;
        EndStroke();
        BeginStroke();
        if (Selection.Count > 0)
            foreach (var index in Selection) SetPixel(index, 0);
        else
            for (var index = 0; index < Document.CellCount; index++) SetPixel(index, 0);
        EndStroke("Clear cells");
    }

    public CellClipboard CopySelection()
    {
        var width = Document.Width;
        var cells = Document.Layers[ActiveLayer].Cells;
        if (Selection.Count == 0)
        {
            // Nothing selected is the whole canvas, whose corner is its first cell.
            var all = new ClipboardCell[Document.CellCount];
            for (var index = 0; index < all.Length; index++) all[index] = new(index % width, index / width, cells[index]);
            return new CellClipboard(all);
        }
        if (!Selection.TryBounds(width, out var left, out var top)) return new CellClipboard([]);
        var copied = new ClipboardCell[Selection.Count];
        var next = 0;
        foreach (var index in Selection) copied[next++] = new(index % width - left, index / width - top, cells[index]);
        return new CellClipboard(copied);
    }

    public void Paste(CellClipboard clipboard, int column, int row)
    {
        if (!CanPaint) return;
        EndStroke();
        BeginStroke(captureSelection: true);
        Selection.Clear();
        selectionPivot = null;
        PaintThenSelect(CellsLandingAt(clipboard, column, row));
        EndStroke("Paste", EditorChange.Selection);
    }

    public void MoveSelection(int columns, int rows)
    {
        if (!CanPaint || Selection.Count == 0) return;
        var clipboard = CopySelection();
        if (!Selection.TryBounds(Document.Width, out var left, out var top)) return;
        EndStroke();
        BeginStroke(captureSelection: true);
        foreach (var index in Selection) SetPixel(index, 0);
        Selection.Clear();
        selectionPivot = null;
        PaintThenSelect(CellsLandingAt(clipboard, left + columns, top + rows));
        EndStroke("Move selection", EditorChange.Selection);
    }

    // Where a clipboard's cells land with its top left corner at this column and row, dropping whatever
    // would fall outside the canvas.
    //
    // One pass. As four chained queries it walked the clipboard four times and built a tuple for every cell
    // at each stage, which on a selection covering a two million cell canvas was most of what moving one
    // cost.
    private (int Index, uint Color)[] CellsLandingAt(CellClipboard clipboard, int column, int row)
    {
        var landing = new List<(int Index, uint Color)>(clipboard.Cells.Length);
        foreach (var cell in clipboard.Cells)
        {
            var atColumn = column + cell.Column;
            var atRow = row + cell.Row;
            if (atColumn < 0 || atColumn >= Document.Width || atRow < 0 || atRow >= Document.Height) continue;
            landing.Add((atRow * Document.Width + atColumn, cell.Color));
        }
        return [.. landing];
    }

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
        // A new layer is blank, so nothing composited changes; and by the time its addition is undone, every
        // stroke made on it has been undone first, so it is blank again then too.
        ChangeDocument("Add layer", artwork: false, () =>
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
        var layerCells = Document.Layers[ActiveLayer].Cells;
        var selected = new (HexCoordinate Coordinate, uint Color)[Selection.Count];
        // The pivot is the average of the selected coordinates, summed on the way past rather than by
        // walking the selection again for each of the two axes.
        long sumQ = 0, sumR = 0;
        var gathered = 0;
        foreach (var index in Selection)
        {
            var coordinate = Document.CoordinateAt(index);
            selected[gathered++] = (coordinate, layerCells[index]);
            sumQ += coordinate.Q;
            sumR += coordinate.R;
        }
        var pivot = selectionPivot ?? HexCoordinate.Round((double)sumQ / selected.Length, (double)sumR / selected.Length);
        var turns = (steps % 6 + 6) % 6;
        if (turns == 0) return;
        BeginStroke(captureSelection: true);
        foreach (var index in Selection) SetPixel(index, 0);
        Selection.Clear();
        // A rotation sends distinct cells to distinct cells, so there is nothing here for a set to
        // collapse and a list will do.
        var destination = new List<int>(selected.Length);
        foreach (var cell in selected)
        {
            var relative = new HexCoordinate(cell.Coordinate.Q - pivot.Q, cell.Coordinate.R - pivot.R);
            for (var turn = 0; turn < turns; turn++) relative = new(-relative.R, relative.Q + relative.R);
            var index = Document.IndexOf(relative + pivot);
            if (index >= 0) { SetPixel(index, cell.Color); destination.Add(index); }
        }
        Selection.UnionWith(destination);
        selectionPivot = pivot;
        EndStroke("Rotate selection", EditorChange.Selection);
    }

    public void DeleteLayer()
    {
        if (Document.Layers.Count == 1) return;
        ChangeDocument("Delete layer", artwork: true, () =>
        {
            Document.Layers.RemoveAt(ActiveLayer);
            ActiveLayer = Math.Min(ActiveLayer, Document.Layers.Count - 1);
        });
    }

    public void MoveLayer(int offset)
    {
        var target = ActiveLayer + offset;
        if (target < 0 || target >= Document.Layers.Count) return;
        ChangeDocument("Reorder layer", artwork: true, () =>
        {
            (Document.Layers[target], Document.Layers[ActiveLayer]) = (Document.Layers[ActiveLayer], Document.Layers[target]);
            ActiveLayer = target;
        });
    }

    public void SetLayerName(string name) { if (!string.IsNullOrWhiteSpace(name) && Document.Layers[ActiveLayer].Name != name.Trim()) ChangeDocument("Rename layer", artwork: false, () => Document.Layers[ActiveLayer].Name = name.Trim()); }
    public void SetLayerVisible(int index, bool visible) { if (Document.Layers[index].Visible != visible) ChangeDocument("Layer visibility", artwork: true, () => Document.Layers[index].Visible = visible); }
    public void SetLayerLocked(int index, bool locked) { if (Document.Layers[index].Locked != locked) ChangeDocument("Layer lock", artwork: false, () => Document.Layers[index].Locked = locked); }
    public void SetLayerOpacity(byte opacity) { if (Document.Layers[ActiveLayer].Opacity != opacity) ChangeDocument("Layer opacity", artwork: true, () => Document.Layers[ActiveLayer].Opacity = opacity); }
    public void SetBackgroundColor(uint color) { var opaque = color | 0xFF000000; if (Document.BackgroundColor != opaque) ChangeDocument("Background color", artwork: true, () => Document.BackgroundColor = opaque); }
    public bool AddPaletteColor(uint color)
    {
        if (Document.Palette.Contains(color) || Document.Palette.Count >= HexDocument.MaximumPaletteColors) return false;
        ChangeDocument("Add palette color", artwork: false, () => Document.Palette.Add(color));
        return true;
    }

    // Records a change to the document's structure. Whether it is one that alters what the cells look like
    // is said by the caller, because it is part of the record: undoing or redoing it later has to report the
    // same thing, and what it reports decides whether the canvas rebuilds its composited colour.
    private void ChangeDocument(string label, bool artwork, Action change)
    {
        EndStroke();
        var before = new EditorState(Document.CloneStructure(), ActiveLayer);
        change();
        var after = new EditorState(Document.CloneStructure(), ActiveLayer);
        var record = new DocumentChange(before, after, artwork);
        Commit(label, record);
        Notify(record.Reports);
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
        Notify(item.Change.Reports);
    }

    public void Redo()
    {
        EndStroke();
        if (redo.Count == 0) return;
        var item = redo[^1]; redo.RemoveAt(redo.Count - 1);
        item.Change.Apply(this, true); revision = item.AfterRevision; undo.Add(item);
        Notify(item.Change.Reports);
    }

    /// <summary>
    /// What each cell held before a stroke first touched it, which is what the stroke's undo entry is made
    /// from.
    /// </summary>
    /// <remarks>
    /// This was a dictionary from cell to previous colour. It is the right shape for the job and the wrong
    /// one for the size: clearing a two million cell canvas, or filling it, or moving a selection that
    /// covers it, put two million entries into a hash table and then read them all back out through a
    /// query. Three parallel pieces do the same job without hashing anything -- a bit a cell saying whether
    /// the cell has been recorded, and the cells and their colours in the order they were first touched --
    /// and all three are reused from one stroke to the next, so a brush dab allocates nothing at all.
    /// </remarks>
    private sealed class StrokeRecord(int cellCount)
    {
        private readonly CellSelection recorded = new(cellCount);
        private readonly List<int> cells = [];
        private readonly List<uint> before = [];

        public int Count => cells.Count;

        public void Reset()
        {
            recorded.Clear();
            cells.Clear();
            before.Clear();
        }

        /// <summary>Notes what a cell held, the first time the stroke touches it and not after.</summary>
        public void Record(int index, uint value)
        {
            if (!recorded.Add(index)) return;
            cells.Add(index);
            before.Add(value);
        }

        /// <summary>
        /// The stroke as undo entries, leaving out the cells that ended the stroke holding what they
        /// started it with.
        /// </summary>
        public PixelDelta[] Edits(uint[] current)
        {
            var edits = new List<PixelDelta>(cells.Count);
            for (var entry = 0; entry < cells.Count; entry++)
            {
                var index = cells[entry];
                if (before[entry] != current[index]) edits.Add(new PixelDelta(index, before[entry], current[index]));
            }
            return [.. edits];
        }
    }

    // What applying the change in either direction touches, which is what the listeners are told. Undoing a
    // brush stroke used to report everything, and everything included a whole-canvas recomposite and
    // re-upload for a change to a handful of cells.
    private interface IChange { long Bytes { get; } EditorChange Reports { get; } void Apply(EditorSession session, bool forward); }
    private sealed record HistoryItem(string Label, IChange Change, long BeforeRevision, long AfterRevision);
    private sealed record EditorState(HexDocument Document, int Layer);
    private sealed record SelectionState(int[] Indices, HexCoordinate? Pivot);
    private SelectionState CaptureSelection() => new(Selection.ToArray(), selectionPivot);
    private readonly record struct PixelDelta(int Index, uint Before, uint After);
    private sealed record PixelChange(int Layer, PixelDelta[] Edits, SelectionState? BeforeSelection, SelectionState? AfterSelection) : IChange
    {
        public long Bytes => 12L * Edits.Length + 4L * ((BeforeSelection?.Indices.Length ?? 0) + (AfterSelection?.Indices.Length ?? 0));
        // The active layer can move, which the panels show, so Structure is reported as well as the cells.
        public EditorChange Reports => EditorChange.Pixels | EditorChange.Selection | EditorChange.History | EditorChange.Structure;
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
    private sealed record DocumentChange(EditorState Before, EditorState After, bool Artwork) : IChange
    {
        // Structure only: a layer's properties and the palette. The cells are shared, not copied, so they are
        // not this record's to account for.
        public long Bytes => StructureBytes(Before) + StructureBytes(After);

        public EditorChange Reports => Artwork ? EditorChange.All : EditorChange.All & ~EditorChange.Artwork;

        private static long StructureBytes(EditorState state) =>
            128L + 64L * state.Document.Layers.Count + 4L * state.Document.Palette.Count;

        public void Apply(EditorSession session, bool forward)
        {
            var state = forward ? After : Before;
            session.Document = state.Document.CloneStructure(); session.ActiveLayer = state.Layer;
            session.Selection.Clear(); session.selectionPivot = null;
            // The cells are the same arrays, but how they look may not be: which layers there are, in what
            // order, how see-through, on what background. A record that changed none of that leaves the
            // composited colour alone.
            if (Artwork) session.MarkAllCellsDirty();
        }
    }
}

public sealed record CellClipboard(ClipboardCell[] Cells);
public readonly record struct ClipboardCell(int Column, int Row, uint Color);
