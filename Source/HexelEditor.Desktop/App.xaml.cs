using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HexelEditor.Desktop;

public partial class App : Application
{
    internal static SessionLog Log { get; private set; } = null!;
    private readonly bool verifying;

    public App()
    {
        var arguments = Environment.GetCommandLineArgs();
        verifying = arguments.Length == 3 && arguments[1] == "--verify-output";
        Log = new SessionLog(verifying ? Path.GetFullPath(arguments[2]) : null);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"Unhandled dispatcher exception: {args.Exception}");
            args.Handled = true;
            if (verifying) { Shutdown(1); return; }
            MessageBox.Show(args.Exception.Message, "Hexel Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args) => Log.Write($"Fatal exception: {args.ExceptionObject}");

    protected override void OnExit(ExitEventArgs e)
    {
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
            // SKElement needs a presentation source. This hidden host never shows on a display.
            using var host = new HwndSource(new HwndSourceParameters("HexelVerification") { Width = 1440, Height = 960, WindowStyle = 0, PositionX = -30000, PositionY = -30000 });
            host.RootVisual = root;
            root.Measure(new Size(1440, 960)); root.Arrange(new Rect(0, 0, 1440, 960)); root.UpdateLayout();
            window.VerifyEditor(folder);
            var bitmap = new RenderTargetBitmap(1440, 960, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            window.VerifyCanvasRendered();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(folder, "Editor.png"))) encoder.Save(stream);
            Log.Write("Desktop verification passed.");
            Shutdown();
            return;
        }
        window.Show();
        if (e.Args.Length == 1 && File.Exists(e.Args[0])) window.OpenProject(e.Args[0]);
    }
}
