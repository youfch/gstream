import * as ws from "ws";
import { Server } from "http";
import * as handler from "./class/websockethandler";

export default class WSSignaling {
  server: Server;
  wss: ws.Server;

  constructor(server: Server, mode: string) {
    this.server = server;
    this.wss = new ws.Server({ server });
    handler.reset(mode);

    this.wss.on("connection", (socket: WebSocket) => {
      handler.add(socket);

      socket.onclose = (): void => {
        handler.remove(socket);
      };

      socket.onmessage = (ev: MessageEvent): void => {
        let msg: any;
        try {
          msg = JSON.parse(ev.data);
        } catch {
          return;
        }
        if (!msg) return;

        console.log(msg);

        switch (msg.type) {
          case "connect":
            handler.onConnect(socket, msg.connectionId);
            break;
          case "disconnect":
            handler.onDisconnect(socket, msg.connectionId);
            break;
          case "offer":
            handler.onOffer(socket, msg.data);
            break;
          case "answer":
            handler.onAnswer(socket, msg.data);
            break;
          case "candidate":
            handler.onCandidate(socket, msg.data);
            break;
        }
      };
    });
  }
}
