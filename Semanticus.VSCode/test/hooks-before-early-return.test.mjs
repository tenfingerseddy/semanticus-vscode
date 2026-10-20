// A component must call every hook on every render. A `return` that can run before a later hook changes the hook count
// between renders, and React stops with error #310 ("Rendered more hooks than during the previous render"). In the
// Studio that blanks the whole webview. The Connections hub did exactly this: `if (!shown) return null;` sat above
// the useState/useRef that follow it, so pressing the header's Connections button (shown flips to true) blanked the Studio.
//
// This sweeps EVERY .ts/.tsx file under webview/src: in each function, no return statement of that function may come
// before the last hook call of that function. It reads the source with the TypeScript parser, so it takes about a second.
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const src = join(root, 'webview', 'src');
const ts = createRequire(join(root, 'webview', 'package.json'))('typescript');

const isFn = (n) => ts.isFunctionDeclaration(n) || ts.isFunctionExpression(n) || ts.isArrowFunction(n) || ts.isMethodDeclaration(n);
const isHookCall = (n) => ts.isCallExpression(n) && (
  (ts.isIdentifier(n.expression) && /^use[A-Z0-9]/.test(n.expression.text)) ||
  (ts.isPropertyAccessExpression(n.expression) && /^use[A-Z0-9]/.test(n.expression.name.text)));
// A hook is only a hook where a component calls it while rendering. A handler named useSomething is not.
const isHookDefinitionOrComponent = (fn) => {
  const name = fn.name?.text ?? (ts.isVariableDeclaration(fn.parent) && ts.isIdentifier(fn.parent.name) ? fn.parent.name.text : '');
  return /^[A-Z]/.test(name) || /^use[A-Z0-9]/.test(name) || (ts.isCallExpression(fn.parent) && /forwardRef|memo/.test(fn.parent.expression.getText()));
};

export function earlyReturnsBeforeHooks(text, fileName = 'x.tsx') {
  const sf = ts.createSourceFile(fileName, text, ts.ScriptTarget.ES2022, true, fileName.endsWith('x') ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
  const found = [];
  const visitFn = (fn) => {
    const hooks = [], returns = [];
    const walk = (n) => {
      if (n !== fn && isFn(n)) { visitFn(n); return; }
      if (n !== fn && isHookCall(n)) hooks.push(n);
      if (ts.isReturnStatement(n)) returns.push(n);
      ts.forEachChild(n, walk);
    };
    ts.forEachChild(fn, walk);
    if (isHookDefinitionOrComponent(fn) && hooks.length) {
      const lastHook = hooks.reduce((a, b) => (b.pos > a.pos ? b : a));
      for (const r of returns) {
        if (r.end <= lastHook.pos) {
          const at = (n) => sf.getLineAndCharacterOfPosition(n.getStart(sf)).line + 1;
          found.push(`${fileName}:${at(r)} returns before the hook at line ${at(lastHook)} (${lastHook.expression.getText(sf)})`);
        }
      }
    }
  };
  const top = (n) => { if (isFn(n)) visitFn(n); else ts.forEachChild(n, top); };
  ts.forEachChild(sf, top);
  return found;
}

// The detector itself catches the shape that blanked the Studio, and passes the safe shape.
assert.equal(earlyReturnsBeforeHooks('function Hub({shown}){ const [a] = useState(0); if(!shown) return null; const r = useRef(null); return a; }').length, 1,
  'the detector must flag a return that sits above a later hook');
assert.equal(earlyReturnsBeforeHooks('function Hub({shown}){ const [a] = useState(0); const r = useRef(null); if(!shown) return null; return a; }').length, 0,
  'a return below every hook is fine');
assert.equal(earlyReturnsBeforeHooks('function Hub(){ const f = () => { return 1; }; const [a] = useState(0); return a; }').length, 0,
  'a return inside a nested handler is not a render return');

const files = (dir) => readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
  e.isDirectory() ? files(join(dir, e.name)) : /\.(ts|tsx)$/.test(e.name) && !/\.d\.ts$/.test(e.name) ? [join(dir, e.name)] : []);
const problems = files(src).flatMap((f) => earlyReturnsBeforeHooks(readFileSync(f, 'utf8'), f.slice(root.length + 1)));
assert.deepEqual(problems, [], 'a return that can run before a later hook blanks the Studio (React error #310):\n  ' + problems.join('\n  '));
console.log('hooks-before-early-return: ok');
