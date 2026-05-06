/**
 * gStream VideoPlayer (videoplayer mode)
 * Manages dual video tracks with data channel for engine control messages.
 * Uses its own signaling and peer connection, independent of GStreamSession.
 */

import { Signaling, WebSocketSignaling } from "../../module/signaling.js";
import Peer from "../../module/peer.js";
import * as Logger from "../../module/logger.js";

// Engine control message types received via data channel
const EngineEventType = {
  SWITCH_VIDEO: 0
};

/** Generate a UUID v4-like identifier */
function generateId() {
  const blob = new Blob();
  const url = URL.createObjectURL(blob);
  const id = url.toString().split(/[:/]/g).pop().toLowerCase();
  URL.revokeObjectURL(url);
  return id;
}

export class VideoPlayer {
  /**
   * @param {HTMLVideoElement[]} elements - Main video and thumbnail video elements
   */
  constructor(elements) {
    this.pc = null;
    this.channel = null;
    this.connectionId = null;

    // Primary video track
    this.localStream = new MediaStream();
    this.video = elements[0];
    this.video.playsInline = true;
    this.video.addEventListener('loadedmetadata', () => {
      this.video.play();
      this.resizeVideo();
    }, true);

    // Secondary video track (thumbnail)
    this.localStream2 = new MediaStream();
    this.videoThumb = elements[1];
    this.videoThumb.playsInline = true;
    this.videoThumb.addEventListener('loadedmetadata', () => {
      this.videoThumb.play();
    }, true);

    this.videoTrackList = [];
    this.videoTrackIndex = 0;
    this.maxVideoTrackLength = 2;

    this.ondisconnect = function () { };
  }

  /**
   * Establish signaling and peer connection.
   * @param {boolean} useWebSocket - Use WebSocket signaling if true, HTTP otherwise
   */
  async setupConnection(useWebSocket) {
    // Close any existing connection
    if (this.pc) {
      Logger.log('Closing existing peer connection');
      this.pc.close();
      this.pc = null;
    }

    this.signaling = useWebSocket ? new WebSocketSignaling() : new Signaling();
    this.connectionId = generateId();

    // Signaling event handlers — set up BEFORE start() so we don't miss events
    this.signaling.addEventListener('connect', (e) => {
      const detail = e.detail;
      Logger.log('Signaling connect:', detail.connectionId, 'polite:', detail.polite);
      if (this.connectionId === detail.connectionId) {
        // Create peer with the polite flag from the signaling server
        this._initPeer(detail.polite);
      }
    });

    this.signaling.addEventListener('disconnect', (e) => {
      const detail = e.detail;
      if (detail.connectionId && detail.connectionId !== this.connectionId) {
        return;
      }
      if (this.pc) {
        this.ondisconnect();
      }
    });

    this.signaling.addEventListener('offer', async (e) => {
      const offer = e.detail;
      if (offer.connectionId !== this.connectionId) {
        return;
      }
      // If we haven't set up a peer yet, create one now
      if (!this.pc) {
        this._initPeer(offer.polite !== undefined ? offer.polite : true);
      }
      const desc = new RTCSessionDescription({ sdp: offer.sdp, type: 'offer' });
      if (this.pc) {
        await this.pc.onGotDescription(offer.connectionId, desc);
      }
    });

    this.signaling.addEventListener('answer', async (e) => {
      const answer = e.detail;
      if (answer.connectionId !== this.connectionId) {
        return;
      }
      const desc = new RTCSessionDescription({ sdp: answer.sdp, type: 'answer' });
      if (this.pc) {
        await this.pc.onGotDescription(answer.connectionId, desc);
      }
    });

    this.signaling.addEventListener('candidate', async (e) => {
      const c = e.detail;
      if (c.connectionId !== this.connectionId) {
        return;
      }
      const ice = new RTCIceCandidate({
        candidate: c.candidate,
        sdpMid: c.sdpMid,
        sdpMLineIndex: c.sdpMLineIndex
      });
      if (this.pc) {
        await this.pc.onGotCandidate(c.connectionId, ice);
      }
    });

    await this.signaling.start();

    // Request a connection from the signaling server
    this.signaling.createConnection(this.connectionId);
  }

  /** Set up the Peer connection wrapper with event handlers. */
  _initPeer(polite) {
    if (this.pc) {
      Logger.log('Closing existing peer connection');
      this.pc.close();
    }

    this.pc = new Peer(this.connectionId, polite);

    this.pc.addEventListener('disconnect', () => {
      this.ondisconnect();
    });

    this.pc.addEventListener('trackevent', (e) => {
      const data = e.detail;
      if (data.track.kind === 'video') {
        this.videoTrackList.push(data.track);
      }
      if (data.track.kind === 'audio') {
        this.localStream.addTrack(data.track);
      }
      if (this.videoTrackList.length === this.maxVideoTrackLength) {
        this.switchVideo(this.videoTrackIndex);
      }
    });

    this.pc.addEventListener('sendoffer', (e) => {
      this.signaling.sendOffer(e.detail.connectionId, e.detail.sdp);
    });

    this.pc.addEventListener('sendanswer', (e) => {
      this.signaling.sendAnswer(e.detail.connectionId, e.detail.sdp);
    });

    this.pc.addEventListener('sendcandidate', (e) => {
      const d = e.detail;
      this.signaling.sendCandidate(d.connectionId, d.candidate, d.sdpMid, d.sdpMLineIndex);
    });

    // Create data channel for engine communication
    this.channel = this.pc.createDataChannel(this.connectionId, 'data');
    this.channel.onopen = () => Logger.log('Data channel opened');
    this.channel.onerror = (e) => Logger.error('Data channel error:', e.error.message);
    this.channel.onclose = () => Logger.log('Data channel closed');
    this.channel.onmessage = async (msg) => {
      let data;
      // Firefox sends Blob, Chrome sends ArrayBuffer
      if (navigator.userAgent.includes('Firefox')) {
        data = await msg.data.arrayBuffer();
      } else {
        data = msg.data;
      }
      const bytes = new Uint8Array(data);
      this.videoTrackIndex = bytes[1];
      if (bytes[0] === EngineEventType.SWITCH_VIDEO) {
        this.switchVideo(this.videoTrackIndex);
      }
    };
  }

  /** Recalculate video letterbox coordinates. */
  resizeVideo() {
    const rect = this.video.getBoundingClientRect();
    const videoRatio = this.videoWidth / this.videoHeight;
    const clientRatio = rect.width / rect.height;

    this._videoScale = videoRatio > clientRatio
      ? rect.width / this.videoWidth
      : rect.height / this.videoHeight;

    const offsetX = videoRatio > clientRatio ? 0 : (rect.width - this.videoWidth * this._videoScale) * 0.5;
    const offsetY = videoRatio > clientRatio ? (rect.height - this.videoHeight * this._videoScale) * 0.5 : 0;
    this._videoOriginX = rect.left + offsetX;
    this._videoOriginY = rect.top + offsetY;
  }

  /**
   * Swap main and thumbnail video tracks.
   * @param {number} index - Which track to show on the main video (0 or 1)
   */
  switchVideo(index) {
    this.video.srcObject = this.localStream;
    this.videoThumb.srcObject = this.localStream2;

    if (index === 0) {
      this._replaceTrack(this.localStream, this.videoTrackList[0]);
      this._replaceTrack(this.localStream2, this.videoTrackList[1]);
    } else {
      this._replaceTrack(this.localStream, this.videoTrackList[1]);
      this._replaceTrack(this.localStream2, this.videoTrackList[0]);
    }
  }

  _replaceTrack(stream, newTrack) {
    const existing = stream.getVideoTracks();
    for (const t of existing) {
      stream.removeTrack(t);
    }
    stream.addTrack(newTrack);
  }

  get videoWidth() { return this.video.videoWidth; }
  get videoHeight() { return this.video.videoHeight; }
  get videoOriginX() { return this._videoOriginX; }
  get videoOriginY() { return this._videoOriginY; }
  get videoScale() { return this._videoScale; }

  /**
   * Send a binary message over the data channel.
   * @param {ArrayBuffer} msg
   */
  sendMsg(msg) {
    if (!this.channel) return;
    switch (this.channel.readyState) {
      case 'open':
        this.channel.send(msg);
        break;
      case 'connecting':
        Logger.log('Data channel not yet open');
        break;
      default:
        Logger.log('Cannot send on closed/closing channel');
        break;
    }
  }

  /** Tear down the connection. */
  async stop() {
    if (this.signaling) {
      await this.signaling.stop();
      this.signaling = null;
    }
    if (this.pc) {
      this.pc.close();
      this.pc = null;
    }
  }
}
