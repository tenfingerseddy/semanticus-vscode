// Every rpc() / sendRequest() call must send the engine's real signature: the right number of arguments, and
// arguments of the right kind in each position.
//
// UAT 2 Oct (UAT-F-S06b-2-012): Validation > a saved SQL check > "Run this one" failed with
//   "runTests/3 ... origin ... Unexpected token StartArray".
// The page called rpc('runTests', persist, only, sections) but the engine's method is
// runTests(persist, origin, only, sections). The list of ticked ids landed in the `origin` string, and a run of
// one section would have landed in `only`. rpc-name-parity.test.mjs only checks that the NAME exists, and
// the uishot harness answers any call, so nothing in the gates could see a wrong argument shape.
//
// This reads each engine method's parameters out of the C# source and asks the TypeScript checker for the type of
// every argument at every call site. A call fails when it sends too few or too many arguments, or an argument whose
// kind cannot be the parameter's (an array into a string, a string into a bool, a bare null into a bool or a number).
// An argument typed `any` or `unknown` cannot be judged and is counted, never silently passed over.
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const repo = resolve(root, '..');
const req = createRequire(join(root, 'webview', 'package.json'));
const ts = req('typescript');

// ---- the engine's side: every public method's parameters ----------------------------------------------
const TARGETS = ['Semanticus.Engine/EngineRpcTarget.cs', 'Semanticus.Engine/HumanGovernanceRpcTarget.cs'];

/** Split on top-level commas: generics, brackets, parentheses, braces and string literals nest. */
function splitTop(text) {
  const out = [];
  let depth = 0, quote = '', cur = '';
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quote) { cur += c; if (c === '\\') { cur += text[++i] ?? ''; } else if (c === quote) quote = ''; continue; }
    if (c === '"' || c === "'") { quote = c; cur += c; continue; }
    if ('<([{'.includes(c)) depth++;
    if ('>)]}'.includes(c)) depth--;
    if (c === ',' && depth === 0) { out.push(cur); cur = ''; continue; }
    cur += c;
  }
  if (cur.trim()) out.push(cur);
  return out;
}

/** The C# type's kind on the wire. 'other' is a class, record, enum or JsonElement: those accept any JSON value. */
function kindOfCs(type) {
  const t = type.replace(/\s+/g, '').replace(/\?$/, '');
  if (t === 'string') return 'string';
  if (t === 'bool') return 'boolean';
  if (/^(int|long|short|byte|double|float|decimal|uint|ulong|ushort)$/.test(t)) return 'number';
  if (/\[\]$/.test(t) || /^(List|IList|IEnumerable|IReadOnlyList|IReadOnlyCollection|ICollection|HashSet|ISet)</.test(t)) return 'array';
  return 'other';
}

const methods = new Map();   // name -> [{ params: [{ name, type, kind, optional, nullable }], single }]
for (const file of TARGETS) {
  const text = readFileSync(resolve(repo, file), 'utf8');
  const head = /^[ \t]*(\[[^\]\n]*\][ \t]*\n[ \t]*)*public\s+(?:async\s+)?(?:override\s+)?[\w<>[\],.\s?]+?\s+(\w+)\s*\(/gm;
  for (const match of text.matchAll(head)) {
    const open = match.index + match[0].length - 1;
    let depth = 0, close = -1, quote = '';
    for (let i = open; i < text.length; i++) {
      const c = text[i];
      if (quote) { if (c === '\\') i++; else if (c === quote) quote = ''; continue; }
      if (c === '"') { quote = c; continue; }
      if (c === '(') depth++;
      if (c === ')' && --depth === 0) { close = i; break; }
    }
    assert.ok(close > 0, `could not find the end of the parameter list of ${match[2]}`);
    const params = [];
    for (const raw of splitTop(text.slice(open + 1, close))) {
      const part = raw.replace(/^\s*(\[[^\]]*\]\s*)+/, '').replace(/^\s*(params|this|ref|out|in)\s+/, '').trim();
      if (!part) continue;
      const at = part.indexOf('=');
      const decl = (at < 0 ? part : part.slice(0, at)).trim();
      const name = decl.split(/\s+/).pop();
      const type = decl.slice(0, decl.length - name.length).trim();
      if (/^CancellationToken/.test(type)) continue;   // the transport supplies it
      params.push({ name, type, kind: kindOfCs(type), optional: at >= 0, nullable: /\?$/.test(type) || at >= 0 && /^\s*null\b/.test(part.slice(at + 1)) });
    }
    const single = /UseSingleObjectParameterDeserialization\s*=\s*true/.test(match[0]);
    const list = methods.get(match[2]) ?? [];
    list.push({ params, single });
    methods.set(match[2], list);
  }
}
assert.ok(methods.size > 200, `the RPC targets should expose hundreds of methods; parsed ${methods.size}, so the parse is wrong`);
const runTests = methods.get('runTests');
assert.ok(runTests, 'sanity: runTests must be found');
assert.deepEqual(runTests[0].params.map((p) => `${p.name}:${p.kind}`), ['persist:boolean', 'origin:string', 'only:array', 'sections:array'],
  'sanity: runTests parses to its real four parameters');

const signaturesFor = (name) => methods.get(name) ?? methods.get(`${name}Async`)
  ?? (name.endsWith('Async') ? methods.get(name.slice(0, -'Async'.length)) : undefined);

// ---- the caller's side: the type of every argument -----------------------------------------------------
/** The wire kinds an expression's type can be, or null when it cannot be judged ('any', 'unknown', a type parameter). */
function kindsOfType(checker, type) {
  const parts = type.isUnion() ? type.types : [type];
  const kinds = new Set();
  for (const part of parts) {
    const f = part.flags;
    if (f & (ts.TypeFlags.Any | ts.TypeFlags.Unknown | ts.TypeFlags.TypeParameter | ts.TypeFlags.Never)) return null;
    if (f & (ts.TypeFlags.Null | ts.TypeFlags.Undefined | ts.TypeFlags.Void)) kinds.add('nullish');
    else if (f & ts.TypeFlags.StringLike) kinds.add('string');
    else if (f & (ts.TypeFlags.NumberLike | ts.TypeFlags.BigIntLike)) kinds.add('number');
    else if (f & ts.TypeFlags.BooleanLike) kinds.add('boolean');
    else if (checker.isArrayType(part) || checker.isTupleType(part)) kinds.add('array');
    else if (f & ts.TypeFlags.Object || f & ts.TypeFlags.Intersection) kinds.add('object');
    else return null;
  }
  return kinds;
}

const compatible = (kind, param) => {
  if (kind === 'nullish') return param.kind === 'string' || param.kind === 'array' || param.kind === 'other' || param.nullable;
  if (param.kind === 'other') return true;
  if (kind === 'object') return false;
  return kind === param.kind;
};

function programFor(tsconfigPath) {
  const cfg = ts.readConfigFile(tsconfigPath, ts.sys.readFile);
  const parsed = ts.parseJsonConfigFileContent(cfg.config, ts.sys, dirname(tsconfigPath));
  return ts.createProgram(parsed.fileNames, { ...parsed.options, noEmit: true });
}

const problems = [];
let checked = 0, unjudged = 0, skippedSingle = 0, dynamic = 0;
const seen = new Set();

function scan(tsconfig, calleeKind) {
  const program = programFor(tsconfig);
  const checker = program.getTypeChecker();
  for (const sf of program.getSourceFiles()) {
    if (sf.isDeclarationFile || sf.fileName.includes('node_modules')) continue;
    const visit = (node) => {
      if (ts.isCallExpression(node)) {
        const callee = node.expression;
        const isRpc = calleeKind === 'rpc'
          ? ts.isIdentifier(callee) && callee.text === 'rpc'
          : ts.isPropertyAccessExpression(callee) && callee.name.text === 'sendRequest';
        if (isRpc && node.arguments.length > 0) {
          const first = node.arguments[0];
          const where = `${relative(repo, sf.fileName)}:${sf.getLineAndCharacterOfPosition(node.getStart()).line + 1}`;
          if (!ts.isStringLiteralLike(first)) { dynamic++; }
          else {
            const name = first.text;
            let args = [...node.arguments].slice(1);
            // sendRequest(name, ParameterStructures.byPosition, ...): the structure flag is not an argument.
            if (args[0] && /ParameterStructures\./.test(args[0].getText())) args = args.slice(1);
            const sigs = signaturesFor(name);
            if (!sigs) { /* a missing name is rpc-name-parity's finding, not this test's */ }
            else if (args.some((a) => ts.isSpreadElement(a))) { dynamic++; }
            else if (sigs.every((s) => s.single)) { skippedSingle++; }
            else {
              checked++;
              const failures = sigs.map((sig) => {
                const required = sig.params.filter((p) => !p.optional).length;
                if (args.length < required) return `sends ${args.length} argument(s), the engine needs at least ${required} (${sig.params.map((p) => p.name).join(', ')})`;
                if (args.length > sig.params.length) return `sends ${args.length} argument(s), the engine takes at most ${sig.params.length} (${sig.params.map((p) => p.name).join(', ')})`;
                for (let i = 0; i < args.length; i++) {
                  const kinds = kindsOfType(checker, checker.getTypeAtLocation(args[i]));
                  if (kinds === null) { unjudged++; continue; }
                  const bad = [...kinds].filter((k) => !compatible(k, sig.params[i]));
                  if (bad.length) return `argument ${i + 1} is ${[...kinds].join(' | ')} (${args[i].getText().slice(0, 60)}) but the engine's ${sig.params[i].name} is ${sig.params[i].type}`;
                }
                return null;
              });
              if (failures.every((f) => f !== null)) {
                const key = `${name}@${where}`;
                if (!seen.has(key)) { seen.add(key); problems.push(`  ${name} (${where}): ${failures[0]}`); }
              }
            }
          }
        }
      }
      ts.forEachChild(node, visit);
    };
    visit(sf);
  }
}

const webviewConfig = resolve(root, 'webview', 'tsconfig.json');
const hostConfig = resolve(root, 'tsconfig.json');
assert.ok(existsSync(webviewConfig) && existsSync(hostConfig), 'both tsconfig files must exist');
scan(webviewConfig, 'rpc');
scan(hostConfig, 'sendRequest');

assert.ok(checked > 400, `expected hundreds of call sites to be checked; checked ${checked}, so the walk is wrong`);

assert.equal(problems.length, 0,
  `these calls do not match the engine's method, so they fail at run time (or send a value into the wrong parameter):\n${problems.join('\n')}\n`
  + 'Fix the caller to send the engine\'s real signature. Do not relax this test.');

console.log(`rpc argument parity: ${checked} call sites checked against ${methods.size} engine methods; `
  + `${unjudged} argument(s) typed any/unknown could not be judged; ${skippedSingle} single-object call(s) and ${dynamic} dynamic or spread call(s) were not checked`);
