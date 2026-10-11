// A minimal MCP stdio client for the installed Semanticus engine (newline-delimited JSON-RPC).
// Used by gather.mjs on the machine that holds the connections. A copy of the columns replay's client (tools/jev-replay/columns/mcp-client.mjs on the replay branch), so this folder runs on its own. It never logs tool arguments or results,
// because both can carry model, table and column names.
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

export class McpClient {
  constructor(command, args, env) {
    this.proc = spawn(command, args, { env, stdio: ['pipe', 'pipe', 'pipe'] });
    this.pending = new Map();
    this.nextId = 1;
    this.stderrTail = [];
    createInterface({ input: this.proc.stdout }).on('line', (line) => {
      let msg;
      try { msg = JSON.parse(line); } catch { return; }
      if (msg.id != null && this.pending.has(msg.id)) {
        const { resolve, reject, timer } = this.pending.get(msg.id);
        clearTimeout(timer);
        this.pending.delete(msg.id);
        if (msg.error) reject(new Error(msg.error.message ?? 'mcp error'));
        else resolve(msg.result);
      }
    });
    createInterface({ input: this.proc.stderr }).on('line', (line) => {
      this.stderrTail.push(line);
      if (this.stderrTail.length > 40) this.stderrTail.shift();
    });
    this.exited = new Promise((r) => this.proc.on('exit', (code) => r(code)));
  }

  request(method, params, timeoutMs = 120_000) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`timeout after ${timeoutMs} ms on ${method}`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this.proc.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    });
  }

  notify(method, params) {
    this.proc.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');
  }

  async init() {
    await this.request('initialize', {
      protocolVersion: '2025-06-18', capabilities: {},
      clientInfo: { name: 'jev-replay-starter', version: '1' },
    }, 60_000);
    this.notify('notifications/initialized', {});
  }

  // Calls a tool and returns its parsed JSON payload. Engine results carry the object as JSON text; a tool-level
  // failure comes back as isError with a message, which is thrown.
  async call(name, args, timeoutMs) {
    const r = await this.request('tools/call', { name, arguments: args }, timeoutMs);
    if (r.structuredContent && !r.isError) return r.structuredContent;
    const texts = (r.content ?? []).filter((c) => c.type === 'text').map((c) => c.text);
    if (r.isError) throw new Error(texts.join('\n') || 'tool error');
    // The engine may append a health note as a second text block, so take the first block that is a JSON object.
    for (const t of texts) {
      try { const v = JSON.parse(t); if (v && typeof v === 'object') return v; } catch { /* not this block */ }
    }
    return { text: texts.join('\n') };
  }

  async close() {
    try { this.proc.stdin.end(); } catch { /* already closed */ }
    const t = setTimeout(() => this.proc.kill('SIGTERM'), 5_000);
    await this.exited;
    clearTimeout(t);
  }
}
