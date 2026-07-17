import type { FocusEvent, KeyboardEvent } from 'react';
import { revealInTree } from './bridge';

// ===================================================================================================
// Shared row affordances for the Studio navigators (the "Option C" tree program: Studio is task-local
// pickers, the native Model tree is the tree of record, a selection bus connects them).
//
// <RevealBtn> is the ONE universal "Reveal in Model tree" affordance — the same glyph button the Lineage
// panel established — so every navigator row that names a model object jumps to the tree of record the
// same way. It stops propagation so it never triggers the row's own click (pick / insert / preview).
// The selection-bus half lives in bridge.ts (selectInProperties): rows call it on focus/click so the
// Properties view follows Studio without stealing focus.
// ===================================================================================================

// Keyboard access for rows that are <div>/<tr> (not <button>): a plain div can't be tab-focused or activated
// with Enter/Space, so a mouse-only onClick strands keyboard users. Spread these props (SAME handler as the
// row's onClick) to make a real, focusable control. Pairs with a `group-focus-within` reveal button (below)
// so the ⤢ affordance appears on keyboard focus, not hover alone.
//
// WHERE to spread them: on a LEAF element (the name/label span) whenever the row hosts nested controls
// (checkbox, Reveal) — a role=button ancestor of other buttons is wrong ARIA, and their Enter/Space would
// bubble into the row action. The row div keeps the mouse onClick for the large click target. Only a <tr>
// (whose cells are caller-rendered, so no leaf is available) carries these props itself; for that case the
// handler below ignores bubbled events (target !== currentTarget) so a nested control never activates the row.
// `focusSelect` (optional) is the "focusing this row selects it in Properties" half: wired to onFocus so a
// keyboard user tabbing onto the row — or the focus that precedes a mouse click — feeds the selection bus
// WITHOUT running the primary action. Enter/Space still run `activate` (the primary action). Guarded to the
// control itself (target === currentTarget) so a focus bubbling up from a sibling control doesn't re-select.
export function rowKeyProps(activate: () => void, focusSelect?: () => void) {
  return {
    role: 'button' as const,
    tabIndex: 0,
    onFocus: focusSelect ? (e: FocusEvent) => { if (e.target === e.currentTarget) focusSelect(); } : undefined,
    onKeyDown: (e: KeyboardEvent) => {
      // Only the ROW itself activates on Enter/Space. A row hosts its own controls (the multi-select checkbox,
      // the Reveal ⤢ button); Enter/Space on one of those BUBBLES here, and without this guard it would both
      // trigger the nested control AND fire the row action — and preventDefault would swallow the control's own
      // click. currentTarget is the row; target is the actually-focused element. Ignore anything but the row.
      if (e.target !== e.currentTarget) return;
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); activate(); }
    },
  };
}

export function RevealBtn({ objRef, className = '' }: { objRef: string; className?: string }) {
  return (
    <button
      className={'text-[10px] shrink-0 ' + className}
      title="Reveal in Model tree"
      onClick={(e) => { e.stopPropagation(); revealInTree(objRef); }}
      style={{ color: 'var(--sem-muted)', background: 'none', border: 'none', cursor: 'pointer', padding: '0 2px' }}>
      ⤢
    </button>
  );
}
