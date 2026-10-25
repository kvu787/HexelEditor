using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HexelEditor.Core;
using Microsoft.Win32;

namespace HexelEditor.Desktop;

public partial class MainWindow : Window
{
    private EditorSession session = new(ExampleDocument.Create());
    private readonly Dictionary<DrawingTool, Button> toolButtons = [];
    private readonly Dictionary<DrawingTool, System.Windows.Shapes.Path> toolIcons = [];
    private CellClipboard? clipboard;
    private string? projectPath;
    private bool refreshing;
    private readonly Brush ink;
    private readonly Brush strongInk;
    private readonly Brush muted;
    private readonly Style listRowStyle;
    private readonly Style swatchStyle;
    private readonly Style toolbarButtonStyle;

    public MainWindow()
    {
        InitializeComponent();
        ink = (Brush)FindResource("Ink");
        strongInk = (Brush)FindResource("StrongInk");
        muted = (Brush)FindResource("Muted");
        listRowStyle = (Style)FindResource("ListRow");
        swatchStyle = (Style)FindResource("SwatchButton");
        toolbarButtonStyle = (Style)FindResource("ToolbarButton");
        BuildTools();
        EditorCanvas.ZoomChanged += () => ZoomLabel.Text = $"{EditorCanvas.Zoom * 100:0}%";
        EditorCanvas.StatusChanged += text => StatusLabel.Text = text;
        EditorCanvas.ColorPicked += SetColor;
        LayerOpacity.ValueChanged += (_, _) => OpacityLabel.Text = $"{LayerOpacity.Value / 255 * 100:0}%";
        FilledShapes.Checked += (_, _) => EditorCanvas.FilledShapes = true;
        FilledShapes.Unchecked += (_, _) => EditorCanvas.FilledShapes = false;
        ShowGrid.IsChecked = EditorCanvas.ShowGrid;
        AttachSession(session);
        SetColor(0xFF000000);
        SetTool(DrawingTool.Pencil);
        Loaded += (_, _) => { EditorCanvas.Fit(); FocusCanvas(); };
        Deactivated += (_, _) => { EditorCanvas.SpacePressed = false; EditorCanvas.FinishGesture(); };
    }

    private void AttachSession(EditorSession value)
    {
        session.Changed -= SessionChanged; session = value;
        session.SelectLayer(session.Document.Layers.Count - 1);
        session.Changed += SessionChanged;
        EditorCanvas.Attach(session); RefreshPanels(); UpdateHeader();
    }

    private void BuildTools()
    {
        var definitions = new (DrawingTool Tool, string Key, string Geometry)[]
        {
            (DrawingTool.Pencil, "B", "M3,21 L5,14 L17,2 L22,7 L10,19 Z M5,14 L10,19 M15,4 L20,9"),
            (DrawingTool.Eraser, "E", "M3,14 L13,3 L22,12 L13,21 L9,21 Z M8,9 L17,17 M12,21 L23,21"),
            (DrawingTool.Fill, "G", "M3,12 L12,3 L21,12 L12,21 Z M3,12 L21,12 M8,2 L15,9 M21,16 L23,20 L21,22 L19,20 Z"),
            (DrawingTool.Eyedropper, "I", "M3,21 L6,15 L16,5 L21,10 L11,20 Z M14,3 L23,12 M16,5 L19,2 L23,6 L21,10"),
            (DrawingTool.Line, "L", "M3,21 L21,3 M2,17 L2,22 L7,22 M17,2 L22,2 L22,7"),
            (DrawingTool.Rectangle, "U", "M3,4 L21,4 L21,20 L3,20 Z"),
            (DrawingTool.Selection, "M", "M3,8 L3,3 L8,3 M16,3 L21,3 L21,8 M21,16 L21,21 L16,21 M8,21 L3,21 L3,16")
        };
        foreach (var definition in definitions)
        {
            var icon = IconPath(definition.Geometry, 22);
            var button = new Button
            {
                Content = icon,
                Style = (Style)FindResource("ToolButton"),
                ToolTip = $"{definition.Tool} ({definition.Key})"
            };
            AutomationProperties.SetName(button, definition.Tool.ToString());
            button.Click += (_, _) => SetTool(definition.Tool);
            ToolsPanel.Children.Add(button);
            toolButtons.Add(definition.Tool, button);
            toolIcons.Add(definition.Tool, icon);
        }
    }

    private System.Windows.Shapes.Path IconPath(string geometry, double size) => new()
    {
        Data = Geometry.Parse(geometry),
        Stroke = muted,
        StrokeThickness = 1.6,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform
    };

    private void SetTool(DrawingTool tool)
    {
        EditorCanvas.FinishGesture(); EditorCanvas.Tool = tool;
        foreach (var (key, button) in toolButtons)
        {
            button.Tag = key == tool ? "Active" : null;
            toolIcons[key].Stroke = key == tool ? strongInk : muted;
        }
        ToolLabel.Text = tool == DrawingTool.Selection ? "Selection  /  arrow keys move cells" : tool.ToString();
        FocusCanvas(); EditorCanvas.InvalidateVisual();
    }

    private void SetColor(uint color)
    {
        EditorCanvas.ForegroundColor = color;
        CurrentColorPreview.Background = BrushFor(color);
        CurrentColorLabel.Text = ColorMath.Format(color); ColorInput.Text = ColorMath.Format(color);
        RefreshPalette(); EditorCanvas.InvalidateVisual();
    }

    // Focusing anything inside a window activates that window, which a validation run on a display nobody is
    // watching must never do: it would take the keyboard from whoever is working on another display.
    private void FocusCanvas() { if (!App.KeepFocusAway) EditorCanvas.Focus(); }

    private static SolidColorBrush BrushFor(uint color) => new(Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color));

    private void SessionChanged(EditorChange change)
    {
        if ((change & EditorChange.Structure) != 0)
        {
            RefreshPanels();
            // A structural change is anything that alters what a cell looks like without altering the cell:
            // a layer hidden, reordered or made see-through, a new canvas background, an undo of any of them.
            // The canvas keeps composited colour in a raster, so all of it is stale.
            EditorCanvas.InvalidateArtwork();
        }
        EditorCanvas.InvalidateVisual(); UpdateHeader();
    }

    private void UpdateHeader()
    {
        var name = projectPath == null ? session.Document.Name : System.IO.Path.GetFileNameWithoutExtension(projectPath);
        DocumentTitle.Text = name;
        UnsavedMarker.Visibility = session.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        Title = $"{name}{(session.IsDirty ? " *" : "")} - Hexel Editor";
        DocumentDetails.Text = $"{session.Document.Width} x {session.Document.Height} hexels  /  {(session.Document.Orientation == HexOrientation.PointyTop ? "Pointy top" : "Flat top")}";
        UndoButton.IsEnabled = UndoMenuItem.IsEnabled = session.CanUndo;
        UndoMenuItem.Header = session.CanUndo ? $"Undo {session.UndoLabel}" : "Undo";
        UndoButton.ToolTip = session.CanUndo ? $"Undo {session.UndoLabel} (Ctrl+Z)" : "Nothing to undo";
        RedoButton.IsEnabled = RedoMenuItem.IsEnabled = session.CanRedo;
        RedoMenuItem.Header = session.CanRedo ? $"Redo {session.RedoLabel}" : "Redo";
        RedoButton.ToolTip = session.CanRedo ? $"Redo {session.RedoLabel} (Ctrl+Y)" : "Nothing to redo";
        CutMenuItem.IsEnabled = ClearMenuItem.IsEnabled = session.CanPaint;
        PasteMenuItem.IsEnabled = clipboard != null && session.CanPaint;
        RotateClockwiseMenuItem.IsEnabled = RotateCounterclockwiseMenuItem.IsEnabled = session.Selection.Count > 0 && session.CanPaint;
        SelectionStatus.Text = string.Join("  |  ", new[] { session.Selection.Count > 0 ? $"{session.Selection.Count} selected" : "", session.CanPaint ? "" : "Layer locked" }.Where(text => text.Length > 0));
    }

    private void RefreshPanels()
    {
        refreshing = true;
        try
        {
            RefreshPalette(); RefreshLayers(); RefreshBackgroundColor();
            LayerOpacity.Value = session.Document.Layers[session.ActiveLayer].Opacity;
            OpacityLabel.Text = $"{LayerOpacity.Value / 255 * 100:0}%";
            DeleteLayerButton.IsEnabled = session.Document.Layers.Count > 1;
        }
        finally { refreshing = false; }
    }

    private void RefreshBackgroundColor()
    {
        var color = session.Document.BackgroundColor;
        BackgroundColorPreview.Background = BrushFor(color);
        BackgroundColorLabel.Text = ColorMath.Format(color);
        BackgroundColorInput.Text = ColorMath.Format(color);
    }

    // A swatch stretches to the width its grid column gives it and has no size of its own, so a UniformGrid
    // would collapse every row. Keeping the swatches as tall as they are wide is what makes the grid square.
    private double SwatchSide
    {
        get
        {
            var side = Math.Floor(PalettePanel.ActualWidth / HexDocument.PaletteColumns);
            return side > 0 ? side : double.NaN;
        }
    }

    private void PaletteResized(object sender, SizeChangedEventArgs args)
    {
        if (!args.WidthChanged) return;
        var side = SwatchSide;
        foreach (FrameworkElement swatch in PalettePanel.Children) swatch.Height = side;
    }

    private void RefreshPalette()
    {
        PalettePanel.Children.Clear();
        PalettePanel.Columns = HexDocument.PaletteColumns;
        var side = SwatchSide;
        foreach (var color in session.Document.Palette)
        {
            var button = new Button
            {
                Content = new Rectangle { Fill = BrushFor(color) },
                Style = swatchStyle,
                Height = side,
                Tag = color == EditorCanvas.ForegroundColor ? "Active" : null,
                ToolTip = ColorMath.Format(color)
            };
            AutomationProperties.SetName(button, $"Color {ColorMath.Format(color)}");
            button.Click += (_, _) => { SetColor(color); FocusCanvas(); };
            PalettePanel.Children.Add(button);
        }
    }

    private void RefreshLayers()
    {
        LayersPanel.Children.Clear();
        for (var index = session.Document.Layers.Count - 1; index >= 0; index--)
        {
            var layerIndex = index; var layer = session.Document.Layers[index]; var active = index == session.ActiveLayer;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var visibility = new CheckBox { IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center, ToolTip = layer.Visible ? "Hide layer" : "Show layer" };
            AutomationProperties.SetName(visibility, $"Show {layer.Name}");
            visibility.Click += (_, _) => Act(() => session.SetLayerVisible(layerIndex, visibility.IsChecked == true));
            grid.Children.Add(visibility);
            var name = new TextBlock
            {
                Text = layer.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = active ? strongInk : ink,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal
            };
            Grid.SetColumn(name, 1); grid.Children.Add(name);
            var padlock = IconPath(layer.Locked
                ? "M 3.5,9.5 L 14.5,9.5 L 14.5,17 L 3.5,17 Z M 6,9.5 L 6,6.5 A 3,3 0 0 1 12,6.5 L 12,9.5"
                : "M 3.5,9.5 L 14.5,9.5 L 14.5,17 L 3.5,17 Z M 6,9.5 L 6,6.5 A 3,3 0 0 1 12,6.5", 18);
            padlock.Stroke = layer.Locked ? strongInk : muted;
            padlock.Opacity = layer.Locked ? 1 : 0.55;
            var locked = new Button { Content = padlock, Style = toolbarButtonStyle, Padding = new Thickness(5, 4, 5, 4), MinHeight = 0, ToolTip = layer.Locked ? "Unlock layer" : "Lock layer" };
            AutomationProperties.SetName(locked, $"{(layer.Locked ? "Unlock" : "Lock")} {layer.Name}");
            locked.Click += (_, args) => { args.Handled = true; Act(() => session.SetLayerLocked(layerIndex, !layer.Locked)); };
            Grid.SetColumn(locked, 2); grid.Children.Add(locked);
            var row = new Border { Child = grid, Style = listRowStyle, Tag = active ? "Active" : null, Cursor = Cursors.Hand };
            row.MouseLeftButtonDown += (_, args) => { if (args.OriginalSource is not CheckBox && args.OriginalSource is not Button) { Act(() => session.SelectLayer(layerIndex)); args.Handled = true; } };
            LayersPanel.Children.Add(row);
        }
    }

    private void Act(Action action)
    {
        EditorCanvas.FinishGesture();
        try { action(); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException) { ShowError(exception.Message, exception); }
    }

    private void ShowError(string message, Exception? exception = null)
    {
        App.Log.Write($"Editor error: {message}{(exception == null ? "" : $"\n{exception}")}");
        MessageBox.Show(this, message, "Hexel Editor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void NewProject()
    {
        if (!CanReplaceDocument()) return;
        var dialog = new NewCanvasDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        projectPath = null; AttachSession(new EditorSession(new HexDocument(dialog.CanvasWidth, dialog.CanvasHeight, dialog.Orientation) { Name = dialog.CanvasName }));
        StatusLabel.Text = "New canvas";
    }

    private void OpenExample() { if (CanReplaceDocument()) { projectPath = null; AttachSession(new EditorSession(ExampleDocument.Create())); } }

    private bool CanReplaceDocument()
    {
        EditorCanvas.FinishGesture();
        if (!session.IsDirty) return true;
        var result = MessageBox.Show(this, "Save your changes before continuing?", "Unsaved artwork", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || (result == MessageBoxResult.Yes && SaveProject(false));
    }

    private void OpenProjectDialog()
    {
        var dialog = new OpenFileDialog { Filter = "Hexel project (*.hexel)|*.hexel", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) OpenProject(dialog.FileName);
    }

    public void OpenProject(string path)
    {
        if (!CanReplaceDocument()) return;
        try { var document = ProjectFile.Load(path); projectPath = System.IO.Path.GetFullPath(path); AttachSession(new EditorSession(document)); StatusLabel.Text = $"Opened {System.IO.Path.GetFileName(path)}"; App.Log.Write($"Opened project: {projectPath}"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException) { ShowError($"Could not open the project.\n\n{exception.Message}", exception); }
    }

    private bool SaveProject(bool saveAs)
    {
        EditorCanvas.FinishGesture();
        var path = projectPath;
        if (path == null || saveAs)
        {
            var dialog = new SaveFileDialog { Filter = "Hexel project (*.hexel)|*.hexel", DefaultExt = ".hexel", FileName = path == null ? session.Document.Name : System.IO.Path.GetFileName(path) };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        try { ProjectFile.Save(session.Document, path); projectPath = path; session.MarkSaved(); StatusLabel.Text = $"Saved {System.IO.Path.GetFileName(path)}"; App.Log.Write($"Saved project: {path}"); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { ShowError($"Could not save the project.\n\n{exception.Message}", exception); return false; }
    }

    private void Copy() { EditorCanvas.FinishGesture(); clipboard = session.CopySelection(); StatusLabel.Text = "Copied cells. Hover the destination and press Ctrl+V to paste."; UpdateHeader(); }
    private void Cut() { Copy(); Act(session.ClearPixels); }

    private void Paste()
    {
        if (clipboard == null) return;
        var coordinate = EditorCanvas.HoverCoordinate;
        var (column, row) = coordinate.HasValue ? HexGeometry.ToOffset(coordinate.Value, session.Document.Orientation) : (0, 0);
        if (column < 0 || column >= session.Document.Width || row < 0 || row >= session.Document.Height) (column, row) = (0, 0);
        Act(() => session.Paste(clipboard, column, row));
    }

    private void ToggleGrid() => ShowGrid.IsChecked = ShowGrid.IsChecked != true;
    private void ChangeBrush(int delta) { EditorCanvas.BrushRadius = Math.Clamp(EditorCanvas.BrushRadius + delta, 0, EditorSession.MaximumBrushRadius); BrushSizeLabel.Text = (EditorCanvas.BrushRadius * 2 + 1).ToString(); EditorCanvas.InvalidateVisual(); }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (Keyboard.FocusedElement is TextBoxBase) { if (args.Key == Key.Escape) FocusCanvas(); return; }
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (Keyboard.FocusedElement is Slider && !control) return;
        if (control)
        {
            switch (args.Key)
            {
                case Key.N: NewProject(); break; case Key.O: OpenProjectDialog(); break; case Key.S: SaveProject(shift); break;
                case Key.Z: Act(shift ? session.Redo : session.Undo); break; case Key.Y: Act(session.Redo); break;
                case Key.A: Act(session.SelectAll); break; case Key.D: Act(session.ClearSelection); break;
                case Key.C: Copy(); break; case Key.X: Cut(); break; case Key.V: Paste(); break;
                case Key.R: Act(() => session.RotateSelection(shift ? -1 : 1)); break;
                default: return;
            }
            args.Handled = true; return;
        }
        switch (args.Key)
        {
            case Key.B: SetTool(DrawingTool.Pencil); break; case Key.E: SetTool(DrawingTool.Eraser); break; case Key.G: SetTool(DrawingTool.Fill); break;
            case Key.I: SetTool(DrawingTool.Eyedropper); break; case Key.L: SetTool(DrawingTool.Line); break; case Key.U: SetTool(DrawingTool.Rectangle); break; case Key.M: SetTool(DrawingTool.Selection); break;
            case Key.F: EditorCanvas.Fit(); break; case Key.H: ToggleGrid(); break;
            case Key.OemOpenBrackets: ChangeBrush(-1); break; case Key.OemCloseBrackets: ChangeBrush(1); break;
            case Key.OemPlus: case Key.Add: EditorCanvas.ChangeZoom(1.2f); break; case Key.OemMinus: case Key.Subtract: EditorCanvas.ChangeZoom(1 / 1.2f); break;
            case Key.Delete: case Key.Back: Act(session.ClearPixels); break;
            case Key.Escape: Act(session.ClearSelection); break;
            case Key.Space: EditorCanvas.SpacePressed = true; break;
            case Key.Left: Act(() => session.MoveSelection(-1, 0)); break; case Key.Right: Act(() => session.MoveSelection(1, 0)); break;
            case Key.Up: Act(() => session.MoveSelection(0, -1)); break; case Key.Down: Act(() => session.MoveSelection(0, 1)); break;
            default: return;
        }
        args.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs args) { if (args.Key == Key.Space) EditorCanvas.SpacePressed = false; }
    private void OnClosing(object? sender, CancelEventArgs args) { if (!CanReplaceDocument()) args.Cancel = true; }

    private void NewProjectClicked(object sender, RoutedEventArgs args) => NewProject();
    private void OpenClicked(object sender, RoutedEventArgs args) => OpenProjectDialog();
    private void OpenExampleClicked(object sender, RoutedEventArgs args) => OpenExample();
    private void SaveClicked(object sender, RoutedEventArgs args) => SaveProject(false);
    private void SaveAsClicked(object sender, RoutedEventArgs args) => SaveProject(true);
    private void ExitClicked(object sender, RoutedEventArgs args) => Close();
    private void UndoClicked(object sender, RoutedEventArgs args) => Act(session.Undo);
    private void RedoClicked(object sender, RoutedEventArgs args) => Act(session.Redo);
    private void CopyClicked(object sender, RoutedEventArgs args) => Copy();
    private void CutClicked(object sender, RoutedEventArgs args) => Cut();
    private void PasteClicked(object sender, RoutedEventArgs args) => Paste();
    private void RotateClockwiseClicked(object sender, RoutedEventArgs args) => Act(() => session.RotateSelection(1));
    private void RotateCounterclockwiseClicked(object sender, RoutedEventArgs args) => Act(() => session.RotateSelection(-1));
    private void SelectAllClicked(object sender, RoutedEventArgs args) => Act(session.SelectAll);
    private void DeselectClicked(object sender, RoutedEventArgs args) => Act(session.ClearSelection);
    private void ClearClicked(object sender, RoutedEventArgs args) => Act(session.ClearPixels);
    private void ToggleGridClicked(object sender, RoutedEventArgs args) => ToggleGrid();
    private void SmallerBrushClicked(object sender, RoutedEventArgs args) => ChangeBrush(-1);
    private void LargerBrushClicked(object sender, RoutedEventArgs args) => ChangeBrush(1);
    private void ZoomOutClicked(object sender, RoutedEventArgs args) => EditorCanvas.ChangeZoom(1 / 1.2f);
    private void ZoomInClicked(object sender, RoutedEventArgs args) => EditorCanvas.ChangeZoom(1.2f);
    private void FitClicked(object sender, RoutedEventArgs args) => EditorCanvas.Fit();
    private void AddLayerClicked(object sender, RoutedEventArgs args) => Act(session.AddLayer);
    private void DeleteLayerClicked(object sender, RoutedEventArgs args) => Act(session.DeleteLayer);
    private void LayerUpClicked(object sender, RoutedEventArgs args) => Act(() => session.MoveLayer(1));
    private void LayerDownClicked(object sender, RoutedEventArgs args) => Act(() => session.MoveLayer(-1));

    private void RenameLayerClicked(object sender, RoutedEventArgs args)
    {
        var name = EditorDialogs.Prompt(this, "Rename layer", "Layer name", session.Document.Layers[session.ActiveLayer].Name);
        if (name != null) Act(() => session.SetLayerName(name));
    }

    private void GridChanged(object sender, RoutedEventArgs args)
    {
        EditorCanvas.ShowGrid = ShowGrid.IsChecked == true;
        GridMenuItem.Header = EditorCanvas.ShowGrid ? "Hide grid" : "Show grid";
        EditorCanvas.InvalidateVisual();
    }

    private void OpacityReleased(object sender, MouseButtonEventArgs args) => ApplyOpacity();
    private void OpacityKeyboardFinished(object sender, KeyboardFocusChangedEventArgs args) => ApplyOpacity();
    private void ApplyOpacity() { if (!refreshing) Act(() => session.SetLayerOpacity((byte)Math.Round(LayerOpacity.Value))); }

    private void SetColorClicked(object sender, RoutedEventArgs args) { try { SetColor(ColorMath.Parse(ColorInput.Text)); FocusCanvas(); } catch (FormatException exception) { ShowError(exception.Message, exception); } }
    private void ColorInputKeyDown(object sender, KeyEventArgs args) { if (args.Key == Key.Enter) { SetColorClicked(sender, args); args.Handled = true; } }

    // The background is opaque by definition, so an alpha the value carries is dropped rather than refused.
    private void SetBackgroundColor(string text)
    {
        uint color;
        try { color = ColorMath.Parse(text) | 0xFF000000; }
        catch (FormatException exception) { ShowError(exception.Message, exception); return; }
        Act(() => session.SetBackgroundColor(color));
        StatusLabel.Text = $"Canvas background {ColorMath.Format(color)}";
        RefreshBackgroundColor();
    }

    private void SetBackgroundColorClicked(object sender, RoutedEventArgs args) { SetBackgroundColor(BackgroundColorInput.Text); FocusCanvas(); }
    private void BackgroundColorInputKeyDown(object sender, KeyEventArgs args) { if (args.Key == Key.Enter) { SetBackgroundColorClicked(sender, args); args.Handled = true; } }

    private void BackgroundColorClicked(object sender, RoutedEventArgs args)
    {
        var text = EditorDialogs.Prompt(this, "Canvas background color", "Background color (#RRGGBB)", ColorMath.Format(session.Document.BackgroundColor));
        if (text != null) SetBackgroundColor(text);
    }

    private void AddColorClicked(object sender, RoutedEventArgs args)
    {
        var color = EditorCanvas.ForegroundColor;
        var full = session.Document.Palette.Count >= HexDocument.MaximumPaletteColors;
        Act(() => StatusLabel.Text = session.AddPaletteColor(color)
            ? $"Added {ColorMath.Format(color)} to the palette."
            : full ? $"The palette already holds its {HexDocument.MaximumPaletteColors} colors."
            : $"{ColorMath.Format(color)} is already in the palette.");
    }

    private void HelpClicked(object sender, RoutedEventArgs args) => EditorDialogs.ShowHelp(this);
}
