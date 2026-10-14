// What the Pending changes page says (hero19 D10): one consistent thing, with no engine words. Pure, so a test loads it without a host.

export interface PendingSummary { total: number; approved: number; applied: number; needsContent: number; unverified?: number }

/** A format string as people read it: the backslashes that only protect a character in the code are not shown. */
export function readableFormat<T extends string | null | undefined>(text: T): T {
  return (typeof text === 'string' ? text.replace(/\\(.)/gu, '$1') : text) as T;
}

/** The counts of the page, only the ones that are not zero. Rows already applied are counted as applied, never as "0 approved". */
export function pendingSummary(s: PendingSummary): string {
  const toReview = Math.max(0, s.total - s.applied - s.approved - s.needsContent);
  const parts = [
    s.applied > 0 ? `${s.applied} applied` : null,
    s.approved > 0 ? `${s.approved} approved` : null,
    s.needsContent > 0 ? `${s.needsContent} need${s.needsContent === 1 ? 's' : ''} content` : null,
    toReview > 0 ? `${toReview} to review` : null,
    s.unverified ? `${s.unverified} unverified` : null,
  ].filter(Boolean);
  return parts.join(' · ');
}

/** The second sentence follows the state: it says nothing is written only while something is still not applied. */
export function pendingNote(s: PendingSummary): string {
  if (s.total > 0 && s.applied >= s.total) return 'All applied. They are in your copy and go to the live model when you publish.';
  if (s.applied > 0) return 'The applied changes are in your copy. The rest are not written until you apply them.';
  return 'Nothing is written until you apply.';
}

/** What a group header says about its rows. Applied is said once, on each row and in the page's counts, never again on the header (hero20 D13); the header
 *  carries only the approved count. Null when none is approved. */
export function groupState(rows: { status: string }[]): string | null {
  const approved = rows.filter((r) => r.status === 'approved').length;
  return approved > 0 ? `${approved} approved` : null;
}

/** The note under a row. The row's chip already says "applied", so the engine's own "applied" is not said again (copy21). What the engine added
 *  in brackets still shows, as a sentence: "applied (checked again first that nothing uses it)" reads "Checked again first that nothing uses it." */
export function rowNote(status: string, note: string | null | undefined): string | null {
  const n = (note ?? '').trim();
  if (!n) return null;
  if (status !== 'applied') return n;
  const m = /^applied\b\s*(?:\(([^)]*)\))?\s*\.?$/iu.exec(n);
  if (!m) return n;
  const extra = (m[1] ?? '').trim();
  return extra ? extra[0].toUpperCase() + extra.slice(1) + (/[.!?]$/u.test(extra) ? '' : '.') : null;
}
