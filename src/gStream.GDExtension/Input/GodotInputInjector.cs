using System;
using Godot;
using InputEvent = gStream.Core.Input.InputEvent;
using InputEventType = gStream.Core.Input.InputEventType;

namespace gStream.GDExtension.Input;

/// <summary>
/// Injects input events into Godot's input system.
/// GDExtension port — uses Godot.Bindings types.
/// </summary>
public sealed class GodotInputInjector
{
    private readonly Viewport _viewport;
    private bool _viewportMouseNotified;

    public GodotInputInjector(Viewport viewport)
    {
        _viewport = viewport;
    }

    public void UpdateViewportSize(int width, int height) { }

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
                global::Godot.Input.Singleton.ParseInputEvent(mouseEvent);
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
                global::Godot.Input.Singleton.ParseInputEvent(mouseEvent);
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
                global::Godot.Input.Singleton.ParseInputEvent(mouseEvent);
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
                global::Godot.Input.Singleton.ParseInputEvent(mouseEvent);
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
                global::Godot.Input.Singleton.ParseInputEvent(keyEvent);
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
                global::Godot.Input.Singleton.ParseInputEvent(keyEvent);
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
                global::Godot.Input.Singleton.ParseInputEvent(touchEvent);
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
                global::Godot.Input.Singleton.ParseInputEvent(dragEvent);
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
                global::Godot.Input.Singleton.ParseInputEvent(touchEvent);
                break;
            }

            case InputEventType.Gamepad:
                InjectGamepadEvent(evt);
                break;
        }
    }

    private void InjectGamepadEvent(InputEvent evt)
    {
        if (Math.Abs(evt.LeftStickX) > 0.001f || Math.Abs(evt.LeftStickY) > 0.001f)
        {
            global::Godot.Input.Singleton.ParseInputEvent(new InputEventJoypadMotion
            {
                Axis = JoyAxis.LeftX, AxisValue = evt.LeftStickX, Device = 0
            });
            global::Godot.Input.Singleton.ParseInputEvent(new InputEventJoypadMotion
            {
                Axis = JoyAxis.LeftY, AxisValue = evt.LeftStickY, Device = 0
            });
        }

        if (Math.Abs(evt.RightStickX) > 0.001f || Math.Abs(evt.RightStickY) > 0.001f)
        {
            global::Godot.Input.Singleton.ParseInputEvent(new InputEventJoypadMotion
            {
                Axis = JoyAxis.RightX, AxisValue = evt.RightStickX, Device = 0
            });
            global::Godot.Input.Singleton.ParseInputEvent(new InputEventJoypadMotion
            {
                Axis = JoyAxis.RightY, AxisValue = evt.RightStickY, Device = 0
            });
        }

        if (Math.Abs(evt.LeftTrigger) > 0.001f)
            global::Godot.Input.Singleton.ParseInputEvent(new InputEventJoypadMotion
            { Axis = JoyAxis.TriggerLeft, AxisValue = evt.LeftTrigger, Device = 0 });

        if (Math.Abs(evt.RightTrigger) > 0.001f)
            global::Godot.Input.Singleton.ParseInputEvent(new InputEventJoypadMotion
            { Axis = JoyAxis.TriggerRight, AxisValue = evt.RightTrigger, Device = 0 });

        for (int i = 0; i < 16; i++)
        {
            bool pressed = (evt.GamepadButtons & (1u << i)) != 0;
            global::Godot.Input.Singleton.ParseInputEvent(new InputEventJoypadButton
            {
                ButtonIndex = UrsKeyMapping.ToGodotJoyButton(i),
                Pressed = pressed,
                Device = 0
            });
        }
    }
}
