/**
 * gStream Pointer Coordinate Corrector
 * Maps viewport/element coordinates to video frame coordinates,
 * accounting for letterboxing (aspect ratio mismatch between
 * the display element and the actual video dimensions).
 */

export const LetterBoxType = {
  Vertical: 0,
  Horizontal: 1
};

export class PointerCorrector {
  /**
   * @param {number} frameWidth - Source video frame width in pixels
   * @param {number} frameHeight - Source video frame height in pixels
   * @param {HTMLElement} element - The DOM element displaying the video
   */
  constructor(frameWidth, frameHeight, element) {
    this._frameWidth = frameWidth;
    this._frameHeight = frameHeight;
    this._element = element;
    this._letterBox = LetterBoxType.Vertical;

    this._calculate();
  }

  /**
   * Recalculate the mapping. Call when the element or video resizes.
   * @param {number} frameWidth
   * @param {number} frameHeight
   * @param {HTMLElement} element
   */
  reset(frameWidth, frameHeight, element) {
    this._frameWidth = frameWidth;
    this._frameHeight = frameHeight;
    this._element = element;
    this._calculate();
  }

  /**
   * Convert a viewport (clientX/Y) coordinate to video-frame coordinates.
   * @param {number} clientX - Viewport X coordinate
   * @param {number} clientY - Viewport Y coordinate
   * @returns {{ x: number, y: number }} Position in video frame pixel space
   */
  map(clientX, clientY) {
    const rect = this._element.getBoundingClientRect();
    const elementX = clientX - rect.left;
    const elementY = clientY - rect.top;

    // Map from element space to frame space
    const x = (elementX - this._offsetX) / this._scale;
    const y = (elementY - this._offsetY) / this._scale;

    return { x, y };
  }

  /** Get the current letterbox type. */
  get letterBox() {
    return this._letterBox;
  }

  /** Get the current scale factor. */
  get scale() {
    return this._scale;
  }

  _calculate() {
    const rect = this._element.getBoundingClientRect();
    const elementAspect = rect.width / rect.height;
    const frameAspect = this._frameWidth / this._frameHeight;

    if (frameAspect > elementAspect) {
      // Video is wider than element → horizontal letterbox (bars top/bottom)
      this._letterBox = LetterBoxType.Horizontal;
      this._scale = rect.width / this._frameWidth;
      this._offsetX = 0;
      this._offsetY = (rect.height - this._frameHeight * this._scale) * 0.5;
    } else {
      // Video is taller than element → vertical letterbox (bars left/right)
      this._letterBox = LetterBoxType.Vertical;
      this._scale = rect.height / this._frameHeight;
      this._offsetX = (rect.width - this._frameWidth * this._scale) * 0.5;
      this._offsetY = 0;
    }
  }
}
