/**
 * gStream Input Device State Serialization
 * Encodes mouse, keyboard, touchscreen, and gamepad input into binary buffers
 * matching the wire format expected by the C# input receiver on the Godot side.
 *
 * Binary layout reference:
 *   InputEvent header: type(int32) + sizeInBytes(int16) + deviceId(int16) + time(float64) + eventId(int16)
 *   StateEvent: InputEvent header + stateFormat(int32) + stateData
 *   TextEvent:  InputEvent header + character(int32)
 *
 * Mouse State (MOUS):     30 bytes
 * Keyboard State (KEYS):  14 bytes
 * Touch State (TOCT):     56 bytes per touch
 * Gamepad State (GPAD):   28 bytes
 */

import { MouseButtons } from "./mousebutton.js";
import { TouchPhase } from "./touchphase.js";

// --- FourCC Helper ---

export class FourCC {
  /**
   * Create a 32-bit FourCC value from a 4-character string.
   * Matches the C# StateFormat convention: firstChar<<24 | second<<16 | third<<8 | fourth.
   * When written with DataView.setInt32(littleEndian=true), the wire bytes match
   * what BitConverter.ToInt32 reads on little-endian (x86) systems.
   * @param {string} code - Exactly 4 ASCII characters
   * @returns {number} 32-bit FourCC integer
   */
  static toInt32(code) {
    return (
      (code.charCodeAt(3)) |
      (code.charCodeAt(2) << 8) |
      (code.charCodeAt(1) << 16) |
      (code.charCodeAt(0) << 24)
    );
  }
}

// FourCC codes for input state formats
const FOURCC_MOUS = FourCC.toInt32('MOUS');
const FOURCC_KEYS = FourCC.toInt32('KEYS');
const FOURCC_TOCT = FourCC.toInt32('TOCT');
const FOURCC_GPAD = FourCC.toInt32('GPAD');
const FOURCC_STAT = FourCC.toInt32('STAT');
const FOURCC_TEXT = FourCC.toInt32('TEXT');

// --- Input Event Header ---
// Layout: type(int32) + sizeInBytes(int16) + deviceId(int16) + time(float64) + eventId(int16)
// Total: 4 + 2 + 2 + 8 + 2 = 18 bytes (but padded to 20 in the protocol)

const INPUT_EVENT_HEADER_SIZE = 20;
const STATE_EVENT_EXTRA_HEADER = 4; // stateFormat int32

export class InputEvent {
  constructor(type, sizeInBytes, deviceId, time, eventId) {
    this.type = type;
    this.sizeInBytes = sizeInBytes;
    this.deviceId = deviceId;
    this.time = time;
    this.eventId = eventId;
  }

  /**
   * Write the InputEvent header into a DataView at the given offset.
   * @param {DataView} view
   * @param {number} offset
   */
  writeTo(view, offset) {
    view.setInt32(offset, this.type, true);
    offset += 4;
    view.setInt16(offset, this.sizeInBytes, true);
    offset += 2;
    view.setInt16(offset, this.deviceId, true);
    offset += 2;
    view.setFloat64(offset, this.time, true);
    offset += 8;
    view.setInt16(offset, this.eventId, true);
  }
}

// --- Input State Interface ---

export class IInputState {
  /**
   * @returns {number} Size of this state in bytes
   */
  get sizeInBytes() { return 0; }

  /**
   * Write this state into a DataView at the given byte offset.
   * @param {DataView} _view
   * @param {number} _offset
   */
  writeTo(_view, _offset) { }
}

// --- Mouse State ---
// Layout (30 bytes):
//   position: float32[2]   (offset 0, 4)
//   delta: float32[2]      (offset 8, 12)
//   scroll: float32[2]     (offset 16, 20)
//   buttons: uint16        (offset 24)  — bitmask
//   displayIndex: uint16   (offset 26)
//   clickCount: uint16     (offset 28)

export class MouseState extends IInputState {
  constructor() {
    super();
    this.position = [0, 0];
    this.delta = [0, 0];
    this.scroll = [0, 0];
    this.buttons = 0;
    this.displayIndex = 0;
    this.clickCount = 0;
  }

  get sizeInBytes() { return 30; }

  writeTo(view, offset) {
    view.setFloat32(offset, this.position[0], true); offset += 4;
    view.setFloat32(offset, this.position[1], true); offset += 4;
    view.setFloat32(offset, this.delta[0], true); offset += 4;
    view.setFloat32(offset, this.delta[1], true); offset += 4;
    view.setFloat32(offset, this.scroll[0], true); offset += 4;
    view.setFloat32(offset, this.scroll[1], true); offset += 4;
    view.setUint16(offset, this.buttons, true); offset += 2;
    view.setUint16(offset, this.displayIndex, true); offset += 2;
    view.setUint16(offset, this.clickCount, true);
  }
}

// --- Keyboard State ---
// Layout (14 bytes): 110 bits for keys, packed into 14 bytes
// Bit i = 1 means key index i is currently pressed

export class KeyboardState extends IInputState {
  constructor() {
    super();
    this.keys = new Uint8Array(14); // 14 bytes = 112 bits (110 keys + 2 padding)
  }

  get sizeInBytes() { return 14; }

  writeTo(view, offset) {
    for (let i = 0; i < 14; i++) {
      view.setUint8(offset + i, this.keys[i]);
    }
  }

  /**
   * Set or clear a key bit.
   * @param {number} keyIndex - Key index (0–109)
   * @param {boolean} pressed
   */
  setKey(keyIndex, pressed) {
    const byteIndex = keyIndex >> 3; // keyIndex / 8
    const bitIndex = keyIndex & 7;   // keyIndex % 8
    if (pressed) {
      this.keys[byteIndex] |= (1 << bitIndex);
    } else {
      this.keys[byteIndex] &= ~(1 << bitIndex);
    }
  }

  /**
   * Check if a key is pressed.
   * @param {number} keyIndex
   * @returns {boolean}
   */
  isKeyPressed(keyIndex) {
    const byteIndex = keyIndex >> 3;
    const bitIndex = keyIndex & 7;
    return (this.keys[byteIndex] & (1 << bitIndex)) !== 0;
  }
}

// --- Touch State ---
// Layout (56 bytes per touch):
//   touchId: int32         (offset 0)
//   position: float32[2]   (offset 4, 8)
//   delta: float32[2]      (offset 12, 16)
//   pressure: float32      (offset 20)
//   radius: float32[2]     (offset 24, 28)
//   phaseId: int8          (offset 32)
//   tapCount: int8         (offset 33)
//   displayIndex: int8     (offset 34)
//   flags: int8            (offset 35)
//   padding: int32         (offset 36)
//   startTime: float64     (offset 40)
//   startPosition: float32[2] (offset 48, 52)

export class TouchState extends IInputState {
  constructor() {
    super();
    this.touchId = 0;
    this.position = [0, 0];
    this.delta = [0, 0];
    this.pressure = 0;
    this.radius = [0, 0];
    this.phaseId = TouchPhase.Began;
    this.tapCount = 0;
    this.displayIndex = 0;
    this.flags = 0;
    this.startTime = 0;
    this.startPosition = [0, 0];
  }

  get sizeInBytes() { return 56; }

  writeTo(view, offset) {
    view.setInt32(offset, this.touchId, true); offset += 4;
    view.setFloat32(offset, this.position[0], true); offset += 4;
    view.setFloat32(offset, this.position[1], true); offset += 4;
    view.setFloat32(offset, this.delta[0], true); offset += 4;
    view.setFloat32(offset, this.delta[1], true); offset += 4;
    view.setFloat32(offset, this.pressure, true); offset += 4;
    view.setFloat32(offset, this.radius[0], true); offset += 4;
    view.setFloat32(offset, this.radius[1], true); offset += 4;
    view.setInt8(offset, this.phaseId); offset += 1;
    view.setInt8(offset, this.tapCount); offset += 1;
    view.setInt8(offset, this.displayIndex); offset += 1;
    view.setInt8(offset, this.flags); offset += 1;
    view.setInt32(offset, 0, true); offset += 4; // padding
    view.setFloat64(offset, this.startTime, true); offset += 8;
    view.setFloat32(offset, this.startPosition[0], true); offset += 4;
    view.setFloat32(offset, this.startPosition[1], true);
  }
}

// --- Touchscreen State ---
// Contains an array of TouchState entries

export class TouchscreenState extends IInputState {
  constructor(maxTouches) {
    super();
    this.maxTouches = maxTouches || 10;
    this.touches = [];
    for (let i = 0; i < this.maxTouches; i++) {
      this.touches.push(new TouchState());
    }
  }

  get sizeInBytes() { return 56 * this.maxTouches; }

  writeTo(view, offset) {
    for (const touch of this.touches) {
      touch.writeTo(view, offset);
      offset += 56;
    }
  }
}

// --- Gamepad State ---
// Layout (28 bytes):
//   buttons: uint32       (offset 0)  — bitmask for each button
//   leftStick: float32[2] (offset 4, 8)   — axes[0], -axes[1]
//   rightStick: float32[2](offset 12, 16) — axes[2], -axes[3]
//   leftTrigger: float32  (offset 20) — buttons[6].value
//   rightTrigger: float32 (offset 24) — buttons[7].value

export class GamepadState extends IInputState {
  constructor() {
    super();
    this.buttons = 0;
    this.leftStick = [0, 0];
    this.rightStick = [0, 0];
    this.leftTrigger = 0;
    this.rightTrigger = 0;
  }

  get sizeInBytes() { return 28; }

  writeTo(view, offset) {
    view.setUint32(offset, this.buttons, true); offset += 4;
    view.setFloat32(offset, this.leftStick[0], true); offset += 4;
    view.setFloat32(offset, this.leftStick[1], true); offset += 4;
    view.setFloat32(offset, this.rightStick[0], true); offset += 4;
    view.setFloat32(offset, this.rightStick[1], true); offset += 4;
    view.setFloat32(offset, this.leftTrigger, true); offset += 4;
    view.setFloat32(offset, this.rightTrigger, true);
  }

  /**
   * Set or clear a button bit.
   * @param {number} buttonIndex - Gamepad button index (0–15)
   * @param {boolean} pressed
   */
  setButton(buttonIndex, pressed) {
    if (pressed) {
      this.buttons |= (1 << buttonIndex);
    } else {
      this.buttons &= ~(1 << buttonIndex);
    }
  }

  /**
   * Update state from a standard Web Gamepad object.
   * @param {Gamepad} gamepad
   */
  updateFromGamepad(gamepad) {
    this.buttons = 0;
    for (let i = 0; i < Math.min(gamepad.buttons.length, 16); i++) {
      if (gamepad.buttons[i].pressed) {
        this.buttons |= (1 << i);
      }
    }

    if (gamepad.axes.length >= 2) {
      this.leftStick[0] = gamepad.axes[0];
      this.leftStick[1] = -gamepad.axes[1];
    }
    if (gamepad.axes.length >= 4) {
      this.rightStick[0] = gamepad.axes[2];
      this.rightStick[1] = -gamepad.axes[3];
    }

    this.leftTrigger = gamepad.buttons.length > 6 ? gamepad.buttons[6].value : 0;
    this.rightTrigger = gamepad.buttons.length > 7 ? gamepad.buttons[7].value : 0;
  }
}

// --- StateEvent ---
// Wraps an InputEvent header + a state format FourCC + state data

export class StateEvent {
  /**
   * @param {number} type - Input event type
   * @param {number} deviceId
   * @param {number} stateFormat - FourCC format code
   * @param {IInputState} state - The state to serialize
   */
  constructor(type, deviceId, stateFormat, state) {
    const stateSize = state.sizeInBytes;
    const totalSize = INPUT_EVENT_HEADER_SIZE + STATE_EVENT_EXTRA_HEADER + stateSize;

    this.header = new InputEvent(type, totalSize, deviceId, performance.now() / 1000, 0);
    this.stateFormat = stateFormat;
    this.state = state;
    this.totalSize = totalSize;
  }

  /**
   * Serialize the complete event into a new ArrayBuffer.
   * @returns {ArrayBuffer}
   */
  serialize() {
    const buffer = new ArrayBuffer(this.totalSize);
    const view = new DataView(buffer);
    let offset = 0;

    this.header.writeTo(view, offset);
    offset += INPUT_EVENT_HEADER_SIZE;

    view.setInt32(offset, this.stateFormat, true);
    offset += STATE_EVENT_EXTRA_HEADER;

    this.state.writeTo(view, offset);
    return buffer;
  }
}

// --- TextEvent ---
// InputEvent header + character code point (int32)

export class TextEvent {
  constructor(type, deviceId, character) {
    const totalSize = INPUT_EVENT_HEADER_SIZE + 4; // +4 for character int32
    this.header = new InputEvent(type, totalSize, deviceId, performance.now() / 1000, 0);
    this.character = character;
    this.totalSize = totalSize;
  }

  serialize() {
    const buffer = new ArrayBuffer(this.totalSize);
    const view = new DataView(buffer);
    let offset = 0;

    this.header.writeTo(view, offset);
    offset += INPUT_EVENT_HEADER_SIZE;

    view.setInt32(offset, this.character, true);
    return buffer;
  }
}

// --- Input Device Base ---

export class InputDevice {
  /**
   * @param {string} name - Device name
   * @param {string} layout - Device layout identifier
   * @param {number} deviceId - Unique device ID
   */
  constructor(name, layout, deviceId) {
    this.name = name;
    this.layout = layout;
    this.deviceId = deviceId;
  }
}

// --- Mouse Device ---

export class Mouse extends InputDevice {
  constructor(deviceId) {
    super('Mouse', 'Mouse', deviceId);
    this.state = new MouseState();
    this._prevButtons = 0;
  }

  /**
   * Update mouse position.
   * @param {number} x - Position X in video frame coordinates
   * @param {number} y - Position Y in video frame coordinates
   */
  setPosition(x, y) {
    this.state.delta[0] = x - this.state.position[0];
    this.state.delta[1] = y - this.state.position[1];
    this.state.position[0] = x;
    this.state.position[1] = y;
  }

  /**
   * Update button state from a DOM mouse event's `buttons` bitmask.
   * @param {number} buttonMask - DOM MouseEvent.buttons value
   */
  setButtonsFromMask(buttonMask) {
    this.state.buttons = 0;
    if (buttonMask & 1) this.state.buttons |= (1 << MouseButtons.Left);
    if (buttonMask & 2) this.state.buttons |= (1 << MouseButtons.Right);
    if (buttonMask & 4) this.state.buttons |= (1 << MouseButtons.Middle);
    if (buttonMask & 8) this.state.buttons |= (1 << MouseButtons.Back);
    if (buttonMask & 16) this.state.buttons |= (1 << MouseButtons.Forward);
  }

  /**
   * Update scroll delta.
   * @param {number} deltaX
   * @param {number} deltaY
   */
  setScroll(deltaX, deltaY) {
    this.state.scroll[0] = deltaX;
    this.state.scroll[1] = deltaY;
  }

  /**
   * Create a StateEvent for this mouse.
   * @returns {StateEvent}
   */
  createStateEvent() {
    return new StateEvent(1, this.deviceId, FOURCC_MOUS, this.state);
  }
}

// --- Keyboard Device ---

export class Keyboard extends InputDevice {
  constructor(deviceId) {
    super('Keyboard', 'Keyboard', deviceId);
    this.state = new KeyboardState();
  }

  /**
   * Create a StateEvent for this keyboard.
   * @returns {StateEvent}
   */
  createStateEvent() {
    return new StateEvent(1, this.deviceId, FOURCC_KEYS, this.state);
  }
}

// --- Touchscreen Device ---

export class Touchscreen extends InputDevice {
  constructor(deviceId, maxTouches) {
    super('Touchscreen', 'Touchscreen', deviceId);
    this.state = new TouchscreenState(maxTouches);
    this._activeTouches = new Map();
  }

  /**
   * Handle a touch start.
   * @param {number} touchId
   * @param {number} x
   * @param {number} y
   */
  touchStart(touchId, x, y) {
    const ts = this._getTouchState(touchId);
    ts.touchId = touchId;
    ts.position[0] = x;
    ts.position[1] = y;
    ts.delta[0] = 0;
    ts.delta[1] = 0;
    ts.phaseId = TouchPhase.Began;
    ts.pressure = 1.0;
    ts.startTime = performance.now() / 1000;
    ts.startPosition[0] = x;
    ts.startPosition[1] = y;
  }

  /**
   * Handle a touch move.
   * @param {number} touchId
   * @param {number} x
   * @param {number} y
   */
  touchMove(touchId, x, y) {
    const ts = this._getTouchState(touchId);
    ts.delta[0] = x - ts.position[0];
    ts.delta[1] = y - ts.position[1];
    ts.position[0] = x;
    ts.position[1] = y;
    ts.phaseId = TouchPhase.Moved;
    ts.pressure = 1.0;
  }

  /**
   * Handle a touch end.
   * @param {number} touchId
   * @param {number} x
   * @param {number} y
   */
  touchEnd(touchId, x, y) {
    const ts = this._getTouchState(touchId);
    ts.delta[0] = x - ts.position[0];
    ts.delta[1] = y - ts.position[1];
    ts.position[0] = x;
    ts.position[1] = y;
    ts.phaseId = TouchPhase.Ended;
    ts.pressure = 0;
  }

  /**
   * Handle a touch cancel.
   * @param {number} touchId
   */
  touchCancel(touchId) {
    const ts = this._getTouchState(touchId);
    ts.phaseId = TouchPhase.Canceled;
    ts.pressure = 0;
  }

  _getTouchState(touchId) {
    if (!this._activeTouches.has(touchId)) {
      // Find an available slot
      const index = this._activeTouches.size;
      if (index < this.state.maxTouches) {
        this._activeTouches.set(touchId, index);
      }
    }
    const index = this._activeTouches.get(touchId) || 0;
    return this.state.touches[index];
  }

  /**
   * Create a StateEvent for this touchscreen.
   * @returns {StateEvent}
   */
  createStateEvent() {
    return new StateEvent(1, this.deviceId, FOURCC_TOCT, this.state);
  }
}

// --- Gamepad Device ---

export class Gamepad extends InputDevice {
  constructor(deviceId) {
    super('Gamepad', 'Gamepad', deviceId);
    this.state = new GamepadState();
  }

  /**
   * Update state from a Web Gamepad object.
   * @param {Gamepad} gamepad
   */
  updateFromGamepad(gamepad) {
    this.state.updateFromGamepad(gamepad);
  }

  /**
   * Create a StateEvent for this gamepad.
   * @returns {StateEvent}
   */
  createStateEvent() {
    return new StateEvent(1, this.deviceId, FOURCC_GPAD, this.state);
  }
}
