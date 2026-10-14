// Person buttons that can need a first sign-in send a press (rpcPressed), never the automatic rpc (m15/signin-auto, verifier probe). Each assert names a person's button that can need a
// first sign-in and checks that its request is a press. It FAILS on 7d0f7cb1 for each button that now answers "Press Connect" instead.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const read = (p) => readFileSync(new URL('../webview/src/' + p, import.meta.url), 'utf8');
const fails = [];
const check = (ok, what) => { if (!ok) fails.push(what); };

// 1. Connections: "Workspace model", "Sign in" (no saved record) and "Sign in another account" all open the picker (setView('workspace')).
const hub = read('connectionshub.tsx');
check(!/data-testid="hub-add-workspace"[^\n]*setView\('workspace'\)/.test(hub) || !read('connectpickers.tsx').includes('void loadWorkspaces(!first)'),
  'Connections "Workspace model" opens the picker, whose first workspace list is sent as automatic (rpc), so a first sign-in answers "Press Connect"');
// 2. Start a model from a SQL source (hero journey 5): the Start button sends startModelFromSql as automatic.
check(/rpcPressed<[^>]*>\('startModelFromSql'/.test(read('startplan.tsx')),
  'Start plan "Start" sends startModelFromSql as automatic (rpc); with no saved sign-in the SQL read answers "Press Connect"');
// 3. Pipelines: the page's own "Sign in" button retries the list the browser way, but as automatic.
check(/rpcPressed<[^>]*>\('listDeploymentPipelines'/.test(read('deploy.tsx')),
  'Pipelines "Sign in" (signInAndRetry -> loadPipelines) sends listDeploymentPipelines as automatic (rpc)');
check(/rpcPressed<[^>]*>\('runTests'/.test(read('tests.tsx')), 'Tests "Run checks" sends runTests as automatic (rpc)');
check(/rpcPressed<[^>]*>\('autogenerateSpecFromFabric'/.test(read('spec.tsx')), 'Spec "from Fabric" sends autogenerateSpecFromFabric as automatic (rpc)');
check(read('connectpickers.tsx').includes('pressedFirst || !first'), 'the workspace picker opened by a button reads its first list as a press');
if (fails.length) { console.error('FAIL\n- ' + fails.join('\n- ')); process.exit(1); }
console.log('ok');
