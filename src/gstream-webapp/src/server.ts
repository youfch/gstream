import express, { Request, Response } from "express";
import * as path from "path";
import * as fs from "fs";
import morgan from "morgan";
import cors from "cors";
import signaling from "./signaling";
import { log, LogLevel } from "./log";
import Options from "./class/options";
import * as httphandler from "./class/httphandler";

export const createServer = (config: Options): express.Application => {
  const app = express();

  httphandler.reset(config.mode || "public");

  // HTTP access logging
  if (config.logging && config.logging !== "none") {
    app.use(morgan(config.logging));
  }

  app.use(cors({ origin: "*" }));
  app.use(express.urlencoded({ extended: true }));
  app.use(express.json());

  // Config endpoint for clients
  app.get("/config", (_req: Request, res: Response) => {
    res.json({
      useWebSocket: config.type === "websocket",
      startupMode: config.mode,
      logging: config.logging,
    });
  });

  // Signaling routes
  app.use("/signaling", signaling);

  // Static file serving
  app.use(express.static(path.join(__dirname, "../client/public")));
  app.use("/module", express.static(path.join(__dirname, "../client/src")));

  // Fallback index page
  app.get("/", (_req: Request, res: Response) => {
    const indexPath = path.join(__dirname, "../client/public/index.html");
    fs.access(indexPath, (err) => {
      if (err) {
        log(LogLevel.warn, `Can't find file '${indexPath}'`);
        res.status(404).send(`Can't find file ${indexPath}`);
      } else {
        res.sendFile(indexPath);
      }
    });
  });

  return app;
};
