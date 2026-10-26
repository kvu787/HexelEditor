using Godot;

namespace GodotAgentDevExperiment;

/// <summary>Feeds the engine the events a mouse and a keyboard would send, through Input.ParseInputEvent.
/// Nothing here touches the operating system: the events enter Godot's own dispatch, reach the same hit
/// testing, focus and popups that real input reaches, and never exist outside this process. Positions are
/// in window pixels, which with stretch mode disabled are also GUI coordinates.</summary>
public static class DrivenInput
{
    private static Vector2 _lastPosition;
    private static MouseButtonMask _held;

    public static Vector2 Position => _lastPosition;

    public static void Move(Vector2 to)
    {
        InputEventMouseMotion motion = new()
        {
            Position = to,
            GlobalPosition = to,
            Relative = to - _lastPosition,
            ButtonMask = _held,
        };
        _lastPosition = to;
        Input.ParseInputEvent(motion);
    }

    public static void Press(Vector2 at, MouseButton button = MouseButton.Left)
    {
        Move(at);
        _held |= MaskOf(button);
        Input.ParseInputEvent(new InputEventMouseButton
        {
            Position = at,
            GlobalPosition = at,
            ButtonIndex = button,
            ButtonMask = _held,
            Pressed = true,
        });
    }

    public static void Release(Vector2 at, MouseButton button = MouseButton.Left)
    {
        _held &= ~MaskOf(button);
        Input.ParseInputEvent(new InputEventMouseButton
        {
            Position = at,
            GlobalPosition = at,
            ButtonIndex = button,
            ButtonMask = _held,
            Pressed = false,
        });
    }

    public static void Click(Vector2 at, MouseButton button = MouseButton.Left)
    {
        Press(at, button);
        Release(at, button);
    }

    public static void Key(Key key, bool control = false, bool shift = false)
    {
        Input.ParseInputEvent(KeyEvent(key, control, shift, pressed: true));
        Input.ParseInputEvent(KeyEvent(key, control, shift, pressed: false));
    }

    private static InputEventKey KeyEvent(Key key, bool control, bool shift, bool pressed)
    {
        return new InputEventKey
        {
            Keycode = key,
            PhysicalKeycode = key,
            KeyLabel = key,
            Pressed = pressed,
            CtrlPressed = control,
            ShiftPressed = shift,
        };
    }

    private static MouseButtonMask MaskOf(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => MouseButtonMask.Left,
            MouseButton.Right => MouseButtonMask.Right,
            MouseButton.Middle => MouseButtonMask.Middle,
            _ => 0,
        };
    }
}
