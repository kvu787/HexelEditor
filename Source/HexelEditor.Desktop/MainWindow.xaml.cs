using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HexelEditor.Core;
using Microsoft.Win32;

namespace HexelEditor.Desktop;

public partial class MainWindow : Window
{
    private EditorSession session = new(ExampleDocument.Create());
    private readonly DispatcherTimer playback = new();
    private readonly Dictionary<DrawingTool, Button> toolButtons = [];
    private readonly Dictionary<int, BitmapSource> thumbnails = [];
    private CellClipboard? clipboard;
    private string? projectPath;
    private bool refreshing;
    private readonly Brush accent;
    private readonly Brush panelRaised;
    private readonly Brush outline;
    private readonly Brush muted;

    public MainWindow()
    {
        InitializeComponent();
        accent = (Brush)FindResource("Accent"); panelRaised = (Brush)FindResource("PanelRaised"); outline = (Brush)FindResource("Outline"); muted = (Brush)FindResource("Muted");
        BuildTools();
        EditorCanvas.ZoomChanged += () => ZoomLabel.Text = $"{EditorCanvas.Zoom * 100:0}%";
        EditorCanvas.StatusChanged += text => StatusLabel.Text = text;
        EditorCanvas.ColorPicked += SetColor;
        LayerOpacity.ValueChanged += (_, _) => OpacityLabel.Text = $"{LayerOpacity.Value / 255 * 100:0}%";
        FilledRectangle.Checked += (_, _) => EditorCanvas.FilledShapes = true;
        FilledRectangle.Unchecked += (_, _) => EditorCanvas.FilledShapes = false;
        playback.Tick += (_, _) =>
        {
            session.SelectFrame((session.ActiveFrame + 1) % session.Document.Frames.Count);
            playback.Interval = TimeSpan.FromMilliseconds(session.Document.Frames[session.ActiveFrame].DurationMilliseconds);
        };
        AttachSession(session);
        SetColor(0xFF5CE4C2);
        SetTool(DrawingTool.Pencil);
        Loaded += (_, _) => { EditorCanvas.Fit(); EditorCanvas.Focus(); };
        Deactivated += (_, _) => { EditorCanvas.SpacePressed = false; EditorCanvas.FinishGesture(); };
    }

    private void AttachSession(EditorSession value)
    {
        StopPlayback(); session.Changed -= SessionChanged; session = value;
        session.SelectLayer(session.Document.Layers.Count - 1);
        session.Changed += SessionChanged;
        thumbnails.Clear(); EditorCanvas.Attach(session); RefreshPanels(); UpdateHeader();
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
            var content = new StackPanel();
            content.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(definition.Geometry), Stroke = (Brush)FindResource("Ink"), StrokeThickness = 1.5, Width = 22, Height = 22, Stretch = Stretch.Uniform });
            content.Children.Add(new TextBlock { Text = definition.Key, FontSize = 9, Foreground = (Brush)FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            var button = new Button { Content = content, Margin = new Thickness(0, 0, 0, 7), Padding = new Thickness(7), ToolTip = $"{definition.Tool} ({definition.Key})" };
            AutomationProperties.SetName(button, definition.Tool.ToString());
            button.Click += (_, _) => SetTool(definition.Tool);
            ToolsPanel.Children.Add(button); toolButtons.Add(definition.Tool, button);
        }
    }

    private void SetTool(DrawingTool tool)
    {
        EditorCanvas.FinishGesture(); EditorCanvas.Tool = tool;
        foreach (var (key, button) in toolButtons) { button.Background = key == tool ? new SolidColorBrush(Color.FromRgb(40, 65, 63)) : Brushes.Transparent; button.BorderBrush = key == tool ? accent : Brushes.Transparent; }
        ToolLabel.Text = tool == DrawingTool.Selection ? "Selection  /  arrows to move" : tool.ToString();
        EditorCanvas.Focus(); EditorCanvas.InvalidateVisual();
    }

    private void SetColor(uint color)
    {
        EditorCanvas.ForegroundColor = color;
        CurrentColorPreview.Background = BrushFor(color);
        CurrentColorLabel.Text = ColorMath.Format(color); ColorInput.Text = ColorMath.Format(color);
        RefreshPalette(); EditorCanvas.InvalidateVisual();
    }

    private static SolidColorBrush BrushFor(uint color) => new(Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color));

    private void SessionChanged(EditorChange change)
    {
        if ((change & EditorChange.Document) != 0) thumbnails.Clear();
        else if ((change & EditorChange.Pixels) != 0) thumbnails.Remove(session.ActiveFrame);
        if ((change & EditorChange.Structure) != 0) RefreshPanels();
        else if ((change & EditorChange.History) != 0) RefreshFrames();
        EditorCanvas.InvalidateVisual(); UpdateHeader();
    }

    private void UpdateHeader()
    {
        var name = projectPath == null ? session.Document.Name : System.IO.Path.GetFileNameWithoutExtension(projectPath);
        DocumentTitle.Text = name + (session.IsDirty ? "  *" : "");
        Title = $"{name}{(session.IsDirty ? " *" : "")} - Hexel Editor";
        DocumentDetails.Text = $"{session.Document.Width} x {session.Document.Height} hexels  /  {(session.Document.Orientation == HexOrientation.PointyTop ? "Pointy top" : "Flat top")}";
        UndoButton.IsEnabled = session.CanUndo; UndoButton.ToolTip = session.CanUndo ? $"Undo {session.UndoLabel} (Ctrl+Z)" : "Nothing to undo";
        RedoButton.IsEnabled = session.CanRedo; RedoButton.ToolTip = session.CanRedo ? $"Redo {session.RedoLabel} (Ctrl+Y)" : "Nothing to redo";
        SelectionStatus.Text = $"{(session.Selection.Count > 0 ? $"{session.Selection.Count} selected  |  " : "")}{(session.CanPaint ? "" : "Layer locked  |  ")}Frame {session.ActiveFrame + 1} / {session.Document.Frames.Count}";
    }

    private void RefreshPanels()
    {
        refreshing = true;
        try
        {
            RefreshPalette(); RefreshLayers(); RefreshFrames();
            LayerOpacity.Value = session.Document.Layers[session.ActiveLayer].Opacity;
            OpacityLabel.Text = $"{LayerOpacity.Value / 255 * 100:0}%";
            DurationInput.Text = session.Document.Frames[session.ActiveFrame].DurationMilliseconds.ToString();
            DeleteLayerButton.IsEnabled = session.Document.Layers.Count > 1;
            DeleteFrameButton.IsEnabled = session.Document.Frames.Count > 1;
        }
        finally { refreshing = false; }
    }

    private void RefreshPalette()
    {
        PalettePanel.Children.Clear();
        foreach (var color in session.Document.Palette)
        {
            var button = new Button { Width = 27, Height = 27, Margin = new Thickness(0, 0, 3, 3), Padding = new Thickness(0), Background = BrushFor(color), BorderBrush = color == EditorCanvas.ForegroundColor ? Brushes.White : Brushes.Transparent, BorderThickness = new Thickness(2), ToolTip = ColorMath.Format(color) };
            AutomationProperties.SetName(button, $"Color {ColorMath.Format(color)}");
            button.Click += (_, _) => { SetColor(color); EditorCanvas.Focus(); };
            PalettePanel.Children.Add(button);
        }
    }

    private void RefreshLayers()
    {
        LayersPanel.Children.Clear(); LayerCountLabel.Text = $"{session.Document.Layers.Count} layers";
        for (var index = session.Document.Layers.Count - 1; index >= 0; index--)
        {
            var layerIndex = index; var layer = session.Document.Layers[index];
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
            var visibility = new CheckBox { IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Layer visibility" };
            AutomationProperties.SetName(visibility, $"Show {layer.Name}");
            visibility.Click += (_, _) => Act(() => session.SetLayerVisible(layerIndex, visibility.IsChecked == true)); grid.Children.Add(visibility);
            var name = new TextBlock { Text = layer.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
            Grid.SetColumn(name, 1); grid.Children.Add(name);
            var locked = new Button { Content = layer.Locked ? "L" : "-", FontSize = 10, Padding = new Thickness(3), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, ToolTip = layer.Locked ? "Unlock layer" : "Lock layer" };
            AutomationProperties.SetName(locked, $"{(layer.Locked ? "Unlock" : "Lock")} {layer.Name}");
            locked.Click += (_, args) => { args.Handled = true; Act(() => session.SetLayerLocked(layerIndex, !layer.Locked)); }; Grid.SetColumn(locked, 2); grid.Children.Add(locked);
            var row = new Border { Child = grid, Padding = new Thickness(9, 10, 5, 10), Background = index == session.ActiveLayer ? new SolidColorBrush(Color.FromRgb(40, 54, 58)) : Brushes.Transparent, BorderBrush = index == session.ActiveLayer ? accent : outline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 0, 5), Cursor = Cursors.Hand };
            row.MouseLeftButtonDown += (_, args) => { if (args.OriginalSource is not CheckBox && args.OriginalSource is not Button) { Act(() => session.SelectLayer(layerIndex)); args.Handled = true; } };
            LayersPanel.Children.Add(row);
        }
    }

    private void RefreshFrames()
    {
        FramesPanel.Children.Clear();
        for (var index = 0; index < session.Document.Frames.Count; index++)
        {
            var frameIndex = index;
            if (!thumbnails.TryGetValue(index, out var thumbnail)) { thumbnail = ArtworkRenderer.Thumbnail(session.Document, index); thumbnails.Add(index, thumbnail); }
            var content = new StackPanel(); content.Children.Add(new Image { Source = thumbnail, Width = 76, Height = 42, Stretch = Stretch.Uniform });
            content.Children.Add(new TextBlock { Text = $"{index + 1:00}    {session.Document.Frames[index].DurationMilliseconds} ms", FontSize = 10, Foreground = index == session.ActiveFrame ? accent : muted, Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button { Content = content, Width = 104, Padding = new Thickness(6, 5, 6, 4), Margin = new Thickness(0, 0, 8, 0), Background = index == session.ActiveFrame ? new SolidColorBrush(Color.FromRgb(35, 51, 52)) : panelRaised, BorderBrush = index == session.ActiveFrame ? accent : outline, ToolTip = $"Frame {index + 1}" };
            AutomationProperties.SetName(button, $"Frame {index + 1}");
            button.Click += (_, _) => Act(() => session.SelectFrame(frameIndex)); FramesPanel.Children.Add(button);
        }
    }

    private void Act(Action action)
    {
        StopPlayback(); EditorCanvas.FinishGesture();
        try { action(); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException) { ShowError(exception.Message); }
    }

    private void ShowError(string message) => MessageBox.Show(this, message, "Hexel Editor", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ShowMenu(Button source, params (string Name, string Shortcut, Action Action, bool Enabled)[] entries)
    {
        var menu = new ContextMenu { PlacementTarget = source, Placement = PlacementMode.Bottom };
        foreach (var entry in entries)
        {
            var item = new MenuItem { Header = entry.Name, InputGestureText = entry.Shortcut, IsEnabled = entry.Enabled };
            item.Click += (_, _) => entry.Action(); menu.Items.Add(item);
        }
        source.ContextMenu = menu; menu.IsOpen = true;
    }

    private void FileMenuClicked(object sender, RoutedEventArgs args) => ShowMenu((Button)sender,
        ("New canvas...", "Ctrl+N", NewProject, true), ("Open project...", "Ctrl+O", OpenProjectDialog, true),
        ("Save", "Ctrl+S", () => SaveProject(false), true), ("Save as...", "Ctrl+Shift+S", () => SaveProject(true), true),
        ("Export PNG...", "", () => Export("png", false), true), ("Export SVG...", "", () => Export("svg", false), true),
        ("Export sprite sheet...", "", () => Export("png", true), true), ("Open example scene", "", OpenExample, true));

    private void EditMenuClicked(object sender, RoutedEventArgs args) => ShowMenu((Button)sender,
        ($"Undo {session.UndoLabel}", "Ctrl+Z", () => Act(session.Undo), session.CanUndo), ($"Redo {session.RedoLabel}", "Ctrl+Y", () => Act(session.Redo), session.CanRedo),
        ("Copy cells", "Ctrl+C", Copy, true), ("Cut cells", "Ctrl+X", Cut, session.CanPaint), ("Paste at cursor", "Ctrl+V", Paste, clipboard != null && session.CanPaint),
        ("Rotate selection clockwise 60 degrees", "Ctrl+R", () => Act(() => session.RotateSelection(1)), session.Selection.Count > 0 && session.CanPaint),
        ("Rotate selection counterclockwise 60 degrees", "Ctrl+Shift+R", () => Act(() => session.RotateSelection(-1)), session.Selection.Count > 0 && session.CanPaint),
        ("Select all", "Ctrl+A", session.SelectAll, true), ("Deselect", "Esc", session.ClearSelection, true), ("Clear cells", "Delete", () => Act(session.ClearPixels), session.CanPaint));

    private void ViewMenuClicked(object sender, RoutedEventArgs args) => ShowMenu((Button)sender,
        ("Fit canvas", "F", EditorCanvas.Fit, true), (EditorCanvas.ShowGrid ? "Hide grid" : "Show grid", "H", ToggleGrid, true),
        ("Zoom in", "+", () => EditorCanvas.ChangeZoom(1.2f), true), ("Zoom out", "-", () => EditorCanvas.ChangeZoom(1 / 1.2f), true));

    private void NewProject()
    {
        var dialog = new NewCanvasDialog { Owner = this };
        if (dialog.ShowDialog() != true || !CanReplaceDocument()) return;
        projectPath = null; AttachSession(new EditorSession(new HexDocument(dialog.CanvasWidth, dialog.CanvasHeight, dialog.Orientation) { Name = dialog.CanvasName }));
        StatusLabel.Text = "New canvas. Choose a color and make your first mark.";
    }

    private void OpenExample() { if (CanReplaceDocument()) { projectPath = null; AttachSession(new EditorSession(ExampleDocument.Create())); } }

    private bool CanReplaceDocument()
    {
        EditorCanvas.FinishGesture(); StopPlayback();
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
        try { var document = ProjectFile.Load(path); projectPath = System.IO.Path.GetFullPath(path); AttachSession(new EditorSession(document)); StatusLabel.Text = $"Opened {System.IO.Path.GetFileName(path)}"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException) { ShowError($"Could not open the project.\n\n{exception.Message}"); }
    }

    private bool SaveProject(bool saveAs)
    {
        EditorCanvas.FinishGesture(); StopPlayback();
        var path = projectPath;
        if (path == null || saveAs)
        {
            var dialog = new SaveFileDialog { Filter = "Hexel project (*.hexel)|*.hexel", DefaultExt = ".hexel", FileName = path == null ? session.Document.Name : System.IO.Path.GetFileName(path) };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        try { ProjectFile.Save(session.Document, path); projectPath = path; session.MarkSaved(); StatusLabel.Text = $"Saved {System.IO.Path.GetFileName(path)}"; return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { ShowError($"Could not save the project.\n\n{exception.Message}"); return false; }
    }

    private void Export(string format, bool sheet)
    {
        StopPlayback(); EditorCanvas.FinishGesture();
        var dialog = new SaveFileDialog { Filter = format == "svg" ? "SVG artwork (*.svg)|*.svg" : "PNG image (*.png)|*.png", DefaultExt = "." + format, FileName = session.Document.Name + (sheet ? " Sheet" : "") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (format == "svg") ArtworkRenderer.ExportSvg(session.Document, session.ActiveFrame, dialog.FileName);
            else ArtworkRenderer.ExportPng(session.Document, session.ActiveFrame, dialog.FileName, sheet);
            StatusLabel.Text = $"Exported {System.IO.Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { ShowError($"Could not export.\n\n{exception.Message}"); }
    }

    private void StopPlayback() { playback.Stop(); EditorCanvas.IsPlaying = false; PlayButton.Content = "Play"; EditorCanvas.InvalidateVisual(); }
    private void TogglePlayback()
    {
        EditorCanvas.FinishGesture();
        if (playback.IsEnabled) { StopPlayback(); return; }
        EditorCanvas.IsPlaying = true; PlayButton.Content = "Stop";
        playback.Interval = TimeSpan.FromMilliseconds(session.Document.Frames[session.ActiveFrame].DurationMilliseconds); playback.Start();
    }

    private void Copy() { EditorCanvas.FinishGesture(); clipboard = session.CopySelection(); StatusLabel.Text = "Copied cells. Hover the destination and press Ctrl+V to paste."; }
    private void Cut() { Copy(); Act(session.ClearPixels); }
    private void Paste()
    {
        if (clipboard == null) return;
        var coordinate = EditorCanvas.HoverCoordinate;
        var (column, row) = coordinate.HasValue ? HexGeometry.ToOffset(coordinate.Value, session.Document.Orientation) : (0, 0);
        if (column < 0 || column >= session.Document.Width || row < 0 || row >= session.Document.Height) (column, row) = (0, 0);
        Act(() => session.Paste(clipboard, column, row));
    }

    private void ToggleGrid() { EditorCanvas.ShowGrid = !EditorCanvas.ShowGrid; EditorCanvas.InvalidateVisual(); }
    private void ChangeBrush(int delta) { EditorCanvas.BrushRadius = Math.Clamp(EditorCanvas.BrushRadius + delta, 0, 8); BrushSizeLabel.Text = (EditorCanvas.BrushRadius * 2 + 1).ToString(); EditorCanvas.InvalidateVisual(); }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (Keyboard.FocusedElement is TextBoxBase) { if (args.Key == Key.Escape) EditorCanvas.Focus(); return; }
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (Keyboard.FocusedElement is Slider && !control) return;
        if (control)
        {
            switch (args.Key)
            {
                case Key.N: NewProject(); break; case Key.O: OpenProjectDialog(); break; case Key.S: SaveProject(shift); break;
                case Key.Z: Act(shift ? session.Redo : session.Undo); break; case Key.Y: Act(session.Redo); break;
                case Key.A: session.SelectAll(); break; case Key.D: session.ClearSelection(); break;
                case Key.C: Copy(); break; case Key.X: Cut(); break; case Key.V: Paste(); break; default: return;
                case Key.R: Act(() => session.RotateSelection(shift ? -1 : 1)); break;
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
            case Key.Escape: StopPlayback(); session.ClearSelection(); break;
            case Key.Space: EditorCanvas.SpacePressed = true; break; case Key.Enter: if (!args.IsRepeat) TogglePlayback(); break;
            case Key.Left: Act(() => session.MoveSelection(-1, 0)); break; case Key.Right: Act(() => session.MoveSelection(1, 0)); break;
            case Key.Up: Act(() => session.MoveSelection(0, -1)); break; case Key.Down: Act(() => session.MoveSelection(0, 1)); break;
            case Key.OemComma: Act(() => session.SelectFrame((session.ActiveFrame + session.Document.Frames.Count - 1) % session.Document.Frames.Count)); break;
            case Key.OemPeriod: Act(() => session.SelectFrame((session.ActiveFrame + 1) % session.Document.Frames.Count)); break;
            default: return;
        }
        args.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs args) { if (args.Key == Key.Space) EditorCanvas.SpacePressed = false; }
    private void OnClosing(object? sender, CancelEventArgs args) { if (!CanReplaceDocument()) args.Cancel = true; else StopPlayback(); }
    private void OpenClicked(object sender, RoutedEventArgs args) => OpenProjectDialog();
    private void SaveClicked(object sender, RoutedEventArgs args) => SaveProject(false);
    private void ExportPngClicked(object sender, RoutedEventArgs args) => Export("png", false);
    private void UndoClicked(object sender, RoutedEventArgs args) => Act(session.Undo);
    private void RedoClicked(object sender, RoutedEventArgs args) => Act(session.Redo);
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
    private void OpacityReleased(object sender, MouseButtonEventArgs args) => ApplyOpacity();
    private void OpacityKeyboardFinished(object sender, KeyboardFocusChangedEventArgs args) => ApplyOpacity();
    private void ApplyOpacity() { if (!refreshing) Act(() => session.SetLayerOpacity((byte)Math.Round(LayerOpacity.Value))); }
    private void AddFrameClicked(object sender, RoutedEventArgs args) => Act(() => session.AddFrame(false));
    private void DuplicateFrameClicked(object sender, RoutedEventArgs args) => Act(() => session.AddFrame(true));
    private void DeleteFrameClicked(object sender, RoutedEventArgs args) => Act(session.DeleteFrame);
    private void PlayClicked(object sender, RoutedEventArgs args) => TogglePlayback();
    private void OnionSkinChanged(object sender, RoutedEventArgs args) { EditorCanvas.ShowOnionSkin = OnionSkin.IsChecked == true; EditorCanvas.InvalidateVisual(); }
    private void SetDurationClicked(object sender, RoutedEventArgs args)
    {
        if (int.TryParse(DurationInput.Text, out var duration)) Act(() => session.SetFrameDuration(duration)); else ShowError("Enter a duration between 20 and 10000 milliseconds.");
    }
    private void SetColorClicked(object sender, RoutedEventArgs args) { try { SetColor(ColorMath.Parse(ColorInput.Text)); EditorCanvas.Focus(); } catch (FormatException exception) { ShowError(exception.Message); } }
    private void ColorInputKeyDown(object sender, KeyEventArgs args) { if (args.Key == Key.Enter) { SetColorClicked(sender, args); args.Handled = true; } }
    private void AddColorClicked(object sender, RoutedEventArgs args) => Act(() => session.AddPaletteColor(EditorCanvas.ForegroundColor));
    private void HelpClicked(object sender, RoutedEventArgs args) => EditorDialogs.ShowHelp(this);

    public void VerifyEditor(string folder)
    {
        var original = session.Document.CompositeAt(0, 0);
        session.Paint(session.Document.CoordinateAt(0), 0xFF00FF00); session.EndStroke();
        if (!UndoButton.IsEnabled || !DocumentTitle.Text.EndsWith('*')) throw new InvalidOperationException("The interface did not reflect the edit.");
        UndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (session.IsDirty || session.Document.CompositeAt(0, 0) != original) throw new InvalidOperationException("The Undo button did not restore the artwork.");
        toolButtons[DrawingTool.Eraser].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (EditorCanvas.Tool != DrawingTool.Eraser) throw new InvalidOperationException("The tool button did not select the eraser.");
        SetTool(DrawingTool.Pencil);
        AttachSession(new EditorSession(ExampleDocument.Create()));
        var path = System.IO.Path.Combine(folder, "Example.hexel"); ProjectFile.Save(session.Document, path);
        var roundTrip = ProjectFile.Load(path);
        if (roundTrip.Frames.Count != 4 || roundTrip.CompositeAt(0, 0) != session.Document.CompositeAt(0, 0)) throw new InvalidDataException("Project round trip failed.");
        ArtworkRenderer.ExportPng(roundTrip, 0, System.IO.Path.Combine(folder, "Artwork.png"), false);
        ArtworkRenderer.ExportPng(roundTrip, 0, System.IO.Path.Combine(folder, "SpriteSheet.png"), true);
        ArtworkRenderer.ExportSvg(roundTrip, 0, System.IO.Path.Combine(folder, "Artwork.svg"));
        using var image = SkiaSharp.SKBitmap.Decode(System.IO.Path.Combine(folder, "Artwork.png"));
        if (image == null || image.Width < 100 || image.Height < 100) throw new InvalidDataException("PNG export verification failed.");
        var bounds = ArtworkRenderer.Bounds(roundTrip);
        for (var index = 0; index < roundTrip.CellCount; index++)
        {
            var (x, y) = HexGeometry.Center(roundTrip.CoordinateAt(index), roundTrip.Orientation, 16);
            var pixel = image.GetPixel((int)Math.Round(x + 1 - bounds.Left * 16 / ArtworkRenderer.CellRadius), (int)Math.Round(y + 1 - bounds.Top * 16 / ArtworkRenderer.CellRadius));
            if ((uint)pixel != roundTrip.CompositeAt(index, 0)) throw new InvalidDataException($"PNG color mismatch at cell {index}.");
        }
        using var sheet = SkiaSharp.SKBitmap.Decode(System.IO.Path.Combine(folder, "SpriteSheet.png"));
        if (sheet.Width != image.Width * roundTrip.Frames.Count || sheet.Height != image.Height) throw new InvalidDataException("Sprite sheet dimensions are incorrect.");
        var svg = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(folder, "Artwork.svg"));
        if (svg.Root?.Elements().Count() != roundTrip.CellCount) throw new InvalidDataException("SVG geometry is incomplete.");
        var blankPath = System.IO.Path.Combine(folder, "Transparent.png");
        ArtworkRenderer.ExportPng(new HexDocument(1, 1, HexOrientation.FlatTop), 0, blankPath, false);
        using var blank = SkiaSharp.SKBitmap.Decode(blankPath);
        if (blank.Pixels.Any(pixel => pixel.Alpha != 0)) throw new InvalidDataException("Empty exported cells are not transparent.");
        EditorCanvas.Fit(); UpdateLayout();
        File.WriteAllText(System.IO.Path.Combine(folder, "Verification.txt"), $"WPF commands and saved-state indicator: passed\nProject save/load: passed\nPNG export: {image.Width} x {image.Height}, all {roundTrip.CellCount} cell colors verified\nSprite sheet dimensions and SVG polygons: passed\nFlat-top transparent export: passed\n");
    }

    public void VerifyCanvasRendered()
    {
        if (EditorCanvas.CanvasSize.Width <= 0 || EditorCanvas.CanvasSize.Height <= 0) throw new InvalidOperationException("The editor canvas did not render.");
    }
}
