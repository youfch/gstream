/**
 * gStream SendVideo Helper
 * Captures local camera/microphone streams and provides track
 * management for the bidirectional streaming mode.
 */

import * as Logger from "../../module/logger.js";

export class SendVideo {
  /**
   * @param {HTMLVideoElement} localVideoElement - Element to display local camera
   * @param {HTMLVideoElement} remoteVideoElement - Element to display remote stream
   */
  constructor(localVideoElement, remoteVideoElement) {
    this.localVideo = localVideoElement;
    this.remoteVideo = remoteVideoElement;
  }

  /**
   * Start capturing local video and audio.
   * @param {string} videoSource - Video device ID
   * @param {string} audioSource - Audio device ID
   * @param {number} videoWidth - Desired video width
   * @param {number} videoHeight - Desired video height
   */
  async startLocalVideo(videoSource, audioSource, videoWidth, videoHeight) {
    try {
      const constraints = {
        video: { deviceId: videoSource ? { exact: videoSource } : undefined },
        audio: { deviceId: audioSource ? { exact: audioSource } : undefined }
      };

      if (videoWidth != null && videoWidth !== 0) {
        constraints.video.width = videoWidth;
      }
      if (videoHeight != null && videoHeight !== 0) {
        constraints.video.height = videoHeight;
      }

      const localStream = await navigator.mediaDevices.getUserMedia(constraints);
      this.localVideo.srcObject = localStream;
      await this.localVideo.play();
    } catch (err) {
      Logger.error(`getUserMedia error: ${err}`);
    }
  }

  /**
   * Get all tracks from the local video stream.
   * @returns {MediaStreamTrack[]}
   */
  getLocalTracks() {
    return this.localVideo.srcObject.getTracks();
  }

  /**
   * Add a remote track to the remote video element.
   * @param {MediaStreamTrack} track
   */
  addRemoteTrack(track) {
    if (this.remoteVideo.srcObject == null) {
      this.remoteVideo.srcObject = new MediaStream();
    }
    this.remoteVideo.srcObject.addTrack(track);
  }
}
