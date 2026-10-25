using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
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
        Antialias.IsChecked = EditorCanvas.Antialias;
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

    private void Export(string format)
    {
        EditorCanvas.FinishGesture();
        var dialog = new SaveFileDialog { Filter = format == "svg" ? "SVG artwork (*.svg)|*.svg" : "PNG image (*.png)|*.png", DefaultExt = "." + format, FileName = session.Document.Name };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (format == "svg") ArtworkRenderer.ExportSvg(session.Document, dialog.FileName);
            else ArtworkRenderer.ExportPng(session.Document, dialog.FileName);
            StatusLabel.Text = $"Exported {System.IO.Path.GetFileName(dialog.FileName)}";
            App.Log.Write($"Exported artwork: {dialog.FileName}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { ShowError($"Could not export.\n\n{exception.Message}", exception); }
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
    private void ToggleAntialias() => Antialias.IsChecked = Antialias.IsChecked != true;
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
            case Key.F: EditorCanvas.Fit(); break; case Key.H: ToggleGrid(); break; case Key.A: ToggleAntialias(); break;
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
    private void ExportPngClicked(object sender, RoutedEventArgs args) => Export("png");
    private void ExportSvgClicked(object sender, RoutedEventArgs args) => Export("svg");
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
    private void ToggleAntialiasClicked(object sender, RoutedEventArgs args) => ToggleAntialias();
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

    private void AntialiasChanged(object sender, RoutedEventArgs args)
    {
        // A checkbox raises Checked while XAML is still being loaded, before the fields it wants
        // exist. Setting the initial state from the constructor avoids that, and this guard keeps
        // a stray early event from throwing where the only place to report it is a message box.
        if (EditorCanvas == null || AntialiasMenuItem == null) return;
        EditorCanvas.Antialias = Antialias.IsChecked == true;
        AntialiasMenuItem.Header = EditorCanvas.Antialias ? "Turn off smoothing" : "Turn on smoothing";
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

    public void VerifyEditor(string folder)
    {
        var original = session.Document.CompositeAt(0);
        session.Paint(session.Document.CoordinateAt(0), 0xFF00FF00); session.EndStroke();
        if (!UndoButton.IsEnabled || UnsavedMarker.Visibility != Visibility.Visible) throw new InvalidOperationException("The interface did not reflect the edit.");
        UndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (session.IsDirty || session.Document.CompositeAt(0) != original) throw new InvalidOperationException("The Undo button did not restore the artwork.");
        toolButtons[DrawingTool.Eraser].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (EditorCanvas.Tool != DrawingTool.Eraser) throw new InvalidOperationException("The tool button did not select the eraser.");
        SetTool(DrawingTool.Pencil);
        AttachSession(new EditorSession(ExampleDocument.Create()));
        var path = System.IO.Path.Combine(folder, "Example.hexel"); ProjectFile.Save(session.Document, path);
        var roundTrip = ProjectFile.Load(path);
        if (roundTrip.Layers.Count != 3 || roundTrip.CompositeAt(0) != session.Document.CompositeAt(0)) throw new InvalidDataException("Project round trip failed.");
        ArtworkRenderer.ExportPng(roundTrip, System.IO.Path.Combine(folder, "Artwork.png"));
        ArtworkRenderer.ExportSvg(roundTrip, System.IO.Path.Combine(folder, "Artwork.svg"));
        using var image = SkiaSharp.SKBitmap.Decode(System.IO.Path.Combine(folder, "Artwork.png"));
        if (image == null || image.Width < 100 || image.Height < 100) throw new InvalidDataException("PNG export verification failed.");
        var bounds = ArtworkRenderer.Bounds(roundTrip);
        SkiaSharp.SKColor Sample(double x, double y) =>
            image.GetPixel((int)Math.Round(x + 1 - bounds.Left * 16 / ArtworkRenderer.CellRadius), (int)Math.Round(y + 1 - bounds.Top * 16 / ArtworkRenderer.CellRadius));
        for (var index = 0; index < roundTrip.CellCount; index++)
        {
            var coordinate = roundTrip.CoordinateAt(index);
            var (x, y) = HexGeometry.Center(coordinate, roundTrip.Orientation, 16);
            if ((uint)Sample(x, y) != roundTrip.CompositeAt(index)) throw new InvalidDataException($"PNG color mismatch at cell {index}.");
            // Opaque neighbors must also meet with no background between them, which is what a seam would be.
            foreach (var direction in HexCoordinate.Directions)
            {
                var neighbor = roundTrip.IndexOf(coordinate + direction);
                if (neighbor < 0 || (roundTrip.CompositeAt(neighbor) >> 24) != 255) continue;
                var (neighborX, neighborY) = HexGeometry.Center(coordinate + direction, roundTrip.Orientation, 16);
                if (Sample((x + neighborX) / 2, (y + neighborY) / 2).Alpha != 255) throw new InvalidDataException($"PNG seam between cells {index} and {neighbor}.");
            }
        }
        var svg = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(folder, "Artwork.svg"));
        if (svg.Root?.Elements().Count() != roundTrip.CellCount) throw new InvalidDataException("SVG geometry is incomplete.");
        var blankPath = System.IO.Path.Combine(folder, "Transparent.png");
        ArtworkRenderer.ExportPng(new HexDocument(1, 1, HexOrientation.FlatTop), blankPath);
        using var blank = SkiaSharp.SKBitmap.Decode(blankPath);
        if (blank.Pixels.Any(pixel => pixel.Alpha != 0)) throw new InvalidDataException("Empty exported cells are not transparent.");
        var backgroundCells = VerifyBackgroundLayer();
        EditorCanvas.Fit(); UpdateLayout();
        File.WriteAllText(System.IO.Path.Combine(folder, "Verification.txt"), $"WPF commands and saved-state indicator: passed\nProject save/load: passed\nPNG export: {image.Width} x {image.Height}, all {roundTrip.CellCount} cell colors verified\nSVG polygons: passed\nFlat-top transparent export: passed\nCanvas background: one solid color under all {backgroundCells} cells\n");
    }

    // The background layer is one color rather than a pattern, so every cell holding no artwork samples the same value.
    private static int VerifyBackgroundLayer()
    {
        const uint background = 0xFF336699;
        var document = new HexDocument(5, 4) { BackgroundColor = background };
        document.Layers[0].Cells[0] = 0xFFFF0000;
        var bounds = ArtworkRenderer.Bounds(document);
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo((int)Math.Ceiling(bounds.Width) + 2, (int)Math.Ceiling(bounds.Height) + 2))
            ?? throw new InvalidOperationException("Could not create the background verification image.");
        surface.Canvas.Clear(SkiaSharp.SKColors.Transparent);
        surface.Canvas.Translate(1 - bounds.Left, 1 - bounds.Top);
        ArtworkRenderer.Draw(surface.Canvas, document, background: true);
        using var image = surface.Snapshot();
        using var bitmap = SkiaSharp.SKBitmap.FromImage(image);
        for (var index = 0; index < document.CellCount; index++)
        {
            var (x, y) = HexGeometry.Center(document.CoordinateAt(index), document.Orientation, ArtworkRenderer.CellRadius);
            var sample = (uint)bitmap.GetPixel((int)Math.Round(x + 1 - bounds.Left), (int)Math.Round(y + 1 - bounds.Top));
            var expected = index == 0 ? 0xFFFF0000 : background;
            if (sample != expected) throw new InvalidDataException($"The canvas background is not solid at cell {index}: {ColorMath.Format(sample)}.");
        }
        return document.CellCount;
    }

    // Opening a menu leaves it highlighted, so this runs after the editor screenshot rather than inside VerifyEditor.
    // The palette is the one panel laid out from the document at runtime, so the grid it builds, the color a
    // swatch sets, and the row an added color lands in are checked here rather than on a physical display.
    public void VerifyPalette(string folder)
    {
        UpdateLayout();
        if (PalettePanel.Columns != HexDocument.PaletteColumns)
            throw new InvalidOperationException("The palette is not laid out on its grid.");
        if (PalettePanel.Children.Count != session.Document.Palette.Count)
            throw new InvalidOperationException("The palette grid does not hold every color.");
        var side = Math.Floor(PalettePanel.ActualWidth / HexDocument.PaletteColumns);
        if (side < 1) throw new InvalidOperationException("The palette grid was given no width to lay swatches out in.");
        foreach (FrameworkElement swatch in PalettePanel.Children)
            if (swatch.Height != side) throw new InvalidOperationException("The palette swatches are not square.");

        // Choosing a color sets the foreground and moves the selection ring onto that swatch.
        var chosenIndex = HexDocument.PaletteColumns * 3 + 10;
        var chosen = session.Document.Palette[chosenIndex];
        ((Button)PalettePanel.Children[chosenIndex]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (EditorCanvas.ForegroundColor != chosen)
            throw new InvalidOperationException("A palette swatch did not set the foreground color.");
        if ((string?)((Button)PalettePanel.Children[chosenIndex]).Tag != "Active")
            throw new InvalidOperationException("The chosen palette swatch is not marked as active.");

        // Adding a color has to keep working now that the default palette fills the grid exactly.
        const uint added = 0xFF7F00FF;
        if (session.Document.Palette.Contains(added))
            throw new InvalidOperationException("The palette already holds the color this check adds.");
        var swatches = PalettePanel.Children.Count;
        SetColor(added);
        AddColorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (session.Document.Palette.Count != swatches + 1 || session.Document.Palette[^1] != added)
            throw new InvalidOperationException("Add current color did not append the color to the palette.");
        UpdateLayout();
        if (PalettePanel.Children.Count != swatches + 1)
            throw new InvalidOperationException("The added color did not reach the palette grid.");

        // Take it back out. Adding a color is an edit like any other, and leaving the session unsaved would
        // stop the window closing, which is how this verification host exits.
        session.Undo();
        if (PalettePanel.Children.Count != swatches || session.IsDirty)
            throw new InvalidOperationException("Undo did not take the added color back out of the palette.");
        File.AppendAllText(System.IO.Path.Combine(folder, "Verification.txt"),
            $"Palette grid: {swatches} swatches in {HexDocument.PaletteColumns} columns at {side} pixels square, choosing and adding a color verified\n");
    }

    // Everything else drives the editor by calling into it. This drives the pointer, which is how the editor
    // is actually used: window messages carrying real positions, through WPF's own input provider, so hit
    // testing decides what is hit, the canvas takes and releases real mouse capture, and the handlers see the
    // moves in the order a hand would make them. Painting is what this application is for, and none of it was
    // covered before: a wrong point-to-cell mapping, a stroke that dropped the cells between two moves, a
    // canvas that something had covered, or a pan fighting with drawing would all have passed.
    public void VerifyPointerInput(string folder)
    {
        // A blank canvas rather than the example, so that a cell holds the stroke or holds nothing. Erasing
        // clears the active layer, which on artwork with three layers would leave the ones underneath showing
        // and make "did the erase work" a question about compositing instead of about the gesture.
        AttachSession(new EditorSession(new HexDocument(40, 32)));
        SetTool(DrawingTool.Pencil);
        EditorCanvas.BrushRadius = 0;
        const uint stroke = 0xFF2040FF;
        SetColor(stroke);
        UpdateLayout();
        EditorCanvas.Fit();
        UpdateLayout();

        var document = session.Document;
        var orientation = document.Orientation;
        var row = document.Height / 2;
        var from = HexGeometry.FromOffset(4, row, orientation);
        var to = HexGeometry.FromOffset(12, row, orientation);
        var line = from.LineTo(to).ToArray();
        if (line.Length < 5) throw new InvalidOperationException("The pointer check needs a run of cells to drag across.");
        if (document.IndexOf(from) < 0 || document.IndexOf(to) < 0)
            throw new InvalidOperationException("The cells the pointer check drags between are outside the canvas.");

        // Hit testing, from the root, so that anything in front of the canvas would be found instead of it.
        var start = EditorCanvas.PointOf(from);
        var hit = PointerInput.HitTest(EditorCanvas, start);
        if (!ReferenceEquals(hit, EditorCanvas))
            throw new InvalidOperationException($"The pointer reaches {hit?.GetType().Name ?? "nothing"} at the canvas, not the canvas.");

        // A move on its own has to land on the cell the point was taken from, which is the mapping between
        // pointer positions and cells working in both directions.
        PointerInput.Move(EditorCanvas, start);
        if (EditorCanvas.HoverCoordinate != from)
            throw new InvalidOperationException($"A pointer move aimed at {start.X:F1},{start.Y:F1} for cell {from.Q},{from.R} was read as cell {EditorCanvas.HoverCoordinate?.ToString() ?? "none"}. {PointerInput.Describe(EditorCanvas)}");

        // The stroke. Capture is checked while the button is down, because losing it mid-gesture is what ends
        // a stroke early, and it has to be given back afterwards or the next click goes to the wrong place.
        PointerInput.Press(EditorCanvas, start, MouseButton.Left);
        if (!ReferenceEquals(Mouse.Captured, EditorCanvas))
            throw new InvalidOperationException("The canvas did not capture the mouse for a stroke.");
        var end = EditorCanvas.PointOf(to);
        for (var step = 1; step <= 8; step++)
        {
            var fraction = (double)step / 8;
            PointerInput.Move(EditorCanvas, new Point(start.X + (end.X - start.X) * fraction, start.Y + (end.Y - start.Y) * fraction), MouseButton.Left);
        }
        PointerInput.Release(EditorCanvas, end, MouseButton.Left);
        if (Mouse.Captured != null)
            throw new InvalidOperationException($"The canvas kept the mouse captured after the stroke, held by {Mouse.Captured}.");

        // Eight moves across nine cells, so the gesture only joins up if the cells between two reported
        // positions are filled in as well.
        foreach (var cell in line)
        {
            var index = document.IndexOf(cell);
            if (index < 0) throw new InvalidOperationException($"Cell {cell.Q},{cell.R} on the stroke is outside the canvas.");
            if (document.CompositeAt(index) != stroke)
                throw new InvalidOperationException($"A dragged stroke missed cell {cell.Q},{cell.R}, which holds {ColorMath.Format(document.CompositeAt(index))}.");
        }

        // One gesture is one undo, however many moves it was made of.
        if (!session.CanUndo) throw new InvalidOperationException("A dragged stroke left nothing to undo.");
        session.Undo();
        if (line.Any(cell => document.CompositeAt(document.IndexOf(cell)) != 0))
            throw new InvalidOperationException("Undoing the stroke did not take all of it back out, so the drag was recorded as more than one command.");

        // The right button erases with the same gesture. Paint first, then take it back out by dragging.
        PointerInput.Drag(EditorCanvas, start, end, MouseButton.Left);
        if (line.Any(cell => document.CompositeAt(document.IndexOf(cell)) != stroke))
            throw new InvalidOperationException("The drag that the erase check paints over did not fill every cell.");
        PointerInput.Drag(EditorCanvas, start, end, MouseButton.Right);
        foreach (var cell in line)
            if (document.CompositeAt(document.IndexOf(cell)) != 0)
                throw new InvalidOperationException($"A right-button drag did not erase cell {cell.Q},{cell.R}.");
        session.Undo();
        session.Undo();

        // Panning moves the canvas under the pointer and must not paint while doing it. The same point
        // reading as a different cell afterwards is the pan having happened.
        var edits = session.CanUndo;
        var before = EditorCanvas.HoverCoordinate;
        PointerInput.Drag(EditorCanvas, start, new Point(start.X + 120, start.Y + 40), MouseButton.Middle);
        PointerInput.Move(EditorCanvas, start);
        var after = EditorCanvas.HoverCoordinate;
        if (after == before)
            throw new InvalidOperationException($"A middle-button drag did not pan the canvas: {start.X},{start.Y} still reads as cell {after}.");
        if (session.CanUndo != edits) throw new InvalidOperationException("Panning the canvas changed the artwork.");

        // The host closes by shutting the application down, which a document with unsaved edits would stop.
        if (session.IsDirty) throw new InvalidOperationException("The pointer checks left the document unsaved.");
        File.AppendAllText(System.IO.Path.Combine(folder, "Verification.txt"),
            $"Pointer input: hit testing, mouse capture, a {line.Length}-cell drag, right-button erase and middle-button pan verified through real window messages\n");
    }

    // The shortcut table, struck rather than called. Keys are injected, so Control and Shift are really held
    // and OnKeyDown reads the modifier state it would read under a hand. Nothing here opens a dialog or
    // touches the clipboard: Ctrl+N, Ctrl+O and Ctrl+S would wait on a modal window that no one can answer,
    // and the clipboard belongs to the window station, which is shared with whoever is using the machine.
    public void VerifyKeyboardInput(string folder)
    {
        AttachSession(new EditorSession(new HexDocument(40, 32)));
        UpdateLayout();
        EditorCanvas.Fit();
        UpdateLayout();
        if (PresentationSource.FromVisual(EditorCanvas) is not HwndSource source)
            throw new InvalidOperationException("The canvas is not in a window, so keys cannot be sent to it.");
        KeyboardInput.Focus(EditorCanvas);

        // Every tool has a key, and picking the wrong tool is the kind of mistake nothing else would catch.
        var tools = new (Key Key, DrawingTool Tool)[]
        {
            (Key.E, DrawingTool.Eraser), (Key.G, DrawingTool.Fill), (Key.I, DrawingTool.Eyedropper),
            (Key.L, DrawingTool.Line), (Key.U, DrawingTool.Rectangle), (Key.M, DrawingTool.Selection),
            (Key.B, DrawingTool.Pencil)
        };
        foreach (var (key, tool) in tools)
        {
            KeyboardInput.Type(source, key);
            KeyboardInput.WaitUntil(() => EditorCanvas.Tool == tool, $"{key} did not select the {tool} tool");
        }

        // The brush keys, which also prove an unmodified punctuation key arrives as itself.
        var radius = EditorCanvas.BrushRadius;
        KeyboardInput.Type(source, Key.OemCloseBrackets);
        KeyboardInput.WaitUntil(() => EditorCanvas.BrushRadius == radius + 1, "] did not grow the brush");
        KeyboardInput.Type(source, Key.OemOpenBrackets);
        KeyboardInput.WaitUntil(() => EditorCanvas.BrushRadius == radius, "[ did not shrink the brush back");

        var grid = EditorCanvas.ShowGrid;
        KeyboardInput.Type(source, Key.H);
        KeyboardInput.WaitUntil(() => EditorCanvas.ShowGrid != grid, "H did not toggle the grid");
        KeyboardInput.Type(source, Key.H);
        KeyboardInput.WaitUntil(() => EditorCanvas.ShowGrid == grid, "H did not toggle the grid back");

        // Space is held rather than struck, and is the one thing that reaches OnKeyUp, so a key that is never
        // released would show up here as a canvas stuck in panning.
        KeyboardInput.Press(source, Key.Space);
        KeyboardInput.WaitUntil(() => EditorCanvas.SpacePressed, "holding Space did not arm panning");
        KeyboardInput.Release(source, Key.Space);
        KeyboardInput.WaitUntil(() => !EditorCanvas.SpacePressed, "releasing Space did not disarm panning");

        // Control, read from the real key state.
        KeyboardInput.Type(source, Key.A, ModifierKeys.Control);
        KeyboardInput.WaitUntil(() => session.Selection.Any(), "Ctrl+A did not select anything");
        KeyboardInput.Type(source, Key.D, ModifierKeys.Control);
        KeyboardInput.WaitUntil(() => !session.Selection.Any(), "Ctrl+D did not clear the selection");

        // Control and Shift together, against something undoable. The stroke is made through the session
        // rather than with the pointer: giving WPF a keyboard focus makes it work out afresh where the
        // pointer is by asking the system, and on a desktop that is not being displayed there is no cursor to
        // report, so it settles outside the window and stops accepting driven moves for good. The pointer
        // pipeline is covered on its own in VerifyPointerInput, which is why that runs before this, and what
        // these keys are here to prove is the shortcut, not the stroke.
        var document = session.Document;
        var cell = HexGeometry.FromOffset(6, document.Height / 2, document.Orientation);
        const uint stroke = 0xFF20C040;
        SetColor(stroke);
        session.BeginStroke();
        session.Paint(cell, stroke);
        session.EndStroke();
        var painted = document.IndexOf(cell);
        if (painted < 0 || document.CompositeAt(painted) != stroke)
            throw new InvalidOperationException($"The cell the keyboard checks undo was not painted: cell {cell.Q},{cell.R} is index {painted}.");
        KeyboardInput.Type(source, Key.Z, ModifierKeys.Control);
        KeyboardInput.WaitUntil(() => document.CompositeAt(painted) == 0, "Ctrl+Z did not undo the stroke");
        KeyboardInput.Type(source, Key.Z, ModifierKeys.Control | ModifierKeys.Shift);
        KeyboardInput.WaitUntil(() => document.CompositeAt(painted) == stroke, "Ctrl+Shift+Z did not redo the stroke");
        KeyboardInput.Type(source, Key.Z, ModifierKeys.Control);
        KeyboardInput.WaitUntil(() => document.CompositeAt(painted) == 0, "Ctrl+Z did not undo the redone stroke");

        // Modifiers must not be left down, or every later key would arrive as a shortcut.
        if (Keyboard.Modifiers != ModifierKeys.None)
            throw new InvalidOperationException($"The keyboard checks left {Keyboard.Modifiers} held down.");
        if (session.IsDirty) throw new InvalidOperationException("The keyboard checks left the document unsaved.");
        File.AppendAllText(System.IO.Path.Combine(folder, "Verification.txt"),
            $"Keyboard input: {tools.Length} tool keys, brush and grid keys, Space held and released, Ctrl+A, Ctrl+D, Ctrl+Z and Ctrl+Shift+Z verified through real key messages with the modifiers really held\n");
    }

    // The three ways the canvas draws cells, each checked where it is actually used. A large canvas is drawn
    // at the zoom it is drawn at, and a cell's colour has to land where that cell is: the raster has its own
    // mapping back to the artwork's coordinates and the mesh has its own corner arithmetic, so either could
    // put the artwork in the wrong place without anything else noticing.
    public void VerifyRenderTiers(string folder)
    {
        const int width = 400, height = 200;
        var document = new HexDocument(width, height);
        var layer = document.Layers[0];
        // Four quadrants of flat colour, so where each one lands says whether the geometry is right.
        var quadrants = new uint[] { 0xFF1E89DA, 0xFF2FC866, 0xFFE4D114, 0xFFD5236C };
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
                layer.Cells[row * width + column] = quadrants[(row < height / 2 ? 0 : 2) + (column < width / 2 ? 0 : 1)];

        using var cache = new ArtworkCache();
        var checks = new List<string>();
        const int side = 600;
        foreach (var cellPixels in new[] { 1.5f, 5f, 10f, 20f })
        {
            var zoom = cellPixels / (float)(Math.Sqrt(3) * ArtworkRenderer.CellRadius);
            var detail = ArtworkRenderer.DetailFor(cellPixels, true);
            // A point a quarter and three quarters of the way along each axis sits well inside one quadrant,
            // far enough from a boundary that filtering cannot carry a neighbour's colour into it. The view is
            // placed to put that cell at the middle of the surface, so this works at any zoom, including the
            // ones where the canvas is far larger than the window, which is the case that matters.
            for (var quadrant = 0; quadrant < 4; quadrant++)
            {
                var column = quadrant % 2 == 0 ? width / 4 : width * 3 / 4;
                var row = quadrant < 2 ? height / 4 : height * 3 / 4;
                var (x, y) = HexGeometry.Center(HexGeometry.FromOffset(column, row, document.Orientation), document.Orientation, ArtworkRenderer.CellRadius);
                using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(side, side))
                    ?? throw new InvalidOperationException("Could not create the render tier surface.");
                surface.Canvas.Clear(SkiaSharp.SKColors.White);
                surface.Canvas.Translate(side / 2f - (float)x * zoom, side / 2f - (float)y * zoom);
                surface.Canvas.Scale(zoom);
                ArtworkRenderer.Draw(surface.Canvas, document, background: true, grid: false, zoom: zoom, cache: cache);
                using var image = surface.Snapshot();
                using var bitmap = SkiaSharp.SKBitmap.FromImage(image);
                var sample = (uint)bitmap.GetPixel(side / 2, side / 2);
                var expected = quadrants[quadrant];
                if (!CloseEnough(sample, expected))
                    throw new InvalidOperationException(
                        $"Drawing at {detail} detail, {cellPixels} pixels a cell, put {ColorMath.Format(sample)} at cell {column},{row} where {ColorMath.Format(expected)} belongs.");
            }
            checks.Add($"{detail} at {cellPixels:0.#} px");
        }
        File.AppendAllText(System.IO.Path.Combine(folder, "Verification.txt"),
            $"Render tiers: {string.Join(", ", checks)} verified on a {width} x {height} canvas\n");
    }

    // Filtering and the raster's two pixels a cell both move a colour slightly, so the check is on being the
    // right colour rather than the exact bytes. A wrong quadrant is nowhere near.
    private static bool CloseEnough(uint sample, uint expected)
    {
        int Channel(uint color, int shift) => (int)((color >> shift) & 255);
        return Math.Abs(Channel(sample, 16) - Channel(expected, 16)) <= 24
            && Math.Abs(Channel(sample, 8) - Channel(expected, 8)) <= 24
            && Math.Abs(Channel(sample, 0) - Channel(expected, 0)) <= 24;
    }

    public void VerifyInterface(string folder)
    {
        var submenuPath = VerifyMenu();
        VerifyDialogs();
        File.AppendAllText(System.IO.Path.Combine(folder, "Verification.txt"), $"Menu bar and dialogs: passed, submenu opened {submenuPath}\n");
    }

    // Returns how the submenu was opened. A WPF menu captures the mouse so it can tell when to close itself,
    // and only a window on the input desktop can hold capture, so in a locked session the menu item refuses
    // to keep its submenu open. Opening the popup directly asks for no capture, which still builds, lays out
    // and measures the submenu. Which path was taken belongs in the report rather than being swallowed: the
    // fallback is expected while the session is locked and a defect while it is not, and only the caller
    // knows which of those it is looking at.
    private string VerifyMenu()
    {
        FileMenu.ApplyTemplate();
        // A menu item refuses to open its submenu before it has been loaded, so let pending Loaded events run first.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        if (FileMenu.Template.FindName("Submenu", FileMenu) is not Popup popup)
            throw new InvalidOperationException("The File menu has no submenu to open.");
        FileMenu.IsSubmenuOpen = true;
        UpdateLayout();
        var submenuPath = "through the menu item";
        var staysOpen = popup.StaysOpen;
        if (!popup.IsOpen)
        {
            popup.StaysOpen = true;
            popup.IsOpen = true;
            UpdateLayout();
            submenuPath = "directly, with no mouse capture";
        }
        if (!popup.IsOpen || popup.Child is not FrameworkElement submenu)
            throw new InvalidOperationException("The File menu did not open.");
        submenu.UpdateLayout();
        if (submenu.ActualWidth < 120 || submenu.ActualHeight < 120) throw new InvalidOperationException("The File menu rendered empty.");
        popup.IsOpen = false;
        // Restored only once the popup is closed, because a popup that is open when StaysOpen goes back to
        // false immediately reaches for the capture it was opened without.
        popup.StaysOpen = staysOpen;
        FileMenu.IsSubmenuOpen = false;
        return submenuPath;
    }

    // The dialogs build their visual tree in code from the application's styles, so measuring one proves those styles apply.
    private static void VerifyDialogs()
    {
        var dialogs = new (string Name, FrameworkElement Content)[]
        {
            ("Shortcuts", EditorDialogs.HelpContent()),
            ("New canvas", (FrameworkElement)new NewCanvasDialog().Content)
        };
        foreach (var (name, content) in dialogs)
        {
            content.Measure(new Size(560, 1000));
            if (content.DesiredSize.Width < 200 || content.DesiredSize.Height < 200) throw new InvalidOperationException($"The {name} dialog did not lay out.");
        }
    }

    public void VerifyCanvasRendered()
    {
        if (EditorCanvas.CanvasSize.Width <= 0 || EditorCanvas.CanvasSize.Height <= 0) throw new InvalidOperationException("The editor canvas did not render.");
    }
}
