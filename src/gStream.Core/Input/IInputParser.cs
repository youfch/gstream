using System;

namespace gStream.Core.Input;

/// <summary>
/// Abstraction for input parsers that consume DataChannel binary messages
/// and produce <see cref="InputEvent"/> structs for injection into Godot.
/// </summary>
public interface IInputParser : IDisposable
{
    /// <summary>
    /// Called when a binary message arrives on the input DataChannel.
    /// </summary>
    void OnDataChannelMessage(byte[] data);

    /// <summary>
    /// Dequeues a single parsed event. Returns false if the queue is empty.
    /// </summary>
    bool TryDequeue(out InputEvent evt);
}
