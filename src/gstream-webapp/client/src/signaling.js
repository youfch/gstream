/**
 * gStream Signaling Transport
 * Provides two signaling implementations (HTTP polling and WebSocket)
 * that share the same event interface for WebRTC session negotiation.
 *
 * Wire protocol:
 *   HTTP:  PUT/GET/DELETE /signaling[/:method] with Session-Id header
 *   WS:    JSON messages { type, connectionId, from, data }
 */

import * as Logger from "./logger.js";

/**
 * HTTP-based signaling using REST polling.
 * Creates a session via PUT, then polls for incoming messages via GET.
 */
export class Signaling extends EventTarget {
  constructor() {
    super();
    this._sessionId = null;
    this._pollTimer = null;
    this._lastTimestamp = 0;
    this._baseURL = `${location.protocol}//${location.host}/signaling`;
  }

  async start() {
    // Create signaling session
    const response = await fetch(this._baseURL, { method: 'PUT', headers: this._headers() });
    const data = await response.json();
    this._sessionId = data.sessionId;

    // Begin polling loop
    this._beginPolling();
    Logger.log('HTTP signaling session started:', this._sessionId);
  }

  stop() {
    if (this._pollTimer !== null) {
      clearInterval(this._pollTimer);
      this._pollTimer = null;
    }
    if (this._sessionId) {
      fetch(this._baseURL, { method: 'DELETE', headers: this._headers() }).catch(() => {});
    }
    this._sessionId = null;
  }

  async createConnection(connectionId) {
    const res = await this._request('PUT', '/connection', { connectionId });
    const data = await res.json();
    // Server returns { connectionId, polite, type: "connect", datetime }
    this.dispatchEvent(new CustomEvent('connect', {
      detail: {
        connectionId: data.connectionId,
        polite: data.polite
      }
    }));
    Logger.log('HTTP createConnection:', data.connectionId, 'polite:', data.polite);
  }

  async deleteConnection(connectionId) {
    const res = await this._request('DELETE', '/connection', { connectionId });
    const json = await res.json();
    this.dispatchEvent(new CustomEvent('disconnect', {
      detail: { connectionId: json.connectionId || connectionId }
    }));
  }

  async sendOffer(connectionId, sdp) {
    await this._request('POST', '/offer', { connectionId, sdp });
  }

  async sendAnswer(connectionId, sdp) {
    await this._request('POST', '/answer', { connectionId, sdp });
  }

  async sendCandidate(connectionId, candidate, sdpMid, sdpMLineIndex) {
    await this._request('POST', '/candidate', { connectionId, candidate, sdpMid, sdpMLineIndex });
  }

  // --- Private helpers ---

  _headers() {
    const h = { 'Content-Type': 'application/json' };
    if (this._sessionId) {
      h['Session-Id'] = this._sessionId;
    }
    return h;
  }

  async _request(method, path, body) {
    const response = await fetch(this._baseURL + path, {
      method,
      headers: this._headers(),
      body: JSON.stringify(body)
    });
    return response;
  }

  _beginPolling() {
    this._pollTimer = setInterval(async () => {
      try {
        const url = `${this._baseURL}?fromtime=${this._lastTimestamp}`;
        const response = await fetch(url, { headers: this._headers() });
        const data = await response.json();

        // Server returns { messages: [...], datetime: timestamp }
        const messages = data.messages || [];
        if (data.datetime) {
          this._lastTimestamp = data.datetime;
        }

        for (const msg of messages) {
          this._handleMessage(msg);
        }
      } catch (err) {
        Logger.warn('Signaling poll error:', err);
      }
    }, 1000);
  }

  _handleMessage(msg) {
    switch (msg.type) {
      case 'connect':
        // Do NOT dispatch 'connect' from polling — it's already dispatched
        // in createConnection() when the server responds to PUT /connection.
        // The original URS Signaling also skips 'connect' in loopGetAll.
        break;
      case 'disconnect':
        this.dispatchEvent(new CustomEvent('disconnect', {
          detail: {
            connectionId: msg.connectionId
          }
        }));
        break;
      case 'offer':
        this.dispatchEvent(new CustomEvent('offer', {
          detail: {
            connectionId: msg.connectionId,
            sdp: msg.sdp,
            polite: msg.polite
          }
        }));
        break;
      case 'answer':
        this.dispatchEvent(new CustomEvent('answer', {
          detail: {
            connectionId: msg.connectionId,
            sdp: msg.sdp
          }
        }));
        break;
      case 'candidate':
        this.dispatchEvent(new CustomEvent('candidate', {
          detail: {
            connectionId: msg.connectionId,
            candidate: msg.candidate,
            sdpMid: msg.sdpMid,
            sdpMLineIndex: msg.sdpMLineIndex
          }
        }));
        break;
    }
  }
}

/**
 * WebSocket-based signaling.
 * Connects to the signaling server and exchanges JSON messages in real time.
 */
export class WebSocketSignaling extends EventTarget {
  constructor() {
    super();
    this._ws = null;
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    this._wsURL = `${protocol}//${location.host}`;
  }

  async start() {
    return new Promise((resolve, reject) => {
      this._ws = new WebSocket(this._wsURL);

      this._ws.onopen = () => {
        Logger.log('WebSocket signaling connected');
        resolve();
      };

      this._ws.onerror = (ev) => {
        Logger.error('WebSocket signaling error:', ev);
        reject(ev);
      };

      this._ws.onmessage = (ev) => {
        const msg = JSON.parse(ev.data);
        if (!msg || !msg.type) return;
        this._handleMessage(msg);
      };

      this._ws.onclose = () => {
        Logger.log('WebSocket signaling closed');
      };
    });
  }

  stop() {
    if (this._ws) {
      this._ws.close();
      this._ws = null;
    }
  }

  createConnection(connectionId) {
    this._send({ type: 'connect', connectionId });
  }

  deleteConnection(connectionId) {
    this._send({ type: 'disconnect', connectionId });
  }

  sendOffer(connectionId, sdp) {
    this._send({
      type: 'offer',
      from: connectionId,
      data: { sdp, connectionId }
    });
  }

  sendAnswer(connectionId, sdp) {
    this._send({
      type: 'answer',
      from: connectionId,
      data: { sdp, connectionId }
    });
  }

  sendCandidate(connectionId, candidate, sdpMid, sdpMLineIndex) {
    this._send({
      type: 'candidate',
      from: connectionId,
      data: { candidate, sdpMid, sdpMLineIndex, connectionId }
    });
  }

  // --- Private helpers ---

  _send(obj) {
    if (this._ws && this._ws.readyState === WebSocket.OPEN) {
      this._ws.send(JSON.stringify(obj));
    }
  }

  _handleMessage(msg) {
    switch (msg.type) {
      case 'connect':
        this.dispatchEvent(new CustomEvent('connect', {
          detail: {
            connectionId: msg.connectionId,
            polite: msg.polite
          }
        }));
        break;
      case 'disconnect':
        this.dispatchEvent(new CustomEvent('disconnect', {
          detail: { connectionId: msg.connectionId }
        }));
        break;
      case 'offer':
        this.dispatchEvent(new CustomEvent('offer', {
          detail: {
            connectionId: msg.from,
            sdp: msg.data.sdp,
            polite: msg.data.polite
          }
        }));
        break;
      case 'answer':
        this.dispatchEvent(new CustomEvent('answer', {
          detail: {
            connectionId: msg.from,
            sdp: msg.data.sdp
          }
        }));
        break;
      case 'candidate':
        this.dispatchEvent(new CustomEvent('candidate', {
          detail: {
            connectionId: msg.from,
            candidate: msg.data.candidate,
            sdpMLineIndex: msg.data.sdpMLineIndex,
            sdpMid: msg.data.sdpMid
          }
        }));
        break;
    }
  }
}
