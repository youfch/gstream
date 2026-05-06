using System;
using System.Collections.Concurrent;

namespace gStream.Core.Input;

/// <summary>
/// Videoplayer input event types (binary protocol from webapp/videoplayer).
/// </summary>
internal enum VideoplayerInputType : byte
{
    Keyboard = 0,
    Mouse = 1,
    MouseWheel = 2,
    Touch = 3,
    ButtonClick = 4,
    Gamepad = 5,
}

/// <summary>
/// Videoplayer gamepad event subtypes.
/// </summary>
internal enum VideoplayerGamepadSubtype : byte
{
    ButtonUp = 0,
    ButtonDown = 1,
    ButtonPressed = 2,
    Axis = 3,
}

/// <summary>
/// Thread-safe input parser for the videoplayer binary protocol.
/// Produces <see cref="InputEvent"/> structs compatible with <see cref="IInputParser"/>.
///
/// Protocol format (little-endian):
///   Keyboard:     [type=0, up/down(1=down,0=up), repeat(u8), keyCode(u8), charCode(u8)] = 5 bytes
///   Mouse:        [type=1, x(i16LE), y(i16LE), buttons(u8)] = 6 bytes
///   MouseWheel:   [type=2, deltaX(f32LE), deltaY(f32LE)] = 9 bytes
///   Touch:        [type=3, count(u8), {id(i32LE), phase(u8), x(i16LE), y(i16LE), force(f32LE)}...]
///   ButtonClick:  [type=4, elementId(i16LE)] = 3 bytes
///   Gamepad:      [type=5, subtype(u8), index(u8), value(f64LE)...]
///     subtype 0=ButtonUp:   19 bytes total
///     subtype 1=ButtonDown: 19 bytes total
///     subtype 2=ButtonPressed: 19 bytes total
///     subtype 3=Axis: 27 bytes total (includes x,y as f64)
///
/// Keyboard keycodes use the same index mapping as URS (identical to UrsKeyMapping).
/// Mouse coordinates are i16 integers; Y is already in Unity-style (bottom-left origin).
/// </summary>
public sealed class VideoplayerInputParser : IInputParser
{
    private readonly ConcurrentQueue<InputEvent> _queue = new();
    private bool _disposed;

    // Mouse button state tracking (for synthesizing MouseDown/MouseUp from buttons bitmask)
    private byte _previousButtons;

    // Gamepad accumulated state
    private uint _gamepadButtons;
    private float _leftStickX, _leftStickY;
    private float _rightStickX, _rightStickY;
    private float _leftTrigger, _rightTrigger;

    /// <summary>Number of pending events (for diagnostics).</summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// Call when a DataChannel message arrives from browser.
    /// Parses the videoplayer binary message format.
    /// </summary>
    public void OnDataChannelMessage(byte[] data)
    {
        if (_disposed || data == null || data.Length < 1) return;

        try
        {
            var type = (VideoplayerInputType)data[0];
            switch (type)
            {
                case VideoplayerInputType.Keyboard:
                    ParseKeyboard(data);
                    break;
                case VideoplayerInputType.Mouse:
                    ParseMouse(data);
                    break;
                case VideoplayerInputType.MouseWheel:
                    ParseMouseWheel(data);
                    break;
                case VideoplayerInputType.Touch:
                    ParseTouch(data);
                    break;
                case VideoplayerInputType.ButtonClick:
                    ParseButtonClick(data);
                    break;
                case VideoplayerInputType.Gamepad:
                    ParseGamepad(data);
                    break;
                default:
                    break;
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Dequeue a single event. Returns false if queue is empty.</summary>
    public bool TryDequeue(out InputEvent evt) => _queue.TryDequeue(out evt);

    // ── Keyboard ────────────────────────────────────────────────────────────

    // Keyboard: [type=0, up/down(1=down,0=up), repeat(u8), keyCode(u8), charCode(u8)] = 5 bytes
    // Keycode index is identical to URS index — UrsKeyMapping.ToGodotKey() works directly.
    private void ParseKeyboard(byte[] data)
    {
        if (data.Length < 5) return;

        byte upDown = data[1];       // 0=up, 1=down
        // byte repeat = data[2];    // not used in InputEvent currently
        byte keyCode = data[3];      // URS-compatible key index
        // byte charCode = data[4];  // not used in InputEvent currently

        var eventType = upDown == 1 ? InputEventType.KeyDown : InputEventType.KeyUp;

        _queue.Enqueue(new InputEvent
        {
            Type = eventType,
            KeyCode = keyCode,
        });
    }

    // ── Mouse ───────────────────────────────────────────────────────────────

    // Mouse: [type=1, x(i16LE), y(i16LE), buttons(u8)] = 6 bytes
    // Y is already in Unity coordinate system (bottom-left origin) from JS.
    // We track previous buttons bitmask to synthesize MouseDown/MouseUp events.
    private void ParseMouse(byte[] data)
    {
        if (data.Length < 6) return;

        short rawX = BitConverter.ToInt16(data, 1);
        short rawY = BitConverter.ToInt16(data, 3);
        byte buttons = data[5];

        float x = rawX;
        float y = rawY;

        // Always emit MouseMove
        _queue.Enqueue(new InputEvent
        {
            Type = InputEventType.MouseMove,
            X = x,
            Y = y,
        });

        // Synthesize MouseDown/MouseUp from button bitmask changes
        for (int i = 0; i < 5; i++)
        {
            byte mask = (byte)(1 << i);
            bool currentPressed = (buttons & mask) != 0;
            bool previousPressed = (_previousButtons & mask) != 0;

            if (currentPressed && !previousPressed)
            {
                _queue.Enqueue(new InputEvent
                {
                    Type = InputEventType.MouseDown,
                    X = x,
                    Y = y,
                    Button = i,
                });
            }
            else if (!currentPressed && previousPressed)
            {
                _queue.Enqueue(new InputEvent
                {
                    Type = InputEventType.MouseUp,
                    X = x,
                    Y = y,
                    Button = i,
                });
            }
        }

        _previousButtons = buttons;
    }

    // ── Mouse Wheel ─────────────────────────────────────────────────────────

    // MouseWheel: [type=2, deltaX(f32LE), deltaY(f32LE)] = 9 bytes
    private void ParseMouseWheel(byte[] data)
    {
        if (data.Length < 9) return;

        float deltaX = BitConverter.ToSingle(data, 1);
        float deltaY = BitConverter.ToSingle(data, 5);

        _queue.Enqueue(new InputEvent
        {
            Type = InputEventType.MouseWheel,
            DeltaX = deltaX,
            DeltaY = deltaY,
        });
    }

    // ── Touch ───────────────────────────────────────────────────────────────

    // Touch: [type=3, count(u8), {id(i32LE), phase(u8), x(i16LE), y(i16LE), force(f32LE)}...]
    // = 2 + 13*count bytes
    // Touch phases: 0=None, 1=Began, 2=Moved, 3=Ended, 4=Canceled, 5=Stationary
    private void ParseTouch(byte[] data)
    {
        if (data.Length < 2) return;

        byte count = data[1];
        int expectedLength = 2 + 13 * count;
        if (data.Length < expectedLength) return;

        int offset = 2;
        for (int i = 0; i < count; i++)
        {
            int touchId = BitConverter.ToInt32(data, offset);
            byte phase = data[offset + 4];
            short rawX = BitConverter.ToInt16(data, offset + 5);
            short rawY = BitConverter.ToInt16(data, offset + 7);
            // float force = BitConverter.ToSingle(data, offset + 9); // not used in InputEvent

            float x = rawX;
            float y = rawY;

            InputEventType eventType = phase switch
            {
                1 => InputEventType.TouchStart,   // Began
                2 => InputEventType.TouchMove,    // Moved
                3 => InputEventType.TouchEnd,     // Ended
                4 => InputEventType.TouchEnd,     // Canceled
                5 => InputEventType.TouchMove,    // Stationary
                _ => InputEventType.TouchMove
            };

            _queue.Enqueue(new InputEvent
            {
                Type = eventType,
                X = x,
                Y = y,
                Button = touchId,
            });

            offset += 13;
        }
    }

    // ── Button Click ────────────────────────────────────────────────────────

    // ButtonClick: [type=4, elementId(i16LE)] = 3 bytes
    private void ParseButtonClick(byte[] data)
    {
        if (data.Length < 3) return;

        short elementId = BitConverter.ToInt16(data, 1);

        _queue.Enqueue(new InputEvent
        {
            Type = InputEventType.ButtonClick,
            ButtonId = elementId,
        });
    }

    // ── Gamepad ─────────────────────────────────────────────────────────────

    // Gamepad button: [type=5, subtype=0/1/2, index(u8), value(f64LE)] = 19 bytes
    // Gamepad axis:   [type=5, subtype=3, index(u8), x(f64LE), y(f64LE)] = 27 bytes
    private void ParseGamepad(byte[] data)
    {
        if (data.Length < 3) return;

        var subtype = (VideoplayerGamepadSubtype)data[1];
        byte index = data[2];

        switch (subtype)
        {
            case VideoplayerGamepadSubtype.ButtonDown:
            case VideoplayerGamepadSubtype.ButtonPressed:
                if (data.Length < 19) return;
                double downValue = BitConverter.ToDouble(data, 3);
                OnGamepadButton(index, true, downValue);
                break;

            case VideoplayerGamepadSubtype.ButtonUp:
                if (data.Length < 19) return;
                double upValue = BitConverter.ToDouble(data, 3);
                OnGamepadButton(index, false, upValue);
                break;

            case VideoplayerGamepadSubtype.Axis:
                if (data.Length < 27) return;
                double axisX = BitConverter.ToDouble(data, 3);
                double axisY = BitConverter.ToDouble(data, 11);
                OnGamepadAxis(index, (float)axisX, (float)axisY);
                break;

            default:
                break;
        }
    }

    private void OnGamepadButton(int buttonIndex, bool pressed, double value)
    {
        // Map button index to bitmask position (same as URS: 0=A, 1=B, 2=X, 3=Y, ...)
        if (buttonIndex is >= 0 and < 32)
        {
            if (pressed)
                _gamepadButtons |= (1u << buttonIndex);
            else
                _gamepadButtons &= ~(1u << buttonIndex);
        }

        // Buttons 6 and 7 in standard gamepad are often triggers.
        // Emit a full Gamepad event with current accumulated state.
        _queue.Enqueue(new InputEvent
        {
            Type = InputEventType.Gamepad,
            LeftStickX = _leftStickX,
            LeftStickY = -_leftStickY,  // Flip Y to match URS convention
            RightStickX = _rightStickX,
            RightStickY = -_rightStickY,
            LeftTrigger = _leftTrigger,
            RightTrigger = _rightTrigger,
            GamepadButtons = _gamepadButtons,
        });
    }

    private void OnGamepadAxis(int axisIndex, float x, float y)
    {
        // Axis index mapping: 0=left stick, 1=right stick
        switch (axisIndex)
        {
            case 0:
                _leftStickX = x;
                _leftStickY = y;
                break;
            case 1:
                _rightStickX = x;
                _rightStickY = y;
                break;
        }

        _queue.Enqueue(new InputEvent
        {
            Type = InputEventType.Gamepad,
            LeftStickX = _leftStickX,
            LeftStickY = -_leftStickY,  // Flip Y to match URS convention
            RightStickX = _rightStickX,
            RightStickY = -_rightStickY,
            LeftTrigger = _leftTrigger,
            RightTrigger = _rightTrigger,
            GamepadButtons = _gamepadButtons,
        });
    }

    public void Dispose()
    {
        _disposed = true;
        while (_queue.TryDequeue(out _)) { }
    }
}
