/** SDP answer exchanged during WebRTC negotiation. */
export default class Answer {
  readonly sdp: string;
  readonly datetime: number;

  constructor(sdp: string, datetime: number) {
    this.sdp = sdp;
    this.datetime = datetime;
  }
}
