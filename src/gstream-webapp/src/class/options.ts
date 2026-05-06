/** Server startup and runtime configuration. */
export default interface Options {
  /** Enable HTTPS when true */
  secure?: boolean;
  /** TCP port (default 80) */
  port?: number;
  /** TLS private key path */
  keyfile?: string;
  /** TLS certificate path */
  certfile?: string;
  /** Transport type: "http" or "websocket" */
  type?: string;
  /** Session routing mode: "public" or "private" */
  mode?: string;
  /** HTTP access-log style: "combined", "dev", "short", "tiny", or "none" */
  logging?: string;
}
