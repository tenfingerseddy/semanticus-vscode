// Verifier test for m11/state-home round 3 (27 Sep), left FAILING on purpose; the verifier's report is
// /tmp/semanticus-joint-review/lane-reports/v-state-home-r3.md.
//
// Both gather tools give the engine a scratch SEMANTICUS_HOME under the temp folder and remove it in a
// process.on('exit') handler, which their comments and the round-3 report call "every way out". Node does not run
// 'exit' handlers when a signal ends the process, so a run stopped with Ctrl+C (SIGINT) or SIGTERM, the usual way to
// stop a gather that is waiting on a slow model, leaves its scratch home behind. Measured with the real engine too
// (sem-home-jev-columns-* and sem-home-jev-starter-* left in TMPDIR after SIGINT and SIGTERM).
//
//   node --test tools/jev-replay/columns/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, existsSync, rmSync, chmodSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const TOOLS = { columns: join(HERE, '..', 'gather.mjs'), 'starter-measures': join(HERE, '..', '..', 'starter-measures', 'gather.mjs') };

// A stand-in engine that records the environment it was given, answers initialize, and never answers a tool call, so
// the gather is waiting on its first connect_xmla when the signal arrives. It leaves when its stdin closes.
const HANG_ENGINE = `#!/usr/bin/env node
import { createInterface } from 'node:readline';
import { writeFileSync } from 'node:fs';
writeFileSync(process.env.HANG_ENGINE_ENV_LOG, JSON.stringify({ home: process.env.SEMANTICUS_HOME ?? null, pid: process.pid }));
const rl = createInterface({ input: process.stdin });
rl.on('line', (line) => {
  let m; try { m = JSON.parse(line); } catch { return; }
  if (m.method === 'initialize') process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: m.id, result: { protocolVersion: '2025-06-18', capabilities: {}, serverInfo: { name: 'hang', version: '0' } } }) + '\\n');
});
rl.on('close', () => process.exit(0));
`;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// POSIX signals and a shebang stand-in engine: Windows has neither, so there the test is skipped and says so.
const SKIP = process.platform === 'win32' ? 'NOT RUN on Windows: needs POSIX signals and a shebang engine. This is not a pass.' : false;

for (const [tool, script] of Object.entries(TOOLS)) {
  for (const signal of ['SIGINT', 'SIGTERM']) {
    test(`${tool} gather removes its scratch home when it is stopped with ${signal}`, { skip: SKIP }, async () => {
      const home = mkdtempSync(join(tmpdir(), 'jrv-home-'));
      const temp = mkdtempSync(join(tmpdir(), 'jrv-tmp-'));
      const engine = join(home, 'hang-engine.mjs');
      writeFileSync(engine, HANG_ENGINE);
      chmodSync(engine, 0o755);
      const conns = join(home, 'connections.json');
      writeFileSync(conns, JSON.stringify([{ kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso', database: 'Sales' }]));
      const envLog = join(home, 'engine-env.json');
      const env = { ...process.env, HOME: home, TMPDIR: temp, SEMANTICUS_ENGINE: engine, HANG_ENGINE_ENV_LOG: envLog };
      delete env.SEMANTICUS_HOME;
      delete env.SEMANTICUS_SIGNIN_DIR;
      const child = spawn(process.execPath, [script, '--connections', conns, '--out', join(home, 'out.json')], { env, stdio: 'ignore' });
      const exited = new Promise((r) => child.on('exit', (code, sig) => r({ code, sig })));
      let seen = null;
      try {
        for (let i = 0; i < 200 && !seen; i++) {
          await sleep(50);
          try { seen = JSON.parse(readFileSync(envLog, 'utf8')); } catch { /* not yet */ }
        }
        assert.ok(seen?.home, 'the stand-in engine was started with a scratch SEMANTICUS_HOME');
        assert.ok(existsSync(seen.home), 'the scratch home exists while the gather runs');
        await sleep(300);                // the gather is now waiting on connect_xmla
        child.kill(signal);
        await exited;
        await sleep(200);
        assert.ok(!existsSync(seen.home),
          `${tool}/gather.mjs was stopped with ${signal} and left its scratch home behind: ${seen.home}`);
      } finally {
        try { child.kill('SIGKILL'); } catch { /* gone */ }
        try { if (seen?.pid) process.kill(seen.pid, 'SIGKILL'); } catch { /* gone */ }
        if (seen?.home) rmSync(seen.home, { recursive: true, force: true });
        rmSync(home, { recursive: true, force: true });
        rmSync(temp, { recursive: true, force: true });
      }
    });
  }
}

// Round 4 (28 Sep), the verifier's report /tmp/semanticus-joint-review/lane-reports/v-state-home-r4.md: the handlers are
// installed with process.once, so a second Ctrl+C while the gather is still closing its engine (the real engine takes
// about a second to go) meets Node's default handler, which ends the process without running the 'exit' handler. The
// scratch home is left behind; measured with the real engine too (an empty sem-home-jev-* folder, both tools).
const SLOW_ENGINE = HANG_ENGINE.replace("rl.on('close', () => process.exit(0));", "rl.on('close', () => setTimeout(() => process.exit(0), 2000));");

for (const [tool, script] of Object.entries(TOOLS)) {
  test(`${tool} gather removes its scratch home when Ctrl+C is pressed twice while it closes`, { skip: SKIP }, async () => {
    const home = mkdtempSync(join(tmpdir(), 'jrv-home-'));
    const temp = mkdtempSync(join(tmpdir(), 'jrv-tmp-'));
    const engine = join(home, 'slow-engine.mjs');
    writeFileSync(engine, SLOW_ENGINE);
    chmodSync(engine, 0o755);
    const conns = join(home, 'connections.json');
    writeFileSync(conns, JSON.stringify([{ kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso', database: 'Sales' }]));
    const envLog = join(home, 'engine-env.json');
    const env = { ...process.env, HOME: home, TMPDIR: temp, SEMANTICUS_ENGINE: engine, HANG_ENGINE_ENV_LOG: envLog };
    delete env.SEMANTICUS_HOME;
    delete env.SEMANTICUS_SIGNIN_DIR;
    const child = spawn(process.execPath, [script, '--connections', conns, '--out', join(home, 'out.json')], { env, stdio: 'ignore' });
    const exited = new Promise((r) => child.on('exit', (code, sig) => r({ code, sig })));
    let seen = null;
    try {
      for (let i = 0; i < 200 && !seen; i++) {
        await sleep(50);
        try { seen = JSON.parse(readFileSync(envLog, 'utf8')); } catch { /* not yet */ }
      }
      assert.ok(seen?.home, 'the stand-in engine was started with a scratch SEMANTICUS_HOME');
      await sleep(300);                // the gather is now waiting on connect_xmla
      child.kill('SIGINT');            // the first Ctrl+C starts the clean exit: close the engine, then leave
      await sleep(150);
      child.kill('SIGINT');            // the second one arrives while the engine is still closing
      await exited;
      await sleep(2500);               // the slow engine has gone by now
      assert.ok(!existsSync(seen.home),
        `${tool}/gather.mjs was stopped with Ctrl+C twice and left its scratch home behind: ${seen.home}`);
    } finally {
      try { child.kill('SIGKILL'); } catch { /* gone */ }
      try { if (seen?.pid) process.kill(seen.pid, 'SIGKILL'); } catch { /* gone */ }
      if (seen?.home) rmSync(seen.home, { recursive: true, force: true });
      rmSync(home, { recursive: true, force: true });
      rmSync(temp, { recursive: true, force: true });
    }
  });
}
