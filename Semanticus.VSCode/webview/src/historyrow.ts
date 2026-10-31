// The words of one History row, kept apart from the hub so a test can run them. A row names what it was about: the
// model, the account and the tenant when they are known, then the reason, then when. A failed row needs this most,
// because "Open failed" with no name cannot be tied to anything.

// One device-local connection-timeline event (mirrors Engine ConnectionHistoryEvent). Holds no credential.
export interface HistoryEvent { id?: string; kind: string; account?: string; endpoint?: string; database?: string; tenantId?: string; modelName?: string; ok: boolean; detail?: string; whenUtc?: string; }

export const historyKindLabel = (e: HistoryEvent): string => {
  switch (e.kind) {
    case 'connect': return e.ok ? 'Connected for queries' : 'Connect failed';
    case 'open': return e.ok ? 'Opened' : 'Open failed';
    case 'switch': return e.ok ? 'Switched account' : 'Account switch failed';
    case 'signin': return e.ok ? 'Signed in' : 'Sign-in failed';
    case 'role': return e.detail ? e.detail.charAt(0).toUpperCase() + e.detail.slice(1) : 'Role changed';
    default: return e.ok ? e.kind : `${e.kind} failed`;
  }
};

const lastSegment = (value?: string): string => {
  const v = (value || '').replace(/[\\/]+$/, '');
  if (!v) return '';
  const i = Math.max(v.lastIndexOf('/'), v.lastIndexOf('\\'));
  return i >= 0 ? v.slice(i + 1) : v;
};

export const shortWhen = (iso?: string): string => {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
};

/** The History row's second line: the model, who, which tenant, why, and when. The reason is left out when it only repeats the title. */
export const historySubLine = (e: HistoryEvent): string => {
  const kind = historyKindLabel(e).trim().toLowerCase();
  const model = e.modelName || e.database || lastSegment(e.endpoint);
  const reason = e.detail && e.detail.trim().toLowerCase() !== kind ? e.detail : null;
  // A model with no detail used to be the whole "about"; now the model always leads and the reason follows.
  return [model, e.account, e.tenantId ? `tenant ${e.tenantId}` : null, reason, shortWhen(e.whenUtc)].filter(Boolean).join(' · ');
};
