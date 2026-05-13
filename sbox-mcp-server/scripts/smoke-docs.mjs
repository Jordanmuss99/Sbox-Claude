import { spawn } from "node:child_process";
import process from "node:process";

const proc = spawn(process.execPath, ["dist/index.js"], { stdio: ["pipe", "pipe", "pipe"] });
let buf = ""; let err = "";
proc.stdout.on("data", c => { buf += c.toString(); });
proc.stderr.on("data", c => { err += c.toString(); });

const send = req => proc.stdin.write(JSON.stringify(req) + "\n");
const read = (timeout = 90000) => new Promise((resolve, reject) => {
  const start = Date.now();
  const tick = () => {
    const i = buf.indexOf("\n");
    if (i >= 0) { const line = buf.slice(0, i).trim(); buf = buf.slice(i + 1); if (line) return resolve(JSON.parse(line)); }
    if (Date.now() - start > timeout) return reject(new Error("timeout; stderr=" + err.slice(-500)));
    setTimeout(tick, 30);
  };
  tick();
});

let id = 1;
const call = async (method, params) => {
  send({ jsonrpc: "2.0", id: id++, method, params });
  return await read();
};
const tool = async (name, args = {}) => {
  const r = await call("tools/call", { name, arguments: args });
  return r.result?.content?.[0]?.text ?? JSON.stringify(r);
};

try {
  const init = await call("initialize", { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "docs-smoke", version: "1" } });
  console.log("init:", init.result?.serverInfo?.name);
  send({ jsonrpc: "2.0", method: "notifications/initialized" });

  console.log("--- sbox_cache_status (pre-crawl) ---");
  console.log((await tool("sbox_cache_status")).slice(0, 800));

  console.log("\n--- sbox_list_doc_categories (triggers crawl, may take ~30s) ---");
  const cats = await tool("sbox_list_doc_categories");
  console.log(cats.slice(0, 1200));

  console.log("\n--- sbox_search_docs query=\"scene component\" ---");
  console.log((await tool("sbox_search_docs", { query: "scene component", limit: 3 })).slice(0, 1500));

  console.log("\n--- sbox_search_docs query=\"prefab\" category=\"scene\" ---");
  console.log((await tool("sbox_search_docs", { query: "prefab", limit: 2, category: "scene" })).slice(0, 1000));

  console.log("\n--- sbox_get_doc_page (first hit URL) ---");
  const searchOut = await tool("sbox_search_docs", { query: "gameobject", limit: 1 });
  const m = searchOut.match(/\((https:[^)]+)\)/);
  if (m) {
    console.log("Fetching:", m[1]);
    console.log((await tool("sbox_get_doc_page", { url: m[1], maxLength: 600 })).slice(0, 900));
  } else {
    console.log("(no URL extracted)");
  }

  console.log("\n--- sbox_cache_status (post-crawl) ---");
  console.log((await tool("sbox_cache_status")).slice(0, 600));

  console.log("\n=== ALL OK ===");
  proc.kill();
  process.exit(0);
} catch (e) {
  console.error("FAIL:", e.message);
  console.error("stderr:", err.slice(-800));
  proc.kill();
  process.exit(1);
}
