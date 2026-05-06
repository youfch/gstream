/**
 * gStream WebRTC Peer
 * Wraps RTCPeerConnection with the Perfect Negotiation pattern
 * (polite/impolite peer logic) for robust offer/answer handling.
 */

import * as Logger from "./logger.js";

export default class Peer extends EventTarget {
  /**
   * @param {string} connectionId - Unique identifier for this peer connection
   * @param {boolean} polite - If true, this peer rolls back on offer glare
   * @param {RTCConfiguration} [config] - WebRTC configuration (ICE servers, etc.)
   */
  constructor(connectionId, polite, config) {
    super();
    this.connectionId = connectionId;
    this.polite = polite;
    this._pc = new RTCPeerConnection(config || {});
    this._makingOffer = false;
    this._ignoreOffer = false;
    this._srdAnswerPending = false;
    this._offerRetryTimer = null;
    this._pendingOffer = null;

    this._setupPeerConnectionHandlers();
  }

  /** Access the underlying RTCPeerConnection */
  get pc() {
    return this._pc;
  }

  /**
   * Create a data channel on this peer connection.
   * @param {string} label - Channel label
   * @param {string} [id] - Optional channel ID
   * @returns {RTCDataChannel}
   */
  createDataChannel(label, id) {
    if (id !== undefined) {
      return this._pc.createDataChannel(label, { id: parseInt(id) });
    }
    return this._pc.createDataChannel(label);
  }

  /**
   * Handle a received SDP description (offer or answer).
   * Implements the polite/impolite peer logic of Perfect Negotiation.
   * @param {string} fromConnectionId
   * @param {RTCSessionDescription} description
   */
  async onGotDescription(fromConnectionId, description) {
    if (description.type === 'offer') {
      // Offer glare: impolite peer ignores offers while we're negotiating
      this._ignoreOffer =
        !this.polite && (this._makingOffer || this._pc.signalingState !== 'stable');

      if (this._ignoreOffer) {
        Logger.log('Ignoring offer due to glare (impolite peer)');
        return;
      }

      this._srdAnswerPending = true;
      await this._pc.setRemoteDescription(description);
      this._srdAnswerPending = false;

      const answer = await this._pc.createAnswer();
      await this._pc.setLocalDescription(answer);

      this.dispatchEvent(new CustomEvent('sendanswer', {
        detail: {
          connectionId: this.connectionId,
          sdp: this._pc.localDescription.sdp
        }
      }));
    } else if (description.type === 'answer') {
      if (this._pc.signalingState !== 'stable') {
        await this._pc.setRemoteDescription(description);
        this._stopResendingOffer();
      }
    }
  }

  /**
   * Add a remote ICE candidate.
   * @param {string} fromConnectionId
   * @param {RTCIceCandidate} candidate
   */
  async onGotCandidate(fromConnectionId, candidate) {
    try {
      await this._pc.addIceCandidate(candidate);
    } catch (err) {
      if (!this._ignoreOffer) {
        Logger.warn('Failed to add ICE candidate:', err);
      }
    }
  }

  /** Close the peer connection and clean up resources. */
  close() {
    this._stopResendingOffer();
    if (this._pc) {
      this._pc.close();
    }
  }

  // --- Private methods ---

  _setupPeerConnectionHandlers() {
    this._pc.onnegotiationneeded = async () => {
      try {
        this._makingOffer = true;
        const offer = await this._pc.createOffer();
        await this._pc.setLocalDescription(offer);

        this._pendingOffer = this._pc.localDescription.sdp;
        this.dispatchEvent(new CustomEvent('sendoffer', {
          detail: {
            connectionId: this.connectionId,
            sdp: this._pendingOffer
          }
        }));

        // Start resend loop
        this._startResendingOffer();
      } catch (err) {
        Logger.error('negotiationneeded error:', err);
      } finally {
        this._makingOffer = false;
      }
    };

    this._pc.onicecandidate = (ev) => {
      if (ev.candidate) {
        this.dispatchEvent(new CustomEvent('sendcandidate', {
          detail: {
            connectionId: this.connectionId,
            candidate: ev.candidate.candidate,
            sdpMid: ev.candidate.sdpMid,
            sdpMLineIndex: ev.candidate.sdpMLineIndex
          }
        }));
      }
    };

    this._pc.ontrack = (ev) => {
      this.dispatchEvent(new CustomEvent('trackevent', {
        detail: {
          track: ev.track,
          streams: ev.streams
        }
      }));
    };

    this._pc.onconnectionstatechange = () => {
      const state = this._pc.connectionState;
      Logger.log(`Peer ${this.connectionId} connection state: ${state}`);
      if (state === 'disconnected' || state === 'failed') {
        this.dispatchEvent(new CustomEvent('disconnect', {
          detail: { connectionId: this.connectionId }
        }));
      }
    };

    this._pc.ondatachannel = (ev) => {
      this.dispatchEvent(new CustomEvent('datachannel', {
        detail: { channel: ev.channel }
      }));
    };
  }

  _startResendingOffer() {
    this._stopResendingOffer();
    this._offerRetryTimer = setInterval(() => {
      if (this._pendingOffer && this._pc.signalingState === 'stable') {
        return; // Got answer, stop resending
      }
      if (this._pendingOffer) {
        this.dispatchEvent(new CustomEvent('sendoffer', {
          detail: {
            connectionId: this.connectionId,
            sdp: this._pendingOffer
          }
        }));
      }
    }, 5000);
  }

  _stopResendingOffer() {
    if (this._offerRetryTimer) {
      clearInterval(this._offerRetryTimer);
      this._offerRetryTimer = null;
    }
    this._pendingOffer = null;
  }
}
