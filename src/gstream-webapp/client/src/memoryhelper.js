/**
 * gStream Memory Helper
 * Bit-level utilities for encoding input state into binary buffers.
 */

export class MemoryHelper {
  /**
   * Set or clear a single bit in a 32-bit integer.
   * @param {number} current - The current integer value
   * @param {number} bitPosition - Zero-indexed bit position (0 = LSB)
   * @param {boolean} isSet - Whether the bit should be 1
   * @returns {number} The modified integer
   */
  static writeSingleBit(current, bitPosition, isSet) {
    if (isSet) {
      return current | (1 << bitPosition);
    }
    return current & ~(1 << bitPosition);
  }
}

/** Size of a 32-bit unsigned integer, in bytes. */
export const sizeOfInt = 4;
