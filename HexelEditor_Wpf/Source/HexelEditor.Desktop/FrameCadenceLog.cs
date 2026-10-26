using System.Diagnostics;
using System.Windows.Threading;

namespace HexelEditor.Desktop;

// Counts the frames the canvas draws and writes what it counted to the session log every two seconds, while
// frames are being drawn and not otherwise. The question it answers is the one nothing offscreen can: how
// many frames a second the window really produces under a real hand on a real display, and how far apart
// the slow ones were. Per-frame cost is measured elsewhere; this is about cadence, which the compositor and
// the display decide as much as the editor does.
//
// Switched on with --log-frames. Off, it costs nothing: the canvas holds a null and never calls here.
public sealed class FrameCadenceLog
{
    private readonly Action<string> log;
    private readonly List<long> stamps = [];
    private readonly DispatcherTimer timer;
    // The frame before the first of a period, so that the gap into the period is counted as well.
    private long previous;

    public FrameCadenceLog(Action<string> log)
    {
        this.log = log;
        timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Report(), Dispatcher.CurrentDispatcher);
        timer.Start();
    }

    public void Record() => stamps.Add(Stopwatch.GetTimestamp());

    private void Report()
    {
        if (stamps.Count == 0) { previous = 0; return; }
        var gaps = new List<double>(stamps.Count);
        var last = previous;
        foreach (var stamp in stamps)
        {
            if (last != 0) gaps.Add((stamp - last) * 1000.0 / Stopwatch.Frequency);
            last = stamp;
        }
        previous = last;
        var frames = stamps.Count;
        stamps.Clear();
        if (gaps.Count == 0) return;
        gaps.Sort();
        var median = gaps[gaps.Count / 2];
        var span = gaps.Sum() / 1000.0;
        // A gap of twice the median is a frame that was late by a whole frame: the kind of stutter a person
        // can see, as opposed to the jitter they cannot.
        var late = gaps.Count(gap => gap > 2 * median);
        log($"Frames: {frames} drawn in {span:0.00} s ({frames / span:0.0} a second); gaps median {median:0.00} ms, " +
            $"shortest {gaps[0]:0.00} ms, longest {gaps[^1]:0.00} ms, {late} over twice the median.");
    }
}
