/**
 * gStream WebRTC Session Orchestrator
 * Manages the lifecycle of WebRTC connections for streaming video and input.
 * Coordinates signaling, peer connections, and data channels.
 *
 * Flow:
 *  1. start() — open signaling transport
 *  2. createConnection(id?) — request a connection from the signaling server
 *  3. Signaling responds with { connectionId, polite } → Peer is created
 *  4. onConnect fires → page code creates data channels
 *  5. SDP negotiation proceeds via offer/answer/candidate events
 */

import Peer from "./peer.js";
import * as Logger from "./logger.js";

export class GStreamSession {
  /**
   * @param {Signaling|WebSocketSignaling} signaling - The signaling transport
   * @param {RTCConfiguration} config - WebRTC peer connection configuration
   */
  constructor(signaling, config) {
    this._signaling = signaling;
    this._config = config;
    this._peer = null;
    this._connectionId = null;

    // Public callbacks — set by page code
    this.onConnect = function () { };
    this.onDisconnect = function () { };
    this.onTrackEvent = function (data) { };
    this.onGotOffer = function () { };
  }

  /** Start the signaling transport and register event handlers. */
  async start() {
    // When the signaling server confirms our connection, it tells us
    // whether we are the polite peer. We create the Peer here with
    // the correct polite flag.
    this._signaling.addEventListener('connect', (e) => {
      const detail = e.detail;
      Logger.log('Signaling connect:', detail.connectionId, 'polite:', detail.polite);

      if (this._connectionId === detail.connectionId) {
        this._preparePeerConnection(detail.connectionId, detail.polite);
        this.onConnect(detail.connectionId);
      }
    });

    this._signaling.addEventListener('disconnect', async (e) => {
      const detail = e.detail;
      if (this._connectionId === detail.connectionId) {
        if (this._peer) {
          this._peer.close();
          this._peer = null;
        }
        this.onDisconnect(detail.connectionId);
      }
    });

    this._signaling.addEventListener('offer', async (e) => {
      const offer = e.detail;
      Logger.log('Received offer from:', offer.connectionId);

      // In public mode, the server broadcasts offers from other clients.
      // We must only process offers that match our own connectionId.
      // Offers from other browser clients should be ignored.
      if (offer.connectionId !== this._connectionId) {
        Logger.log('Ignoring offer for different connectionId:', offer.connectionId);
        return;
      }

      // If we haven't set up a peer yet (e.g. remote offer arrived first),
      // create one now. Default to polite since we're the answerer.
      if (!this._peer) {
        this._preparePeerConnection(offer.connectionId, offer.polite !== undefined ? offer.polite : true);
      }

      this.onGotOffer();

      const desc = new RTCSessionDescription({ sdp: offer.sdp, type: 'offer' });
      try {
        await this._peer.onGotDescription(offer.connectionId, desc);
      } catch (err) {
        Logger.warn('Error handling offer:', err);
      }
    });

    this._signaling.addEventListener('answer', async (e) => {
      const answer = e.detail;
      if (answer.connectionId !== this._connectionId) {
        return;
      }
      Logger.log('Received answer from:', answer.connectionId);
      if (this._peer) {
        const desc = new RTCSessionDescription({ sdp: answer.sdp, type: 'answer' });
        try {
          await this._peer.onGotDescription(answer.connectionId, desc);
        } catch (err) {
          Logger.warn('Error handling answer:', err);
        }
      }
    });

    this._signaling.addEventListener('candidate', async (e) => {
      const candidate = e.detail;
      if (candidate.connectionId !== this._connectionId) {
        return;
      }
      if (this._peer) {
        const iceCandidate = new RTCIceCandidate({
          candidate: candidate.candidate,
          sdpMid: candidate.sdpMid,
          sdpMLineIndex: candidate.sdpMLineIndex
        });
        await this._peer.onGotCandidate(candidate.connectionId, iceCandidate);
      }
    });

    await this._signaling.start();
  }

  /**
   * Create a new WebRTC connection.
   * @param {string} [connectionId] - Optional ID; auto-generated if omitted
   */
  async createConnection(connectionId) {
    this._connectionId = connectionId || this._generateId();
    this._signaling.createConnection(this._connectionId);
  }

  /**
   * Delete the current connection.
   */
  async deleteConnection() {
    if (this._connectionId) {
      this._signaling.deleteConnection(this._connectionId);
    }
    if (this._peer) {
      this._peer.close();
      this._peer = null;
    }
    this._connectionId = null;
  }

  /** Stop everything. */
  async stop() {
    if (this._peer) {
      this._peer.close();
      this._peer = null;
    }
    if (this._signaling) {
      this._signaling.stop();
    }
    this._connectionId = null;
  }

  /**
   * Create a data channel on the current peer connection.
   * @param {string} label - Channel label
   * @returns {RTCDataChannel}
   */
  createDataChannel(label) {
    if (!this._peer) {
      Logger.warn('Cannot create data channel: no peer');
      return null;
    }
    return this._peer.createDataChannel(label);
  }

  /**
   * Add a media transceiver to the current peer connection.
   * @param {MediaStreamTrack} track
   * @param {RTCRtpTransceiverInit} init
   * @returns {RTCRtpTransceiver}
   */
  addTransceiver(track, init) {
    if (!this._peer) return null;
    return this._peer.pc.addTransceiver(track, init);
  }

  /**
   * Get all transceivers from the current peer connection.
   * @returns {RTCRtpTransceiver[]}
   */
  getTransceivers() {
    return this._peer ? this._peer.pc.getTransceivers() : [];
  }

  /**
   * Get connection stats.
   * @returns {Promise<RTCStatsReport|null>}
   */
  async getStats() {
    if (!this._peer || !this._peer.pc) {
      return null;
    }
    return this._peer.pc.getStats();
  }

  // ---- Private helpers ----

  /**
   * Set up the RTCPeerConnection wrapper with the correct polite flag
   * and wire all signaling events through it.
   */
  _preparePeerConnection(connectionId, polite) {
    // Close any existing peer
    if (this._peer) {
      Logger.log('Closing existing peer before creating new one');
      this._peer.close();
      this._peer = null;
    }

    this._peer = new Peer(connectionId, polite, this._config);

    this._peer.addEventListener('sendoffer', (e) => {
      const detail = e.detail;
      this._signaling.sendOffer(detail.connectionId, detail.sdp);
    });

    this._peer.addEventListener('sendanswer', (e) => {
      const detail = e.detail;
      this._signaling.sendAnswer(detail.connectionId, detail.sdp);
    });

    this._peer.addEventListener('sendcandidate', (e) => {
      const detail = e.detail;
      this._signaling.sendCandidate(
        detail.connectionId,
        detail.candidate,
        detail.sdpMid,
        detail.sdpMLineIndex
      );
    });

    this._peer.addEventListener('disconnect', () => {
      Logger.log('Peer ICE disconnected');
      this.onDisconnect(connectionId);
    });

    this._peer.addEventListener('trackevent', (e) => {
      this.onTrackEvent(e.detail);
    });

    this._peer.addEventListener('datachannel', (e) => {
      Logger.log('Data channel received');
    });
  }

  _generateId() {
    const tempUrl = URL.createObjectURL(new Blob());
    const uuid = tempUrl.toString();
    URL.revokeObjectURL(tempUrl);
    return uuid.split(/[:/]/g).pop().toLowerCase();
  }
}
