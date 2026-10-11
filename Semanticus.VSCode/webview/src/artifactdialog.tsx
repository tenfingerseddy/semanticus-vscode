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
      else if (!next.html && !next.json && !next.markdown && !next.note) setError('There is no report to show.');
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
      saveLabel: `Export ${format === 'html' ? 'HTML' : format === 'json' ? 'JSON report' : 'Markdown'} report`,
      successLabel: title,
    });
  };
  const doSave = () => {
    if (!save || !artifact?.json || saving) return;
    setSaving(true); setSaveNote(null);
    save().then((result) => {
      setSaveNote(result.saved ? 'Saved with the model for source control and team review.' : result.error ?? result.note ?? 'The report was not saved.');
      if (result.saved) onSaved?.();
    }).catch((reason: unknown) => setSaveNote(reason instanceof Error ? reason.message : String(reason))).finally(() => setSaving(false));
  };

  return (
    <div className="fixed inset-0 z-[80] flex items-center justify-center p-4" role="presentation"
      style={{ background: 'color-mix(in srgb, var(--sem-bg) 72%, transparent)' }} onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}>
      <section role="dialog" aria-modal="true" aria-label={title} className="flex h-[min(860px,92vh)] w-[min(1180px,94vw)] min-h-0 flex-col overflow-hidden rounded-(--sem-radius-card) border shadow-2xl"
        style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)' }}>
        <header className="flex items-center gap-3 border-b px-4 py-3" style={{ borderColor: 'var(--sem-rule)' }}>
          <div className="min-w-0 flex-1"><h2 className="sem-card-title">{title}</h2><p className="sem-text-meta m-0 mt-1 truncate">{subtitle}</p></div>
          <button autoFocus onClick={onClose} className="sem-btn sem-btn-sm">Close</button>
        </header>
        <div className="flex items-center gap-2 border-b px-4 py-2" style={{ borderColor: 'var(--sem-rule)' }}>
          <div className="sem-seg" role="group" aria-label="Format">
            {formats.map((item) => <button key={item} type="button" onClick={() => setFormat(item)} disabled={!!artifact && !artifact[item]}
              aria-pressed={format === item} className="sem-seg-item capitalize disabled:opacity-40">{item}</button>)}
          </div>
          {format === 'html' && <button onClick={() => artifact?.html && printDoc(artifact.html, baseName)} disabled={!artifact?.html}
            className="sem-btn sem-btn-sm" title="Open the report in your browser to print or save as PDF">Print / PDF</button>}
          <button onClick={doExport} disabled={!content || !!error} className="sem-btn sem-btn-sm sem-btn-primary"
            title={!content ? 'Nothing to export yet. Run the suite, then open Report again.' : 'Save a local copy of this report'}
            >Export…</button>
          {save && <button onClick={doSave} disabled={!artifact?.json || !!error || saving} className="sem-btn sem-btn-sm"
            title="Save the report and its data beside this model. Export saves a separate copy.">{saving ? 'Saving…' : 'Save with model'}</button>}
          <span className="sem-text-meta sem-num ml-auto max-w-[48%] truncate" title={artifact?.contentHash}>{artifact?.contentHash ? `SHA-256 ${artifact.contentHash}` : format === 'json' ? 'The report as stored data' : 'Self-contained HTML'}</span>
        </div>
        {saveNote && <div className="border-b px-4 py-2 text-xs flex items-baseline gap-2" style={{ borderColor: 'var(--sem-rule)', color: 'var(--sem-fg)' }}>
          <span aria-hidden className="w-1 h-1 shrink-0 rounded-full" style={{ background: saveNote.startsWith('Saved') ? 'var(--sem-accent)' : 'var(--sem-warn)', transform: 'translateY(-2px)' }} />{saveNote}</div>}
        <div className="min-h-0 flex-1" style={{ background: 'var(--sem-surface-2)' }}>
          {!artifact && !error ? <div className="p-6 text-xs" style={{ color: 'var(--sem-muted)' }}>Preparing the report…</div>
            : error ? <Message color="var(--sem-bad)">{error}</Message>
              : artifact?.note && !content ? <Message color="var(--sem-warn)">{artifact.note}</Message>
                : format === 'html' ? <iframe title={`${title} HTML preview`} srcDoc={artifact?.html ?? ''} sandbox="" className="h-full w-full border-0" style={{ background: '#fff' }} />
                  : <pre className="m-0 h-full overflow-auto whitespace-pre-wrap p-4 text-xs" style={{ fontFamily: 'ui-monospace,Consolas,monospace', color: 'var(--sem-fg)' }}>{content ?? ''}</pre>}
        </div>
      </section>
    </div>
  );
}

function Message({ children, color }: { children: React.ReactNode; color: string }) {
  // Instrument: the words in the text colour, a dot in the state's colour, on the page's own ground.
  return <div className="m-4 flex items-baseline gap-2 text-xs" style={{ color: 'var(--sem-fg)' }}>
    <span aria-hidden className="w-1 h-1 shrink-0 rounded-full" style={{ background: color, transform: 'translateY(-2px)' }} />{children}</div>;
}
