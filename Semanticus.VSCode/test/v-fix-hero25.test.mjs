// Verifier v-fix-hero25: an assistant event kind has ONE consumer. useClaudeReflection keeps "last applied seq per kind" at module
// scope (activity.tsx: "Keyed by kind; each kind maps to one tab"), so the first hook to see a new run_dax marks it applied and
// every other hook of that kind skips it. fix-hero25 added a second run_dax hook in query-notebook.tsx; it runs first, so the
// Query page's own run_dax hook in daxlab.tsx never fires and the assistant's result is never shown (the harness shows the
// person's old number in the row the assistant's run opened; with the notebook hook renamed, the assistant's number shows).
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const src = join(resolve(dirname(fileURLToPath(import.meta.url)), '..'), 'webview', 'src');
const files = [];
const walk = (d) => { for (const e of readdirSync(d, { withFileTypes: true })) { const p = join(d, e.name); if (e.isDirectory()) walk(p); else if (/\.tsx?$/.test(e.name)) files.push(p); } };
walk(src);
const seen = new Map();
for (const f of files) {
  for (const m of readFileSync(f, 'utf8').matchAll(/useClaudeReflection\(\s*['"]([a-z_]+)['"]/g)) {
    seen.set(m[1], [...(seen.get(m[1]) ?? []), f.slice(src.length + 1)]);
  }
}
const twice = [...seen].filter(([, where]) => where.length > 1).map(([k, where]) => `${k}: ${where.join(', ')}`);
assert.deepEqual(twice, [], `an assistant event kind with two consumers: only the first one ever fires\n${twice.join('\n')}`);
console.log('ok   every assistant event kind has one consumer');
