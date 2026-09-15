import type { Ref } from 'react';
import { SIGN_IN_MODES } from './copy';

// ===================================================================================================
// "Sign in as" — the ONE control that asks who should sign in to Fabric, shared by the Data agent page
// and by Promote on the Published page.
//
// It used to exist only on the Data agent page. Promote hard-coded the Azure command line instead, so on
// a computer that has never run that command line (Kane's laptop, 2026-09-14) the only thing Promote
// could do was print a red line about a command he had no reason to know. The choice is the same choice
// on both pages, so it is one component: a page that grows a Fabric call cannot quietly ship without it.
//
// The values are the engine's mode ids (see SIGN_IN_MODES in copy.ts). The tenant box appears only for
// the ways of signing in that can target somewhere other than your usual place; the Azure command line
// signs in wherever it was last signed in, so asking there would be a box that does nothing.
//
// Sizing comes from the shared control scale tokens in styles.css, so this row lines up with the buttons
// beside it instead of picking its own height.
// ===================================================================================================

const control = {
  boxSizing: 'border-box' as const,
  height: 'var(--sem-control-h-sm)',
  padding: 'var(--sem-control-pad-sm)',
  borderRadius: 'var(--sem-control-radius)',
  fontSize: 'var(--sem-control-font)',
  background: 'var(--sem-surface-2)',
  color: 'var(--sem-fg)',
  border: '1px solid var(--sem-border)',
  outline: 'none',
};

export interface AccountPickerProps {
  mode: string;
  onMode: (mode: string) => void;
  tenantId: string;
  onTenantId: (tenantId: string) => void;
  /** Extra words after the choice, for a page that needs to say what the choice is for. */
  hint?: string;
  /**
   * What the tenant box is for, in this page's own noun. It reads "where the model lives" by default, which is
   * right on the Data agent page and on Promote and wrong on a SQL source, where the thing that lives somewhere
   * is a database. One prop, so the two pages keep one control instead of forking it.
   */
  tenantLabel?: string;
  /** Onto the select, so a "Choose a different account" action elsewhere can focus it. */
  ref?: Ref<HTMLSelectElement>;
}

/**
 * The picker. The ref lands on the select so a "Choose a different account" action elsewhere on the page
 * can put the person straight on it rather than telling them to go and look for it.
 */
export function AccountPicker({ mode, onMode, tenantId, onTenantId, hint, tenantLabel, ref }: AccountPickerProps) {
  const chosen = SIGN_IN_MODES.find((m) => m.value === mode);
  const tenantWords = tenantLabel ?? 'where the model lives (optional)';
  const tenantAria = tenantWords.replace(/\s*\(optional\)\s*$/, '');
  return (
    <div className="flex items-center gap-2 flex-wrap" data-testid="account-picker">
      <span style={{ color: 'var(--sem-muted)', fontSize: 'var(--sem-control-font)' }}>Sign in as</span>
      <select ref={ref} value={mode} onChange={(e) => onMode(e.target.value)}
        aria-label="How to sign in"
        title={chosen ? chosen.detail : 'How to sign in'}
        style={{ ...control, minWidth: 210 }}>
        {SIGN_IN_MODES.map((m) => <option key={m.value} value={m.value}>{m.label}</option>)}
      </select>
      {mode !== 'azcli' && (
        <input value={tenantId} onChange={(e) => onTenantId(e.target.value)}
          aria-label={tenantAria}
          placeholder={tenantWords}
          title="Fill this in only when it sits somewhere other than your usual place. Your workspace administrator can tell you what to put here."
          style={{ ...control, width: 260 }} />
      )}
      {hint && <span style={{ color: 'var(--sem-muted)', fontSize: 'var(--sem-control-font-sm)' }}>{hint}</span>}
    </div>
  );
}
