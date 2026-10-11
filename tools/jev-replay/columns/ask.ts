// Ask Jev every call in both arms. The pilot's own client (docs/redesign/jev-pilot/run.ts): same endpoint, model,
// timeout, concurrency and one retry on 429 / 5xx. Node 26: node --experimental-strip-types ask.ts
//
// Refusals, as the replay's live oracle has them: JEV_REPLAY_LIVE must be exactly "1", and both the call list and the
// answers must live outside this repository, because they carry model, table and column names. The key is read from
// ~/.config/typesafe/env into this process only, used for one header, and never printed, returned or written.
import { readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

if (process.env.JEV_REPLAY_LIVE !== '1') throw new Error('refusing: JEV_REPLAY_LIVE must be exactly "1"');
const REPO = resolve(fileURLToPath(new URL('.', import.meta.url)), '..', '..', '..');
const CACHE = join(homedir(), '.cache', 'jev-replay-columns');
const CALLS = resolve(process.env.JEV_REPLAY_CALLS ?? join(CACHE, 'calls.json'));
const OUTDIR = resolve(process.env.JEV_REPLAY_OUT ?? CACHE);
for (const p of [CALLS, OUTDIR]) if (p === REPO || p.startsWith(REPO + sep)) throw new Error('refusing: a path inside the repository');

const ENDPOINT = 'https://api.typesafe.ai/v1/systemone';
const MODEL = 'jev-latest';
const TIMEOUT_MS = Number(process.env.JEV_TIMEOUT_MS ?? 20_000);
const CONCURRENCY = Number(process.env.JEV_CONCURRENCY ?? 6);
const LIMIT = Number(process.env.JEV_LIMIT ?? 0);
const ARMS = (process.env.JEV_ARMS ?? 'facts,names').split(',');

const key = (readFileSync(join(homedir(), '.config', 'typesafe', 'env'), 'utf8')
  .match(/^\s*(?:export\s+)?[A-Z0-9_]*(?:KEY|TOKEN)[A-Z0-9_]*\s*=\s*["']?([^"'\s]+)/m) ?? [])[1] ?? '';
if (!key) throw new Error('no key');
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

type Call = { id: string; questions: Record<string, unknown>; arms: Record<string, unknown> };
const all: Call[] = JSON.parse(readFileSync(CALLS, 'utf8'));
const work = LIMIT > 0 ? all.slice(0, LIMIT) : all;

type Row = { id: string; answers?: Record<string, unknown>; usage?: Record<string, number>;
  ms?: number; error?: string; attempts?: number; jev_version?: string };

async function runArm(arm: string) {
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
            body: JSON.stringify({ model: MODEL, state: c.arms[arm], questions: c.questions }),
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
      if (done % 50 === 0) console.log(`${arm} ${done}/${work.length}`);
    }
  }
  await Promise.all(Array.from({ length: CONCURRENCY }, worker));
  writeFileSync(join(OUTDIR, `raw-${arm}.json`), JSON.stringify(out));
  const ok = out.filter((r) => r && !r.error);
  const tok = ok.reduce((a, r) => a + (r.usage?.input_tokens ?? 0), 0);
  const lat = ok.map((r) => r.ms ?? 0).sort((a, b) => a - b);
  console.log(`${arm}: ok ${ok.length}/${out.length} input_tokens ${tok} cost_input_usd ${((tok / 1e9) * 42).toFixed(5)} p50 ${lat[Math.floor(lat.length / 2)] ?? 0} ms version ${ok[0]?.jev_version ?? '-'}`);
}

for (const arm of ARMS) await runArm(arm);
