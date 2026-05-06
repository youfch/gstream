using Godot;

namespace gStream.GDExtension.Input;

/// <summary>
/// Maps URS key/button indices to Godot Key/MouseButton/JoyButton enums.
/// Pure C# — no Godot API changes needed for GDExtension port.
/// </summary>
public static class UrsKeyMapping
{
    private static readonly Key[] _ursKeyMap =
    [
        Key.None,
        Key.Space,
        Key.Enter,
        Key.Tab,
        Key.Quoteleft,
        Key.Apostrophe,
        Key.Semicolon,
        Key.Comma,
        Key.Period,
        Key.Slash,
        Key.Backslash,
        Key.Bracketleft,
        Key.Bracketright,
        Key.Minus,
        Key.Equal,
        Key.A, Key.B, Key.C, Key.D, Key.E,
        Key.F, Key.G, Key.H, Key.I, Key.J,
        Key.K, Key.L, Key.M, Key.N, Key.O,
        Key.P, Key.Q, Key.R, Key.S, Key.T,
        Key.U, Key.V, Key.W, Key.X, Key.Y,
        Key.Z,
        Key.Key1, Key.Key2, Key.Key3, Key.Key4, Key.Key5,
        Key.Key6, Key.Key7, Key.Key8, Key.Key9, Key.Key0,
        Key.Shift,
        Key.Shift,
        Key.Alt,
        Key.Alt,
        Key.Ctrl,
        Key.Ctrl,
        Key.Meta,
        Key.Meta,
        Key.Menu,
        Key.Escape,
        Key.Left,
        Key.Right,
        Key.Up,
        Key.Down,
        Key.Backspace,
        Key.Pageup,
        Key.Pageup,
        Key.Home,
        Key.End,
        Key.Insert,
        Key.Delete,
        Key.Capslock,
        Key.Numlock,
        Key.Print,
        Key.Scrolllock,
        Key.Pause,
        Key.KpEnter,
        Key.KpDivide,
        Key.KpMultiply,
        Key.KpAdd,
        Key.KpSubtract,
        Key.KpPeriod,
        Key.None,
        Key.Kp0,
        Key.Kp1,
        Key.Kp2,
        Key.Kp3,
        Key.Kp4,
        Key.Kp5,
        Key.Kp6,
        Key.Kp7,
        Key.Kp8,
        Key.Kp9,
        Key.F1,
        Key.F2,
        Key.F3,
        Key.F4,
        Key.F5,
        Key.F6,
        Key.F7,
        Key.F8,
        Key.F9,
        Key.F10,
        Key.F11,
        Key.F12,
    ];

    public static Key ToGodotKey(uint ursIndex)
    {
        if (ursIndex < _ursKeyMap.Length)
            return _ursKeyMap[ursIndex];
        return Key.None;
    }

    public static MouseButton ToGodotMouseButton(int button)
    {
        return button switch
        {
            0 => MouseButton.Left,
            1 => MouseButton.Right,
            2 => MouseButton.Middle,
            3 => MouseButton.Xbutton1,
            4 => MouseButton.Xbutton2,
            _ => MouseButton.Left
        };
    }

    public static JoyButton ToGodotJoyButton(int ursButtonIndex)
    {
        return ursButtonIndex switch
        {
            0 => JoyButton.A,
            1 => JoyButton.B,
            2 => JoyButton.X,
            3 => JoyButton.Y,
            4 => JoyButton.LeftShoulder,
            5 => JoyButton.RightShoulder,
            6 => JoyButton.LeftStick,
            7 => JoyButton.RightStick,
            8 => JoyButton.Back,
            9 => JoyButton.Start,
            10 => JoyButton.LeftStick,
            11 => JoyButton.RightStick,
            12 => JoyButton.DpadUp,
            13 => JoyButton.DpadDown,
            14 => JoyButton.DpadLeft,
            15 => JoyButton.DpadRight,
            _ => JoyButton.Invalid
        };
    }
}
