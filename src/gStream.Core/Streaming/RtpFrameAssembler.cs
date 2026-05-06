// RtpFrameAssembler.cs — Assembles incoming RTP packets into complete H.264 access units (RFC 6184)

using System;
using System.Collections.Generic;
using System.IO;

namespace gStream.Core.Streaming;

/// <summary>
/// Assembles RTP packets carrying H.264 payload (RFC 6184) into complete NAL units.
/// Handles Single NAL Unit Packets, STAP-A aggregation, and FU-A fragmentation.
/// Thread-safe: all public methods are synchronized.
/// </summary>
public sealed class RtpFrameAssembler : IDisposable
{
    /// <summary>
    /// Fired when a complete access unit (one or more NAL units forming a video frame) is assembled.
    /// The byte[] contains Annex-B formatted data (00 00 00 01 prefixed NAL units).
    /// </summary>
    public event Action<byte[], int, bool>? OnFrameAssembled;

    // Accumulator for current frame's RTP packets (same timestamp)
    private readonly List<RtpPacketInfo> _currentFramePackets = new();
    private uint _currentTimestamp;
    private bool _hasTimestamp;

    // FU-A fragmentation reassembly buffer
    private readonly MemoryStream _fuBuffer = new();

    private bool _disposed;

    /// <summary>
    /// Processes a raw RTP payload. Call for each received RTP packet.
    /// When a complete frame is assembled (marker bit = 1), fires OnFrameAssembled.
    /// </summary>
    /// <param name="payload">RTP payload bytes (after RTP header has been stripped).</param>
    /// <param name="seqNum">RTP sequence number.</param>
    /// <param name="timestamp">RTP timestamp.</param>
    /// <param name="markerBit">RTP marker bit (1 = last packet of frame).</param>
    public void ProcessRtpPacket(byte[] payload, ushort seqNum, uint timestamp, int markerBit)
    {
        if (_disposed || payload == null || payload.Length == 0) return;

        // Timestamp change = new frame. Flush any accumulated packets for previous frame.
        if (_hasTimestamp && timestamp != _currentTimestamp)
        {
            // Previous frame was incomplete (no marker bit seen). Discard and start fresh.
            _currentFramePackets.Clear();
            _fuBuffer.SetLength(0);
        }

        _currentTimestamp = timestamp;
        _hasTimestamp = true;

        _currentFramePackets.Add(new RtpPacketInfo(seqNum, payload));

        if (markerBit == 1)
        {
            // End of frame — reorder by sequence number and process
            if (_currentFramePackets.Count > 1)
            {
                _currentFramePackets.Sort((a, b) =>
                {
                    // Handle wraparound (assume max ~2000 packets per frame)
                    return (Math.Abs(b.SeqNum - a.SeqNum) > (0xFFFF - 2000))
                        ? -a.SeqNum.CompareTo(b.SeqNum)
                        : a.SeqNum.CompareTo(b.SeqNum);
                });
            }

            var frame = ProcessFrame(_currentFramePackets, out bool isKeyFrame);
            _currentFramePackets.Clear();

            if (frame != null && frame.Length > 0)
            {
                OnFrameAssembled?.Invoke(frame, frame.Length, isKeyFrame);
            }
        }
    }

    /// <summary>
    /// Processes all RTP packets for a single frame (same timestamp, marker bit on last).
    /// Returns Annex-B formatted H.264 access unit or null if assembly failed.
    /// </summary>
    private byte[]? ProcessFrame(List<RtpPacketInfo> packets, out bool isKeyFrame)
    {
        isKeyFrame = false;
        var nalUnits = new List<byte[]>();

        foreach (var pkt in packets)
        {
            var data = pkt.Payload;
            if (data.Length == 0) continue;

            byte firstByte = data[0];
            int nalType = firstByte & 0x1F;

            if (nalType >= 1 && nalType <= 23)
            {
                // Single NAL Unit Packet — the entire payload is one NAL
                CheckKeyFrame(nalType, ref isKeyFrame);
                nalUnits.Add(data);
            }
            else if (nalType == 24)
            {
                // STAP-A — multiple NAL units aggregated in one packet
                ParseStapA(data, nalUnits, ref isKeyFrame);
            }
            else if (nalType == 28)
            {
                // FU-A — Fragmentation Unit
                ParseFuA(data, nalUnits, ref isKeyFrame);
            }
            // Types 25 (STAP-B), 26 (MTAP16), 27 (MTAP24), 29 (FU-B) not supported
        }

        // Flush any remaining FU-A data
        if (_fuBuffer.Length > 0)
        {
            var fragData = _fuBuffer.ToArray();
            if (fragData.Length > 0)
            {
                int nalType = fragData[0] & 0x1F;
                CheckKeyFrame(nalType, ref isKeyFrame);
                nalUnits.Add(fragData);
            }
            _fuBuffer.SetLength(0);
        }

        if (nalUnits.Count == 0) return null;

        // Build Annex-B output: 00 00 00 01 prefix + NAL data for each unit
        long totalSize = 0;
        for (int i = nalUnits.Count - 1; i >= 0; i--)
        {
            if (nalUnits[i].Length == 0)
            {
                nalUnits.RemoveAt(i);
            }
            else
            {
                totalSize += nalUnits[i].Length + 4; // 4 bytes for 00 00 00 01
            }
        }

        if (nalUnits.Count == 0) return null;

        var result = new byte[totalSize];
        int offset = 0;
        foreach (var nal in nalUnits)
        {
            result[offset++] = 0;
            result[offset++] = 0;
            result[offset++] = 0;
            result[offset++] = 1;
            Buffer.BlockCopy(nal, 0, result, offset, nal.Length);
            offset += nal.Length;
        }

        return result;
    }

    /// <summary>
    /// Parses STAP-A (Single-Time Aggregation Packet type A) payload.
    /// Multiple NALs with 16-bit size headers.
    /// </summary>
    private static void ParseStapA(byte[] data, List<byte[]> nalUnits, ref bool isKeyFrame)
    {
        int ptr = 1; // Skip the STAP-A indicator byte (type 24)
        while (ptr + 2 < data.Length)
        {
            int size = (data[ptr] << 8) | data[ptr + 1];
            ptr += 2;

            if (ptr + size > data.Length) break;

            var nal = new byte[size];
            Buffer.BlockCopy(data, ptr, nal, 0, size);

            if (nal.Length > 0)
            {
                int nalType = nal[0] & 0x1F;
                CheckKeyFrame(nalType, ref isKeyFrame);
                nalUnits.Add(nal);
            }

            ptr += size;
        }
    }

    /// <summary>
    /// Parses FU-A (Fragmentation Unit type A) payload.
    /// Accumulates fragments and emits a complete NAL when the last fragment arrives.
    /// </summary>
    private void ParseFuA(byte[] data, List<byte[]> nalUnits, ref bool isKeyFrame)
    {
        if (data.Length < 3) return;

        int fuIndicator = data[0]; // F bit, NRI, type=28
        int fuHeader = data[1];
        int startBit = (fuHeader >> 7) & 0x01;
        int endBit = (fuHeader >> 6) & 0x01;
        int fuType = fuHeader & 0x1F;

        if (startBit == 1)
        {
            // Start of fragmentation — reset buffer
            _fuBuffer.SetLength(0);

            // Reconstruct original NAL header: F + NRI from FU indicator, type from FU header
            byte reconstructedNalHeader = (byte)((fuIndicator & 0xE0) | fuType);
            _fuBuffer.WriteByte(reconstructedNalHeader);

            // Append payload after FU indicator (1 byte) + FU header (1 byte)
            _fuBuffer.Write(data, 2, data.Length - 2);
        }
        else if (endBit == 1)
        {
            // End of fragmentation — append and emit complete NAL
            _fuBuffer.Write(data, 2, data.Length - 2);

            var completeNal = _fuBuffer.ToArray();
            if (completeNal.Length > 0)
            {
                int nalType = completeNal[0] & 0x1F;
                CheckKeyFrame(nalType, ref isKeyFrame);
                nalUnits.Add(completeNal);
            }
            _fuBuffer.SetLength(0);
        }
        else
        {
            // Middle fragment — just append
            _fuBuffer.Write(data, 2, data.Length - 2);
        }
    }

    /// <summary>
    /// Determines if a NAL unit type indicates a keyframe.
    /// Keyframe NAL types: SPS (7), PPS (8), IDR slice (5).
    /// Non-keyframe: Non-IDR slice (1).
    /// </summary>
    private static void CheckKeyFrame(int nalType, ref bool isKeyFrame)
    {
        if (nalType == 7 || nalType == 8 || nalType == 5)
        {
            isKeyFrame = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fuBuffer.Dispose();
        _currentFramePackets.Clear();
    }

    private readonly struct RtpPacketInfo
    {
        public readonly ushort SeqNum;
        public readonly byte[] Payload;

        public RtpPacketInfo(ushort seqNum, byte[] payload)
        {
            SeqNum = seqNum;
            Payload = payload;
        }
    }
}
