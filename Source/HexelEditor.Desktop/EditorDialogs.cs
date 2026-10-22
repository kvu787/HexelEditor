using System.Windows;
using System.Windows.Controls;
using HexelEditor.Core;

namespace HexelEditor.Desktop;

public static class EditorDialogs
{
    public static string? Prompt(Window owner, string title, string label, string initial)
    {
        var dialog = new Window { Title = title, Owner = owner, Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 10) });
        var input = new TextBox { Text = initial, MaxLength = 120 }; panel.Children.Add(input);
        var save = new Button { Content = "Apply", IsDefault = true, Margin = new Thickness(0, 16, 0, 0) };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        panel.Children.Add(save); dialog.Content = panel; dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    public static void ShowHelp(Window owner)
    {
        var dialog = new Window { Title = "Hexel Editor - Shortcuts", Owner = owner, Width = 570, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Shortcuts", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = "B  Pencil     E  Eraser     G  Fill     I  Eyedropper\nL  Line        U  Rectangle        M  Selection\n\nRight-click paints transparency. Alt-click picks a color.\n[ / ] changes the hex brush radius.\nSpace + drag or middle-drag pans. Scroll zooms at the cursor.\nF fits the canvas. H toggles the grid.\n\nCtrl+N / O / S  New / Open / Save\nCtrl+Shift+S  Save as\nCtrl+Z / Y  Undo / Redo\nCtrl+A / D  Select all / Deselect\nCtrl+C / X / V  Copy / Cut / Paste at the cursor\nDelete  Clear selection, or the whole active cel\nArrows  Move selected cells (clipped at the canvas edge)\nEsc  Deselect and stop playback\nEnter  Play / Stop     , / .  Previous / Next frame\n\nArtwork is stored as hexels. PNG and SVG exports have a\ntransparent background. Sprite sheets arrange frames left to right.", LineHeight = 22, FontSize = 12 });
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, Margin = new Thickness(0, 20, 0, 0) }; panel.Children.Add(close); dialog.Content = panel; dialog.ShowDialog();
    }
}

public sealed class NewCanvasDialog : Window
{
    private readonly TextBox widthInput = new() { Text = "40", MaxLength = 3 };
    private readonly TextBox heightInput = new() { Text = "32", MaxLength = 3 };
    private readonly TextBox nameInput = new() { Text = "Untitled", MaxLength = 120 };
    private readonly RadioButton pointy = new() { Content = "Pointy top", IsChecked = true, GroupName = "Orientation" };
    public int CanvasWidth { get; private set; } = 40;
    public int CanvasHeight { get; private set; } = 32;
    public string CanvasName => string.IsNullOrWhiteSpace(nameInput.Text) ? "Untitled" : nameInput.Text.Trim();
    public HexOrientation Orientation => pointy.IsChecked == true ? HexOrientation.PointyTop : HexOrientation.FlatTop;

    public NewCanvasDialog()
    {
        Title = "New hexel canvas"; Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "New hexel canvas", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 24) });
        void Field(string label, TextBox input) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 7) }); panel.Children.Add(input); input.Margin = new Thickness(0, 0, 0, 16); }
        Field("Project name", nameInput); Field($"Columns (1-{HexDocument.MaximumDimension})", widthInput); Field($"Rows (1-{HexDocument.MaximumDimension})", heightInput);
        panel.Children.Add(new TextBlock { Text = "Hexagon orientation", Margin = new Thickness(0, 0, 0, 4) }); panel.Children.Add(pointy); panel.Children.Add(new RadioButton { Content = "Flat top", GroupName = "Orientation" });
        var create = new Button { Content = "Create canvas", IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton"), Margin = new Thickness(0, 20, 0, 0) };
        create.Click += (_, _) =>
        {
            if (!int.TryParse(widthInput.Text, out var width) || !int.TryParse(heightInput.Text, out var height) ||
                width is < 1 or > HexDocument.MaximumDimension || height is < 1 or > HexDocument.MaximumDimension)
            { MessageBox.Show(this, $"Enter dimensions between 1 and {HexDocument.MaximumDimension} hexels.", "Canvas size"); return; }
            CanvasWidth = width; CanvasHeight = height; DialogResult = true;
        };
        panel.Children.Add(create); Content = panel;
    }
}
