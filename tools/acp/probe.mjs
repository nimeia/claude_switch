#!/usr/bin/env node
// Raw ACP probe: speaks NDJSON JSON-RPC to the claude-agent-acp adapter and
// prints every frame. Used to pin down the protocol shape and to answer the
// go/no-go questions for claude-switch:
//
//   1. does `initialize` advertise `loadSession` (resume support)?
//   2. does the adapter authenticate with the OAuth subscription, or does it
//      demand an API key (`authMethods` non-empty / prompt fails)?
//   3. does `CLAUDE_CONFIG_DIR` reach the underlying Claude Code, i.e. can a
//      session run as a specific stored account?
//
// Usage:
//   node probe.mjs [--config-dir <dir>] [--cwd <dir>] [--prompt <text>]
//                  [--load <sessionId>] [--timeout <ms>]

import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import path from "node:path";
import process from "node:process";

const HERE = path.dirname(fileURLToPath(import.meta.url));

function parseArgs(argv) {
  const out = { timeout: 120000, prompt: "Reply with exactly: ACP-OK" };
  for (let i = 0; i < argv.length; i += 1) {
    const key = argv[i];
    if (!key.startsWith("--")) continue;
    const value = argv[i + 1];
    switch (key) {
      case "--config-dir": out.configDir = value; i += 1; break;
      case "--cwd": out.cwd = value; i += 1; break;
      case "--prompt": out.prompt = value; i += 1; break;
      case "--load": out.load = value; i += 1; break;
      case "--timeout": out.timeout = Number(value); i += 1; break;
      default: break;
    }
  }
  return out;
}

const args = parseArgs(process.argv.slice(2));
const cwd = path.resolve(args.cwd ?? process.cwd());

// The adapter ships a .cmd shim on Windows; node runs dist/index.js directly so
// we do not depend on shell resolution.
const adapter = path.join(
  HERE, "node_modules", "@agentclientprotocol", "claude-agent-acp", "dist", "index.js");

const env = { ...process.env };
if (args.configDir) env.CLAUDE_CONFIG_DIR = path.resolve(args.configDir);
// An inherited API key would mask whether OAuth works, which is the thing
// under test.
delete env.ANTHROPIC_API_KEY;
delete env.ANTHROPIC_AUTH_TOKEN;

const child = spawn(process.execPath, [adapter], {
  cwd,
  env,
  stdio: ["pipe", "pipe", "pipe"],
});

let nextId = 1;
const pending = new Map();
const log = (dir, obj) => {
  const stamp = new Date().toISOString().slice(11, 23);
  console.log(`${stamp} ${dir} ${JSON.stringify(obj)}`);
};

function send(obj) {
  log("-->", obj);
  child.stdin.write(`${JSON.stringify(obj)}\n`);
}

function request(method, params) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    send({ jsonrpc: "2.0", id, method, params });
  });
}

function respond(id, result) {
  send({ jsonrpc: "2.0", id, result });
}

child.stderr.on("data", (buf) => {
  process.stderr.write(`[adapter stderr] ${buf}`);
});

createInterface({ input: child.stdout }).on("line", (line) => {
  if (!line.trim()) return;
  let msg;
  try {
    msg = JSON.parse(line);
  } catch {
    console.log(`<-- (non-json) ${line}`);
    return;
  }
  log("<--", msg);

  // Response to something we sent.
  if (msg.id !== undefined && msg.method === undefined) {
    const waiter = pending.get(msg.id);
    if (!waiter) return;
    pending.delete(msg.id);
    if (msg.error) waiter.reject(new Error(JSON.stringify(msg.error)));
    else waiter.resolve(msg.result);
    return;
  }

  // Agent -> client request. Answer the ones a real client must handle so the
  // turn can actually complete.
  if (msg.id !== undefined && msg.method) {
    switch (msg.method) {
      case "session/request_permission": {
        // Pick the first "allow"-ish option so the probe runs unattended.
        const options = msg.params?.options ?? [];
        const chosen =
          options.find((o) => o.kind === "allow_always") ??
          options.find((o) => o.kind === "allow_once") ??
          options[0];
        respond(msg.id, chosen
          ? { outcome: { outcome: "selected", optionId: chosen.optionId } }
          : { outcome: { outcome: "cancelled" } });
        return;
      }
      case "fs/read_text_file": {
        // We advertised the capability; honour it.
        import("node:fs/promises").then(async (fs) => {
          try {
            const content = await fs.readFile(msg.params.path, "utf8");
            respond(msg.id, { content });
          } catch (e) {
            send({ jsonrpc: "2.0", id: msg.id, error: { code: -32603, message: String(e) } });
          }
        });
        return;
      }
      case "fs/write_text_file": {
        import("node:fs/promises").then(async (fs) => {
          try {
            await fs.writeFile(msg.params.path, msg.params.content, "utf8");
            respond(msg.id, {});
          } catch (e) {
            send({ jsonrpc: "2.0", id: msg.id, error: { code: -32603, message: String(e) } });
          }
        });
        return;
      }
      default:
        send({ jsonrpc: "2.0", id: msg.id, error: { code: -32601, message: `unhandled ${msg.method}` } });
    }
  }
});

const bail = (why, code) => {
  console.log(`\n=== PROBE END: ${why} ===`);
  child.kill();
  process.exit(code);
};

const timer = setTimeout(() => bail(`timeout after ${args.timeout}ms`, 2), args.timeout);

try {
  const init = await request("initialize", {
    protocolVersion: 1,
    clientCapabilities: {
      fs: { readTextFile: true, writeTextFile: true },
      terminal: false,
    },
  });

  console.log("\n### initialize result");
  console.log(JSON.stringify(init, null, 2));
  console.log(`### loadSession supported: ${init?.agentCapabilities?.loadSession === true}`);
  console.log(`### authMethods: ${JSON.stringify(init?.authMethods ?? [])}`);
  console.log(`### CLAUDE_CONFIG_DIR sent: ${env.CLAUDE_CONFIG_DIR ?? "(none)"}\n`);

  let sessionId;
  if (args.load) {
    await request("session/load", { sessionId: args.load, cwd, mcpServers: [] });
    sessionId = args.load;
    console.log(`\n### session/load OK: ${sessionId}\n`);
  } else {
    const created = await request("session/new", { cwd, mcpServers: [] });
    sessionId = created.sessionId;
    console.log(`\n### session/new OK: ${sessionId}\n`);
  }

  const turn = await request("session/prompt", {
    sessionId,
    prompt: [{ type: "text", text: args.prompt }],
  });
  console.log("\n### prompt result");
  console.log(JSON.stringify(turn, null, 2));
  console.log(`### stopReason: ${turn?.stopReason}`);
  console.log(`### sessionId: ${sessionId}`);

  clearTimeout(timer);
  bail("ok", 0);
} catch (err) {
  clearTimeout(timer);
  console.log(`\n### FAILED: ${err.message}`);
  bail("error", 1);
}
