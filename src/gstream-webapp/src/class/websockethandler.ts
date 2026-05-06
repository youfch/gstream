import Offer from "./offer";
import Answer from "./answer";
import Candidate from "./candidate";

// ---------------------------------------------------------------------------
// Module state
// ---------------------------------------------------------------------------

let privateMode = false;

/** WebSocket → set of connectionIds that socket owns. */
const socketConnections = new Map<WebSocket, Set<string>>();

/** connectionId → [firstSocket, secondSocket | null] */
const peerLinks = new Map<string, [WebSocket, WebSocket | null]>();

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function ensureConnSet(ws: WebSocket): Set<string> {
  let set = socketConnections.get(ws);
  if (!set) {
    set = new Set();
    socketConnections.set(ws, set);
  }
  return set;
}

/** Find the "other" socket in a peer link for a given connection. */
function otherPeer(
  ws: WebSocket,
  cid: string
): WebSocket | null | undefined {
  const link = peerLinks.get(cid);
  if (!link) return null;
  return link[0] === ws ? link[1] : link[0];
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/** Reset state; called at startup with mode string. */
function reset(mode: string): void {
  privateMode = mode === "private";
}

/** Register a newly connected WebSocket. */
function add(ws: WebSocket): void {
  socketConnections.set(ws, new Set());
}

/** Tear down a disconnected WebSocket and notify partners. */
function remove(ws: WebSocket): void {
  const conns = socketConnections.get(ws);
  if (conns) {
    for (const cid of conns) {
      const partner = otherPeer(ws, cid);
      if (partner) {
        partner.send(JSON.stringify({ type: "disconnect", connectionId: cid }));
      }
      peerLinks.delete(cid);
    }
  }
  socketConnections.delete(ws);
}

/** Handle "connect" message — join or create a connection. */
function onConnect(ws: WebSocket, connectionId: string): void {
  let polite = true;

  if (privateMode) {
    const link = peerLinks.get(connectionId);
    if (link) {
      if (link[0] != null && link[1] != null) {
        ws.send(
          JSON.stringify({
            type: "error",
            message: `${connectionId}: This connection id is already used.`,
          })
        );
        return;
      }
      if (link[0] != null) {
        peerLinks.set(connectionId, [link[0], ws]);
      }
    } else {
      peerLinks.set(connectionId, [ws, null]);
      polite = false;
    }
  }

  ensureConnSet(ws).add(connectionId);
  ws.send(
    JSON.stringify({ type: "connect", connectionId, polite })
  );
}

/** Handle "disconnect" message — leave a connection. */
function onDisconnect(ws: WebSocket, connectionId: string): void {
  const conns = socketConnections.get(ws);
  if (conns) {
    conns.delete(connectionId);
  }

  const partner = otherPeer(ws, connectionId);
  if (partner) {
    partner.send(
      JSON.stringify({ type: "disconnect", connectionId })
    );
  }
  peerLinks.delete(connectionId);
  ws.send(JSON.stringify({ type: "disconnect", connectionId }));
}

/** Handle "offer" message — relay SDP offer. */
function onOffer(ws: WebSocket, payload: any): void {
  const cid: string = payload.connectionId;
  const offer = new Offer(payload.sdp, Date.now(), false);

  if (privateMode) {
    const partner = otherPeer(ws, cid);
    if (partner) {
      const politeOffer = new Offer(payload.sdp, Date.now(), true);
      partner.send(
        JSON.stringify({ from: cid, to: "", type: "offer", data: politeOffer })
      );
    }
    return;
  }

  peerLinks.set(cid, [ws, null]);
  for (const [sock] of socketConnections) {
    if (sock === ws) continue;
    sock.send(
      JSON.stringify({ from: cid, to: "", type: "offer", data: offer })
    );
  }
}

/** Handle "answer" message — relay SDP answer. */
function onAnswer(ws: WebSocket, payload: any): void {
  const cid: string = payload.connectionId;
  ensureConnSet(ws).add(cid);
  const answer = new Answer(payload.sdp, Date.now());

  const link = peerLinks.get(cid);
  if (!link) return;

  const partner = link[0] === ws ? link[1] : link[0];
  if (!partner) return;

  if (!privateMode) {
    peerLinks.set(cid, [partner, ws]);
  }

  partner.send(
    JSON.stringify({ from: cid, to: "", type: "answer", data: answer })
  );
}

/** Handle "candidate" message — relay ICE candidate. */
function onCandidate(ws: WebSocket, payload: any): void {
  const cid: string = payload.connectionId;
  const ice = new Candidate(
    payload.candidate,
    payload.sdpMLineIndex,
    payload.sdpMid,
    Date.now()
  );

  if (privateMode) {
    const partner = otherPeer(ws, cid);
    if (partner) {
      partner.send(
        JSON.stringify({ from: cid, to: "", type: "candidate", data: ice })
      );
    }
    return;
  }

  for (const [sock] of socketConnections) {
    if (sock === ws) continue;
    sock.send(
      JSON.stringify({ from: cid, to: "", type: "candidate", data: ice })
    );
  }
}

export {
  reset,
  add,
  remove,
  onConnect,
  onDisconnect,
  onOffer,
  onAnswer,
  onCandidate,
};
