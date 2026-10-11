// A hook runtime that mounts several components at once over ONE set of modules, and a fake of the webview's persisted
// state, for the drafts tests (join 11: typed words outlive the editor).
//
// The shared helper (webview-load.mjs) gives each runtime its own copy of the modules, so it can model a reload (a fresh
// module set over the same persisted state) but not a tab switch (the same modules, the editor unmounted and mounted
// again). This one does both: `mount` starts a new component instance on the same React stub, and `unmount` ends it.
// useMemo and useCallback keep their value while their dependencies are equal, as React does (the policy form lists its
// derived arrays as effect dependencies, so a runtime that recomputes them re-runs those effects forever).
import { makeLoader } from './webview-load.mjs';

export function makeReact() {
  let active = null;
  const same = (a, b) => !!a && !!b && a.length === b.length && a.every((d, k) => Object.is(d, b[k]));
  const React = {
    useState(init) {
      const inst = active, i = inst.cursor++;
      if (!(i in inst.slots)) inst.slots[i] = { v: typeof init === 'function' ? init() : init };
      const s = inst.slots[i];
      return [s.v, (n) => { const nv = typeof n === 'function' ? n(s.v) : n; if (Object.is(nv, s.v)) return; s.v = nv; inst.render(); }];
    },
    useRef(init) { const inst = active, i = inst.cursor++; if (!(i in inst.slots)) inst.slots[i] = { current: init }; return inst.slots[i]; },
    useCallback(f, deps) { return React.useMemo(() => f, deps); },
    useMemo(f, deps) {
      const inst = active, i = inst.cursor++, s = inst.slots[i];
      if (s && deps && same(deps, s.deps)) return s.v;
      inst.slots[i] = { v: f(), deps };
      return inst.slots[i].v;
    },
    memo(component) { return component; },
    lazy() { return () => null; },
    Suspense({ children }) { return children; },
    useSyncExternalStore(subscribe, get) {
      const inst = active, i = inst.cursor++;
      const s = (inst.slots[i] ??= { subscribed: false });
      if (!s.subscribed) { s.subscribed = true; s.cleanup = subscribe(() => inst.render()); }
      return get();
    },
    useEffect(f, deps) {
      const inst = active, i = inst.cursor++;
      const s = (inst.slots[i] ??= { deps: undefined, first: true });
      const changed = s.first || !deps || !s.deps || deps.some((d, k) => !Object.is(d, s.deps[k]));
      if (changed) inst.queue.push(() => { s.first = false; s.deps = deps; if (typeof s.cleanup === 'function') s.cleanup(); s.cleanup = f(); });
    },
  };
  React.useLayoutEffect = React.useEffect;
  function mount(fn) {
    const inst = { slots: [], cursor: 0, queue: [], rendering: false, again: false, mounted: true, result: null };
    inst.render = () => {
      if (!inst.mounted) return;
      if (inst.rendering) { inst.again = true; return; }
      inst.rendering = true;
      const prev = active;
      let guard = 0;
      try {
        do {
          if (++guard > 200) throw new Error('render loop');
          inst.again = false; inst.cursor = 0; inst.queue = [];
          active = inst; inst.result = fn(); active = prev;
          for (const e of inst.queue) { if (inst.mounted) e(); }
        } while (inst.again && inst.mounted);
      } finally { inst.rendering = false; active = prev; }
    };
    inst.rerender = () => inst.render();
    inst.unmount = () => {
      inst.mounted = false;
      for (const s of inst.slots) if (s && typeof s.cleanup === 'function') { s.cleanup(); s.cleanup = undefined; }
    };
    inst.render();
    return inst;
  }
  return { React, mount };
}

/** The webview's persisted state (loadState / saveState), kept by the test so a fresh module set reads what the last one saved. */
export function makeDisk() {
  const map = new Map();
  return {
    map,
    loadState: (k, d) => (map.has(k) ? structuredClone(map.get(k)) : d),
    saveState: (k, v) => { map.set(k, structuredClone(v)); },
  };
}

/** One page: a React stub and one copy of the modules over `disk`. `stubs(disk)` builds the import stubs. A new page is a reload. */
export function makePage(stubs, model = '/m/Model.bim') {
  const { React, mount } = makeReact();
  const load = makeLoader(stubs, React);
  const drafts = load('drafts.ts');
  drafts.setDraftModel(model);
  return { React, mount, load, drafts };
}

export function walk(node, visit) {
  if (node == null || typeof node === 'boolean' || typeof node === 'string' || typeof node === 'number') return;
  if (Array.isArray(node)) { node.forEach((n) => walk(n, visit)); return; }
  visit(node);
  walk(node.props?.children, visit);
}
export const find = (tree, pred) => { let hit = null; walk(tree, (el) => { if (!hit && pred(el)) hit = el; }); return hit; };
export const findAll = (tree, pred) => { const out = []; walk(tree, (el) => { if (pred(el)) out.push(el); }); return out; };
export const text = (el) => [].concat(el?.props?.children ?? []).filter((c) => typeof c === 'string').join('').trim();
export const draftMark = (tree) => find(tree, (el) => el.type?.name === 'DraftMark' && el.props.show);
export const movedBanner = (tree) => find(tree, (el) => el.type?.name === 'EditMovedBanner');
export const settleAll = async () => { for (let i = 0; i < 8; i++) await new Promise((r) => setTimeout(r, 0)); };
export const wait = (ms) => new Promise((r) => setTimeout(r, ms));
/** Every draft the disk holds, as [model, scope, field] keys. */
export const heldKeys = (disk) => Object.keys(disk.map.get('studio.drafts') ?? {}).map((k) => JSON.parse(k));
