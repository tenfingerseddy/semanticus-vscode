// Connect another assistant (1.5, lane connect-any): the words and the shape of the picker, apart from the page so a test can run them.
// The engine does the work (listAssistants, connectAssistant); this only turns its answers into what the person reads.

export interface ConnectableAssistant {
  id: string; name: string; found: boolean;
  /** notFound | notConnected | connected | outdated */
  state: string;
  configPath?: string; nextStep?: string;
}
export interface ConnectAssistantResult {
  assistant: string; name: string; ok: boolean; changed: boolean;
  message: string; nextStep?: string; configPath?: string; backupPath?: string; licenceNote?: string;
}

export type RowAction = 'connect' | 'update' | 'done';
export interface AssistantRowView { id: string; name: string; status: string; action: RowAction; actionLabel: string; muted: boolean }

export const PANEL_TITLE = 'Connect another assistant';
export const PANEL_LEAD = 'Semanticus adds itself to the assistant’s settings and changes nothing else in them.';
export const NOTHING_LISTED = 'Semanticus could not look for your assistants just now. Try again in a moment.';

/** One row of the picker. A connected row has nothing left to press; one that points somewhere else offers Update. */
export function rowView(a: ConnectableAssistant): AssistantRowView {
  // Connected is said once, on the row's button. The line under the name says where the assistant is, never Connected again (copy21).
  if (a.state === 'connected') return { id: a.id, name: a.name, status: 'On this computer', action: 'done', actionLabel: 'Connected', muted: false };
  if (a.state === 'outdated') return { id: a.id, name: a.name, status: 'Needs an update', action: 'update', actionLabel: 'Update', muted: false };
  return { id: a.id, name: a.name, status: a.found ? 'On this computer' : 'Not found here', action: 'connect', actionLabel: 'Connect', muted: !a.found };
}

/** What the person reads after a press: what happened, then the one thing to do next, then anything Semanticus saved. */
export function resultLines(r: ConnectAssistantResult): string[] {
  // The row says Connected once, in its status line, so a good press does not say it again: it says what to do next.
  const lines = r.ok && r.changed && r.nextStep ? [] : [r.message];
  if (r.ok && r.nextStep) lines.push(r.nextStep);
  if (r.ok && r.backupPath) lines.push('A copy of the old file is next to it.');
  if (r.ok && r.licenceNote) lines.push(r.licenceNote);
  return lines.filter(Boolean);
}
