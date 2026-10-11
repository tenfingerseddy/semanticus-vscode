// A tiny loader for the webview's TypeScript in a plain Node test: it transpiles a source file on the fly, resolves
// its relative imports the same way, and lets a test replace any import with a stub. Kept beside the tests that use
// it (ask-data-page.test.mjs carries the same idea inline) so a behaviour check can run the real module, not a
// copy of its logic.
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
export const webview = join(root, 'webview');
export const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');

export function makeLoader(stubs = {}, reactImpl = null) {
  const cache = new Map();
  const load = (file) => {
    const full = file.endsWith('.ts') || file.endsWith('.tsx') || file.endsWith('.mjs') ? file
      : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
    if (cache.has(full)) return cache.get(full);
    const js = ts.transpileModule(readFileSync(full, 'utf8'), {
      compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true, allowJs: true },
      fileName: full.replace(/\.mjs$/, '.js'),
    }).outputText;
    const module = { exports: {} };
    cache.set(full, module.exports);
    const localRequire = (name) => {
      if (name in stubs) return stubs[name];
      // Vite inlines `x.woff2?inline` as a data: URI at build time (docfonts.ts). Node has no bundler, so a test gets a stand-in string.
      if (name.endsWith('?inline')) return 'data:font/woff2;base64,AAAA';
      if (name === 'react' && reactImpl) return reactImpl;
      if (name.startsWith('.')) return load(resolve(dirname(full), name));
      return req(name);
    };
    new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
    cache.set(full, module.exports);
    return module.exports;
  };
  return (name) => load(join(webview, 'src', name));
}

/** A minimal hook runtime: enough of React's state, ref and effect hooks to drive one hook through several renders
 *  with no DOM. A state change re-renders at once; effects run after the render whose dependencies changed. */
/** A React for modules shared by several mounted components: each call goes to whichever runtime is rendering right now (a runtime holds one component's hook slots). */
const rendering_ = { react: null };
export const ambientReact = new Proxy({}, { get: (_t, key) => (rendering_.react ?? req('react'))[key] });

export function makeHookRuntime() {
  const slots = [];
  let cursor = 0, current = null, queue = [], rendering = false, again = false;
  const React = {
    useState(init) {
      const i = cursor++;
      if (!(i in slots)) slots[i] = { v: typeof init === 'function' ? init() : init };
      const s = slots[i];
      return [s.v, (n) => { const nv = typeof n === 'function' ? n(s.v) : n; if (Object.is(nv, s.v)) return; s.v = nv; render(); }];
    },
    useRef(init) { const i = cursor++; if (!(i in slots)) slots[i] = { current: init }; return slots[i]; },
    useCallback(f) { cursor++; return f; },
    memo(component) { return component; },
    useMemo(f) { cursor++; return f(); },
    useEffect(f, deps) {
      const i = cursor++;
      const s = (slots[i] ??= { deps: undefined, first: true });
      const changed = s.first || !deps || !s.deps || deps.some((d, k) => !Object.is(d, s.deps[k]));
      if (changed) queue.push(() => { s.first = false; s.deps = deps; if (typeof s.cleanup === 'function') s.cleanup(); s.cleanup = f(); });
    },
  };
  function render() {
    if (rendering) { again = true; return; }
    rendering = true;
    const outer = rendering_.react;
    try {
      do { again = false; cursor = 0; queue = []; rendering_.react = React; current.result = current.fn(); rendering_.react = outer; for (const e of queue) e(); } while (again);
    } finally { rendering = false; rendering_.react = outer; }
  }
  return {
    React,
    // `rerender` runs the function again with whatever its closure holds now: how a test hands a component new props.
    renderHook(fn) { current = { fn, result: null, rerender: () => render() }; render(); return current; },
    /** Unmount: run every effect's cleanup, as React does when the component leaves the page. */
    unmount() { for (const s of slots) if (s && typeof s.cleanup === 'function') { s.cleanup(); s.cleanup = undefined; } },
  };
}
export const settle = () => new Promise((r) => setTimeout(r, 0));
