/** Convert an engine identifier or enum value into user-facing words. */
export function uiLabel(value: unknown, fallback = 'Not available'): string {
  const raw = String(value ?? '').trim();
  if (!raw) return fallback;
  const words = raw
    .replace(/[_-]+/g, ' ')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/\s+/g, ' ')
    .trim()
    .split(' ')
    .map((word, index) => /^[A-Z0-9]{2,}$/.test(word)
      ? word
      : index === 0
        ? word.charAt(0).toUpperCase() + word.slice(1).toLowerCase()
        : word.toLowerCase())
    .join(' ');
  return words;
}

// The spellings worth hand-writing, where the generic humaniser reads flat. Everything else falls through
// to uiLabel, so no engine id can reach the audit trail (UX-02 / UX-06, D-209).
const OP_LABEL: Record<string, string> = {
  blame_value: 'What moved this number?',
  apply_plan: 'Plan applied',
  'apply_plan-item': 'Plan item applied',
  optimize_measure: 'Measure optimization',
  set_dax: 'DAX change',
  create_measure: 'New measure',
  create_column: 'New column',
  create_relationship: 'New relationship',
  compare_baseline: 'Baseline comparison',
  deploy_live: 'Live deploy',
  save_model: 'Model saved',
};

/** The audit trail's name for a model operation. */
export function opLabel(op: unknown): string {
  const raw = String(op ?? '').trim();
  if (!raw) return 'An edit';
  return OP_LABEL[raw] ?? uiLabel(raw, 'An edit');
}

/**
 * The chords this panel handles itself. VS Code never sees them, so the Keyboard Shortcuts editor cannot
 * rebind them, and the cheat sheet has to say so (UX-04, D-211).
 */
export const PANEL_ONLY_CHORDS =
  '?, Esc, Ctrl+Alt+Z and Ctrl+Alt+Shift+Z (model undo and redo), Ctrl+Alt+T, Shift+Alt+F, and the Ctrl+Alt+letter tab jumps';
