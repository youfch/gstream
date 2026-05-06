using System;
using System.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using Encoding = System.Text.Encoding;

namespace gStream.Core.Input;

/// <summary>
/// Input event types relayed from browser DataChannel.
/// </summary>
public enum InputEventType : byte
{
    MouseMove = 1,
    MouseDown = 2,
    MouseUp = 3,
    MouseWheel = 4,
    KeyDown = 5,
    KeyUp = 6,
    TouchStart = 7,
    TouchMove = 8,
    TouchEnd = 9,
    Gamepad = 10,
    ButtonClick = 11,
}

/// <summary>
/// Parsed input event from browser client.
/// </summary>
public readonly struct InputEvent
{
    public InputEventType Type { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public int Button { get; init; }
    public float DeltaX { get; init; }
    public float DeltaY { get; init; }
    public uint KeyCode { get; init; }
    
    // Gamepad specific
    public float LeftStickX { get; init; }
    public float LeftStickY { get; init; }
    public float RightStickX { get; init; }
    public float RightStickY { get; init; }
    public float LeftTrigger { get; init; }
    public float RightTrigger { get; init; }
    public uint GamepadButtons { get; init; }
    
    /// <summary>Element ID for ButtonClick events (videoplayer protocol).</summary>
    public int ButtonId { get; init; }
}

/// <summary>
/// Message types in URS input protocol.
/// </summary>
internal enum MessageType : int
{
    Connect = 0,
    Disconnect = 1,
    NewLayout = 2,
    NewDevice = 3,
    NewEvents = 4,
    RemoveDevice = 5,
    RemoveLayout = 6,
    ChangeUsages = 7,
    StartSending = 8,
    StopSending = 9,
}

/// <summary>
/// State format FourCC codes (big-endian encoding: firstChar<<24|second<<16|third<<8|fourth).
/// URS makeFourCC uses this format, then DataView.setInt32 writes little-endian bytes.
/// </summary>
internal static class StateFormat
{
    public const int Mouse = 0x4D4F5553;      // 'MOUS' (M<<24|O<<16|U<<8|S)
    public const int Keyboard = 0x4B455953;   // 'KEYS' (K<<24|E<<16|Y<<8|S)
    public const int Gamepad = 0x47504144;    // 'GPAD' (G<<24|P<<16|A<<8|D)
    public const int Touch = 0x544F4354;      // 'TOCT' (T<<24|O<<16|C<<8|T)
    public const int Touchscreen = 0x54534352; // 'TSCR' (T<<24|S<<16|C<<8|R)
    public const int Text = 0x54455854;       // 'TEXT' (T<<24|E<<16|X<<8|T)
    public const int State = 0x53544154;      // 'STAT' (S<<24|T<<16|A<<8|T)
}

/// <summary>
/// Thread-safe input relay that parses URS binary DataChannel messages from browser.
/// Compatible with Unity Render Streaming input protocol.
/// </summary>
public sealed class InputRelay : IInputParser
{
    private readonly ConcurrentQueue<InputEvent> _queue = new();
    private bool _disposed;
    
    // Device tracking
    private int _mouseDeviceId = -1;
    private int _keyboardDeviceId = -1;
    private int _gamepadDeviceId = -1;

    /// <summary>Number of pending events (for diagnostics).</summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// Call when a DataChannel message arrives from browser.
    /// Parses the URS binary message format.
    /// </summary>
    public void OnDataChannelMessage(byte[] data)
    {
        if (_disposed || data.Length < 12) return;
        
        try
        {
            ParseMessage(data);
        }
        catch
        {
            // NOTE: Do NOT call Debug.WriteLine here — this runs on the SCTP
            // receive thread, not the Godot main thread. Calling Godot's native
            // print from a background thread causes "Unexpected NUL character"
            // errors in Godot's UTF-8 string marshaling.
        }
    }

    /// <summary>
    /// Dequeues all pending input events. Call from main/game thread.
    /// </summary>
    public int Drain(Span<InputEvent> buffer)
    {
        int count = 0;
        while (count < buffer.Length && _queue.TryDequeue(out var evt))
        {
            buffer[count++] = evt;
        }
        return count;
    }

    /// <summary>Dequeue a single event. Returns false if queue is empty.</summary>
    public bool TryDequeue(out InputEvent evt) => _queue.TryDequeue(out evt);

    private void ParseMessage(byte[] data)
    {
        // Message format: [participant_id:4][type:4][length:4][data:N]
        var type = (MessageType)BitConverter.ToInt32(data, 4);
        var length = BitConverter.ToInt32(data, 8);
        
        // NOTE: Do NOT call Debug.WriteLine here — this runs on the SCTP
        // receive thread. See OnDataChannelMessage for details.
        
        if (length <= 0 || data.Length < 12 + length) return;
        
        var messageData = new byte[length];
        Array.Copy(data, 12, messageData, 0, length);
        
        switch (type)
        {
            case MessageType.NewDevice:
                ParseNewDevice(messageData);
                break;
            case MessageType.NewEvents:
                ParseNewEvents(messageData);
                break;
        }
    }

    private void ParseNewDevice(byte[] data)
    {
        // NewDevice message data is JSON string (may have null terminator)
        try
        {
            // Find the actual end of JSON data (before null terminator)
            int jsonLength = data.Length;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == 0)
                {
                    jsonLength = i;
                    break;
                }
            }
            
            // Convert bytes to string (URS uses single-byte chars)
            var json = System.Text.Encoding.ASCII.GetString(data, 0, jsonLength);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "";
            var deviceId = root.TryGetProperty("deviceId", out var idEl) ? idEl.GetInt32() : -1;
            
            // Track device IDs for event routing
            if (name?.Contains("Mouse") == true)
                _mouseDeviceId = deviceId;
            else if (name?.Contains("Keyboard") == true)
                _keyboardDeviceId = deviceId;
            else if (name?.Contains("Gamepad") == true)
                _gamepadDeviceId = deviceId;
             
            // Debug logging removed — runs on SCTP thread, not Godot main thread
        }
        catch
        {
            // Silently ignore — see OnDataChannelMessage for thread safety notes
        }
    }

    private void ParseNewEvents(byte[] data)
    {
        if (data.Length < 24) return;
        
        // StateEvent format: [InputEvent:20][stateFormat:4][stateData:N]
        var stateFormat = BitConverter.ToInt32(data, 20);
        
        // Debug logging removed — runs on SCTP thread, not Godot main thread
        
        var stateData = new byte[data.Length - 24];
        if (stateData.Length > 0)
            Array.Copy(data, 24, stateData, 0, stateData.Length);
        
        switch (stateFormat)
        {
            case StateFormat.Mouse:
                MouseStateParser.Parse(stateData, _queue);
                break;
            case StateFormat.Keyboard:
                KeyboardStateParser.Parse(stateData, _queue);
                break;
            case StateFormat.Gamepad:
                ParseGamepadState(stateData);
                break;
            case StateFormat.Touch:
            case StateFormat.Touchscreen:
                ParseTouchState(stateData);
                break;
            case StateFormat.Text:
                break;
            default:
                // Unknown stateFormat — silently skip (runs on SCTP thread)
                break;
        }
    }

    private void ParseGamepadState(byte[] data)
    {
        // GamepadState format: [buttons:4][leftStick:8][rightStick:8][leftTrigger:4][rightTrigger:4] = 28 bytes
        if (data.Length < 28) return;
        
        var buttons = BitConverter.ToUInt32(data, 0);
        var leftStickX = BitConverter.ToSingle(data, 4);
        var leftStickY = BitConverter.ToSingle(data, 8);
        var rightStickX = BitConverter.ToSingle(data, 12);
        var rightStickY = BitConverter.ToSingle(data, 16);
        var leftTrigger = BitConverter.ToSingle(data, 20);
        var rightTrigger = BitConverter.ToSingle(data, 24);
        
        _queue.Enqueue(new InputEvent
        {
            Type = InputEventType.Gamepad,
            LeftStickX = leftStickX,
            LeftStickY = -leftStickY, // Flip Y
            RightStickX = rightStickX,
            RightStickY = -rightStickY,
            LeftTrigger = leftTrigger,
            RightTrigger = rightTrigger,
            GamepadButtons = buttons,
        });
    }

    private void ParseTouchState(byte[] data)
    {
        // TouchState format: [touchId:4][position:8][delta:8][pressure:4][radius:8][phase:1][tapCount:1][displayIndex:1][flags:1][padding:4][startTime:8][startPosition:8] = 56 bytes
        if (data.Length < 56) return;
        
        var touchId = BitConverter.ToInt32(data, 0);
        var x = BitConverter.ToSingle(data, 4);
        var y = BitConverter.ToSingle(data, 8);
        var phase = data[32]; // TouchPhase enum
        
        InputEventType eventType = phase switch
        {
            1 => InputEventType.TouchStart,  // Began
            2 => InputEventType.TouchMove,   // Moved
            3 => InputEventType.TouchEnd,    // Ended
            4 => InputEventType.TouchEnd,    // Canceled
            _ => InputEventType.TouchMove
        };
        
        _queue.Enqueue(new InputEvent
        {
            Type = eventType,
            X = x,
            Y = y,
            Button = touchId,
        });
    }

    public void Dispose()
    {
        _disposed = true;
        while (_queue.TryDequeue(out _)) { }
    }
}