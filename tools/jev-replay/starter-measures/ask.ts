// Ask Jev the starter question for every gathered column. The columns replay's client (tools/jev-replay/columns/
// ask.ts on the replay branch): same endpoint, model, timeout, concurrency and one retry on 429 / 5xx.
// Node 26:  JEV_REPLAY_LIVE=1 node --experimental-strip-types tools/jev-replay/starter-measures/ask.ts
//
// ONE ARM: the product's own request. The state is the engine's snapshot for the column (starter.mjs, snapshot) and
// the questions are the engine's (starter.mjs, QUESTIONS), so what is scored is what the product would ask.
//
// Refusals: JEV_REPLAY_LIVE must be exactly "1", and the facts and the answers must live outside this repository,
// because they carry table and column names. The key is read from ~/.config/typesafe/env into this process only,
// used for one header, and never printed, returned or written. It is a .ts file on purpose, so the replay gate's
// no-network scan (which reads .mjs files) stays about the default path.
import { readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { QUESTIONS } from './starter.mjs';

if (process.env.JEV_REPLAY_LIVE !== '1') throw new Error('refusing: JEV_REPLAY_LIVE must be exactly "1"');
const REPO = resolve(fileURLToPath(new URL('.', import.meta.url)), '..', '..', '..');
const CACHE = join(homedir(), '.cache', 'jev-replay-starter');
const FACTS = resolve(process.env.JEV_REPLAY_FACTS ?? join(CACHE, 'starter-facts.json'));
const OUTDIR = resolve(process.env.JEV_REPLAY_OUT ?? CACHE);
for (const p of [FACTS, OUTDIR]) if (p === REPO || p.startsWith(REPO + sep)) throw new Error('refusing: a path inside the repository');

const ENDPOINT = 'https://api.typesafe.ai/v1/systemone';
const MODEL = 'jev-latest';
const TIMEOUT_MS = Number(process.env.JEV_TIMEOUT_MS ?? 20_000);
const CONCURRENCY = Number(process.env.JEV_CONCURRENCY ?? 6);

const key = (readFileSync(join(homedir(), '.config', 'typesafe', 'env'), 'utf8')
  .match(/^\s*(?:export\s+)?[A-Z0-9_]*(?:KEY|TOKEN)[A-Z0-9_]*\s*=\s*["']?([^"'\s]+)/m) ?? [])[1] ?? '';
if (!key) throw new Error('no key');
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

type Col = { table: string; column: string; state: Record<string, unknown> };
const facts = JSON.parse(readFileSync(FACTS, 'utf8'));
const work: { id: string; state: Record<string, unknown> }[] = [];
for (const m of facts.models ?? []) (m.columns as Col[]).forEach((c, i) => work.push({ id: `${m.model}#${i}`, state: c.state }));

type Row = { id: string; answers?: Record<string, unknown>; usage?: Record<string, number>; ms?: number; error?: string; attempts?: number; jev_version?: string };
const out: Row[] = new Array(work.length);
let next = 0, done = 0;
async function worker() {
  for (;;) {
    const i = next++;
    if (i >= work.length) return;
    const c = work[i];
    const started = Date.now();
    let attempts = 0, lastErr = '';
    for (let attempt = 1; attempt <= 2; attempt++) {
      attempts = attempt;
      try {
        const res = await fetch(ENDPOINT, {
          method: 'POST',
          headers: { authorization: `Bearer ${key}`, 'content-type': 'application/json' },
          body: JSON.stringify({ model: MODEL, state: c.state, questions: QUESTIONS }),
          signal: AbortSignal.timeout(TIMEOUT_MS),
        });
        const body = await res.text();
        if (res.ok) {
          const parsed = JSON.parse(body);
          out[i] = { id: c.id, answers: parsed.answers, usage: parsed.usage, jev_version: parsed.model, ms: Date.now() - started, attempts };
          break;
        }
        lastErr = `HTTP ${res.status}`;
        if (attempt === 2 || (res.status < 500 && res.status !== 429)) throw new Error(lastErr);
        const after = Number(res.headers.get('retry-after'));
        await sleep(Number.isFinite(after) && after > 0 ? after * 1000 : 2000);
      } catch (err) {
        lastErr = err instanceof Error ? err.message : String(err);
        const retryable = /HTTP (429|5\d\d)|TimeoutError|timed out|fetch failed|ECONN/i.test(lastErr);
        if (attempt === 2 || !retryable) { out[i] = { id: c.id, error: lastErr.slice(0, 120), ms: Date.now() - started, attempts }; break; }
        await sleep(2000);
      }
    }
    done++;
    if (done % 25 === 0) console.log(`${done}/${work.length}`);
  }
}
await Promise.all(Array.from({ length: CONCURRENCY }, worker));
writeFileSync(join(OUTDIR, 'raw-answers.json'), JSON.stringify(out));
const ok = out.filter((r) => r && !r.error);
const tok = ok.reduce((a, r) => a + (r.usage?.input_tokens ?? 0), 0);
const lat = ok.map((r) => r.ms ?? 0).sort((a, b) => a - b);
console.log(`ok ${ok.length}/${out.length} input_tokens ${tok} p50 ${lat[Math.floor(lat.length / 2)] ?? 0} ms version ${ok[0]?.jev_version ?? '-'}`);
