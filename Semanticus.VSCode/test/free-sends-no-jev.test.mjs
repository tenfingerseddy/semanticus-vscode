// H1 (completeness audit, 27 Sep): on Free, the webview sends zero askData and zero listDataQuestions calls, and
// every other webview call that can reach Jev is held by an engine gate that a named test proves.
//
// Why this file exists: the only check that Free makes no askData call was scene 11 of tools/uishot/askdata-drive.mjs,
// which needs a browser and never runs in CI. npm test runs this file in CI.
//
// What each check proves, honestly:
//   1. THE ENGINE-GATED CALLERS. Every caller of composeTask, editDraft, describeWorkflowSteps, routeAsk and
//      getModelPlan in webview/src is found by reading the source, and each must be on the list in section 3 for its
//      method. A caller is read four ways (m11/completeness D3, so a name that is only data is not a call, and a real
//      call is not missed):
//        a. a file that sends (calls rpc or postMessage) and quotes the method name anywhere;
//        b. a file that binds the name to a const and uses that const again;
//        c. a file that imports, from a file in webview/src that does not send on its own, a name bound to the method
//           or an exported function whose text uses it (a namespace or default import counts as taking everything);
//        d. anything else only holds the name as data and is not a caller.
//      Its limits: the reading goes one import deep, and a data file that exported a list or a map that a sender
//      indexed to call the method would be missed. Nothing in webview/src does either today.
//   2. ASK YOUR DATA: WHAT FREE CAN REACH, NOT WHICH FILE (m11/completeness R1). The engine refuses askData and
//      listDataQuestions on Free too (RequireProFeature, m11/askdata), but H1 promises more: Free does not even send
//      the call, so no path Free can run in the page may reach them. The rule is not a list of
//      allowed files; it is that every path from the page's entry points to a data call passes a Pro gate. It is read
//      with the TypeScript parser (no type checker), in four steps:
//        a. A METHOD VALUE is a quoted data method name, or a const bound to one (local, or imported from another
//           webview file through any number of re-exports). Not a use: the argument of a grant read (pro.tsx's
//           useFeature), anything in a type, and the binding's own declaration. Every other quoted name counts
//           wherever it sits, in a file that sends or not (a map or a ternary later handed to rpc is still caught, and
//           so is a map or a list of method names in a data file that a free page indexes, verifier round 3 L1), except
//           at the top level of features.ts when it neither sends nor imports the bridge, whose lists name the Pro
//           feature askData as data.
//           A top-level const whose value is an object literal is read member by member (`META.title` does not reach
//           through `META.feature`), and used whole or indexed it reaches when any member does.
//        b. REACH. A named function (a declaration, a const arrow or function, or a useCallback of one) or a class
//           reaches when its body holds an UNHELD use of a method value or of anything that reaches, worked to a fixed
//           point across files through imports and re-exports. A const that is not a function reaches when its value
//           does. A function that returns an object literal is read member by member: `const v = useX(...)` makes v
//           a carrier, `v.m` reaches only when member m reaches, and v used whole (`ask={ask}`, passed on, spread)
//           reaches when any member does. Such a function reaches ON ITS OWN only through code outside its returned
//           members (its effects, its top level, callbacks it hands to other functions). A closure handed to a
//           function in webview/src counts as called unless that function's every use of the parameter is held
//           (followed through `useRef(p)` and `r.current = p`): useConsentCard runs its re-ask only after
//           `if (!missing || tier !== 'pro') return;`. A closure handed to anything else (useEffect, a JSX handler,
//           a promise) counts as called. A dynamic import counts as taking every export.
//        c. HELD. A use is held when it sits on the side of a condition that implies Pro: `g === 'granted'` (or g not
//           'denied' and not 'unknown') for g = useFeature(<a Pro feature from features.ts>), `t === 'pro'` for
//           t = useTier(), through `!`, `&&`, `||`, ternaries, if and else, the statements after
//           `if (<not Pro>) return;` in the same block, and consts whose value is such a condition (askOn, chipsHere,
//           askOwns). App's featureGrant is useFeature(featureOfTool(tab)), the grant of the CURRENT tab, so it holds a
//           use only together with `tab === '<id>'` for a tool whose feature in FEATURE_OF_TOOL is a Pro feature (the
//           Ask your data tab); Queries is a free tab and its mount is not held by it. `const askOn = true` is not a
//           gate. Code under a condition that can never be true (`false && ...`) is held, because it never runs.
//        d. THE RULE. The entry points are every file's top-level code (main.tsx renders App from there). No entry
//           point may reach a data method unheld; a failure names each step from the entry to the method, with its
//           file and line.
//      Its limits, plainly: a prop is never followed into the child, so passing a carrier or a reaching callback as a
//      prop counts where it is passed even when the child gates it (the safe direction); a switch, a loop or a
//      variable that is assigned again is not read as a gate; a Free page that indexes features.ts's own lists for a
//      method name (they hold the Pro feature id askData, which is also the method's name) is not seen; an array of
//      objects that names the method in one member is read whole (`META[0].title` counts as a use, the safe direction); a method name
//      built at run time from pieces, a value
//      reached through a JavaScript `this` or a computed property, or a callback stored by a function outside
//      webview/src and run later from elsewhere is not seen; nothing here checks the page's syntax (the webview build
//      does, and a file the parser had to recover is named on the summary line).
//   3. THE ENGINE GATES. The engine-gated callers can press a button on Free, but the engine refuses before any Jev
//      request (RequireProFeature, then the registry's ProOnly and the central refusal). Each gate is named with the
//      C# test that proves it sends nothing and with the files that call it today; this file checks those tests exist,
//      so the link cannot rot silently, and fails on a caller in any other file.
// There is no DOM in this suite (no jsdom), so a real click on Free is not driven here. askdata-drive.mjs scene 11 does
// that in a browser, and stays the stronger proof of the page itself.
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join, relative, resolve, sep, posix } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
// The parser. A copy of this file run from a scratch folder (test/completeness-free-guard*.test.mjs do that) has no
// node_modules above it, so it also looks from the working directory, which is where npm test and CI start it.
const ts = (() => {
  for (const from of [fileURLToPath(import.meta.url), join(process.cwd(), 'x.js'), join(process.cwd(), 'Semanticus.VSCode', 'x.js')])
    try { return createRequire(from)('typescript'); } catch { /* the next place */ }
  throw new Error('free-sends-no-jev needs the typescript package (a devDependency of Semanticus.VSCode); run npm install');
})();
const src = join(root, 'webview', 'src');
const tests = join(root, '..', 'Semanticus.Tests');

const DATA = ['askData', 'listDataQuestions'];
// method -> the engine gate that refuses Free before Jev, the C# test(s) that prove zero requests, and every file that
// calls it today. A caller in any other file fails until it is added here, with what holds Free out of it.
const ENGINE_GATED = {
  composeTask: ['RequireProFeature (DraftRunner)', ['JevDraftBuilderTests.cs', 'Free_asks_nothing_and_counts_nothing'],
    ['askbox.tsx', 'plan.tsx', 'workflows.tsx']],
  editDraft: ['RequireProFeature (DraftRunner.EditDraftAsync)', ['JevDraftBuilderTests.cs', 'A_refine_after_Pro_ends_asks_nothing_and_counts_nothing'],
    ['composer-draft.tsx', 'workflows.tsx']],
  describeWorkflowSteps: ['RequireProFeature (DescribedSteps)', ['JevDraftBuilderTests.cs', 'Free_asks_nothing_and_counts_nothing'],
    ['workflows.tsx']],
  routeAsk: ['RequireProFeature (LocalEngine.Plan)', ['AskRouteTests.cs', 'A_free_session_is_refused_before_any_word_is_matched'],
    ['askbox.tsx']],
  getModelPlan: ['plan.rank is Pro only', ['ModelPlanCacheTests.cs', 'A_free_plan_starts_no_ranking_run_and_says_the_order_is_Pro'],
    ['App.tsx', 'plan.tsx', 'sendtask.tsx', 'studio/suggest.tsx', 'workflowdesign.tsx']],
};
const GATED = Object.keys(ENGINE_GATED);

function walk(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? walk(join(dir, e.name)) : /\.(tsx?|mjs)$/.test(e.name) ? [join(dir, e.name)] : []);
}
const files = walk(src).map((f) => ({ f, rel: relative(src, f).split(sep).join('/'), text: readFileSync(f, 'utf8') }));

// 1. Every engine-gated caller, found by reading (a to d in the header).
const quotes = (text, m) => new RegExp(`['"\`]${m}['"\`]`).test(text);
const sends = (text) => /\brpc\s*(?:<[^()]*?>)?\s*\(|\bpostMessage\s*\(/.test(text);
const esc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const count = (text, re) => [...text.matchAll(re)].length;
const keyOf = (rel) => rel.replace(/\.(tsx?|mjs)$/, '');
const byModule = new Map(files.map((x) => [keyOf(x.rel), x]));
// Names a file binds straight to a method: `const ASK_METHOD = 'routeAsk'`, exported or not.
function bindings(text, m) {
  return [...text.matchAll(new RegExp(`\\bconst\\s+(\\w+)\\s*(?::[^=\\n]+)?=\\s*['"\`]${m}['"\`]`, 'g'))].map((b) => b[1]);
}
// The file's own use of the method: the name quoted in a file that sends, or a bound name used past its binding.
function usesDirectly(text, m) {
  if (quotes(text, m) && sends(text)) return true;
  return bindings(text, m).some((n) => count(text, new RegExp(`\\b${esc(n)}\\b`, 'g')) > 1);
}
// A top-level declaration's text: from its line to the next line that starts a top-level declaration.
function exportedBodies(text) {
  const lines = text.split(/\r?\n/);
  const out = new Map();
  for (let i = 0; i < lines.length; i++) {
    // Functions only: an exported function, or a const holding an arrow or function expression. A list or a map
    // that holds the name is data.
    const d = /^export\s+(?:default\s+)?(?:async\s+)?function\*?\s+(\w+)/.exec(lines[i])
      ?? /^export\s+const\s+(\w+)\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*(?::[^=]+)?=>|\w+\s*=>)/.exec(lines[i]);
    if (!d) continue;
    let j = i + 1;
    while (j < lines.length && !/^(?:export|function|async|const|let|var|class|type|interface|import)\b/.test(lines[j])) j++;
    out.set(d[1], lines.slice(i, j).join('\n'));
  }
  return out;
}
// Exported names of a file that reach the method: a name bound to it, or an exported function whose text uses the
// quoted name or a bound name. A file that sends on its own is already a caller in its own right (1a), and importing a
// component from it adds no call site, so only a file that does not send is followed.
function reaching(x, m) {
  if (sends(x.text)) return new Set();
  const names = new Set(bindings(x.text, m));
  const refs = [...names].map((n) => new RegExp(`\\b${esc(n)}\\b`));
  for (const [name, body] of exportedBodies(x.text))
    if (quotes(body, m) || refs.some((re) => re.test(body))) names.add(name);
  return names;
}
function importsReaching(text, m) {
  for (const im of text.matchAll(/import\s+(type\s+)?([\s\S]*?)\s+from\s+['"]\.\/([\w./-]+)['"]/g)) {
    if (im[1]) continue;   // a type-only import carries no value
    const mod = byModule.get(im[3].replace(/\.(tsx?|mjs|js)$/, ''));
    if (!mod) continue;
    const reach = reaching(mod, m);
    if (reach.size === 0) continue;
    const clause = im[2];
    if (/\*\s+as\s+\w+/.test(clause) || /^\w+/.test(clause.trim())) return true;   // a namespace or default import takes everything
    const named = (/\{([\s\S]*)\}/.exec(clause)?.[1] ?? '').split(',').map((n) => n.trim().replace(/^type\s+/, '').split(/\s+as\s+/)[0]).filter(Boolean);
    if (named.some((n) => reach.has(n))) return true;
  }
  return false;
}
const callers = new Map(GATED.map((m) => [m, new Set()]));
const dataOnly = new Map(GATED.map((m) => [m, new Set()]));
for (const { rel, text } of files)
  for (const m of GATED) {
    if (usesDirectly(text, m) || importsReaching(text, m)) callers.get(m).add(rel);
    else if (quotes(text, m)) dataOnly.get(m).add(rel);
  }

// 2. Ask your data: no entry point reaches askData or listDataQuestions without passing a Pro gate.
const K = ts.SyntaxKind;
const isFn = (n) => ts.isFunctionDeclaration(n) || ts.isFunctionExpression(n) || ts.isArrowFunction(n) || ts.isMethodDeclaration(n)
  || ts.isConstructorDeclaration(n) || ts.isGetAccessorDeclaration(n) || ts.isSetAccessorDeclaration(n);
const isScope = (n) => ts.isSourceFile(n) || isFn(n) || ts.isBlock(n) || ts.isForStatement(n) || ts.isForOfStatement(n)
  || ts.isForInStatement(n) || ts.isCatchClause(n) || ts.isCaseBlock(n);
function strip(e) {
  while (e && (ts.isParenthesizedExpression(e) || ts.isAsExpression(e) || ts.isSatisfiesExpression(e) || ts.isNonNullExpression(e)
    || ts.isTypeAssertionExpression(e))) e = e.expression;
  return e;
}
const isConst = (d) => ts.isVariableDeclarationList(d.parent) && (d.parent.flags & ts.NodeFlags.Const) !== 0;
const propText = (n) => (ts.isIdentifier(n) || ts.isStringLiteralLike(n) || ts.isNumericLiteral(n) ? n.text : '*');
const scriptKind = (rel) => (rel.endsWith('.tsx') ? ts.ScriptKind.TSX : rel.endsWith('.mjs') ? ts.ScriptKind.JS : ts.ScriptKind.TS);

const mods = new Map();
for (const x of files) {
  const sf = ts.createSourceFile(x.rel, x.text, ts.ScriptTarget.Latest, true, scriptKind(x.rel));
  const mod = { rel: x.rel, sf, sends: sends(x.text), scopes: new Map(), exports: new Map(), stars: [], declOf: new Map() };
  mod.top = { kind: 'top', mod, name: `${x.rel} (top-level code)`, events: [], memberEvents: new Map() };
  mods.set(keyOf(x.rel), mod);
}
function specOf(mod, spec) {
  if (!spec.startsWith('.')) return null;
  const k = keyOf(posix.normalize(posix.join(posix.dirname(mod.rel), spec)).replace(/\.js$/, ''));
  return mods.has(k) ? k : null;
}
const containers = new Map();   // the node whose body a binding owns -> that binding
const returnObjects = new Map(); // an object literal a function returns -> that function's node
const allBindings = [];

// Declarations, scope by scope.
function declare(mod, scope, nameNode, make, pattern = null) {
  if (ts.isIdentifier(nameNode)) {
    let m = mod.scopes.get(scope);
    if (!m) mod.scopes.set(scope, (m = new Map()));
    const b = { mod, name: nameNode.text, nameNode, events: [], memberEvents: new Map(), initEvents: [], ...make(nameNode, pattern) };
    m.set(b.name, b);
    allBindings.push(b);
    return;
  }
  for (const el of nameNode.elements)
    if (!ts.isOmittedExpression(el)) declare(mod, scope, el.name, make, { el, top: el.parent === pattern?.root || pattern === null, root: pattern?.root ?? nameNode });
}
const scopeAbove = (n) => { for (let p = n.parent; p; p = p.parent) if (isScope(p)) return p; return null; };
const fnScopeAbove = (n) => { for (let p = n.parent; p; p = p.parent) if (isFn(p) || ts.isSourceFile(p)) return p; return null; };
function objectReturns(F) {
  if (ts.isArrowFunction(F) && !ts.isBlock(F.body)) { const e = strip(F.body); return ts.isObjectLiteralExpression(e) ? [e] : null; }
  if (!F.body) return null;
  const out = []; let ok = true;
  const v = (n) => {
    if (isFn(n) || ts.isClassLike(n)) return;
    if (ts.isReturnStatement(n)) { const e = n.expression && strip(n.expression); if (e && ts.isObjectLiteralExpression(e)) out.push(e); else ok = false; }
    ts.forEachChild(n, v);
  };
  ts.forEachChild(F.body, v);
  return ok && out.length ? out : null;
}
function ownFunction(b, F) {
  b.fnNode = F;
  containers.set(F, b);
  const objs = objectReturns(F);
  if (objs) { b.hasMembers = true; for (const o of objs) returnObjects.set(o, F); }
}
for (const mod of mods.values()) {
  const visit = (n) => {
    if (ts.isVariableDeclaration(n)) {
      const list = n.parent;
      const scope = ts.isCatchClause(list) ? list
        : ts.isVariableDeclarationList(list) && (list.flags & (ts.NodeFlags.Let | ts.NodeFlags.Const)) === 0 ? fnScopeAbove(n) : scopeAbove(n);
      const top = ts.isVariableDeclarationList(list) && ts.isVariableStatement(list.parent) && ts.isSourceFile(list.parent.parent);
      const init = n.initializer && strip(n.initializer);
      declare(mod, scope, n.name, (id, pat) => {
        if (pat) {
          const prop = !pat.top || pat.el.dotDotDotToken ? '*' : pat.el.propertyName ? propText(pat.el.propertyName) : id.text;
          return { kind: 'part', decl: n, prop, top };
        }
        if (init && (ts.isArrowFunction(init) || ts.isFunctionExpression(init))) return { kind: 'fn', decl: n, fnInit: init, top };
        if (init && ts.isCallExpression(init) && ts.isIdentifier(init.expression) && init.expression.text === 'useCallback'
          && init.arguments[0] && isFn(strip(init.arguments[0]))) return { kind: 'fn', decl: n, fnInit: strip(init.arguments[0]), top };
        if (init && ts.isStringLiteralLike(init) && DATA.includes(init.text)) return { kind: 'method', decl: n, method: init.text, top };
        return { kind: 'value', decl: n, top };
      });
    } else if (ts.isParameter(n)) {
      declare(mod, n.parent, n.name, () => ({ kind: 'param', decl: n }));
    } else if ((ts.isFunctionDeclaration(n) || ts.isClassDeclaration(n) || ts.isEnumDeclaration(n)) && n.name) {
      declare(mod, scopeAbove(n), n.name, () => ({ kind: ts.isFunctionDeclaration(n) ? 'fn' : ts.isClassDeclaration(n) ? 'class' : 'other', decl: n }));
    } else if (ts.isFunctionExpression(n) && n.name) {
      declare(mod, n, n.name, () => ({ kind: 'other', decl: n }));
    } else if (ts.isImportDeclaration(n) && n.importClause && !n.importClause.isTypeOnly && ts.isStringLiteral(n.moduleSpecifier)) {
      const from = specOf(mod, n.moduleSpecifier.text);
      const c = n.importClause;
      if (c.name) declare(mod, mod.sf, c.name, () => ({ kind: 'import', from, importName: 'default', decl: n }));
      if (c.namedBindings && ts.isNamespaceImport(c.namedBindings)) declare(mod, mod.sf, c.namedBindings.name, () => ({ kind: 'ns', module: from, decl: n }));
      else if (c.namedBindings) for (const el of c.namedBindings.elements)
        if (!el.isTypeOnly) declare(mod, mod.sf, el.name, () => ({ kind: 'import', from, importName: (el.propertyName ?? el.name).text, decl: n }));
    } else if (ts.isExportDeclaration(n) && !n.isTypeOnly) {
      const from = n.moduleSpecifier && ts.isStringLiteral(n.moduleSpecifier) ? specOf(mod, n.moduleSpecifier.text) : undefined;
      if (!n.exportClause) { if (from) mod.stars.push(from); }
      else if (ts.isNamespaceExport(n.exportClause)) { if (from) mod.exports.set(n.exportClause.name.text, { ns: from }); }
      else for (const el of n.exportClause.elements) {
        if (el.isTypeOnly) continue;
        const inner = (el.propertyName ?? el.name).text;
        mod.exports.set(el.name.text, from !== undefined ? { from, name: inner } : { local: inner });
      }
    } else if (ts.isExportAssignment(n) && ts.isIdentifier(strip(n.expression))) {
      mod.exports.set('default', { local: strip(n.expression).text });
    }
    ts.forEachChild(n, visit);
  };
  visit(mod.sf);
  // What the file exports by declaration, and which nodes each binding owns.
  for (const b of allBindings.filter((x) => x.mod === mod)) {
    const d = b.decl;
    if (b.kind === 'fn') ownFunction(b, b.fnInit ?? d);
    else if (b.kind === 'class') containers.set(d, b);
    else if ((b.kind === 'value' || b.kind === 'method') && b.top) containers.set(d, b);
    const stmt = ts.isVariableDeclaration(d) ? d.parent.parent : d;
    const mods_ = ts.canHaveModifiers(stmt) ? ts.getModifiers(stmt) ?? [] : [];
    if (b.kind !== 'param' && b.kind !== 'import' && b.kind !== 'ns' && mods_.some((m) => m.kind === K.ExportKeyword)
      && (ts.isVariableDeclaration(d) ? ts.isSourceFile(d.parent.parent.parent) : ts.isSourceFile(d.parent)))
      mod.exports.set(mods_.some((m) => m.kind === K.DefaultKeyword) ? 'default' : b.name, { binding: b });
  }
}
function lookup(mod, id) {
  for (let p = id.parent; p; p = p.parent) { const b = mod.scopes.get(p)?.get(id.text); if (b) return b; }
  return null;
}
function resolveExport(key, name, seen = new Set()) {
  const mod = key && mods.get(key);
  if (!mod || seen.has(`${key}:${name}`)) return null;
  seen.add(`${key}:${name}`);
  const e = mod.exports.get(name);
  if (e?.binding) return resolveBinding(e.binding, seen);
  if (e?.local) { const b = mod.scopes.get(mod.sf)?.get(e.local); return b ? resolveBinding(b, seen) : null; }
  if (e?.from !== undefined) return resolveExport(e.from, e.name, seen);
  if (e?.ns) return { kind: 'ns', module: e.ns, name: name };
  for (const s of mod.stars) { const r = resolveExport(s, name, seen); if (r) return r; }
  return null;
}
function resolveBinding(b, seen = new Set()) {
  if (!b) return null;
  if (b.kind === 'import') return resolveExport(b.from, b.importName, seen);
  return b;
}
function exportNames(key, seen = new Set()) {
  const mod = key && mods.get(key);
  if (!mod || seen.has(key)) return [];
  seen.add(key);
  return [...mod.exports.keys(), ...mod.stars.flatMap((s) => exportNames(s, seen))];
}
function calleeOf(mod, call) {
  const e = strip(call.expression);
  if (ts.isIdentifier(e)) return resolveBinding(lookup(mod, e));
  if (ts.isPropertyAccessExpression(e) && ts.isIdentifier(e.expression)) {
    const ns = resolveBinding(lookup(mod, e.expression));
    if (ns?.kind === 'ns') return resolveExport(ns.module, e.name.text);
  }
  return null;
}
const isFrom = (b, rel, name) => b && b.mod?.rel === rel && b.name === name;

// The Pro line, read from features.ts: which features are Pro, and which tools belong to one.
const PRO = new Set();
const PRO_TOOLS = new Set();
{
  const f = mods.get('features');
  const init = (name) => { const b = f?.scopes.get(f.sf)?.get(name); return b?.decl?.initializer && strip(b.decl.initializer); };
  const list = init('PRO_FEATURES');
  if (list && ts.isArrayLiteralExpression(list)) for (const e of list.elements) if (ts.isStringLiteralLike(e)) PRO.add(e.text);
  const map = init('FEATURE_OF_TOOL');
  if (map && ts.isObjectLiteralExpression(map)) for (const p of map.properties)
    if (ts.isPropertyAssignment(p) && ts.isStringLiteralLike(strip(p.initializer)) && PRO.has(strip(p.initializer).text)) PRO_TOOLS.add(propText(p.name));
}
// What a grant variable is: useTier(), useFeature(<a Pro feature>), or App's useFeature(featureOfTool(tab)).
function featureArg(mod, a, depth = 0) {
  a = a && strip(a);
  if (!a || depth > 4) return null;
  if (ts.isStringLiteralLike(a)) return PRO.has(a.text) ? { kind: 'pro' } : null;
  if (ts.isIdentifier(a)) {
    const b = resolveBinding(lookup(mod, a));
    if (b?.kind === 'method') return PRO.has(b.method) ? { kind: 'pro' } : null;
    return b?.decl && ts.isVariableDeclaration(b.decl) && isConst(b.decl) && ts.isIdentifier(b.decl.name) ? featureArg(b.mod, b.decl.initializer, depth + 1) : null;
  }
  if (ts.isCallExpression(a) && isFrom(calleeOf(mod, a), 'features.ts', 'featureOfTool') && a.arguments[0] && ts.isIdentifier(strip(a.arguments[0])))
    return { kind: 'tool', tool: lookup(mod, strip(a.arguments[0])) };
  return null;
}
const grants = new Map();
function grantOf(b) {
  if (grants.has(b)) return grants.get(b);
  let g = null;
  const d = b?.decl;
  if (d && ts.isVariableDeclaration(d) && isConst(d) && ts.isIdentifier(d.name) && d.initializer && ts.isCallExpression(strip(d.initializer))) {
    const call = strip(d.initializer);
    const cb = calleeOf(b.mod, call);
    if (isFrom(cb, 'pro.tsx', 'useTier')) g = { kind: 'tier' };
    else if (isFrom(cb, 'pro.tsx', 'useFeature')) g = featureArg(b.mod, call.arguments[0]);
  }
  grants.set(b, g);
  return g;
}
// A condition, known true or false where a node sits, broken into facts `x === 'lit'` / `x !== 'lit'`.
function atomsOf(mod, facts) {
  const atoms = []; let never = false;
  const expand = (e, v, depth) => {
    e = strip(e);
    if (!e || depth > 8) return;
    if (e.kind === K.TrueKeyword) { if (!v) never = true; return; }
    if (e.kind === K.FalseKeyword) { if (v) never = true; return; }
    if (ts.isPrefixUnaryExpression(e) && e.operator === K.ExclamationToken) return expand(e.operand, !v, depth);
    if (ts.isBinaryExpression(e)) {
      const k = e.operatorToken.kind;
      if (k === K.AmpersandAmpersandToken) { if (v) { expand(e.left, true, depth); expand(e.right, true, depth); } return; }
      if (k === K.BarBarToken) { if (!v) { expand(e.left, false, depth); expand(e.right, false, depth); } return; }
      if ([K.EqualsEqualsEqualsToken, K.EqualsEqualsToken, K.ExclamationEqualsEqualsToken, K.ExclamationEqualsToken].includes(k)) {
        const neg = k === K.ExclamationEqualsEqualsToken || k === K.ExclamationEqualsToken;
        const [x, lit] = ts.isStringLiteralLike(strip(e.right)) ? [strip(e.left), strip(e.right)] : ts.isStringLiteralLike(strip(e.left)) ? [strip(e.right), strip(e.left)] : [];
        if (x && ts.isIdentifier(x)) { const b = lookup(mod, x); if (b) atoms.push({ b, eq: v !== neg, lit: lit.text }); }
      }
      return;
    }
    if (ts.isIdentifier(e)) {
      const b = lookup(mod, e);
      if (b?.decl && ts.isVariableDeclaration(b.decl) && isConst(b.decl) && ts.isIdentifier(b.decl.name) && b.decl.initializer) expand(b.decl.initializer, v, depth + 1);
    }
  };
  for (const [e, v] of facts) expand(e, v, 0);
  return { atoms, never };
}
function impliesPro(mod, facts) {
  const { atoms, never } = atomsOf(mod, facts);
  if (never) return true;
  const has = (b, eq, lit) => atoms.some((a) => a.b === b && a.eq === eq && a.lit === lit);
  const granted = (b) => has(b, true, 'granted') || (has(b, false, 'denied') && has(b, false, 'unknown'));
  for (const a of atoms) {
    const g = grantOf(a.b);
    if (!g) continue;
    if (g.kind === 'tier' && has(a.b, true, 'pro')) return true;
    if (g.kind === 'pro' && granted(a.b)) return true;
    if (g.kind === 'tool' && granted(a.b) && atoms.some((t) => t.b === g.tool && t.eq && PRO_TOOLS.has(t.lit))) return true;
  }
  return false;
}
const exits = (s) => ts.isReturnStatement(s) || ts.isThrowStatement(s) || (ts.isBlock(s) && s.statements.length > 0 && exits(s.statements.at(-1)));
// Held: the conditions on the way from the node up to its owner imply Pro.
function held(mod, node, boundary) {
  const facts = [];
  for (let c = node; c !== boundary && c.parent; c = c.parent) {
    const p = c.parent;
    if (ts.isConditionalExpression(p)) { if (c === p.whenTrue) facts.push([p.condition, true]); else if (c === p.whenFalse) facts.push([p.condition, false]); }
    else if (ts.isBinaryExpression(p) && c === p.right) {
      if (p.operatorToken.kind === K.AmpersandAmpersandToken) facts.push([p.left, true]);
      else if (p.operatorToken.kind === K.BarBarToken) facts.push([p.left, false]);
    } else if (ts.isIfStatement(p)) { if (c === p.thenStatement) facts.push([p.expression, true]); else if (c === p.elseStatement) facts.push([p.expression, false]); }
    if (ts.isBlock(p) || ts.isSourceFile(p) || ts.isCaseClause(p) || ts.isDefaultClause(p))
      for (const s of p.statements) { if (s === c) break; if (ts.isIfStatement(s) && !s.elseStatement && exits(s.thenStatement)) facts.push([s.expression, false]); }
  }
  return impliesPro(mod, facts);
}
// Every reference to a binding in its own file, for following a parameter.
const refsOf = new Map();
// Does the function hold every use of its i-th parameter behind a Pro gate? Followed through `const r = useRef(p)`
// and `r.current = p`; the parameter used in any other way (passed on, returned, stored) is not held.
const paramMemo = new Map();
function paramHeld(b, i) {
  const key = `${i}`;
  if (!b || b.kind !== 'fn') return false;
  const memo = paramMemo.get(b) ?? new Map();
  paramMemo.set(b, memo);
  if (memo.has(key)) return memo.get(key);
  memo.set(key, false);
  const F = b.fnNode;
  const prm = F.parameters?.[i];
  let ok = !!prm && ts.isIdentifier(prm.name) && !prm.dotDotDotToken;
  const pb = ok ? b.mod.scopes.get(F)?.get(prm.name.text) : null;
  const aliases = new Set();
  const isParamRef = (e) => ts.isIdentifier(strip(e)) && lookup(b.mod, strip(e)) === pb;
  const writes = [];
  for (const r of ok ? refsOf.get(pb) ?? [] : []) {
    const par = r.parent;
    if (ts.isCallExpression(par) && ts.isIdentifier(par.expression) && par.expression.text === 'useRef' && par.arguments[0] === r
      && ts.isVariableDeclaration(par.parent) && ts.isIdentifier(par.parent.name)) { aliases.add(lookup(b.mod, par.parent.name)); continue; }
    if (ts.isBinaryExpression(par) && par.operatorToken.kind === K.EqualsToken && par.right === r) { writes.push(par.left); continue; }
    if (!held(b.mod, r, F)) { ok = false; break; }
  }
  const isAliasCurrent = (e) => ts.isPropertyAccessExpression(e) && e.name.text === 'current' && ts.isIdentifier(e.expression) && aliases.has(lookup(b.mod, e.expression));
  if (ok && !writes.every(isAliasCurrent)) ok = false;
  for (const a of ok ? aliases : []) for (const r of refsOf.get(a) ?? []) {
    const par = r.parent;
    if (ts.isPropertyAccessExpression(par) && par.expression === r && par.name.text === 'current') {
      const pp = par.parent;
      if (ts.isBinaryExpression(pp) && pp.left === par && pp.operatorToken.kind === K.EqualsToken) { if (!isParamRef(pp.right)) ok = false; continue; }
      if (!held(b.mod, par, F)) ok = false;
      continue;
    }
    ok = false;   // the ref itself is handed on or read whole
  }
  memo.set(key, ok);
  return ok;
}
const isGrantRead = (cb) => isFrom(cb, 'pro.tsx', 'useFeature');
const line = (mod, n) => ts.getLineAndCharacterOfPosition(mod.sf, n.getStart(mod.sf)).line + 1;
const hasFnAbove = (n) => { for (let p = n.parent; p; p = p.parent) if (isFn(p)) return true; return false; };
const declName = (id) => {
  const p = id.parent;
  if (ts.isShorthandPropertyAssignment(p)) return false;
  return p.name === id || p.propertyName === id || p.label === id;
};
// One use: which owner it belongs to (the nearest named function, class or top-level const, else the file's top
// level), whether it is inside a member of that function's returned object, and whether it is held.
function place(mod, node, ev) {
  let crossing = null, topMember = null;
  for (let c = node; c.parent; c = c.parent) {
    const p = c.parent;
    if (ts.isCallExpression(p) && p.arguments.includes(c)) {
      const cb = calleeOf(mod, p);
      if (isGrantRead(cb)) return;                                 // a grant read, not a call
      if (paramHeld(cb, p.arguments.indexOf(c))) return;           // the callee holds what it is handed
      if (cb?.kind === 'fn' && !ev.handedTo) ev.handedTo = `${cb.name} (${cb.mod.rel}:${line(cb.mod, cb.nameNode)})`;
    }
    if (ts.isVariableDeclaration(p) && c === p.initializer) {
      const b = ts.isIdentifier(p.name) ? lookup(mod, p.name) : null;
      if (b?.kind === 'method' && b.decl === p) return;            // the binding's own declaration
      if (!b || !b.top) for (const pb of bindingsOf(mod, p)) pb.initEvents.push(ev);
    }
    if ((ts.isPropertyAssignment(p) || ts.isShorthandPropertyAssignment(p) || ts.isSpreadAssignment(p) || ts.isMethodDeclaration(p))
      && returnObjects.has(p.parent) && !crossing) crossing = p;
    // A member of a top-level const's object literal (`const META = { feature: 'askData', title: 'x' }`) is read
    // member by member, as a returned object is, so `META.title` does not reach through `feature`.
    if ((ts.isPropertyAssignment(p) || ts.isShorthandPropertyAssignment(p) || ts.isMethodDeclaration(p)) && !crossing
      && ts.isObjectLiteralExpression(p.parent)) topMember = p;
    const owner = containers.get(p) ?? (ts.isSourceFile(p) ? mod.top : null);
    if (!owner) continue;
    ev.boundary = p;
    ev.owner = owner;
    const topObject = topMember && owner.kind === 'value' && owner.top && ts.isVariableDeclaration(p) && p.initializer
      && strip(p.initializer) === topMember.parent;
    if (crossing && returnObjects.get(crossing.parent) === p) {
      const name = ts.isSpreadAssignment(crossing) ? '*' : propText(crossing.name);
      if (!owner.memberEvents.has(name)) owner.memberEvents.set(name, []);
      owner.memberEvents.get(name).push(ev);
    } else if (topObject) {
      const name = propText(topMember.name);
      if (!owner.memberEvents.has(name)) owner.memberEvents.set(name, []);
      owner.memberEvents.get(name).push(ev);
    } else owner.events.push(ev);
    return;
  }
}
function bindingsOf(mod, decl) {
  const out = [];
  const v = (n) => { if (ts.isIdentifier(n)) { const b = lookup(mod, n); if (b) out.push(b); } else for (const el of n.elements) if (!ts.isOmittedExpression(el)) v(el.name); };
  v(decl.name);
  return out;
}
const events = [];
for (const mod of mods.values()) {
  const pending = [];
  const visit = (n) => {
    if (ts.isInterfaceDeclaration(n) || ts.isTypeAliasDeclaration(n) || ts.isImportDeclaration(n) || ts.isExportDeclaration(n)
      || ts.isModuleDeclaration(n) || ts.isJsxClosingElement(n)) return;
    if (ts.isExpressionWithTypeArguments(n)) return visit(n.expression);
    if (ts.isTypeNode(n)) return;
    if (ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n)) {
      const p = n.parent;
      if (!DATA.includes(n.text) || (p.name === n) || (mod.rel === 'features.ts' && !mod.sends && !/from\s*['"]\.\/bridge['"]/.test(mod.sf.text) && !hasFnAbove(n))) return;
      return void pending.push([n, { mod, node: n, seed: n.text }]);
    }
    if (ts.isIdentifier(n)) {
      const p = n.parent;
      if (declName(n)) return;
      const b = lookup(mod, n);
      if (!b) return;
      if (!refsOf.has(b)) refsOf.set(b, []);
      refsOf.get(b).push(n);
      let how = 'bare', member, bound = false;
      if (ts.isPropertyAccessExpression(p) && p.expression === n) { how = 'member'; member = p.name.text; }
      else if (ts.isCallExpression(p) && strip(p.expression) === n) { how = 'call'; bound = ts.isVariableDeclaration(p.parent) && p.parent.initializer === p; }
      else if ((ts.isJsxOpeningElement(p) || ts.isJsxSelfClosingElement(p)) && p.tagName === n) how = 'jsx';
      return void pending.push([n, { mod, node: n, binding: b, how, member, bound }]);
    }
    if (ts.isCallExpression(n) && n.expression.kind === K.ImportKeyword && n.arguments[0] && ts.isStringLiteralLike(n.arguments[0])) {
      const key = specOf(mod, n.arguments[0].text);
      if (key) pending.push([n, { mod, node: n, how: 'module', module: key }]);
      return;
    }
    ts.forEachChild(n, visit);
  };
  visit(mod.sf);
  mod.pending = pending;
}
// Placed after every reference is indexed, because following a parameter reads them all.
for (const mod of mods.values()) {
  for (const [n, ev] of mod.pending) { place(mod, n, ev); if (ev.owner) events.push(ev); }
  // A top-level const whose initialiser runs a call runs it when the file loads.
  for (const s of mod.sf.statements) if (ts.isVariableStatement(s)) for (const d of s.declarationList.declarations) {
    const b = ts.isIdentifier(d.name) ? lookup(mod, d.name) : null;
    let runs = false;
    const v = (n) => { if (runs || isFn(n) || ts.isClassLike(n)) return; if (ts.isCallExpression(n) || ts.isNewExpression(n) || ts.isTaggedTemplateExpression(n) || ts.isAwaitExpression(n)) runs = true; else ts.forEachChild(n, v); };
    if (b && (b.kind === 'value') && d.initializer) v(d.initializer);
    if (runs) mod.top.events.push({ mod, node: d, binding: b, how: 'bare', held: false, owner: mod.top });
  }
}

// Reach, to a fixed point.
const own = new Map();
const members = new Map();
const memberOf = (b, m) => members.get(b)?.get(m) ?? members.get(b)?.get('*') ?? null;
const anyMember = (b) => { const mm = members.get(b); return mm && mm.size ? mm.values().next().value : null; };
const anyExport = (key) => { for (const n of exportNames(key)) { const t = valueTrace(resolveExport(key, n)); if (t) return t; } return null; };
function carrierOf(b) {
  const init = b.decl && ts.isVariableDeclaration(b.decl) && b.decl.initializer && strip(b.decl.initializer);
  if (!init || !ts.isCallExpression(init)) return null;
  const h = calleeOf(b.mod, init);
  return h?.kind === 'fn' && h.hasMembers ? h : null;
}
// Held is worked out only for a use that would reach, because it walks every condition above the use.
const isHeld = (ev) => (ev.held ??= held(ev.mod, ev.node, ev.boundary));
function firstReach(evs) { for (const ev of evs) { const t = evalEvent(ev); if (t && !isHeld(ev)) return t; } return null; }
const tracing = new Set();
function valueTrace(b) {
  if (!b || tracing.has(b)) return null;
  tracing.add(b);
  try { return traceValue(b); } finally { tracing.delete(b); }
}
function traceValue(b) {
  switch (b.kind) {
    case 'method': return { at: `${b.mod.rel}:${line(b.mod, b.nameNode)}`, what: `${b.name} is '${b.method}'` };
    case 'fn': case 'class': return own.get(b) ?? anyMember(b);
    case 'value': case 'part': {
      if (b.top && b.kind === 'value') return own.get(b) ?? anyMember(b);
      const h = carrierOf(b);
      if (h) { const t = b.kind === 'part' && b.prop !== '*' ? memberOf(h, b.prop) : anyMember(h); if (t) return t; }
      return firstReach(b.initEvents);
    }
    case 'ns': return b.name ? anyExport(b.module) : anyExport(b.module);
    default: return null;
  }
}
function memberTrace(b, m) {
  if (b.kind === 'ns') return valueTrace(resolveExport(b.module, m));
  if (b.kind === 'value' && !b.top) { const h = carrierOf(b); if (h) return memberOf(h, m); }
  if (b.kind === 'value' && b.top && members.has(b)) return own.get(b) ?? memberOf(b, m);
  return valueTrace(b);
}
function callTrace(b, bound) {
  if (b.kind === 'fn') return own.get(b) ?? (bound ? null : anyMember(b));
  return valueTrace(b);
}
function evalEvent(ev) {
  let t = null, what;
  if (ev.seed) return { at: `${ev.mod.rel}:${line(ev.mod, ev.node)}`, what: `quotes '${ev.seed}'${ev.mod.sends ? ' in a file that sends' : ''}` };
  if (ev.how === 'module') { t = anyExport(ev.module); what = `loads ${ev.module} at run time`; }
  else {
    const b = resolveBinding(ev.binding);
    if (!b) return null;
    const name = ev.binding.name;
    if (ev.how === 'member') { t = memberTrace(b, ev.member); what = `uses ${name}.${ev.member}`; }
    else if (ev.how === 'call') { t = callTrace(b, ev.bound); what = `calls ${name}`; }
    else if (ev.how === 'jsx') { t = callTrace(b, false); what = `mounts <${name}>`; }
    else { t = valueTrace(b); what = ts.isVariableDeclaration(ev.node) ? `runs ${name} when the file loads` : `uses ${name} whole`; }
  }
  if (ev.handedTo) what += `, in a callback handed to ${ev.handedTo}, which does not hold its use of it`;
  return t ? { at: `${ev.mod.rel}:${line(ev.mod, ev.node)}`, what, next: t } : null;
}
const owners = [...new Set([...containers.values(), ...[...mods.values()].map((m) => m.top)])];
for (let changed = true; changed;) {
  changed = false;
  for (const o of owners) {
    if (!own.has(o)) { const t = firstReach(o.events); if (t) { own.set(o, t); changed = true; } }
    for (const [m, evs] of o.memberEvents) {
      if (!members.has(o)) members.set(o, new Map());
      if (members.get(o).has(m)) continue;
      const t = firstReach(evs);
      if (t) { members.get(o).set(m, t); changed = true; }
    }
  }
}
const chain = (t) => { const out = []; for (let s = t; s; s = s.next) out.push(`${s.at} ${s.what}`); return out.join(' -> '); };
const leaks = [...mods.values()].filter((m) => own.has(m.top)).map((m) => chain(own.get(m.top)));
assert.ok(leaks.length === 0, `Free can reach an Ask your data method with no Pro gate on the way: ${leaks.join(' | ')}. `
  + 'A gate is a condition that implies Pro (useFeature(<a Pro feature>) === \'granted\', useTier() === \'pro\', or App\'s '
  + 'current-tab grant on a Pro tab); put the use behind one, or say why Free cannot run it.');
// What stands between Free and a data method today: each held use that would reach if its gate were gone.
const heldAt = events.filter((ev) => evalEvent(ev) && isHeld(ev)).map((ev) => `${ev.mod.rel}:${line(ev.mod, ev.node)}`);
const recovered = [...mods.values()].filter((m) => m.sf.parseDiagnostics?.length).map((m) => m.rel);
console.log(`free-sends-no-jev: askData and listDataQuestions reached by ${own.size + [...members.values()].reduce((n, mm) => n + mm.size, 0)} `
  + `functions and members, none from an entry point unheld; held behind a Pro gate at ${heldAt.length ? [...new Set(heldAt)].join(', ') : 'nothing (no caller on this branch)'}`
  + (recovered.length ? `; read with syntax errors the parser recovered (the build reports them): ${recovered.join(', ')}` : ''));

// 3. Every other caller is engine-gated, the named proof exists, and no file calls it that the list does not name.
for (const [m, [gate, [file, test], known]] of Object.entries(ENGINE_GATED)) {
  const body = readFileSync(join(tests, file), 'utf8');
  assert.ok(body.includes(test), `${m} is held by ${gate}; its proof ${file} ${test} is missing`);
  for (const rel of callers.get(m))
    assert.ok(known.includes(rel), `${rel} is a new caller of ${m}; say what holds Free out of it and add it to ENGINE_GATED`);
}

// Nothing reaches the Jev service from the webview itself: no vendor or relay address, no key header.
for (const { rel, text } of files)
  assert.doesNotMatch(text, /api\.anthropic\.com|x-api-key|relay\.semanticus|\/v1\/messages/i, `${rel} talks to a model service directly`);

const data = GATED.flatMap((m) => [...dataOnly.get(m)].map((rel) => `${m} in ${rel}`));
console.log(`free-sends-no-jev: ${files.length} webview files read; engine-gated callers: ${GATED.map((m) => `${m} ${callers.get(m).size}`).join(', ')}`
  + (data.length ? `; held as data only, not a call: ${data.join(', ')}` : ''));
