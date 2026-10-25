using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HexelEditor.Desktop;

public partial class App : Application
{
    internal static SessionLog Log { get; private set; } = null!;

    // Set for a validation run. The editor checks it before focusing anything, because focus is what pulls a
    // window to the foreground and with it the keyboard of whoever is using the machine.
    internal static bool KeepFocusAway { get; private set; }

    private readonly bool verifying;

    public App()
    {
        var arguments = Environment.GetCommandLineArgs();
        verifying = arguments.Length == 3 && arguments[1] == "--verify-output";
        if (verifying || Array.IndexOf(arguments, ShowWithoutFocusOption) >= 0)
        {
            // A crash would otherwise put a Windows Error Reporting window on whichever display Windows
            // likes, which for a run meant to be invisible is the worst kind of interruption. Failing
            // silently is right here: the session log and the exit code carry the diagnosis.
            SetErrorMode(FailCriticalErrors | NoGeneralProtectionFaultErrorBox | NoOpenFileErrorBox);
        }
        Log = new SessionLog(verifying ? Path.GetFullPath(arguments[2]) : null);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"Unhandled dispatcher exception: {args.Exception}");
            args.Handled = true;
            if (verifying)
            {
                // Ends the process rather than asking WPF to shut down. A check that fails after driving the
                // pointer has left the input system mid-gesture, and a shutdown from there has been seen to
                // hang, which turns a failed check into a timeout and buries the reason. The log is written
                // with AutoFlush, so the diagnosis is already on disk by the time this runs.
                Log.Write("Application exited. Exit code: 1.");
                Log.Dispose();
                Environment.Exit(1);
            }
            MessageBox.Show(args.Exception.Message, "Hexel Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args) => Log.Write($"Fatal exception: {args.ExceptionObject}");

    protected override void OnExit(ExitEventArgs e)
    {
        // The GL context, its thread and its GPU surfaces outlive every window, so the
        // application is the only thing with the standing to let them go.
        GpuHexRenderer.DisposeShared();
        try
        {
            base.OnExit(e);
            Log.Write($"Application exited. Exit code: {e.ApplicationExitCode}.");
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            Log.Dispose();
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        if (verifying)
        {
            // Render without opening a physical display. Used by the verification script.
            var folder = Log.FolderPath;
            Log.Write("Desktop verification started.");
            window.Width = 1440; window.Height = 960;
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            // SKElement needs a presentation source. This host never shows on a display: it sits at
            // -30000,-30000, which is on no monitor, and validation runs it on a desktop that is not being
            // displayed at all.
            //
            // It is marked visible all the same, because WPF's hit testing for pointer input asks the window
            // manager which window lies at a point, and the window manager only answers with visible windows.
            // Without WS_VISIBLE a driven pointer's position arrives correctly and then lands on nothing, so
            // no element ever sees a mouse event. WS_EX_TOOLWINDOW keeps it out of the taskbar regardless.
            using var host = new HwndSource(new HwndSourceParameters("HexelVerification")
            {
                Width = 1440,
                Height = 960,
                PositionX = -30000,
                PositionY = -30000,
                WindowStyle = unchecked((int)(VisibleStyle | PopupStyle)),
                ExtendedWindowStyle = ToolWindowStyle
            });
            host.RootVisual = root;
            root.Measure(new Size(1440, 960)); root.Arrange(new Rect(0, 0, 1440, 960)); root.UpdateLayout();
            window.VerifyEditor(folder);
            var bitmap = new RenderTargetBitmap(1440, 960, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            window.VerifyCanvasRendered();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(folder, "Editor.png"))) encoder.Save(stream);
            window.VerifyInterface(folder);
            window.VerifyPalette(folder);
            window.VerifyRenderScales(folder);
            // These two go last, because they drive the pointer and the keyboard for real: they take mouse
            // capture, take the keyboard focus, and attach sessions of their own that no earlier check should
            // be looking at.
            window.VerifyPointerInput(folder);
            window.VerifyKeyboardInput(folder);
            Log.Write("Desktop verification passed.");
            Shutdown();
            return;
        }
        if (ParsePlacement(e.Args) is { } placement)
        {
            // Placing the window from SourceInitialized puts it on the wanted display before its first frame,
            // so it never appears anywhere else first, and ShowActivated leaves the keyboard and mouse with
            // whoever is using the machine. Together these let a validation run share a desk with its user.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowActivated = false;
            // No taskbar button means nothing to appear on the user's taskbar, and nothing to flash there if
            // the window is ever denied the foreground, which is how Windows answers a refused activation.
            window.ShowInTaskbar = false;
            KeepFocusAway = true;
            window.SourceInitialized += (_, _) => Place(window, placement);
        }
        window.Show();
        var project = e.Args.FirstOrDefault(File.Exists);
        if (project != null) window.OpenProject(project);
    }

    // Bounds are physical pixels, the units the placing script measures displays in. WPF's own Left and Top
    // are device-independent, which is not the same thing on a display that is scaled.
    private static Int32Rect? ParsePlacement(string[] arguments)
    {
        var index = Array.IndexOf(arguments, ShowWithoutFocusOption);
        if (index < 0) return null;
        var parts = index + 1 < arguments.Length ? arguments[index + 1].Split(',') : [];
        if (parts.Length != 4 || !parts.All(part => int.TryParse(part, out _)))
            throw new ArgumentException($"{ShowWithoutFocusOption} takes x,y,width,height in whole pixels.");
        var bounds = parts.Select(int.Parse).ToArray();
        return new Int32Rect(bounds[0], bounds[1], bounds[2], bounds[3]);
    }

    private static void Place(Window window, Int32Rect bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        // ShowActivated alone is not enough: the editor focuses its canvas as it loads, and focusing anything
        // inside a window activates that window. WS_EX_NOACTIVATE refuses the foreground outright, so no
        // amount of focusing here, or stray clicking, can pull the user's keyboard over to this window.
        SetWindowLongPtrW(handle, ExtendedStyle, GetWindowLongPtrW(handle, ExtendedStyle) | NoActivateStyle);
        SetWindowPos(handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoZOrder | NoActivate | NoOwnerZOrder);
    }

    private const string ShowWithoutFocusOption = "--show-without-focus";
    private const uint NoZOrder = 0x0004, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
    private const int ExtendedStyle = -20;
    private static readonly IntPtr NoActivateStyle = 0x08000000;
    private const uint FailCriticalErrors = 0x0001, NoGeneralProtectionFaultErrorBox = 0x0002, NoOpenFileErrorBox = 0x8000;
    private const uint VisibleStyle = 0x10000000, PopupStyle = 0x80000000;
    private const int ToolWindowStyle = 0x00000080;

    // The generated LibraryImport marshaller needs unsafe code switched on for the whole project, which is a
    // poor trade for one call made once per launch.
#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr handle, int index, IntPtr value);

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
#pragma warning restore SYSLIB1054
}
