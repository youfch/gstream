using System;
using System.Collections.Concurrent;

namespace gStream.Core.Input;

internal static class MouseStateParser
{
    private static uint _previousButtons;

    public static void Parse(byte[] data, ConcurrentQueue<InputEvent> queue)
    {
        if (data.Length < 30) return;

        var x = BitConverter.ToSingle(data, 0);
        var y = BitConverter.ToSingle(data, 4);
        var deltaX = BitConverter.ToSingle(data, 8);
        var deltaY = BitConverter.ToSingle(data, 12);
        var scrollX = BitConverter.ToSingle(data, 16);
        var scrollY = BitConverter.ToSingle(data, 20);
        var buttons = BitConverter.ToUInt16(data, 24);

        queue.Enqueue(new InputEvent
        {
            Type = InputEventType.MouseMove,
            X = x,
            Y = y,
            DeltaX = deltaX,
            DeltaY = deltaY,
        });

        if (Math.Abs(scrollX) > 0.1f || Math.Abs(scrollY) > 0.1f)
        {
            queue.Enqueue(new InputEvent
            {
                Type = InputEventType.MouseWheel,
                X = x,
                Y = y,
                DeltaX = scrollX,
                DeltaY = scrollY,
            });
        }

        for (int i = 0; i < 5; i++)
        {
            uint mask = (uint)(1 << i);
            bool currentPressed = (buttons & mask) != 0;
            bool previousPressed = (_previousButtons & mask) != 0;

            if (currentPressed && !previousPressed)
            {
                queue.Enqueue(new InputEvent
                {
                    Type = InputEventType.MouseDown,
                    X = x,
                    Y = y,
                    Button = i,
                });
            }
            else if (!currentPressed && previousPressed)
            {
                queue.Enqueue(new InputEvent
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
}
