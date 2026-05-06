import { Command } from "commander";
import * as express from "express";
import * as https from "https";
import { Server } from "http";
import * as fs from "fs";
import * as os from "os";
import { createServer } from "./server";
import { AddressInfo } from "net";
import WSSignaling from "./websocket";
import Options from "./class/options";

export class GStreamWebApp {
  public static run(argv: string[]): GStreamWebApp {
    const cli = new Command();
    cli
      .usage("[options] <apps...>")
      .option(
        "-p, --port <n>",
        "Port to start the server on.",
        process.env.PORT || "80"
      )
      .option(
        "-s, --secure",
        "Enable HTTPS (you need server.key and server.cert).",
        process.env.SECURE === "true"
      )
      .option(
        "-k, --keyfile <path>",
        "https key file.",
        process.env.KEYFILE || "server.key"
      )
      .option(
        "-c, --certfile <path>",
        "https cert file.",
        process.env.CERTFILE || "server.cert"
      )
      .option(
        "-t, --type <type>",
        "Type of signaling protocol, Choose websocket or http.",
        process.env.TYPE || "websocket"
      )
      .option(
        "-m, --mode <type>",
        "Choose Communication mode public or private.",
        process.env.MODE || "public"
      )
      .option(
        "-l, --logging <type>",
        "Choose http logging type combined, dev, short, tiny or none.",
        process.env.LOGGING || "dev"
      )
      .parse(argv);

    const raw = cli.opts();
    const options: Options = {
      port: Number(raw.port),
      secure: !!raw.secure,
      keyfile: raw.keyfile,
      certfile: raw.certfile,
      type: raw.type || "websocket",
      mode: raw.mode || "public",
      logging: raw.logging || "dev",
    };
    return new GStreamWebApp(options);
  }

  public app: express.Application;
  public server: Server;
  public options: Options;

  constructor(options: Options) {
    this.options = options;
    this.app = createServer(this.options);

    // Create HTTP(S) server
    if (options.secure) {
      this.server = https.createServer(
        {
          key: fs.readFileSync(options.keyfile || "server.key"),
          cert: fs.readFileSync(options.certfile || "server.cert"),
        },
        this.app
      );
    } else {
      this.server = this.app.listen(options.port);
    }

    // Log listening address
    if (options.secure) {
      this.server.listen(options.port, () => {
        const info = this.server.address() as AddressInfo;
        for (const addr of this.listLocalIPv4()) {
          console.log(`https://${addr}:${info.port}`);
        }
      });
    } else {
      const info = this.server.address() as AddressInfo;
      if (info) {
        for (const addr of this.listLocalIPv4()) {
          console.log(`http://${addr}:${info.port}`);
        }
      }
    }

    // Validate and set up signaling transport
    if (options.type === "http") {
      console.log("Use http polling for signaling server.");
    } else if (options.type !== "websocket") {
      console.log(
        `signaling type should be set "websocket" or "http". ${options.type} is not supported.`
      );
      console.log("Changing signaling type to websocket.");
      options.type = "websocket";
    }

    if (options.type === "websocket") {
      const addrs = this.listLocalIPv4();
      console.log(`Use websocket for signaling server ws://${addrs[0]}`);
      new WSSignaling(this.server, options.mode || "public");
    }

    console.log(`start as ${options.mode} mode`);
  }

  /** Collect all local IPv4 addresses from all network interfaces. */
  private listLocalIPv4(): string[] {
    const result: string[] = [];
    const ifaces = os.networkInterfaces();
    for (const name in ifaces) {
      const entries = ifaces[name];
      if (!entries) continue;
      for (const entry of entries) {
        if (entry.family === "IPv4") {
          result.push(entry.address);
        }
      }
    }
    return result;
  }
}

GStreamWebApp.run(process.argv);
