import { useEffect, useState } from 'react';
import { exportDoc, printDoc } from './bridge';
import { usePersistedState } from './hooks';

export interface EvidenceArtifactW {
  html?: string; json?: string; contentHash?: string; markdown?: string; note?: string; error?: string;
}
export interface EvidenceSaveResultW {
  saved: boolean; item?: { id?: string; kind?: string; contentHash?: string }; note?: string; error?: string;
}

export function EvidenceArtifactDialog({ title, subtitle, baseName, stateKey, load, save, includeMarkdown = false, onSaved, onClose }: {
  title: string; subtitle: string; baseName: string; stateKey: string;
  load: () => Promise<EvidenceArtifactW>; save?: () => Promise<EvidenceSaveResultW>; includeMarkdown?: boolean;
  onSaved?: () => void; onClose: () => void;
}) {
  const [format, setFormat] = usePersistedState<'html' | 'markdown' | 'json'>(stateKey, 'html');
  const [artifact, setArtifact] = useState<EvidenceArtifactW | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveNote, setSaveNote] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    load().then((next) => {
      if (cancelled) return;
      setArtifact(next);
      if (next.error) setError(next.error);
      else if (!next.html && !next.json && !next.markdown && !next.note) setError('The engine returned no evidence artifact.');
      if (!next.html && next.json) setFormat('json');
    }).catch((reason: unknown) => { if (!cancelled) setError(reason instanceof Error ? reason.message : String(reason)); });
    return () => { cancelled = true; };
    // The loader describes this dialog instance and must run once; live changes create a new keyed dialog.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const key = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') onClose(); };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [onClose]);

  const formats = includeMarkdown ? (['html', 'markdown', 'json'] as const) : (['html', 'json'] as const);
  const content = format === 'html' ? artifact?.html : format === 'json' ? artifact?.json : artifact?.markdown;
  const doExport = () => {
    if (!content) return;
    exportDoc(format, content, baseName, {
      saveLabel: `Export ${format === 'html' ? 'HTML' : format === 'json' ? 'JSON evidence' : 'Markdown'} report`,
      successLabel: title,
    });
  };
  const doSave = () => {
    if (!save || !artifact?.json || saving) return;
    setSaving(true); setSaveNote(null);
    save().then((result) => {
      setSaveNote(result.saved ? 'Saved with the model for source control and team review.' : result.error ?? result.note ?? 'The evidence was not saved.');
      if (result.saved) onSaved?.();
    }).catch((reason: unknown) => setSaveNote(reason instanceof Error ? reason.message : String(reason))).finally(() => setSaving(false));
  };

  return (
    <div className="fixed inset-0 z-[80] flex items-center justify-center p-5" role="presentation"
      style={{ background: 'color-mix(in srgb, var(--sem-bg) 72%, transparent)' }} onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}>
      <section role="dialog" aria-modal="true" aria-label={title} className="flex h-[min(860px,92vh)] w-[min(1180px,94vw)] min-h-0 flex-col overflow-hidden rounded-xl border shadow-2xl"
        style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)' }}>
        <header className="flex items-center gap-3 border-b px-4 py-3" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
          <div className="min-w-0 flex-1"><h2 className="m-0 text-[14px] font-semibold">{title}</h2><p className="m-0 mt-0.5 truncate text-[11px]" style={{ color: 'var(--sem-muted)' }}>{subtitle}</p></div>
          <button autoFocus onClick={onClose} className="rounded-md border px-2.5 py-1 text-[12px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>Close</button>
        </header>
        <div className="flex items-center gap-2 border-b px-4 py-2" style={{ borderColor: 'var(--sem-border)' }}>
          <div className="inline-flex overflow-hidden rounded-md border" style={{ borderColor: 'var(--sem-border)' }}>
            {formats.map((item) => <button key={item} onClick={() => setFormat(item)} disabled={!!artifact && !artifact[item]}
              className="px-3 py-1 text-[11px] font-medium capitalize disabled:opacity-40" style={format === item ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)' }}>{item}</button>)}
          </div>
          {format === 'html' && <button onClick={() => artifact?.html && printDoc(artifact.html, baseName)} disabled={!artifact?.html}
            className="rounded-md border px-3 py-1 text-[11px] disabled:opacity-40" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }} title="Open the report in your browser to print or save as PDF">Print / PDF</button>}
          <button onClick={doExport} disabled={!content || !!error} className="rounded-md border px-3 py-1 text-[11px] font-semibold disabled:opacity-40"
            style={{ borderColor: 'var(--sem-accent)', background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Export…</button>
          {save && <button onClick={doSave} disabled={!artifact?.json || !!error || saving} className="rounded-md border px-3 py-1 text-[11px] font-semibold disabled:opacity-40"
            style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', color: 'var(--sem-fg)' }} title="Write the sealed JSON and HTML beside this model; export alone writes nothing">{saving ? 'Saving…' : 'Save with model'}</button>}
          <span className="ml-auto max-w-[48%] truncate text-[10px]" title={artifact?.contentHash} style={{ color: 'var(--sem-muted)' }}>{artifact?.contentHash ? `SHA-256 ${artifact.contentHash}` : format === 'json' ? 'Canonical evidence record' : 'Self-contained HTML'}</span>
        </div>
        {saveNote && <div className="border-b px-4 py-2 text-[11px]" style={{ borderColor: 'var(--sem-border)', color: saveNote.startsWith('Saved') ? 'var(--sem-good)' : 'var(--sem-warn)' }}>{saveNote}</div>}
        <div className="min-h-0 flex-1" style={{ background: 'var(--sem-surface-2)' }}>
          {!artifact && !error ? <div className="p-6 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Building the evidence artifact…</div>
            : error ? <Message color="var(--sem-bad)">{error}</Message>
              : artifact?.note && !content ? <Message color="var(--sem-warn)">{artifact.note}</Message>
                : format === 'html' ? <iframe title={`${title} HTML preview`} srcDoc={artifact?.html ?? ''} sandbox="" className="h-full w-full border-0" style={{ background: '#fff' }} />
                  : <pre className="m-0 h-full overflow-auto whitespace-pre-wrap p-5 text-[12px]" style={{ fontFamily: 'ui-monospace,Consolas,monospace', color: 'var(--sem-fg)' }}>{content ?? ''}</pre>}
        </div>
      </section>
    </div>
  );
}

function Message({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="m-4 rounded-md border px-3 py-2 text-[12px]" style={{ color, borderColor: `color-mix(in srgb, ${color} 45%, transparent)`, background: `color-mix(in srgb, ${color} 10%, transparent)` }}>{children}</div>;
}
