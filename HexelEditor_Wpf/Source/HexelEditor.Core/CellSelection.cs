using System.Collections;
using System.Numerics;

namespace HexelEditor.Core;

/// <summary>
/// Which cells of a document are selected, as one bit each.
/// </summary>
/// <remarks>
/// A set of indices was the obvious way to hold this and the wrong one at the sizes the editor allows. Two
/// million selected cells cost a hash lookup each to ask about and some thirty megabytes to keep, and the
/// editor asks per cell while painting and per cell while drawing. A bit each is 250 KB for the same canvas,
/// answers from one shift and one mask, and lets selecting everything, or a run along a row, be done a word
/// at a time rather than a cell at a time.
///
/// Out-of-range indices are ignored rather than refused. Callers arrive with coordinates from a hex disk or
/// a rotation, which routinely fall outside a rectangular canvas, and every one of them would otherwise need
/// the same guard.
/// </remarks>
public sealed class CellSelection : IEnumerable<int>
{
    private const int Shift = 6;
    private const int Bits = 1 << Shift;

    private readonly ulong[] words;
    private readonly int capacity;

    public CellSelection(int cellCount)
    {
        if (cellCount < 0) throw new ArgumentOutOfRangeException(nameof(cellCount));
        capacity = cellCount;
        words = new ulong[(cellCount + Bits - 1) / Bits];
    }

    /// <summary>How many cells are selected.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Bumped whenever the selected cells changed. What is drawn from a selection is expensive enough to
    /// want keeping, and this is how a keeper knows its copy is stale.
    /// </summary>
    public int Version { get; private set; }

    public bool Contains(int index) =>
        (uint)index < (uint)capacity && (words[index >> Shift] & 1UL << (index & Bits - 1)) != 0;

    /// <summary>Selects one cell, and says whether that changed anything.</summary>
    public bool Add(int index)
    {
        if ((uint)index >= (uint)capacity) return false;
        ref var word = ref words[index >> Shift];
        var bit = 1UL << (index & Bits - 1);
        if ((word & bit) != 0) return false;
        word |= bit;
        Count++;
        Version++;
        return true;
    }

    public void Clear()
    {
        if (Count == 0) return;
        Array.Clear(words);
        Count = 0;
        Version++;
    }

    public void UnionWith(IEnumerable<int> indices)
    {
        foreach (var index in indices) Add(index);
    }

    /// <summary>Selects every cell.</summary>
    public void SelectAll()
    {
        if (words.Length == 0) return;
        Array.Fill(words, ulong.MaxValue);
        // The last word runs past the end of the canvas, and a bit set out there would be counted and
        // enumerated as a cell that does not exist.
        var tail = capacity & Bits - 1;
        if (tail != 0) words[^1] = (1UL << tail) - 1;
        Count = capacity;
        Version++;
    }

    /// <summary>
    /// Selects every cell from one index to another inclusive, a word at a time. For a rectangle, which is
    /// a run along each of its rows, and which the editor rebuilds on every mouse move of a drag.
    /// </summary>
    public void AddRange(int from, int toInclusive)
    {
        if (capacity == 0) return;
        from = Math.Max(from, 0);
        toInclusive = Math.Min(toInclusive, capacity - 1);
        if (from > toInclusive) return;
        var firstWord = from >> Shift;
        var lastWord = toInclusive >> Shift;
        // Every bit at or above the run's first, and every bit at or below its last. Where the run lies
        // inside one word the two masks are combined; where it spans words the ends are masked and the
        // middle is filled.
        var firstMask = ulong.MaxValue << (from & Bits - 1);
        var lastShift = Bits - 1 - (toInclusive & Bits - 1);
        var lastMask = ulong.MaxValue >> lastShift;
        if (firstWord == lastWord)
        {
            Set(firstWord, firstMask & lastMask);
            return;
        }
        Set(firstWord, firstMask);
        for (var word = firstWord + 1; word < lastWord; word++) Set(word, ulong.MaxValue);
        Set(lastWord, lastMask);

        void Set(int word, ulong mask)
        {
            var before = words[word];
            var after = before | mask;
            if (after == before) return;
            words[word] = after;
            Count += BitOperations.PopCount(after) - BitOperations.PopCount(before);
            Version++;
        }
    }

    public int[] ToArray()
    {
        var indices = new int[Count];
        var next = 0;
        foreach (var index in this) indices[next++] = index;
        return indices;
    }

    /// <summary>
    /// The top left of the selection's bounding box, in columns and rows, or false when nothing is
    /// selected. One pass, where asking for the smallest column and the smallest row separately is two.
    /// </summary>
    public bool TryBounds(int width, out int left, out int top)
    {
        left = 0;
        top = 0;
        if (Count == 0 || width <= 0) return false;
        var smallestColumn = int.MaxValue;
        var smallestRow = int.MaxValue;
        foreach (var index in this)
        {
            var column = index % width;
            if (column < smallestColumn) smallestColumn = column;
            // Indices rise as the walk goes on, so the first one seen is on the smallest row and no later
            // one can beat it.
            if (smallestRow == int.MaxValue) smallestRow = index / width;
        }
        left = smallestColumn;
        top = smallestRow;
        return true;
    }

    public Enumerator GetEnumerator() => new(words, capacity);
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Walks the selected cells in order, skipping empty words whole. A struct, so the editor's own loops
    /// allocate nothing; the interface implementations box it for callers that want LINQ.
    /// </summary>
    public struct Enumerator : IEnumerator<int>
    {
        private readonly ulong[] words;
        private readonly int capacity;
        private int word;
        private ulong remaining;

        internal Enumerator(ulong[] words, int capacity)
        {
            this.words = words;
            this.capacity = capacity;
            word = -1;
            remaining = 0;
            Current = -1;
        }

        public int Current { get; private set; }
        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            while (remaining == 0)
            {
                if (++word >= words.Length) return false;
                remaining = words[word];
            }
            var bit = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            Current = (word << Shift) + bit;
            return Current < capacity;
        }

        public void Reset()
        {
            word = -1;
            remaining = 0;
            Current = -1;
        }

        public void Dispose() { }
    }
}
