using Experiment.Core;
using Godot;

namespace GodotAgentDevExperiment;

/// <summary>A small stand-in for the level editor: an orthographic overhead camera over a hex ground that
/// can be painted, a Blender-exported piece under SimplePaint, a menu bar, dialogs and shortcuts. Everything
/// is built in code so that nothing depends on scene files. The --checks mode drives all of it from inside
/// the process and reports what worked, which is the experiment.</summary>
public partial class Main : Node
{
    public static readonly Color PaintColor = new(0.85f, 0.20f, 0.15f);
    public static readonly Color PaperColor = new(0.93f, 0.93f, 0.90f);
    private const float ThreeQuarterPitchDegrees = 55.0f;

    public Arguments Arguments { get; private set; } = null!;
    public SessionLog Log { get; private set; } = null!;
    public Camera3D Camera { get; private set; } = null!;
    public HexGround Ground { get; private set; } = null!;
    public MenuBar MenuBar { get; private set; } = null!;
    public PopupMenu FileMenu { get; private set; } = null!;
    public Button PaintButton { get; private set; } = null!;
    public Button EraseButton { get; private set; } = null!;
    public Button TopDownButton { get; private set; } = null!;
    public ConfirmationDialog NewMapDialog { get; private set; } = null!;
    public FileDialog OpenPieceDialog { get; private set; } = null!;
    public Label Status { get; private set; } = null!;
    public PieceLoader.LoadedPiece? Piece { get; private set; }

    // What the checks read back.
    public string Tool { get; private set; } = "paint";
    public int ToolPresses { get; private set; }
    public int NewMapRequests { get; private set; }
    public int NewMapConfirmations { get; private set; }
    public int OpenPieceRequests { get; private set; }
    public int UndoRequests { get; private set; }
    public int PitchToggles { get; private set; }
    public bool TopDown { get; private set; }
    public HexCell? HoverCell { get; private set; }
    public HexCell? LastPaintedCell { get; private set; }
    public int PaintedCells { get; private set; }
    public double LastUploadMilliseconds { get; private set; }

    private readonly Stack<(HexCell Cell, Color Previous)> _undo = new();
    private bool _quitting;

    public override void _Ready()
    {
        Arguments = Arguments.Parse(OS.GetCmdlineUserArgs());
        Log = new SessionLog(Arguments.LogFolder);
        Log.Write($"Display server {DisplayServer.GetName()}; rendering {RenderingServer.GetCurrentRenderingMethod()} on {RenderingServer.GetCurrentRenderingDriverName()}; adapter '{RenderingServer.GetVideoAdapterName()}'.");
        Log.Write($"Window {DisplayServer.WindowGetSize()} at {DisplayServer.WindowGetPosition()} on screen {DisplayServer.WindowGetCurrentScreen()} of {DisplayServer.GetScreenCount()}; scale {DisplayServer.ScreenGetScale()}.");
        if (DisplayServer.GetName() == "headless")
        {
            // Without a display server the root window is a few pixels square, so the interface would be laid
            // out into nothing. The size still drives the layout, so it is given the size a real window has.
            GetWindow().Size = new Vector2I(1280, 800);
        }

        BuildWorld();
        BuildInterface();
        LoadPiece(Arguments.Piece ?? Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "Assets", "LBuilding.glb")));

        if (Arguments.Checks)
        {
            _ = Checks.Run(this);
        }
    }

    public override void _ExitTree()
    {
        Log.Dispose();
    }

    private void BuildWorld()
    {
        Ground = new HexGround { Name = "Ground" };
        AddChild(Ground);
        Ground.Build(Arguments.Columns, Arguments.Rows, PaperColor);

        Camera = new Camera3D
        {
            Name = "Camera",
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = 36.0f,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Near = 0.1f,
            Far = 400.0f,
        };
        AddChild(Camera);
        Camera.MakeCurrent();
        SetPitch(ThreeQuarterPitchDegrees);
    }

    /// <summary>Points the camera at the middle of the grid from the given pitch above the ground plane,
    /// ninety degrees being straight down. The camera orbits toward the viewer, so north is up on screen.</summary>
    private void SetPitch(float degrees)
    {
        (double width, double height) = HexGround.GridExtent(Ground.Columns, Ground.Rows);
        Vector3 target = new((float)Math.Min(width, 40.0) / 2.0f, 0.0f, (float)Math.Min(height, 30.0) / 2.0f);
        float radians = Mathf.DegToRad(degrees);
        const float distance = 120.0f;
        Camera.Position = target + new Vector3(0.0f, distance * Mathf.Sin(radians), distance * Mathf.Cos(radians));
        // Looking straight down, "up" cannot be the world's up, so the screen's up is taken to be north.
        Camera.LookAt(target, degrees > 89.0f ? Vector3.Forward : Vector3.Up);
    }

    private void BuildInterface()
    {
        Control overlay = new() { Name = "Overlay", MouseFilter = Control.MouseFilterEnum.Ignore };
        overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(overlay);

        PanelContainer topBar = new() { Name = "TopBar", MouseFilter = Control.MouseFilterEnum.Ignore };
        topBar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        overlay.AddChild(topBar);
        HBoxContainer topRow = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        topBar.AddChild(topRow);

        MenuBar = new MenuBar { Name = "MenuBar" };
        topRow.AddChild(MenuBar);
        // Godot's own menu, drawn inside the window. A native Windows menu would be a window of its own.
        FileMenu = new PopupMenu { Name = "File", PreferNativeMenu = false };
        FileMenu.AddItem("New map...", 0);
        FileMenu.AddItem("Open piece...", 1);
        FileMenu.AddSeparator();
        FileMenu.AddItem("Quit", 2);
        FileMenu.IdPressed += OnFileMenu;
        MenuBar.AddChild(FileMenu);

        Status = new Label
        {
            Name = "Status",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Right,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        topRow.AddChild(Status);

        VBoxContainer rail = new() { Name = "Rail", Position = new Vector2(8.0f, 48.0f), MouseFilter = Control.MouseFilterEnum.Ignore };
        overlay.AddChild(rail);
        PaintButton = AddTool(rail, "Paint (B)", "paint");
        EraseButton = AddTool(rail, "Erase (E)", "erase");
        TopDownButton = new Button { Name = "TopDownButton", Text = "Top-down (T)" };
        TopDownButton.Pressed += TogglePitch;
        rail.AddChild(TopDownButton);

        NewMapDialog = new ConfirmationDialog
        {
            Name = "NewMapDialog",
            Title = "New map",
            DialogText = "Replace the map with a blank one? This cannot be undone.",
        };
        NewMapDialog.Confirmed += OnNewMapConfirmed;
        AddChild(NewMapDialog);

        OpenPieceDialog = new FileDialog
        {
            Name = "OpenPieceDialog",
            Title = "Open piece",
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = false,
            Filters = ["*.glb ; glTF binary"],
            Size = new Vector2I(760, 520),
        };
        OpenPieceDialog.FileSelected += path => LoadPiece(path);
        AddChild(OpenPieceDialog);
    }

    private Button AddTool(Container rail, string text, string tool)
    {
        Button button = new() { Name = tool + "Button", Text = text };
        button.Pressed += () => SelectTool(tool);
        rail.AddChild(button);
        return button;
    }

    private void SelectTool(string tool)
    {
        Tool = tool;
        ToolPresses++;
        Log.Write($"Tool: {tool}.");
    }

    private void TogglePitch()
    {
        TopDown = !TopDown;
        PitchToggles++;
        SetPitch(TopDown ? 90.0f : ThreeQuarterPitchDegrees);
        Log.Write(TopDown ? "Camera: top-down." : "Camera: three-quarter.");
    }

    private void OnFileMenu(long id)
    {
        switch (id)
        {
            case 0:
                NewMapRequests++;
                NewMapDialog.PopupCentered();
                break;
            case 1:
                OpenPieceRequests++;
                OpenPieceDialog.CurrentDir = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "Assets"));
                OpenPieceDialog.PopupCentered();
                break;
            case 2:
                Quit(0);
                break;
        }
    }

    private void OnNewMapConfirmed()
    {
        NewMapConfirmations++;
        Ground.Build(Ground.Columns, Ground.Rows, PaperColor);
        _undo.Clear();
        Log.Write("New map.");
    }

    private void LoadPiece(string path)
    {
        Piece?.Root.QueueFree();
        Piece = PieceLoader.Load(path, GD.Load<Shader>("res://Shaders/SimplePaint.gdshader"), Log);
        if (Piece is null)
        {
            return;
        }
        AddChild(Piece.Root);
        // One building covering a few cells near the middle of the view, at twice its exported size.
        Piece.Root.Position = Ground.CellCenter(new HexCell(12, 10));
        Piece.Root.Scale = Vector3.One * 2.0f;
        Log.Write($"Piece '{Path.GetFileName(path)}': {Piece.Surfaces} surfaces, {Piece.PaintedSurfaces} with SimplePaint parameters from the file.");
        foreach (string note in Piece.Notes)
        {
            Log.Write("  " + note);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } key:
                if (HandleKey(key))
                {
                    GetViewport().SetInputAsHandled();
                }
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } press:
                HoverCell = HexGround.Pick(Camera, press.Position);
                PaintAt(press.Position);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseMotion motion:
                HoverCell = HexGround.Pick(Camera, motion.Position);
                if ((motion.ButtonMask & MouseButtonMask.Left) != 0)
                {
                    PaintAt(motion.Position);
                    GetViewport().SetInputAsHandled();
                }
                break;
        }
    }

    private bool HandleKey(InputEventKey key)
    {
        if (key.CtrlPressed && key.Keycode == Key.Z)
        {
            Undo();
            return true;
        }
        switch (key.Keycode)
        {
            case Key.B: SelectTool("paint"); return true;
            case Key.E: SelectTool("erase"); return true;
            case Key.T: TogglePitch(); return true;
            default: return false;
        }
    }

    private void PaintAt(Vector2 windowPosition)
    {
        HexCell? picked = HexGround.Pick(Camera, windowPosition);
        if (picked is not HexCell cell || !Ground.Contains(cell))
        {
            return;
        }
        Color color = Tool == "erase" ? PaperColor : PaintColor;
        Color previous = Ground.CellColor(cell);
        // A color read back from the image has been truncated to bytes, so it is compared to within one.
        if (Mathf.Abs(previous.R - color.R) < 1.5f / 255.0f && Mathf.Abs(previous.G - color.G) < 1.5f / 255.0f && Mathf.Abs(previous.B - color.B) < 1.5f / 255.0f)
        {
            return;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Ground.Paint(cell, color);
        LastUploadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _undo.Push((cell, previous));
        LastPaintedCell = cell;
        PaintedCells++;
    }

    private void Undo()
    {
        UndoRequests++;
        if (_undo.Count == 0)
        {
            return;
        }
        (HexCell cell, Color previous) = _undo.Pop();
        Ground.Paint(cell, previous);
    }

    public override void _Process(double delta)
    {
        string cell = HoverCell is HexCell hover ? $"cell {hover.Column},{hover.Row}" : "no cell";
        Status.Text = $"{Engine.GetFramesPerSecond():0} fps   {RenderingServer.GetCurrentRenderingDriverName()}   tool {Tool}   {cell}   ";
    }

    public void Quit(int exitCode)
    {
        if (_quitting)
        {
            return;
        }
        _quitting = true;
        Log.Write($"Quitting with exit code {exitCode}.");
        GetTree().Quit(exitCode);
    }
}
