/**
 * gStream Input Remoting
 * Transports input device events over an RTCDataChannel.
 * Manages device registration and event serialization using the
 * binary message envelope protocol.
 *
 * Message envelope: participant_id(int32) + type(int32) + length(int32) + data(ArrayBuffer)
 *
 * Message types (must match C# MessageType enum):
 *   NewDevice   = 3
 *   NewEvents   = 4
 */

import { StateEvent, TextEvent } from "./inputdevice.js";
import * as Logger from "./logger.js";

// Message types — must match C# MessageType values
const MSG_NEW_DEVICE = 3;
const MSG_NEW_EVENTS = 4;

/** Base class for input event sources. */
export class LocalInputManager {
  constructor() {
    this._devices = [];
    this._eventBuffer = [];
  }

  /**
   * Register a device for input tracking.
   * @param {import('./inputdevice.js').InputDevice} device
   */
  registerDevice(device) {
    this._devices.push(device);
  }

  /**
   * Queue a state event for sending.
   * @param {StateEvent|TextEvent} event
   */
  queueEvent(event) {
    this._eventBuffer.push(event);
  }

  /**
   * Drain and return all queued events, clearing the buffer.
   * @returns {Array<StateEvent|TextEvent>}
   */
  drainEvents() {
    const events = this._eventBuffer;
    this._eventBuffer = [];
    return events;
  }

  /**
   * Get all registered devices.
   * @returns {Array<import('./inputdevice.js').InputDevice>}
   */
  get devices() {
    return this._devices;
  }
}

/**
 * Wraps a payload in the message envelope.
 * Envelope: participant_id(int32) + type(int32) + length(int32) + data
 * @param {number} participantId
 * @param {number} msgType
 * @param {ArrayBuffer} payload
 * @returns {ArrayBuffer}
 */
function wrapEnvelope(participantId, msgType, payload) {
  const envelopeSize = 12; // 3 × int32
  const buffer = new ArrayBuffer(envelopeSize + payload.byteLength);
  const view = new DataView(buffer);

  view.setInt32(0, participantId, true);
  view.setInt32(4, msgType, true);
  view.setInt32(8, payload.byteLength, true);
  new Uint8Array(buffer, envelopeSize).set(new Uint8Array(payload));

  return buffer;
}

/**
 * InputRemoting manages the lifecycle of sending input data over a DataChannel.
 * It subscribes to a LocalInputManager and forwards device registrations and
 * input events through the channel.
 */
export class InputRemoting {
  /**
   * @param {LocalInputManager} inputManager - The source of input events
   */
  constructor(inputManager) {
    this._inputManager = inputManager;
    this._subscriber = null;
    this._sendingInterval = null;
    this._participantId = 0;
  }

  /**
   * Set the message subscriber (typically an Observer wrapping a DataChannel).
   * @param {{ onDeviceMessage: function, onEventMessage: function }} subscriber
   */
  subscribe(subscriber) {
    this._subscriber = subscriber;
  }

  /**
   * Start sending input events periodically.
   */
  startSending() {
    if (this._sendingInterval) return;

    // First, register all devices
    for (const device of this._inputManager.devices) {
      this._sendNewDevice(device);
    }

    // Then poll for events
    this._sendingInterval = setInterval(() => {
      const events = this._inputManager.drainEvents();
      if (events.length > 0 && this._subscriber) {
        this._subscriber.onEventMessage(events);
      }
    }, 1000 / 60); // ~60Hz
  }

  /**
   * Stop sending input events.
   */
  stopSending() {
    if (this._sendingInterval) {
      clearInterval(this._sendingInterval);
      this._sendingInterval = null;
    }
  }

  _sendNewDevice(device) {
    if (this._subscriber) {
      this._subscriber.onDeviceMessage(device);
    }
  }
}
