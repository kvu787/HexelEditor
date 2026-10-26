using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace HexelEditor.Desktop;

// Drives the keyboard during validation by sending key messages to the host window and holding the modifier
// keys down in the thread's own key state.
//
// Two things had to be true at once. A shortcut is read through Keyboard.Modifiers, which WPF answers by
// asking the operating system whether Control is down, and a key message does not change that: posting
// WM_KEYDOWN for Control leaves the key state untouched, so every shortcut needing it would be unreachable.
// But the obvious fix, injecting real input with SendInput, cannot be used here: injected input only ever
// goes to the desktop currently being displayed, and refuses with ERROR_ACCESS_DENIED from anywhere else, so
// on a desktop of its own it does nothing, and on the displayed one it would type into whatever the person
// at the machine is doing.
//
// SetKeyboardState is the way through. It sets the key state of the calling thread only, which is the same
// state GetKeyState reports and therefore the state WPF reads. Nothing outside this process can see it, and
// the messages go to one window handle that this process owns, so this is safe on any desktop: it cannot
// reach another window, another process, or the person using the machine.
internal static class KeyboardInput
{
    private const int KeyDownMessage = 0x0100, KeyUpMessage = 0x0101;
    private const int SystemKeyDownMessage = 0x0104, SystemKeyUpMessage = 0x0105;
    private const int VirtualKeyToScanCode = 0;
    private const byte Down = 0x80;
    private const int KeyUpFlag = 1 << 31, ExtendedFlag = 1 << 24;

    // Gives WPF an element to send keys to. WPF only reports keyboard input for a window holding the Win32
    // focus, and a tunneling shortcut event travels from the root of the focused element's tree, so without
    // this the keys would arrive and route nowhere.
    public static void Focus(IInputElement element)
    {
        // Deliberately not SetFocus. Taking the Win32 focus makes WPF work out afresh where the pointer is by
        // asking the system, and on a desktop that is not being displayed there is no cursor to report, so it
        // settles on a position outside the window and treats the pointer as having left for good. WPF's own
        // focus is enough: a key message sent to this window is routed to whatever WPF believes is focused.
        Keyboard.Focus(element);
        Settle();
        if (!ReferenceEquals(Keyboard.FocusedElement, element))
            throw new InvalidOperationException(
                $"The keyboard focus is on {Keyboard.FocusedElement?.GetType().Name ?? "nothing"} rather than " +
                $"{element.GetType().Name}, so keys would not reach the editor.");
    }

    public static void Press(HwndSource source, Key key) => Send(source, key, false, ModifierKeys.None);

    public static void Release(HwndSource source, Key key) => Send(source, key, true, ModifierKeys.None);

    // A shortcut: the modifiers are held in the key state, the key is struck, and the modifiers let go. They
    // are let go even if the key throws, because leaving Control down would turn every later key into one.
    public static void Type(HwndSource source, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        try
        {
            Hold(modifiers);
            Send(source, key, false, modifiers);
            Send(source, key, true, modifiers);
        }
        finally
        {
            Hold(ModifierKeys.None);
            Settle();
        }
    }

    // Waits on the result rather than on a delay, so a check does not depend on how busy the machine is.
    public static void WaitUntil(Func<bool> condition, string expectation, int milliseconds = 2000)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        do
        {
            Settle();
            if (condition()) return;
            Thread.Sleep(5);
        }
        while (Environment.TickCount64 < deadline);
        throw new InvalidOperationException($"{expectation} within {milliseconds} ms of the keys being sent.");
    }

    // Holds exactly the wanted modifiers down in this thread's key state and nothing else. The whole table is
    // read and written back, because setting one key means handing the rest of it over unchanged.
    private static void Hold(ModifierKeys modifiers)
    {
        var state = new byte[256];
        if (!GetKeyboardState(state))
            throw new InvalidOperationException($"The key state could not be read: error {Marshal.GetLastWin32Error()}.");
        void Set(Key key, bool held)
        {
            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            state[virtualKey] = held ? Down : (byte)0;
        }
        var control = (modifiers & ModifierKeys.Control) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;
        var alt = (modifiers & ModifierKeys.Alt) != 0;
        Set(Key.LeftCtrl, control); Set(Key.RightCtrl, false);
        Set(Key.LeftShift, shift); Set(Key.RightShift, false);
        Set(Key.LeftAlt, alt); Set(Key.RightAlt, false);
        state[0x11] = control ? Down : (byte)0;
        state[0x10] = shift ? Down : (byte)0;
        state[0x12] = alt ? Down : (byte)0;
        if (!SetKeyboardState(state))
            throw new InvalidOperationException($"The key state could not be set: error {Marshal.GetLastWin32Error()}.");
    }

    private static void Send(HwndSource source, Key key, bool releasing, ModifierKeys modifiers)
    {
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var scanCode = MapVirtualKeyW((uint)virtualKey, VirtualKeyToScanCode);
        var lParam = (int)(scanCode << 16) | 1;
        if (IsExtended(key)) lParam |= ExtendedFlag;
        if (releasing) lParam |= KeyUpFlag;
        // Alt makes a key a system key, which is a different message, and the editor would never see a plain
        // WM_KEYDOWN for one.
        var system = (modifiers & ModifierKeys.Alt) != 0;
        var message = releasing
            ? (system ? SystemKeyUpMessage : KeyUpMessage)
            : (system ? SystemKeyDownMessage : KeyDownMessage);
        // Sent rather than posted, so the window procedure runs inline and WPF has routed the key by the time
        // this returns.
        SendMessageW(source.Handle, message, (IntPtr)virtualKey, (IntPtr)lParam);
    }

    // The keys Windows marks as extended. WPF tells the arrows apart from the number pad by this bit, so
    // without it an arrow key arrives as its keypad twin.
    private static bool IsExtended(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down
        or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Insert or Key.Delete
        or Key.RightCtrl or Key.RightAlt or Key.NumLock or Key.Divide;

    // Runs the message loop until everything waiting has been handled. Invoking at a low priority from the
    // dispatcher's own thread pumps a nested loop, which lets queued work run while a check is still going.
    private static void Settle()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Input);
    }

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] state);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState([In] byte[] state);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

#pragma warning restore SYSLIB1054
}
