/** SDP offer exchanged during WebRTC negotiation. */
export default class Offer {
  readonly sdp: string;
  readonly datetime: number;
  readonly polite: boolean;

  constructor(sdp: string, datetime: number, polite: boolean) {
    this.sdp = sdp;
    this.datetime = datetime;
    this.polite = polite;
  }
}
