using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HexelEditor.Core;

namespace HexelEditor.Desktop;

public static class EditorDialogs
{
    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)Application.Current.FindResource("StrongInk"),
        Margin = new Thickness(0, 0, 0, 20)
    };

    public static StackPanel Footer(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        foreach (var button in buttons) { button.Margin = new Thickness(8, 0, 0, 0); button.MinWidth = 96; panel.Children.Add(button); }
        return panel;
    }

    public static string? Prompt(Window owner, string title, string label, string initial)
    {
        var dialog = new Window { Title = title, Owner = owner, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(Heading(title));
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 7) });
        var input = new TextBox { Text = initial, MaxLength = 120 };
        panel.Children.Add(input);
        var apply = new Button { Content = "Apply", IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton") };
        apply.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        panel.Children.Add(Footer(new Button { Content = "Cancel", IsCancel = true }, apply));
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    public static void ShowHelp(Window owner)
    {
        var dialog = new Window { Title = "Hexel Editor - Shortcuts", Owner = owner, Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = HelpContent();
        dialog.ShowDialog();
    }

    public static StackPanel HelpContent()
    {
        var shortcuts = new (string Action, string Keys)[]
        {
            ("Pencil / eraser / fill / eyedropper", "B / E / G / I"),
            ("Line / rectangle / selection", "L / U / M"),
            ("Erase with the current brush", "Right mouse button"),
            ("Temporarily pick a color", "Alt + click"),
            ("Decrease / increase brush radius", "[ / ]"),
            ("Pan", "Space + drag, or middle drag"),
            ("Zoom", "Mouse wheel, or + / -"),
            ("Fit canvas / toggle grid", "F / H"),
            ("New / open / save", "Ctrl+N / Ctrl+O / Ctrl+S"),
            ("Save as", "Ctrl+Shift+S"),
            ("Undo / redo", "Ctrl+Z / Ctrl+Y or Ctrl+Shift+Z"),
            ("Copy / cut / paste at cursor", "Ctrl+C / Ctrl+X / Ctrl+V"),
            ("Select all / deselect", "Ctrl+A / Ctrl+D"),
            ("Move selected cells", "Arrow keys"),
            ("Rotate selection 60 degrees", "Ctrl+R / Ctrl+Shift+R"),
            ("Clear selected cells or the layer", "Delete or Backspace"),
            ("Deselect", "Esc")
        };
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(Heading("Shortcuts"));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var muted = (Brush)Application.Current.FindResource("Muted");
        for (var index = 0; index < shortcuts.Length; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var action = new TextBlock { Text = shortcuts[index].Action, Margin = new Thickness(0, 5, 24, 5) };
            Grid.SetRow(action, index); grid.Children.Add(action);
            var keys = new TextBlock { Text = shortcuts[index].Keys, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 5, 0, 5) };
            Grid.SetRow(keys, index); Grid.SetColumn(keys, 1); grid.Children.Add(keys);
        }
        panel.Children.Add(grid);
        panel.Children.Add(Footer(new Button { Content = "Close", IsDefault = true, IsCancel = true }));
        return panel;
    }
}

public sealed class NewCanvasDialog : Window
{
    // Taken from the limit rather than written out, so the box can always hold the largest value
    // the label offers. These disagreed before: the label said 1-2048 and the box took three digits.
    private static readonly int DimensionDigits = HexDocument.MaximumDimension.ToString().Length;
    private readonly TextBox widthInput = new() { Text = "40", MaxLength = DimensionDigits };
    private readonly TextBox heightInput = new() { Text = "32", MaxLength = DimensionDigits };
    private readonly TextBox nameInput = new() { Text = "Untitled", MaxLength = 120 };
    private readonly RadioButton pointy = new() { Content = "Pointy top", IsChecked = true, GroupName = "Orientation" };
    public int CanvasWidth { get; private set; } = 40;
    public int CanvasHeight { get; private set; } = 32;
    public string CanvasName => string.IsNullOrWhiteSpace(nameInput.Text) ? "Untitled" : nameInput.Text.Trim();
    public HexOrientation Orientation => pointy.IsChecked == true ? HexOrientation.PointyTop : HexOrientation.FlatTop;

    public NewCanvasDialog()
    {
        Title = "New hexel canvas"; Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(EditorDialogs.Heading("New hexel canvas"));
        void Field(string label, TextBox input)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 7) });
            input.Margin = new Thickness(0, 0, 0, 16);
            panel.Children.Add(input);
        }
        Field("Project name", nameInput);
        Field($"Columns (1-{HexDocument.MaximumDimension})", widthInput);
        Field($"Rows (1-{HexDocument.MaximumDimension})", heightInput);
        panel.Children.Add(new TextBlock { Text = "Hexagon orientation", Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(pointy);
        panel.Children.Add(new RadioButton { Content = "Flat top", GroupName = "Orientation" });
        var create = new Button { Content = "Create canvas", IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton") };
        create.Click += (_, _) =>
        {
            if (!int.TryParse(widthInput.Text, out var width) || !int.TryParse(heightInput.Text, out var height) ||
                width is < 1 or > HexDocument.MaximumDimension || height is < 1 or > HexDocument.MaximumDimension)
            { MessageBox.Show(this, $"Enter dimensions between 1 and {HexDocument.MaximumDimension} hexels.", "Canvas size"); return; }
            if ((long)width * height > HexDocument.MaximumCellCount)
            { MessageBox.Show(this, $"A canvas holds at most {HexDocument.MaximumCellCount:N0} hexels, and {width} by {height} is {(long)width * height:N0}.", "Canvas size"); return; }
            CanvasWidth = width; CanvasHeight = height; DialogResult = true;
        };
        panel.Children.Add(EditorDialogs.Footer(new Button { Content = "Cancel", IsCancel = true }, create));
        Content = panel;
    }
}
