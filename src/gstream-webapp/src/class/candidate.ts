/** ICE candidate exchanged during WebRTC negotiation. */
export default class Candidate {
  readonly candidate: string;
  readonly sdpMLineIndex: number;
  readonly sdpMid: string;
  readonly datetime: number;

  constructor(candidate: string, sdpMLineIndex: number, sdpMid: string, datetime: number) {
    this.candidate = candidate;
    this.sdpMLineIndex = sdpMLineIndex;
    this.sdpMid = sdpMid;
    this.datetime = datetime;
  }
}
