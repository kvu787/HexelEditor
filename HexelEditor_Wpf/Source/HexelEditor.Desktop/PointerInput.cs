using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace HexelEditor.Desktop;

// Drives the pointer during validation by sending the window messages a mouse would send.
//
// Neither easier route works. Calling a handler, or raising MouseDown on a control, skips hit testing
// altogether, so it passes just as happily on a control that is covered by another, has no size, or has been
// taken out of hit testing. And a hand-made MouseEventArgs cannot carry a position: GetPosition asks the
// mouse device where the pointer is, which is wherever the physical pointer happens to be, not the point the
// event was built with. A window message carries the position that WPF then reports as the pointer's, and it
// arrives through the same input provider a real mouse arrives through, so hit testing, mouse capture, the
// order of enter, move, down and up, and the handlers themselves are all the real ones.
//
// Every coordinate here is in some element's own units and is converted on the way out: to the root visual,
// then through the composition target into the device pixels a message carries.
internal static class PointerInput
{
    private const int MouseMove = 0x0200;
    private const int LeftDown = 0x0201, LeftUp = 0x0202;
    private const int RightDown = 0x0204, RightUp = 0x0205;
    private const int MiddleDown = 0x0207, MiddleUp = 0x0208;
    private const int LeftHeld = 0x0001, RightHeld = 0x0002, MiddleHeld = 0x0010;

    public static void Move(Visual element, Point point, MouseButton? held = null)
    {
        Send(element, point, MouseMove, held);
    }

    public static void Press(Visual element, Point point, MouseButton button)
    {
        // A press is preceded by a move to the same point, as it would be in use, so that the pointer is over
        // the element before the button goes down rather than arriving with it.
        Send(element, point, MouseMove, null);
        Send(element, point, ButtonMessage(button, down: true), button);
    }

    public static void Release(Visual element, Point point, MouseButton button)
    {
        Send(element, point, ButtonMessage(button, down: false), null);
    }

    private static int ButtonMessage(MouseButton button, bool down) => button switch
    {
        MouseButton.Left => down ? LeftDown : LeftUp,
        MouseButton.Right => down ? RightDown : RightUp,
        MouseButton.Middle => down ? MiddleDown : MiddleUp,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Only the left, right and middle buttons are driven.")
    };

    // Drags from one point to another in steps, so that the moves in between are reported the way a hand
    // would report them. A stroke that only looked at the end points would miss whether the gesture joins up.
    public static void Drag(Visual element, Point from, Point to, MouseButton button, int steps = 8)
    {
        if (steps < 1) throw new ArgumentOutOfRangeException(nameof(steps), steps, "A drag needs at least one step.");
        Press(element, from, button);
        for (var step = 1; step <= steps; step++)
        {
            var fraction = (double)step / steps;
            Move(element, new Point(from.X + (to.X - from.X) * fraction, from.Y + (to.Y - from.Y) * fraction), button);
        }
        Release(element, to, button);
    }

    // What the pointer would actually reach at this point, found from the root so that anything in front of
    // the element counts. Returns null where nothing is hit.
    public static IInputElement? HitTest(Visual element, Point point)
    {
        var (_, root, inRoot) = Locate(element, point);
        return root is UIElement rootElement ? rootElement.InputHitTest(inRoot) : null;
    }

    // What WPF believes about the pointer, for when a check that drove it disagrees with what happened. The
    // position says where the input was taken from: the point a message carried, or the physical cursor.
    public static string Describe(UIElement element)
    {
        var position = Mouse.GetPosition(element);
        var over = Mouse.PrimaryDevice.DirectlyOver;
        var source = PresentationSource.FromVisual(element) as HwndSource;
        return $"WPF reports the pointer at {position.X:F1},{position.Y:F1} in this element, over " +
            $"{over?.GetType().Name ?? "nothing"}, captured by {Mouse.Captured?.GetType().Name ?? "nothing"}. " +
            $"Window {source?.Handle ?? IntPtr.Zero}, active window {GetActiveWindow()}, foreground window {GetForegroundWindow()}, " +
            $"cursor at {(TryGetCursorPos(out var cursor) ? $"{cursor.X},{cursor.Y}" : "unknown")}.";
    }

    private static void Send(Visual element, Point point, int message, MouseButton? held)
    {
        var (source, _, inRoot) = Locate(element, point);
        var device = source.CompositionTarget.TransformToDevice.Transform(inRoot);
        var x = (int)Math.Round(device.X);
        var y = (int)Math.Round(device.Y);
        var keys = held switch
        {
            MouseButton.Left => LeftHeld,
            MouseButton.Right => RightHeld,
            MouseButton.Middle => MiddleHeld,
            _ => 0
        };
        // Sent rather than posted: the window procedure runs inline on this thread, so WPF has delivered the
        // input events by the time this returns and a check can read the result straight away.
        var position = ((y & 0xFFFF) << 16) | (x & 0xFFFF);
        SendMessageW(source.Handle, message, (IntPtr)keys, (IntPtr)position);
    }

    private static (HwndSource Source, Visual Root, Point InRoot) Locate(Visual element, Point point)
    {
        if (PresentationSource.FromVisual(element) is not HwndSource source)
            throw new InvalidOperationException("The element is not in a window, so the pointer cannot be aimed at it.");
        if (source.RootVisual is not Visual root)
            throw new InvalidOperationException("The window has no root visual to measure the point against.");
        return (source, root, element.TransformToAncestor(root).Transform(point));
    }

    private static bool TryGetCursorPos(out Point position)
    {
        var located = GetCursorPos(out var point);
        position = new Point(point.X, point.Y);
        return located;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
#pragma warning restore SYSLIB1054
}
