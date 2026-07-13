import { useEffect } from 'react';

// ===================================================================================================
// Keyboard shortcuts — the single source of truth the '?' overlay renders from.
//
// TWO LAYERS (the load-bearing design — see docs/keyboard-shortcuts.md):
//   1. VS Code keybindings (package.json `contributes.keybindings`) own every gesture that carries a
//      Ctrl/Cmd chord safe to fire while typing (Ctrl+F, Ctrl+S, Ctrl+Shift+1–5, Ctrl+Alt+arrows).
//      VS Code forwards webview keydowns to its keybinding service, so these work whether focus is
//      inside Studio or on its chrome — and users can REMAP them (Keyboard Shortcuts → "Semanticus").
//   2. This webview handles the gestures VS Code must NOT own: unmodified keys ('?', Esc — typing a
//      '?' in a search box must never open an overlay, so only in-page code can check the target)
//      and Ctrl+Alt+letter jumps (on Windows AltGr == Ctrl+Alt, so a package.json binding would fire
//      while a European-layout user types 'ż'/'ß'/'µ' in a Studio input — in-page we skip editable
//      targets, making them safe). Every in-page gesture still has a command counterpart
//      (semanticus.studioGoTab etc.) so power users can bind their own keys to the same actions.
//
// Keep this file, package.json `contributes.keybindings`, and docs/keyboard-shortcuts.md in sync —
// they change in the same PR, always.
// ===================================================================================================

export const IS_MAC = /mac/i.test(navigator.platform ?? '');

// Ctrl+Alt+<letter> → Studio tab, handled by the App-root keydown handler (never while typing).
// The label is what the overlay shows; the id must be a real StudioTab id (App validates via goTab).
export const KEY_TABS: { key: string; tab: string; label: string }[] = [
  { key: 'd', tab: 'diagram', label: 'Diagram' },
  { key: 'm', tab: 'mcode', label: 'M Code' },
  { key: 'l', tab: 'daxlab', label: 'DAX Lab' },
  { key: 'r', tab: 'readiness', label: 'AI Readiness (re-scans)' },
  { key: 'b', tab: 'bpa', label: 'BPA (scans on open)' },
  { key: 'h', tab: 'history', label: 'Edit History' },
  { key: 'w', tab: 'workflows', label: 'Workflows' },
  { key: 'k', tab: 'knowledge', label: 'Knowledge' },
];

interface ShortcutItem { win: string; mac?: string; action: string; note?: string }
interface ShortcutGroup { title: string; hint?: string; items: ShortcutItem[] }

const mod = (s: string) => (IS_MAC ? s.replace(/^Ctrl\+/, '⌘') : s);

export const SHORTCUT_GROUPS: ShortcutGroup[] = [
  {
    title: 'Studio',
    items: [
      { win: mod('Ctrl+F'), action: 'Search & Replace across the model', note: 'names, descriptions, DAX; the Search tab, find box focused' },
      { win: mod('Ctrl+S'), action: 'Save the model to disk' },
      { win: 'Ctrl+Alt+Z', action: 'Undo the last model change', note: 'the shared you-and-AI timeline, not text undo' },
      { win: 'Ctrl+Alt+Shift+Z', action: 'Redo a model change' },
      { win: 'Ctrl+Alt+T', action: 'Focus the Model tree (side bar)' },
      { win: '?', action: 'Show or hide this cheat sheet' },
      { win: 'Esc', action: 'Close this cheat sheet / the help panel' },
    ],
  },
  {
    title: 'Switch tabs',
    items: [
      { win: 'Ctrl+Shift+1…5', action: 'Jump to an intent: Understand · Change · Improve · Prove · Ship', note: 'returns to the tab you last used in that intent' },
      { win: 'Ctrl+Alt+← / →', action: 'Previous / next Studio tab' },
      ...KEY_TABS.map((t) => ({ win: `Ctrl+Alt+${t.key.toUpperCase()}`, action: `Go to ${t.label}` })),
    ],
  },
  {
    title: 'Model tree (side bar)',
    items: [
      { win: mod('Ctrl+F'), action: 'Find in Model', note: 'searches every object: names, descriptions, DAX' },
      { win: mod('Ctrl+S'), action: 'Save the model to disk' },
      { win: mod('Ctrl+Z'), action: 'Undo the last model change' },
      { win: mod('Ctrl+Shift+Z'), mac: '⌘⇧Z', action: 'Redo a model change', note: IS_MAC ? undefined : 'Ctrl+Y works too' },
      { win: 'F2', action: 'Rename the selected object', note: 'DAX references are rewritten automatically' },
      { win: 'Delete', mac: '⌘⌫', action: 'Delete the selected object(s)', note: 'always asks first' },
      { win: 'Ctrl+Alt+N', action: 'New measure on the selected table' },
      { win: 'Ctrl+Alt+S', action: 'Open Semanticus Studio' },
      { win: mod('Ctrl+C') + ' / ' + mod('Ctrl+V'), action: 'Copy the selected object, paste to duplicate it', note: 'a measure pasted onto another table lands there; also pastes Reference Model tree copies' },
    ],
  },
  {
    title: 'DAX & scripts',
    items: [
      { win: mod('Ctrl+S'), action: 'Save the DAX you are editing back to the model', note: 'in any measure / column / calc-item editor' },
      { win: 'Ctrl+Enter', mac: '⌘↩', action: 'Run the query (DAX Lab) · Apply a DAX/TMDL script', note: 'scripts: the editable Script ▸ documents' },
      { win: 'Shift+Alt+F', mac: '⇧⌥F', action: 'Format DAX (offline, built in)' },
      { win: 'Ctrl+Alt+F', action: 'Format DAX with DAX Formatter (online)' },
    ],
  },
];

// The Studio-side keydown handler's contract, shared so App.tsx doesn't re-encode gesture strings.
// Returns the tab id for a Ctrl+Alt+letter jump, or null. Callers already guarded editable targets.
export function tabForKey(e: KeyboardEvent): string | null {
  if (!e.ctrlKey || !e.altKey || e.shiftKey || e.metaKey) return null;
  const hit = KEY_TABS.find((t) => t.key === e.key.toLowerCase());
  return hit ? hit.tab : null;
}

// True when the event target is somewhere the user is TYPING — inputs, textareas, selects, and any
// contenteditable host (CodeMirror renders one). In-page gestures must stand down there, both so keys
// type normally and so AltGr-produced characters (reported as Ctrl+Alt on Windows) never trigger jumps.
export function isTypingTarget(t: EventTarget | null): boolean {
  const el = t as HTMLElement | null;
  return !!el?.closest?.('input, textarea, select, [contenteditable="true"], .cm-editor');
}

function Key({ children }: { children: React.ReactNode }) {
  return (
    <kbd className="text-[11px] px-1.5 py-0.5 rounded tnum whitespace-nowrap"
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', boxShadow: '0 1px 0 var(--sem-border)', fontFamily: 'inherit' }}>
      {children}
    </kbd>
  );
}

// The '?' cheat sheet — a centered modal listing every shortcut, grouped, in plain English.
// Opened by '?', the Studio help panel's link, or the "Semanticus: Keyboard Shortcuts" command.
export function ShortcutsOverlay({ open, onClose }: { open: boolean; onClose: () => void }) {
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [open, onClose]);
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-6" onClick={onClose} style={{ background: 'rgba(0,0,0,0.45)' }}>
      <div onClick={(e) => e.stopPropagation()} className="rounded-xl border flex flex-col overflow-hidden"
        style={{ width: 780, maxWidth: '94vw', maxHeight: '88vh', background: 'var(--sem-surface)', borderColor: 'var(--sem-border)', boxShadow: '0 16px 48px rgba(0,0,0,0.5)' }}>
        <div className="flex items-center gap-2 px-5 py-3 border-b shrink-0" style={{ borderColor: 'var(--sem-border)' }}>
          <span className="text-[14px] font-semibold">Keyboard shortcuts</span>
          <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>press ? any time</span>
          <button onClick={onClose} aria-label="Close" className="ml-auto text-[14px]" style={{ color: 'var(--sem-muted)' }}>✕</button>
        </div>
        <div className="flex-1 overflow-auto px-5 py-4">
          <div className="grid gap-x-8 gap-y-5" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))' }}>
            {SHORTCUT_GROUPS.map((g) => (
              <div key={g.title} className="flex flex-col gap-1.5 min-w-0">
                <div className="text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-accent)' }}>{g.title}</div>
                {g.items.map((it, i) => (
                  <div key={i} className="flex items-baseline gap-3">
                    <span className="w-[140px] shrink-0 text-right"><Key>{IS_MAC && it.mac ? it.mac : it.win}</Key></span>
                    <span className="text-[12px] min-w-0" style={{ color: 'var(--sem-fg)' }}>
                      {it.action}
                      {it.note && <span style={{ color: 'var(--sem-muted)' }}>: {it.note}</span>}
                    </span>
                  </div>
                ))}
              </div>
            ))}
          </div>
        </div>
        <div className="px-5 py-3 border-t text-[11px] shrink-0" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
          Every Ctrl-chord here is a normal VS Code keybinding. Change any of them in <b>File → Preferences → Keyboard Shortcuts</b> (search “Semanticus”).
          Shortcuts only ever apply inside Semanticus surfaces (Studio, the Model tree, DAX editors). Your usual VS Code keys are untouched everywhere else.
          Full map with scoping notes: <span style={{ color: 'var(--sem-fg)' }}>docs/keyboard-shortcuts.md</span>.
        </div>
      </div>
    </div>
  );
}
