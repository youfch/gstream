/**
 * gStream Input Sender
 * Captures mouse, keyboard, touch, and gamepad input from a video element
 * and forwards serialized events through the input remoting pipeline.
 */

import { Mouse, Keyboard, Touchscreen, Gamepad, StateEvent, TextEvent } from "./inputdevice.js";
import { LocalInputManager, InputRemoting } from "./inputremoting.js";
import { PointerCorrector } from "./pointercorrect.js";
import { GamepadHandler } from "./gamepadhandler.js";
import { Keymap } from "./keymap.js";
import * as Logger from "./logger.js";

/**
 * Sender captures browser input events and translates them into
 * binary input state events for transmission over WebRTC.
 */
export class Sender extends LocalInputManager {
  /**
   * @param {HTMLVideoElement} videoElement - The video element for coordinate mapping
   */
  constructor(videoElement) {
    super();
    this._videoElement = videoElement;
    this._corrector = null;
    this._mouse = null;
    this._keyboard = null;
    this._touchscreen = null;
    this._gamepad = null;
    this._gamepadHandler = null;
    this._deviceCounter = 1;

    this._boundHandlers = {};
  }

  /** Register a mouse input device and start listening for mouse events. */
  addMouse() {
    this._mouse = new Mouse(this._deviceCounter++);
    this.registerDevice(this._mouse);

    const handler = (e) => this._onMouseEvent(e);
    this._videoElement.addEventListener('mousedown', handler);
    this._videoElement.addEventListener('mouseup', handler);
    this._videoElement.addEventListener('mousemove', handler);
    this._videoElement.addEventListener('wheel', (e) => this._onWheelEvent(e));

    this._boundHandlers.mouse = handler;
  }

  /** Register a keyboard input device and start listening for keyboard events. */
  addKeyboard() {
    this._keyboard = new Keyboard(this._deviceCounter++);
    this.registerDevice(this._keyboard);

    const downHandler = (e) => this._onKeyDown(e);
    const upHandler = (e) => this._onKeyUp(e);
    document.addEventListener('keydown', downHandler);
    document.addEventListener('keyup', upHandler);

    this._boundHandlers.keydown = downHandler;
    this._boundHandlers.keyup = upHandler;
  }

  /** Register a touchscreen input device and start listening for touch events. */
  addTouchscreen() {
    this._touchscreen = new Touchscreen(this._deviceCounter++, 10);
    this.registerDevice(this._touchscreen);

    const startHandler = (e) => this._onTouchStart(e);
    const moveHandler = (e) => this._onTouchMove(e);
    const endHandler = (e) => this._onTouchEnd(e);
    const cancelHandler = (e) => this._onTouchCancel(e);

    this._videoElement.addEventListener('touchstart', startHandler);
    this._videoElement.addEventListener('touchmove', moveHandler);
    this._videoElement.addEventListener('touchend', endHandler);
    this._videoElement.addEventListener('touchcancel', cancelHandler);

    this._boundHandlers.touchstart = startHandler;
    this._boundHandlers.touchmove = moveHandler;
    this._boundHandlers.touchend = endHandler;
    this._boundHandlers.touchcancel = cancelHandler;
  }

  /** Register a gamepad input device and start polling gamepad state. */
  addGamepad() {
    this._gamepad = new Gamepad(this._deviceCounter++);
    this.registerDevice(this._gamepad);

    this._gamepadHandler = new GamepadHandler();
    this._gamepadPollTimer = setInterval(() => this._pollGamepad(), 1000 / 60);
  }

  // --- Mouse handlers ---

  _onMouseEvent(e) {
    this._ensureCorrector();

    const coords = this._corrector
      ? this._corrector.map(e.clientX, e.clientY)
      : { x: e.clientX, y: e.clientY };

    // Y is flipped: video frame Y=0 is at the bottom
    const videoHeight = this._videoElement.videoHeight || 1;
    const y = videoHeight - coords.y;

    this._mouse.setPosition(coords.x, y);
    this._mouse.setButtonsFromMask(e.buttons);

    const event = this._mouse.createStateEvent();
    this.queueEvent(event);
  }

  _onWheelEvent(e) {
    this._mouse.setScroll(e.deltaX, -e.deltaY);
    const event = this._mouse.createStateEvent();
    this.queueEvent(event);
  }

  // --- Keyboard handlers ---

  _onKeyDown(e) {
    if (!this._keyboard) return;
    const keyIndex = Keymap[e.code];
    if (keyIndex !== undefined) {
      this._keyboard.state.setKey(keyIndex, true);
    }
    const event = this._keyboard.createStateEvent();
    this.queueEvent(event);

    // Also send a text event for printable characters
    if (e.key.length === 1) {
      const textEvent = new TextEvent(2, this._keyboard.deviceId, e.key.charCodeAt(0));
      this.queueEvent(textEvent);
    }
  }

  _onKeyUp(e) {
    if (!this._keyboard) return;
    const keyIndex = Keymap[e.code];
    if (keyIndex !== undefined) {
      this._keyboard.state.setKey(keyIndex, false);
    }
    const event = this._keyboard.createStateEvent();
    this.queueEvent(event);
  }

  // --- Touch handlers ---

  _onTouchStart(e) {
    e.preventDefault();
    this._ensureCorrector();
    for (const touch of e.changedTouches) {
      const coords = this._corrector
        ? this._corrector.map(touch.clientX, touch.clientY)
        : { x: touch.clientX, y: touch.clientY };
      const videoHeight = this._videoElement.videoHeight || 1;
      this._touchscreen.touchStart(touch.identifier, coords.x, videoHeight - coords.y);
    }
    const event = this._touchscreen.createStateEvent();
    this.queueEvent(event);
  }

  _onTouchMove(e) {
    e.preventDefault();
    this._ensureCorrector();
    for (const touch of e.changedTouches) {
      const coords = this._corrector
        ? this._corrector.map(touch.clientX, touch.clientY)
        : { x: touch.clientX, y: touch.clientY };
      const videoHeight = this._videoElement.videoHeight || 1;
      this._touchscreen.touchMove(touch.identifier, coords.x, videoHeight - coords.y);
    }
    const event = this._touchscreen.createStateEvent();
    this.queueEvent(event);
  }

  _onTouchEnd(e) {
    e.preventDefault();
    this._ensureCorrector();
    for (const touch of e.changedTouches) {
      const coords = this._corrector
        ? this._corrector.map(touch.clientX, touch.clientY)
        : { x: touch.clientX, y: touch.clientY };
      const videoHeight = this._videoElement.videoHeight || 1;
      this._touchscreen.touchEnd(touch.identifier, coords.x, videoHeight - coords.y);
    }
    const event = this._touchscreen.createStateEvent();
    this.queueEvent(event);
  }

  _onTouchCancel(e) {
    e.preventDefault();
    for (const touch of e.changedTouches) {
      this._touchscreen.touchCancel(touch.identifier);
    }
    const event = this._touchscreen.createStateEvent();
    this.queueEvent(event);
  }

  // --- Gamepad ---

  _pollGamepad() {
    if (!this._gamepad) return;
    const gamepads = navigator.getGamepads();
    for (const gp of gamepads) {
      if (gp) {
        this._gamepad.updateFromGamepad(gp);
        const event = this._gamepad.createStateEvent();
        this.queueEvent(event);
        break; // Use first connected gamepad
      }
    }
  }

  // --- Helpers ---

  _ensureCorrector() {
    if (this._videoElement && this._videoElement.videoWidth > 0) {
      if (!this._corrector ||
          this._lastWidth !== this._videoElement.videoWidth ||
          this._lastHeight !== this._videoElement.videoHeight) {
        this._corrector = new PointerCorrector(
          this._videoElement.videoWidth,
          this._videoElement.videoHeight,
          this._videoElement
        );
        this._lastWidth = this._videoElement.videoWidth;
        this._lastHeight = this._videoElement.videoHeight;
      }
    }
  }
}

/**
 * Observer receives input events from InputRemoting and sends them
 * over an RTCDataChannel using the binary message envelope protocol.
 */
export class Observer {
  /**
   * @param {RTCDataChannel} channel
   */
  constructor(channel) {
    this._channel = channel;
  }

  /**
   * Called when a new device is registered.
   * @param {import('./inputdevice.js').InputDevice} device
   */
  onDeviceMessage(device) {
    if (this._channel.readyState !== 'open') return;
    const msg = this._createNewDeviceMessage(device);
    this._channel.send(msg);
  }

  /**
   * Called when new input events are available.
   * @param {Array<StateEvent|TextEvent>} events
   */
  onEventMessage(events) {
    if (this._channel.readyState !== 'open') return;
    const msg = this._createNewEventsMessage(events);
    this._channel.send(msg);
  }

  _createNewDeviceMessage(device) {
    // C# ParseNewDevice expects a JSON string payload:
    // {"name":"Mouse","layout":"Mouse","deviceId":1}
    const json = JSON.stringify({ name: device.name, layout: device.layout, deviceId: device.deviceId });
    const jsonBytes = new TextEncoder().encode(json);
    // Add a null terminator as the C# parser looks for it
    const payloadSize = jsonBytes.length + 1;
    const envelopeSize = 12;
    const totalSize = envelopeSize + payloadSize;

    const buffer = new ArrayBuffer(totalSize);
    const view = new DataView(buffer);
    let offset = 0;

    // Envelope header
    view.setInt32(offset, 0, true); offset += 4; // participantId
    view.setInt32(offset, 3, true); offset += 4; // type = NewDevice (3)
    view.setInt32(offset, payloadSize, true); offset += 4;

    // Payload: JSON string + null terminator
    new Uint8Array(buffer, offset, jsonBytes.length).set(jsonBytes);
    offset += jsonBytes.length;
    view.setUint8(offset, 0); // null terminator

    return buffer;
  }

  _createNewEventsMessage(events) {
    // C# ParseNewEvents expects the data section to contain
    // raw StateEvent serializations concatenated together.
    // Each StateEvent = [InputEvent header:20][stateFormat:4][stateData:N]
    // The C# parser reads stateFormat at offset 20 of the data section.
    const serializedEvents = events.map(e => e.serialize());
    let payloadSize = 0;
    for (const buf of serializedEvents) {
      payloadSize += buf.byteLength;
    }

    const envelopeSize = 12;
    const totalSize = envelopeSize + payloadSize;
    const buffer = new ArrayBuffer(totalSize);
    const view = new DataView(buffer);
    let offset = 0;

    // Envelope header
    view.setInt32(offset, 0, true); offset += 4; // participantId
    view.setInt32(offset, 4, true); offset += 4; // type = NewEvents (4)
    view.setInt32(offset, payloadSize, true); offset += 4;

    // Payload: concatenated StateEvent serializations
    for (const eventBuf of serializedEvents) {
      new Uint8Array(buffer, offset, eventBuf.byteLength).set(new Uint8Array(eventBuf));
      offset += eventBuf.byteLength;
    }

    return buffer;
  }
}
