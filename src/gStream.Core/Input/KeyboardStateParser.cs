using System;
using System.Collections.Concurrent;

namespace gStream.Core.Input;

internal static class KeyboardStateParser
{
    private static readonly byte[] _previousKeys = new byte[14];

    public static void Parse(byte[] data, ConcurrentQueue<InputEvent> queue)
    {
        if (data.Length < 14) return;

        for (int bitIndex = 0; bitIndex < 110; bitIndex++)
        {
            int byteIndex = bitIndex / 8;
            int bitInByte = bitIndex % 8;
            byte mask = (byte)(1 << bitInByte);

            bool currentPressed = (data[byteIndex] & mask) != 0;
            bool previousPressed = (_previousKeys[byteIndex] & mask) != 0;

            if (currentPressed && !previousPressed)
            {
                queue.Enqueue(new InputEvent
                {
                    Type = InputEventType.KeyDown,
                    KeyCode = (uint)bitIndex,
                });
            }
            else if (!currentPressed && previousPressed)
            {
                queue.Enqueue(new InputEvent
                {
                    Type = InputEventType.KeyUp,
                    KeyCode = (uint)bitIndex,
                });
            }
        }

        Array.Copy(data, _previousKeys, 14);
    }
}
