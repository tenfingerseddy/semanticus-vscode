import { useCallback, useRef, useState, useEffect } from 'react';
import { rpc, copyText, loadState, saveState, onDidChange } from './bridge';

/**
 * A draggable, persisted panel size. Returns the current size (px) and a pointer-down handler to attach to a
 * drag grip; the size survives tab switches + reloads (via usePersistedState). `axis:'x'` resizes width,
 * `'y'` height; `dir` is +1 when dragging right/down grows the panel, -1 when it shrinks it (e.g. a bottom
 * panel grows as the splitter moves UP → dir:-1).
 */
export function useResizable(key: string, initial: number, opts: { axis: 'x' | 'y'; dir?: 1 | -1; min: number; max: number }) {
  const { axis, dir = 1, min, max } = opts;
  const [size, setSize] = usePersistedState(key, initial);
  const sizeRef = useRef(size);
  sizeRef.current = size;
  const onPointerDown = useCallback((e: React.PointerEvent) => {
    e.preventDefault();
    const start = axis === 'x' ? e.clientX : e.clientY;
    const base = sizeRef.current;
    const el = e.currentTarget as HTMLElement;
    el.setPointerCapture(e.pointerId);
    const move = (ev: PointerEvent) => {
      const cur = axis === 'x' ? ev.clientX : ev.clientY;
      setSize(Math.max(min, Math.min(max, base + dir * (cur - start))));
    };
    const up = () => { el.removeEventListener('pointermove', move); el.removeEventListener('pointerup', up); };
    el.addEventListener('pointermove', move); el.addEventListener('pointerup', up);
  }, [axis, dir, min, max, setSize]);
  return [size, onPointerDown, setSize] as const;
}

// Component state that survives Studio tab switches (which UNMOUNT the tab) and webview reloads, by mirroring
// to the webview's persisted state. Used by the code-entry surfaces (DAX Query / Lab / Pivot) so a typed query
// isn't lost when you change tabs. Drop-in replacement for useState.
export function usePersistedState<T>(key: string, initial: T) {
  const [v, setV] = useState<T>(() => loadState('input:' + key, initial));
  useEffect(() => { saveState('input:' + key, v); }, [key, v]);
  return [v, setV] as const;
}

type FixState = 'fixing' | 'copied' | 'done';
interface FixPrompt { prompt: string }

/** Drop stale "fixed" ticks. Undo, redo, or any other model change can restore the row; the tick must not stick (D-078). */
export function dropDoneFixState(state: Record<string, FixState>): Record<string, FixState> {
  let changed = false;
  const next: Record<string, FixState> = {};
  for (const [k, v] of Object.entries(state)) {
    if (v === 'done') { changed = true; continue; }
    next[k] = v;
  }
  return changed ? next : state;
}

/**
 * The per-finding fix / ask-Claude state machine shared by the AI-Readiness findings list and the
 * BPA tab — identical except for the two RPC method names. `fix` applies a deterministic safe fix;
 * `ask` copies a grounded remediation prompt for the user's Claude. `keyOf` keys state by ruleId+ref.
 */
export function useFixState(fixMethod: string, promptMethod: string) {
  const [state, setState] = useState<Record<string, FixState>>({});
  useEffect(() => onDidChange(() => setState((s) => dropDoneFixState(s))), []);
  const keyOf = (ruleId: string, objectRef: string) => `${ruleId}::${objectRef}`;

  async function fix(ruleId: string, objectRef: string, afterFix?: () => void) {
    const k = keyOf(ruleId, objectRef);
    setState((s) => ({ ...s, [k]: 'fixing' }));
    try {
      await rpc(fixMethod, ruleId, objectRef, 'human');
      setState((s) => ({ ...s, [k]: 'done' }));
      afterFix?.();
    } catch {
      setState((s) => { const n = { ...s }; delete n[k]; return n; });
    }
  }

  async function ask(ruleId: string, objectRef: string) {
    const k = keyOf(ruleId, objectRef);
    try {
      const fp = await rpc<FixPrompt>(promptMethod, ruleId, objectRef);
      if (await copyText(fp.prompt)) {
        setState((s) => ({ ...s, [k]: 'copied' }));
        window.setTimeout(() => setState((s) => { const n = { ...s }; if (n[k] === 'copied') delete n[k]; return n; }), 1600);
      }
    } catch { /* ignore */ }
  }

  return { state, keyOf, fix, ask };
}
