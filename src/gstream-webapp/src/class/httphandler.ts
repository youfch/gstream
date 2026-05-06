import { Request, Response } from "express";
import { v4 as uuidv4 } from "uuid";
import Offer from "./offer";
import Answer from "./answer";
import Candidate from "./candidate";

// ---------------------------------------------------------------------------
// Internal data structures
// ---------------------------------------------------------------------------

/** Represents a peer that disconnected, queued for delivery to its partner. */
class PeerDropEvent {
  constructor(public readonly peerId: string, public readonly when: number) {}
}

/** Per-session bucket: one session = one signalling participant. */
interface SessionBucket {
  /** Active connections owned by this session. */
  activeConnections: Set<string>;
  /** Offers received by this session (keyed by connectionId). */
  incomingOffers: Map<string, Offer>;
  /** Answers received by this session (keyed by connectionId). */
  incomingAnswers: Map<string, Answer>;
  /** ICE candidates received by this session (keyed by connectionId). */
  incomingCandidates: Map<string, Candidate[]>;
  /** Disconnection events pending delivery to this session. */
  dropEvents: PeerDropEvent[];
  /** Epoch-ms of the last HTTP poll from this session. */
  lastPollAt: number;
}

// ---------------------------------------------------------------------------
// Module-level state
// ---------------------------------------------------------------------------

const SESSION_TTL_MS = 10_000;

let privateMode = false;

/** sessionId → bucket */
const sessionStore = new Map<string, SessionBucket>();

/** connectionId → [firstSessionId, secondSessionId | null] */
const pairingTable = new Map<string, [string, string | null]>();

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function nowMs(): number {
  return Date.now();
}

function ensureBucket(sid: string): SessionBucket {
  let b = sessionStore.get(sid);
  if (!b) {
    b = {
      activeConnections: new Set(),
      incomingOffers: new Map(),
      incomingAnswers: new Map(),
      incomingCandidates: new Map(),
      dropEvents: [],
      lastPollAt: nowMs(),
    };
    sessionStore.set(sid, b);
  }
  return b;
}

/** Expire sessions that have not polled within TTL. */
function sweepStaleSessions(): void {
  const cutoff = nowMs() - SESSION_TTL_MS;
  for (const sid of Array.from(sessionStore.keys())) {
    const bucket = sessionStore.get(sid)!;
    if (bucket.lastPollAt < cutoff) {
      teardownSession(sid);
    }
  }
}

/** Remove a session and notify partners about dropped connections. */
function teardownSession(sid: string): void {
  const bucket = sessionStore.get(sid);
  if (!bucket) return;

  for (const cid of Array.from(bucket.activeConnections)) {
    dropConnectionInternal(sid, cid, nowMs());
  }

  sessionStore.delete(sid);
}

/**
 * Break a connection and enqueue drop-events for the other party.
 * Removes the connection from both participants' sets and the pairing table.
 */
function dropConnectionInternal(
  initiatorSid: string,
  cid: string,
  stamp: number
): void {
  const bucket = sessionStore.get(initiatorSid);
  if (bucket) {
    bucket.activeConnections.delete(cid);
    bucket.incomingOffers.delete(cid);
    bucket.incomingAnswers.delete(cid);
    bucket.incomingCandidates.delete(cid);
    bucket.dropEvents.push(new PeerDropEvent(cid, stamp));
  }

  const pair = pairingTable.get(cid);
  if (!pair) return;

  const otherSid = pair[0] === initiatorSid ? pair[1] : pair[0];
  if (otherSid) {
    const otherBucket = sessionStore.get(otherSid);
    if (otherBucket) {
      otherBucket.activeConnections.delete(cid);
      otherBucket.dropEvents.push(new PeerDropEvent(cid, stamp));
    }
  }
  pairingTable.delete(cid);
}

// ---------------------------------------------------------------------------
// Filtering helpers
// ---------------------------------------------------------------------------

function offersForSession(
  sid: string,
  since: number
): Array<[string, Offer]> {
  const result: Array<[string, Offer]> = [];

  if (privateMode) {
    const bucket = sessionStore.get(sid);
    if (bucket) {
      for (const [cid, offer] of bucket.incomingOffers) {
        if (since <= 0 || offer.datetime >= since) {
          result.push([cid, offer]);
        }
      }
    }
  } else {
    for (const [ownerSid, bucket] of sessionStore) {
      if (ownerSid === sid) continue;
      for (const [cid, offer] of bucket.incomingOffers) {
        if (since <= 0 || offer.datetime >= since) {
          result.push([cid, offer]);
        }
      }
    }
  }

  return result;
}

function answersForSession(
  sid: string,
  since: number
): Array<[string, Answer]> {
  const bucket = sessionStore.get(sid);
  if (!bucket) return [];
  const result: Array<[string, Answer]> = [];
  for (const [cid, ans] of bucket.incomingAnswers) {
    if (since <= 0 || ans.datetime >= since) {
      result.push([cid, ans]);
    }
  }
  return result;
}

function candidatesForSession(
  sid: string,
  since: number
): Array<[string, Candidate]> {
  const bucket = sessionStore.get(sid);
  if (!bucket) return [];
  const result: Array<[string, Candidate]> = [];

  for (const cid of bucket.activeConnections) {
    const pair = pairingTable.get(cid);
    if (!pair) continue;

    const otherSid = sid === pair[0] ? pair[1] : pair[0];
    if (!otherSid) continue;

    const otherBucket = sessionStore.get(otherSid);
    if (!otherBucket) continue;

    const cands = otherBucket.incomingCandidates.get(cid);
    if (!cands) continue;

    for (const c of cands) {
      if (since <= 0 || c.datetime >= since) {
        result.push([cid, c]);
      }
    }
  }

  return result;
}

function dropEventsForSession(
  sid: string,
  since: number
): PeerDropEvent[] {
  const bucket = sessionStore.get(sid);
  if (!bucket) return [];
  if (since <= 0) return bucket.dropEvents;
  return bucket.dropEvents.filter((e) => e.when >= since);
}

// ---------------------------------------------------------------------------
// Express middleware & route handlers
// ---------------------------------------------------------------------------

/** Middleware: validate Session-Id header on non-root paths. */
function checkSessionId(req: Request, res: Response, next: () => void): void {
  if (req.url === "/") {
    next();
    return;
  }
  const sid = req.header("session-id");
  if (!sid || !sessionStore.has(sid)) {
    res.sendStatus(404);
    return;
  }
  sessionStore.get(sid)!.lastPollAt = nowMs();
  next();
}

/** PUT /signaling — create a new session. */
function createSession(req: Request | string, res: Response): void {
  const sid = typeof req === "string" ? req : uuidv4();
  ensureBucket(sid);
  res.json({ sessionId: sid });
}

/** DELETE /signaling — tear down a session. */
function deleteSession(req: Request, res: Response): void {
  const sid = req.header("session-id")!;
  teardownSession(sid);
  res.sendStatus(200);
}

/** GET /signaling — poll all pending messages. */
function getAll(req: Request, res: Response): void {
  sweepStaleSessions();
  const sid = req.header("session-id")!;
  const bucket = sessionStore.get(sid)!;
  const since = req.query.fromtime ? Number(req.query.fromtime) : 0;
  const stamp = bucket.lastPollAt;

  const conns = Array.from(bucket.activeConnections);
  const ofrs = offersForSession(sid, since);
  const anss = answersForSession(sid, since);
  const cands = candidatesForSession(sid, since);
  const drops = dropEventsForSession(sid, since);

  const messages: any[] = [];

  for (const cid of conns) {
    const pair = pairingTable.get(cid);
    // Determine polite: first session in the pair is impolite, second is polite
    let polite = true;
    if (pair) {
      polite = pair[0] !== sid;
    }
    messages.push({ connectionId: cid, polite, type: "connect", datetime: stamp });
  }
  for (const [cid, o] of ofrs) {
    messages.push({
      connectionId: cid,
      sdp: o.sdp,
      polite: o.polite,
      type: "offer",
      datetime: o.datetime,
    });
  }
  for (const [cid, a] of anss) {
    messages.push({
      connectionId: cid,
      sdp: a.sdp,
      type: "answer",
      datetime: a.datetime,
    });
  }
  for (const [cid, c] of cands) {
    messages.push({
      connectionId: cid,
      candidate: c.candidate,
      sdpMLineIndex: c.sdpMLineIndex,
      sdpMid: c.sdpMid,
      type: "candidate",
      datetime: c.datetime,
    });
  }
  for (const d of drops) {
    messages.push({ connectionId: d.peerId, type: "disconnect", datetime: d.when });
  }

  messages.sort((a, b) => a.datetime - b.datetime);
  res.json({ messages, datetime: stamp });
}

/** GET /signaling/connection — list active connections. */
function getConnection(req: Request, res: Response): void {
  sweepStaleSessions();
  const sid = req.header("session-id")!;
  const bucket = sessionStore.get(sid)!;
  const ids = Array.from(bucket.activeConnections);
  res.json({
    connections: ids.map((cid) => {
      const pair = pairingTable.get(cid);
      let polite = true;
      if (pair) {
        polite = pair[0] !== sid;
      }
      return {
        connectionId: cid,
        polite,
        type: "connect",
        datetime: nowMs(),
      };
    }),
  });
}

/** PUT /signaling/connection — join or create a connection. */
function createConnection(req: Request, res: Response): void {
  const sid = req.header("session-id")!;
  const bucket = sessionStore.get(sid)!;
  const { connectionId: cid } = req.body;
  const stamp = bucket.lastPollAt;

  if (cid == null) {
    res.status(400).json({ error: new Error("connectionId is required") });
    return;
  }

  let polite = true;

  if (privateMode) {
    const pair = pairingTable.get(cid);
    if (pair) {
      if (pair[0] != null && pair[1] != null) {
        const err = new Error(`${cid}: This connection id is already used.`);
        res.status(400).json({ error: err });
        return;
      }
      if (pair[0] != null) {
        pairingTable.set(cid, [pair[0], sid]);
        ensureBucket(pair[0]).activeConnections.add(cid);
      }
    } else {
      pairingTable.set(cid, [sid, null]);
      polite = false;
    }
  }

  bucket.activeConnections.add(cid);
  res.json({ connectionId: cid, polite, type: "connect", datetime: stamp });
}

/** DELETE /signaling/connection — leave a connection. */
function deleteConnection(req: Request, res: Response): void {
  const sid = req.header("session-id")!;
  const { connectionId: cid } = req.body;
  const stamp = sessionStore.get(sid)!.lastPollAt;
  dropConnectionInternal(sid, cid, stamp);
  res.json({ connectionId: cid });
}

/** POST /signaling/offer — send an SDP offer. */
function postOffer(req: Request, res: Response): void {
  const sid = req.header("session-id")!;
  const { connectionId: cid, sdp } = req.body;
  const stamp = sessionStore.get(sid)!.lastPollAt;

  if (privateMode) {
    const pair = pairingTable.get(cid);
    if (pair) {
      const otherSid = pair[0] === sid ? pair[1] : pair[0];
      if (otherSid) {
        ensureBucket(otherSid).incomingOffers.set(
          cid,
          new Offer(sdp, stamp, true)
        );
      }
    }
    res.sendStatus(200);
    return;
  }

  if (!pairingTable.has(cid)) {
    pairingTable.set(cid, [sid, null]);
  }

  sessionStore.get(sid)!.incomingOffers.set(cid, new Offer(sdp, stamp, false));
  res.sendStatus(200);
}

/** POST /signaling/answer — send an SDP answer. */
function postAnswer(req: Request, res: Response): void {
  const sid = req.header("session-id")!;
  const { connectionId: cid, sdp } = req.body;
  const stamp = sessionStore.get(sid)!.lastPollAt;

  sessionStore.get(sid)!.activeConnections.add(cid);

  const pair = pairingTable.get(cid);
  if (!pair) {
    res.sendStatus(200);
    return;
  }

  const otherSid = pair[0] === sid ? pair[1] : pair[0];
  if (!otherSid || !sessionStore.has(otherSid)) {
    res.sendStatus(200);
    return;
  }

  if (!privateMode) {
    pairingTable.set(cid, [otherSid, sid]);
  }

  ensureBucket(otherSid).incomingAnswers.set(cid, new Answer(sdp, stamp));

  // Retarget any pending candidates from the offerer's bucket to the answer's timestamp
  const otherBucket = sessionStore.get(otherSid);
  if (otherBucket) {
    const pending = otherBucket.incomingCandidates.get(cid);
    if (pending) {
      for (const c of pending) {
        (c as any).datetime = stamp;
      }
    }
  }

  res.sendStatus(200);
}

/** POST /signaling/candidate — send an ICE candidate. */
function postCandidate(req: Request, res: Response): void {
  const sid = req.header("session-id")!;
  const { connectionId: cid, candidate, sdpMLineIndex, sdpMid } = req.body;
  const stamp = sessionStore.get(sid)!.lastPollAt;

  const bucket = sessionStore.get(sid)!;
  let arr = bucket.incomingCandidates.get(cid);
  if (!arr) {
    arr = [];
    bucket.incomingCandidates.set(cid, arr);
  }
  arr.push(new Candidate(candidate, sdpMLineIndex, sdpMid, stamp));
  res.sendStatus(200);
}

/** GET /signaling/offer — poll pending offers. */
function getOffer(req: Request, res: Response): void {
  const since = req.query.fromtime ? Number(req.query.fromtime) : 0;
  const sid = req.header("session-id")!;
  const ofrs = offersForSession(sid, since);
  res.json({
    offers: ofrs.map(([cid, o]) => ({
      connectionId: cid,
      sdp: o.sdp,
      polite: o.polite,
      type: "offer",
      datetime: o.datetime,
    })),
  });
}

/** GET /signaling/answer — poll pending answers. */
function getAnswer(req: Request, res: Response): void {
  const since = req.query.fromtime ? Number(req.query.fromtime) : 0;
  const sid = req.header("session-id")!;
  const anss = answersForSession(sid, since);
  res.json({
    answers: anss.map(([cid, a]) => ({
      connectionId: cid,
      sdp: a.sdp,
      type: "answer",
      datetime: a.datetime,
    })),
  });
}

/** GET /signaling/candidate — poll pending ICE candidates. */
function getCandidate(req: Request, res: Response): void {
  const since = req.query.fromtime ? Number(req.query.fromtime) : 0;
  const sid = req.header("session-id")!;
  const cands = candidatesForSession(sid, since);
  res.json({
    candidates: cands.map(([cid, c]) => ({
      connectionId: cid,
      candidate: c.candidate,
      sdpMLineIndex: c.sdpMLineIndex,
      sdpMid: c.sdpMid,
      type: "candidate",
      datetime: c.datetime,
    })),
  });
}

/** Reset all state; called at server startup with the chosen mode. */
function reset(mode: string): void {
  privateMode = mode === "private";
  sessionStore.clear();
  pairingTable.clear();
}

export {
  reset,
  checkSessionId,
  getAll,
  getConnection,
  getOffer,
  getAnswer,
  getCandidate,
  createSession,
  deleteSession,
  createConnection,
  deleteConnection,
  postOffer,
  postAnswer,
  postCandidate,
};
