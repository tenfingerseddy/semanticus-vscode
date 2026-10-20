import { useState, type CSSProperties } from 'react';
import { rpc } from './bridge';
import { AccountPicker } from './accountpicker';
import { defaultSignInMode } from './copy';

// The small form that adds a SQL source from inside a check (UAT 1.5.104: "Add a SQL source" sent the person to Connections and left
// them there, and from Query it did nothing at all). It lives in the check's own drawer, so it works wherever the drawer opens, and
// when it is saved the person is back in the check with the new source chosen. It writes the same record Connections writes
// (saveSqlSource), so there is still one list of sources. Nothing here is a model edit: sources live outside the model.

const inp: CSSProperties = { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', borderColor: 'var(--sem-border)' };

export interface AddedSource { id: string; name: string }

/** What Save refuses before the engine is asked, in the words of the box that is wrong. One complaint at a time, in field order. */
export function addSourceError(f: { name: string; server: string; database: string }): string | null {
  const name = f.name.trim(), server = f.server.trim(), database = f.database.trim();
  if (!name) return 'Give this source a name, so you can recognise it later.';
  if (!server) return 'Type the server address, for example contoso-sql.database.windows.net.';
  if (!database) return 'Type the database name.';
  if (/[;=]/.test(server) || /[;=]/.test(database)) return 'Type just the server address and the database name, not a connection string. Semanticus signs you in and never keeps a password.';
  return null;
}

/** What the check says once Save and check got through: the form closes, so this line is the only word that it connected (round 2 N29). */
export function connectedLine(name: string): string { return `Connected to ${name}.`; }

export function SqlSourceAddForm({ initialServer, onAdded, onCancel }: { initialServer?: string; onAdded: (added: AddedSource, keepOpen: boolean, connected?: string) => void; onCancel: () => void }) {
  const [name, setName] = useState('');
  const [server, setServer] = useState(initialServer ?? '');
  const [database, setDatabase] = useState('');
  const [authMode, setAuthMode] = useState(defaultSignInMode(null));
  const [tenantId, setTenantId] = useState('');
  const [busy, setBusy] = useState<'save' | 'test' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tested, setTested] = useState<{ ok: boolean; note: string; detail?: string } | null>(null);
  // A source that was saved and then failed its test stays open for a fix, and Save then updates that same source instead of adding a second.
  const [savedId, setSavedId] = useState<string | null>(null);

  const save = async (): Promise<AddedSource | null> => {
    const refusal = addSourceError({ name, server, database });
    if (refusal) { setError(refusal); return null; }
    const saved = await rpc<{ id: string; name: string }>('saveSqlSource', savedId, name.trim(), server.trim(), database.trim(), authMode, tenantId.trim() || null, 'human', null);
    if (!saved?.id) throw new Error('The source was not saved.');
    setSavedId(saved.id);
    // The open model reads its checks from its own source. A model that has none takes this one, so the next check starts on it.
    // A model that already has one keeps it: adding a second source never moves the first.
    try {
      const ctx = await rpc<{ sqlEndpoint?: { sourceId?: string } | null }>('connectionContext');
      if (!ctx?.sqlEndpoint?.sourceId) await rpc('setModelSqlEndpoint', saved.id, null, 'human');
    } catch { /* the source is saved and chosen in this check either way */ }
    return { id: saved.id, name: saved.name || name.trim() };
  };
  const run = async (test: boolean) => {
    setBusy(test ? 'test' : 'save'); setError(null); setTested(null);
    try {
      const added = await save();
      if (!added) return;
      let keepOpen = false;
      let connected: string | undefined;
      if (test) {
        const result = await rpc<{ ok: boolean; note?: string; detail?: string }>('testSqlSource', added.id, 'human').catch((e: unknown) => ({ ok: false, note: e instanceof Error ? e.message : String(e) }));
        keepOpen = !result?.ok;
        if (result?.ok) connected = connectedLine(added.name);
        setTested({ ok: !!result?.ok, note: result?.note ?? (result?.ok ? 'The connection works.' : 'The connection did not work.'), detail: (result as { detail?: string })?.detail });
      }
      onAdded(added, keepOpen, connected);
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(null); }
  };

  return (
    <div className="sem-inset mb-1" data-testid="check-add-source">
      <div className="mb-2 text-(length:--sem-text-label) font-semibold">Add a SQL source</div>
      <label className="mb-2 block text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Name
        <input value={name} onChange={(e) => { setName(e.target.value); setError(null); }} placeholder="Contoso warehouse" autoComplete="off" aria-label="Source name"
          className="mt-1 min-h-7 w-full min-w-0 rounded border px-2 text-(length:--sem-text-meta)" style={inp} />
      </label>
      <label className="mb-2 block text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Server
        <input value={server} onChange={(e) => { setServer(e.target.value); setError(null); }} placeholder="contoso-sql.database.windows.net" autoComplete="off" aria-label="Source server"
          className="mt-1 min-h-7 w-full min-w-0 rounded border px-2 text-(length:--sem-text-meta)" style={inp} />
      </label>
      <label className="mb-2 block text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Database
        <input value={database} onChange={(e) => { setDatabase(e.target.value); setError(null); }} placeholder="Warehouse" autoComplete="off" aria-label="Source database"
          className="mt-1 min-h-7 w-full min-w-0 rounded border px-2 text-(length:--sem-text-meta)" style={inp} />
      </label>
      <div className="mb-2 min-w-0">
        <AccountPicker mode={authMode} onMode={setAuthMode} tenantId={tenantId} onTenantId={setTenantId} tenantLabel="where the database lives (optional)" />
      </div>
      {error && <div role="alert" className="mb-2 text-(length:--sem-text-label)" style={{ color: 'var(--sem-bad)' }}>{error}</div>}
      {tested && <div role="status" className="mb-2 text-(length:--sem-text-label)" style={{ color: tested.ok ? 'var(--sem-ok)' : 'var(--sem-bad)' }}>{tested.note}
        {tested.detail && <details className="mt-1" data-testid="check-add-source-detail"><summary>Details</summary><span style={{ color: 'var(--sem-muted)' }}>{tested.detail}</span></details>}</div>}
      <div className="flex flex-wrap items-center justify-end gap-2">
        <button type="button" className="sem-btn sem-btn-sm" disabled={busy != null} onClick={onCancel} data-testid="check-add-source-cancel">Cancel</button>
        <button type="button" className="sem-btn sem-btn-sm" disabled={busy != null} onClick={() => void run(true)} data-testid="check-add-source-test">{busy === 'test' ? 'Checking…' : 'Save and check'}</button>
        <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy != null} onClick={() => void run(false)} data-testid="check-add-source-save">{busy === 'save' ? 'Saving…' : 'Save'}</button>
      </div>
    </div>
  );
}
