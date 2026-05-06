/**
 * gStream Gamepad Handler
 * Polls connected gamepads at animation frame rate and dispatches
 * events when button/axis states change.
 */

export class GamepadHandler extends EventTarget {
  constructor() {
    super();
    this._running = false;
    this._previousButtons = {};
    this._previousAxes = {};
    this._frameId = null;

    window.addEventListener('gamepadconnected', (e) => {
      this._storeState(e.gamepad);
      if (!this._running) {
        this._startPolling();
      }
    });

    window.addEventListener('gamepaddisconnected', (e) => {
      delete this._previousButtons[e.gamepad.index];
      delete this._previousAxes[e.gamepad.index];
    });
  }

  _startPolling() {
    this._running = true;
    this._poll();
  }

  _poll() {
    if (!this._running) return;

    const gamepads = navigator.getGamepads();
    for (let i = 0; i < gamepads.length; i++) {
      const gp = gamepads[i];
      if (!gp) continue;

      const prevButtons = this._previousButtons[gp.index];
      const prevAxes = this._previousAxes[gp.index];

      if (prevButtons) {
        // Check buttons
        for (let b = 0; b < gp.buttons.length; b++) {
          const pressed = gp.buttons[b].pressed;
          const value = gp.buttons[b].value;
          const wasPressed = prevButtons[b] !== undefined ? prevButtons[b] : false;

          if (pressed !== wasPressed) {
            this.dispatchEvent(new CustomEvent(pressed ? 'gamepadbuttondown' : 'gamepadbuttonup', {
              detail: { index: b, value, gamepadIndex: gp.index, id: gp.id }
            }));
          }
        }

        // Check axes
        for (let a = 0; a < gp.axes.length; a++) {
          if (prevAxes[a] !== undefined && Math.abs(gp.axes[a] - prevAxes[a]) > 0.01) {
            this.dispatchEvent(new CustomEvent('gamepadaxischange', {
              detail: { index: a, value: gp.axes[a], gamepadIndex: gp.index, id: gp.id }
            }));
          }
        }
      }

      this._storeState(gp);
    }

    this._frameId = requestAnimationFrame(() => this._poll());
  }

  _storeState(gp) {
    this._previousButtons[gp.index] = {};
    for (let b = 0; b < gp.buttons.length; b++) {
      this._previousButtons[gp.index][b] = gp.buttons[b].pressed;
    }
    this._previousAxes[gp.index] = Array.from(gp.axes);
  }

  stop() {
    this._running = false;
    if (this._frameId !== null) {
      cancelAnimationFrame(this._frameId);
    }
  }
}
