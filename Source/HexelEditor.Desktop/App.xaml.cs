using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HexelEditor.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            if (e.Args.Length == 2 && e.Args[0] == "--verify-output")
            {
                Directory.CreateDirectory(e.Args[1]);
                File.WriteAllText(Path.Combine(e.Args[1], "Failure.txt"), args.Exception.ToString());
                args.Handled = true; Shutdown(1); return;
            }
            MessageBox.Show(args.Exception.Message, "Hexel Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow();
        MainWindow = window;
        if (e.Args.Length == 2 && e.Args[0] == "--verify-output")
        {
            // Render without opening a physical display. Used by the verification script.
            var folder = Path.GetFullPath(e.Args[1]);
            Directory.CreateDirectory(folder);
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
            Shutdown();
            return;
        }
        window.Show();
        if (e.Args.Length == 1 && File.Exists(e.Args[0])) window.OpenProject(e.Args[0]);
    }
}
