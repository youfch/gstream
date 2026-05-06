/**
 * gStream Gamepad Event Dispatcher
 * Polls connected gamepads and fires CustomEvents on the document
 * when button or axis state changes. Used by the videoplayer mode.
 */

import * as Logger from "../../module/logger.js";

const DEADZONE = 0.09;
const POLL_INTERVAL = 16.67; // ~60 FPS
const AXIS_OFFSET = 100;
const AXIS_Y_INVERT = -1;

let pollTimer = null;
let prevButtonStates = {};
let prevAxisStates = {};
let connectedTimestamps = {};

class GamepadButtonEvent extends Event {
  constructor(type, detail) {
    super(type);
    this.index = detail.index;
    this.id = detail.id;
    this.value = detail.value;
  }
}

class GamepadAxisEvent extends Event {
  constructor(type, detail) {
    super(type);
    this.index = detail.index;
    this.x = detail.x;
    this.y = detail.y;
    this.id = detail.id;
  }
}

function saveState(gamepad) {
  prevButtonStates[gamepad.index] = {};
  gamepad.buttons.forEach((btn, i) => {
    prevButtonStates[gamepad.index][i] = { value: btn.value, pressed: btn.pressed };
  });
  prevAxisStates[gamepad.index] = [];
  for (let i = 0; i < gamepad.axes.length; i++) {
    prevAxisStates[gamepad.index][i] = gamepad.axes[i];
  }
}

function checkAxes(gamepad, prevAxes) {
  for (let i = 0; i < gamepad.axes.length; i += 2) {
    const x = gamepad.axes[i];
    const y = gamepad.axes[i + 1];
    const absX = Math.abs(x);
    const absY = Math.abs(y);

    if (absX > DEADZONE || absY > DEADZONE) {
      document.dispatchEvent(new GamepadAxisEvent('gamepadAxis', {
        id: connectedTimestamps[gamepad.index],
        index: i / 2 + AXIS_OFFSET,
        x: x,
        y: y * AXIS_Y_INVERT
      }));
    } else if (Math.abs(prevAxes[i]) > DEADZONE || Math.abs(prevAxes[i + 1]) > DEADZONE) {
      // Axis returned to center — send zeroed event
      document.dispatchEvent(new GamepadAxisEvent('gamepadAxis', {
        id: connectedTimestamps[gamepad.index],
        index: i / 2 + AXIS_OFFSET,
        x: 0.0,
        y: 0.0
      }));
    }
  }
}

function pollLoop() {
  Object.keys(prevAxisStates).forEach(idx => {
    const gamepad = navigator.getGamepads()[idx];
    if (!gamepad) return;

    const prevBtns = prevButtonStates[idx];

    gamepad.buttons.forEach((btn, i) => {
      const isDown = btn.value > 0 || btn.pressed;
      const wasDown = prevBtns[i].value > 0 || prevBtns[i].pressed;

      if (isDown !== wasDown) {
        const type = isDown ? 'gamepadButtonDown' : 'gamepadButtonUp';
        document.dispatchEvent(new GamepadButtonEvent(type, {
          id: connectedTimestamps[gamepad.index],
          index: i,
          value: isDown ? btn.value : 0
        }));
      } else if (isDown) {
        document.dispatchEvent(new GamepadButtonEvent('gamepadButtonPressed', {
          id: connectedTimestamps[gamepad.index],
          index: i,
          value: btn.value
        }));
      }
    });

    checkAxes(gamepad, prevAxisStates[idx]);
    saveState(gamepad);
  });
}

function readCookie(name) {
  const decoded = decodeURIComponent(document.cookie);
  const entries = decoded.split(';');
  for (const entry of entries) {
    let c = entry.trim();
    if (c.startsWith(name + '=')) {
      return c.substring(name.length + 1);
    }
  }
  return '';
}

/**
 * Handle gamepad connect/disconnect events.
 * @param {GamepadEvent} event
 * @param {boolean} connecting
 */
export function gamepadHandler(event, connecting) {
  const gamepad = event.gamepad;
  const key = gamepad.id.replace(/\s/g, '');
  const cookieTs = readCookie(key);

  if (connecting) {
    saveState(gamepad);
    if (Object.keys(prevAxisStates).length === 1) {
      pollTimer = setInterval(pollLoop, POLL_INTERVAL);
    }

    if (!cookieTs) {
      document.cookie = key + '=' + gamepad.timestamp;
      connectedTimestamps[gamepad.index] = gamepad.timestamp;
    } else {
      connectedTimestamps[gamepad.index] = parseFloat(cookieTs);
    }

    Logger.log('gamepad connected: ' + connectedTimestamps[gamepad.index]);
  } else {
    delete prevAxisStates[gamepad.index];
    delete prevButtonStates[gamepad.index];
    if (Object.keys(prevAxisStates).length === 0 && pollTimer) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
    Logger.log('gamepad disconnected: ' + gamepad.id);
  }
}
