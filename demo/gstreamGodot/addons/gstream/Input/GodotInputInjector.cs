using System;
using Godot;
using InputEvent = gStream.Core.Input.InputEvent;
using InputEventType = gStream.Core.Input.InputEventType;

namespace gStream.Godot;

public sealed class GodotInputInjector
{
    private readonly Viewport _viewport;
    private bool _viewportMouseNotified;

    public GodotInputInjector(Viewport viewport)
    {
        _viewport = viewport;
    }

    /// <summary>
    /// Called when the viewport resolution changes.
    /// This injector reads viewport size dynamically via GetVisibleRect() each call,
    /// so no cached state needs updating. Kept for API consistency.
    /// </summary>
    public void UpdateViewportSize(int width, int height)
    {
        // No-op: viewport size is read dynamically in InjectEvent() via _viewport.GetVisibleRect().
    }

    public void InjectEvent(InputEvent evt)
    {
        if (!_viewportMouseNotified)
        {
            _viewport.Notification((int)Viewport.NotificationVpMouseEnter);
            _viewportMouseNotified = true;
        }

        var viewportSize = _viewport.GetVisibleRect().Size;
        float godotX = evt.X;
        float godotY = viewportSize.Y - evt.Y;

        switch (evt.Type)
        {
            case InputEventType.MouseMove:
            {
                var mouseEvent = new InputEventMouseMotion
                {
                    Position = new Vector2(godotX, godotY),
                    GlobalPosition = new Vector2(godotX, godotY),
                    Relative = new Vector2(evt.DeltaX, -evt.DeltaY)
                };
                _viewport.PushInput(mouseEvent);
                global::Godot.Input.ParseInputEvent(mouseEvent);
                break;
            }

            case InputEventType.MouseDown:
            {
                var mouseEvent = new InputEventMouseButton
                {
                    Position = new Vector2(godotX, godotY),
                    GlobalPosition = new Vector2(godotX, godotY),
                    ButtonIndex = UrsKeyMapping.ToGodotMouseButton(evt.Button),
                    Pressed = true
                };
                _viewport.PushInput(mouseEvent);
                global::Godot.Input.ParseInputEvent(mouseEvent);
                break;
            }

            case InputEventType.MouseUp:
            {
                var mouseEvent = new InputEventMouseButton
                {
                    Position = new Vector2(godotX, godotY),
                    GlobalPosition = new Vector2(godotX, godotY),
                    ButtonIndex = UrsKeyMapping.ToGodotMouseButton(evt.Button),
                    Pressed = false
                };
                _viewport.PushInput(mouseEvent);
                global::Godot.Input.ParseInputEvent(mouseEvent);
                break;
            }

            case InputEventType.MouseWheel:
            {
                var mouseEvent = new InputEventMouseButton
                {
                    Position = new Vector2(godotX, godotY),
                    GlobalPosition = new Vector2(godotX, godotY),
                    ButtonIndex = evt.DeltaY > 0 ? MouseButton.WheelUp : MouseButton.WheelDown,
                    Pressed = true
                };
                _viewport.PushInput(mouseEvent);
                global::Godot.Input.ParseInputEvent(mouseEvent);
                break;
            }

            case InputEventType.KeyDown:
            {
                var godotKey = UrsKeyMapping.ToGodotKey(evt.KeyCode);
                var keyEvent = new InputEventKey
                {
                    Keycode = godotKey,
                    PhysicalKeycode = godotKey,
                    Pressed = true
                };
                global::Godot.Input.ParseInputEvent(keyEvent);
                break;
            }

            case InputEventType.KeyUp:
            {
                var godotKey = UrsKeyMapping.ToGodotKey(evt.KeyCode);
                var keyEvent = new InputEventKey
                {
                    Keycode = godotKey,
                    PhysicalKeycode = godotKey,
                    Pressed = false
                };
                global::Godot.Input.ParseInputEvent(keyEvent);
                break;
            }

            case InputEventType.TouchStart:
            {
                var touchEvent = new InputEventScreenTouch
                {
                    Position = new Vector2(godotX, godotY),
                    Pressed = true,
                    Index = evt.Button,
                    Canceled = false
                };
                _viewport.PushInput(touchEvent);
                global::Godot.Input.ParseInputEvent(touchEvent);
                break;
            }

            case InputEventType.TouchMove:
            {
                var dragEvent = new InputEventScreenDrag
                {
                    Position = new Vector2(godotX, godotY),
                    Relative = new Vector2(evt.DeltaX, -evt.DeltaY),
                    Index = evt.Button
                };
                _viewport.PushInput(dragEvent);
                global::Godot.Input.ParseInputEvent(dragEvent);
                break;
            }

            case InputEventType.TouchEnd:
            {
                var touchEvent = new InputEventScreenTouch
                {
                    Position = new Vector2(godotX, godotY),
                    Pressed = false,
                    Index = evt.Button,
                    Canceled = false
                };
                _viewport.PushInput(touchEvent);
                global::Godot.Input.ParseInputEvent(touchEvent);
                break;
            }

            case InputEventType.Gamepad:
            {
                InjectGamepadEvent(evt);
                break;
            }
        }
    }

    private void InjectGamepadEvent(InputEvent evt)
    {
        // Left stick
        if (Math.Abs(evt.LeftStickX) > 0.001f || Math.Abs(evt.LeftStickY) > 0.001f)
        {
            var motion = new InputEventJoypadMotion
            {
                Axis = JoyAxis.LeftX,
                AxisValue = evt.LeftStickX,
                Device = 0
            };
            global::Godot.Input.ParseInputEvent(motion);

            motion = new InputEventJoypadMotion
            {
                Axis = JoyAxis.LeftY,
                AxisValue = evt.LeftStickY,
                Device = 0
            };
            global::Godot.Input.ParseInputEvent(motion);
        }

        // Right stick
        if (Math.Abs(evt.RightStickX) > 0.001f || Math.Abs(evt.RightStickY) > 0.001f)
        {
            var motion = new InputEventJoypadMotion
            {
                Axis = JoyAxis.RightX,
                AxisValue = evt.RightStickX,
                Device = 0
            };
            global::Godot.Input.ParseInputEvent(motion);

            motion = new InputEventJoypadMotion
            {
                Axis = JoyAxis.RightY,
                AxisValue = evt.RightStickY,
                Device = 0
            };
            global::Godot.Input.ParseInputEvent(motion);
        }

        // Triggers
        if (Math.Abs(evt.LeftTrigger) > 0.001f)
        {
            global::Godot.Input.ParseInputEvent(new InputEventJoypadMotion
            {
                Axis = JoyAxis.TriggerLeft,
                AxisValue = evt.LeftTrigger,
                Device = 0
            });
        }
        if (Math.Abs(evt.RightTrigger) > 0.001f)
        {
            global::Godot.Input.ParseInputEvent(new InputEventJoypadMotion
            {
                Axis = JoyAxis.TriggerRight,
                AxisValue = evt.RightTrigger,
                Device = 0
            });
        }

        // Buttons — emit press/release for each changed bit
        for (int i = 0; i < 16; i++)
        {
            bool pressed = (evt.GamepadButtons & (1u << i)) != 0;
            var buttonEvent = new InputEventJoypadButton
            {
                ButtonIndex = UrsKeyMapping.ToGodotJoyButton(i),
                Pressed = pressed,
                Device = 0
            };
            global::Godot.Input.ParseInputEvent(buttonEvent);
        }
    }
}
