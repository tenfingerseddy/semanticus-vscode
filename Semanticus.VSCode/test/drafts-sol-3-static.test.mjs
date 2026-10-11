// drafts-sol round 3, J3: the structure that makes the model fence impossible to forget (the advisor's redesign of the check's T1). Three facts, each checked on the source:
//   * ONE `registerCommand(` call exists in src/, inside registerPersonCommand, so no command can be added that skips the scope that names its model;
//   * every command package.json contributes (87) is registered through it;
//   * the extension has ONE engine connection constructor, its writer is the fenced writer, and the pipe is opened outside any action's scope (a socket created inside an action
//     would carry that action's model for ever).
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const srcDir = join(root, 'src');
const files = readdirSync(srcDir).filter((f) => f.endsWith('.ts')).map((f) => [f, readFileSync(join(srcDir, f), 'utf8').replace(/\r\n/g, '\n')]);
const extension = readFileSync(join(srcDir, 'extension.ts'), 'utf8').replace(/\r\n/g, '\n');
const manifest = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));

/** The source of the body of one top-level function or method-less `function name(...) {...}` up to its closing brace at column 0. */
function bodyOf(src, header) {
  const start = src.indexOf(header);
  assert.ok(start >= 0, `${header} exists`);
  const end = src.indexOf('\n}\n', start);
  return src.slice(start, end + 3);
}

test('J3: exactly one registerCommand( call in src/, and it is inside registerPersonCommand', () => {
  const calls = [];
  for (const [name, src] of files) {
    src.split('\n').forEach((line, i) => { if (/\bregisterCommand\(/.test(line) && !/^\s*(\/\/|\*)/.test(line)) calls.push(`${name}:${i + 1}`); });
  }
  assert.equal(calls.length, 1, `registerCommand( appears ${calls.length} times (${calls.join(', ')}); a command registered any other way skips the scope that names its model`);
  const wrapper = bodyOf(extension, 'function registerPersonCommand(');
  assert.match(wrapper, /\bregisterCommand\(/, 'the one call is in the wrapper');
  assert.match(wrapper, /runPersonAction\(/, 'the wrapper runs every handler through runPersonAction');
  assert.match(bodyOf(extension, 'function runPersonAction'), /personAction\(/, '...which opens the action scope through the compiled personAction');
  assert.equal([...extension.matchAll(/\bpersonAction\(/g)].length, 3, 'personAction is called from runPersonAction (its two branches) and from the DAX editor writeFile only');
});

test('J3: every command id package.json contributes is registered through registerPersonCommand', () => {
  const consts = new Map([...extension.matchAll(/\bconst ([A-Z_]+) = '(semanticus[A-Za-z.]*)'/g)].map((m) => [m[1], m[2]]));
  const registered = new Set();
  for (const m of extension.matchAll(/registerPersonCommand\(\s*('([^']+)'|([A-Z_]+))/g)) registered.add(m[2] ?? consts.get(m[3]));
  const ids = manifest.contributes.commands.map((c) => c.command);
  assert.equal(ids.length, 87, 'package.json contributes 87 commands (a number change here means a command was added or removed: check it is registered)');
  const missing = ids.filter((id) => !registered.has(id));
  assert.deepEqual(missing, [], `contributed commands not registered through registerPersonCommand: ${missing.join(', ')}`);
  assert.ok(registered.size >= 87, `${registered.size} ids registered through the wrapper`);
  assert.equal([...extension.matchAll(/vscode\.commands\.registerCommand\(/g)].length, 1, 'no raw vscode.commands.registerCommand outside the wrapper');
});

test('J3: one createMessageConnection in src/, using FencedMessageWriter, and connectPipe opens the pipe outside any action', () => {
  const makers = [];
  for (const [name, src] of files) for (const m of src.matchAll(/createMessageConnection\(/g)) makers.push(name + ':' + src.slice(0, m.index).split('\n').length);
  const calls = makers;
  assert.equal(calls.length, 1, `createMessageConnection( is called ${calls.length} times (${calls.join(', ')})`);
  assert.match(extension, /createMessageConnection\(new StreamMessageReader\(socket\), new FencedMessageWriter\(new StreamMessageWriter\(socket\)\)\)/);
  const pipe = bodyOf(extension, 'async function connectPipe(');
  assert.match(pipe, /outsideAnyAction\(/, 'the pipe\'s reader and writer must not be created inside the scope of the action that happened to connect');
  assert.match(extension, /import \{[^}]*\boutsideAnyAction\b[^}]*\} from '\.\/modelFence'/);
});

test('J3: the standalone Connections page names the model it shows, as Studio does, so the engine\'s rule for a request that names none does not refuse its own writes', () => {
  const hub = readFileSync(join(root, 'webview', 'src', 'connectionshub.tsx'), 'utf8').replace(/\r\n/g, '\n');
  const standalone = hub.slice(hub.indexOf('export function ConnectionsStandalone('));
  assert.match(standalone, /const \{ session \} = useConnection\(\);\s+setPageModelIdentity\(session\?\.sessionId \? session\.modelIdentity : undefined\);/);
  assert.match(hub, /import \{ setPageModelIdentity \} from '\.\/pagemodel';/);
  // Studio does the same in App.tsx (setDraftModel -> setPageModelIdentity): the two pages that can send a write are the two that name their model
  const app = readFileSync(join(root, 'webview', 'src', 'App.tsx'), 'utf8').replace(/\r\n/g, '\n');
  assert.match(app, /if \(session\?\.sessionId\) setDraftModel\(/);
});
