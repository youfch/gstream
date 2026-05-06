/** Logging severity levels. */
export enum LogLevel {
  log = "log",
  warn = "warn",
  error = "error",
}

const TAG = "[gStream]";

/** Emit a message at the given severity. */
export function log(level: LogLevel, ...args: unknown[]): void {
  console[level](TAG, ...args);
}

/** No-op kept for API compatibility. */
export function reset(): void {
  // intentionally empty
}
