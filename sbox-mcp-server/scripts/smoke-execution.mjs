import { spawn } from "node:child_process";
import process from "node:process";
const proc = spawn(process.execPath, ["dist/index.js"], { stdio: ["pipe","pipe","pipe"] });
let buf=""; let err="";
proc.stdout.on("data", c => buf += c.toString());
proc.stderr.on("data", c => err += c.toString());
const send = r => proc.stdin.write(JSON.stringify(r)+"\n");
const read = (t=30000) => new Promise((res, rej) => {
  const s = Date.now();
  const tick = () => {
    const i = buf.indexOf("\n");
    if (i >= 0) { const ln = buf.slice(0,i).trim(); buf = buf.slice(i+1); if (ln) return res(JSON.parse(ln)); }
    if (Date.now() - s > t) return rej(new Error("timeout; stderr=" + err.slice(-500)));
    setTimeout(tick, 30);
  };
  tick();
});
let id = 1;
const call = async (m,p) => { send({jsonrpc:"2.0",id:id++,method:m,params:p}); return await read(); };
const tool = async (n,a={}) => {
  const r = await call("tools/call",{name:n,arguments:a});
  return r.result?.content?.[0]?.text ?? JSON.stringify(r);
};
try {
  await call("initialize",{protocolVersion:"2024-11-05",capabilities:{},clientInfo:{name:"s8-smoke",version:"1"}});
  send({jsonrpc:"2.0",method:"notifications/initialized"});

  console.log("=== console_run real command (developer 0) ===");
  console.log(await tool("console_run", { command: "developer 0" }));

  console.log("\n=== console_run rejected command (echo — not an s&box convar) ===");
  console.log(await tool("console_run", { command: "echo nope" }));

  console.log("\n=== execute_csharp simple expression ===");
  console.log(await tool("execute_csharp", { code: "1 + 2 * 3" }));

  console.log("\n=== execute_csharp using Sandbox namespace ===");
  console.log(await tool("execute_csharp", { code: "Vector3.Up.ToString()" }));

  console.log("\n=== execute_csharp scene query ===");
  console.log(await tool("execute_csharp", { code: "Editor.SceneEditorSession.Active?.Scene?.Name ?? \"(no scene)\"" }));

  console.log("\n=== execute_csharp deliberate compile error ===");
  console.log(await tool("execute_csharp", { code: "this is not valid C#" }));

  console.log("\n=== console_run missing command ===");
  console.log(await tool("console_run", { command: "" }));

  console.log("\nALL OK");
  proc.kill();
  process.exit(0);
} catch (e) {
  console.error("FAIL:", e.message);
  console.error("stderr:", err.slice(-800));
  proc.kill();
  process.exit(1);
}
