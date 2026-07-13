import { useDeferredValue, useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onDidChange, copyText, exportDoc, printDoc, openExternal } from './bridge';
import { usePersistedState } from './hooks';
import { useClaudeReflection } from './activity';
import {
  renderDoc, DEFAULT_DOC_CONFIG, DEFAULT_DOC_BRANDING,
  type DocModelDto, type DocConfig, type DocBranding, type DocNarrative,
} from './docrender';

// The Documentation tab: a three-pane authoring surface over the same live session as the user's Claude.
//   left   — what to include (DocConfig) + branding (DocBranding)
//   centre — a live preview (HTML in a sandboxed iframe, or the Markdown source), with Print/Save-as-PDF + Export
//   right  — the authored NARRATIVE: pick an object, edit its sections (→ set_doc_section), or ask Claude to write them
// The preview + narrative editor re-read getDocModel on every model/didChange, so Claude's set_doc_section shows live.

type DocOutlineItem = { ref: string; name: string; kind: string; sections: string[] };
type DocOutline = { items: DocOutlineItem[] };

// Standard section keys offered per object kind (existing custom keys are merged in at render time).
const SECTION_KEYS: Record<string, string[]> = {
  model: ['overview', 'glossary', 'methodology'],
  table: ['overview', 'businessContext', 'notes'],
  measure: ['businessContext', 'notes'],
};

const CONFIG_LABELS: { key: keyof DocConfig; label: string }[] = [
  { key: 'perTableDetail', label: 'Per-table detail' },
  { key: 'columnsDetail', label: 'Columns' },
  { key: 'daxExpressions', label: 'DAX expressions' },
  { key: 'measuresIndex', label: 'Measures index' },
  { key: 'relationships', label: 'Relationships' },
  { key: 'diagram', label: 'Relationship diagram' },
  { key: 'hierarchies', label: 'Hierarchies' },
  { key: 'calcGroups', label: 'Calculation groups' },
  { key: 'kpis', label: 'KPIs' },
  { key: 'rls', label: 'Roles & RLS' },
  { key: 'lineage', label: 'Sources & lineage' },
  { key: 'storageStats', label: 'Storage (VertiPaq)' },
  { key: 'readinessScorecard', label: 'AI-readiness scorecard' },
  { key: 'bpaScorecard', label: 'Best-practices summary' },
  { key: 'prepForAi', label: 'Prep-for-AI surface' },
  { key: 'narrative', label: 'Authored narrative' },
  { key: 'hiddenObjects', label: 'Include hidden objects' },
];

function prettyKey(k: string): string {
  return String(k).replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, (c) => c.toUpperCase());
}

export function DocumentationView() {
  const [dto, setDto] = useState<DocModelDto | null>(null);
  const [outline, setOutline] = useState<DocOutline | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const [config, setConfig] = usePersistedState<DocConfig>('doc.config', DEFAULT_DOC_CONFIG);
  const [branding, setBranding] = usePersistedState<DocBranding>('doc.branding', DEFAULT_DOC_BRANDING);
  const [format, setFormat] = usePersistedState<'html' | 'markdown'>('doc.format', 'html');
  const [agentNote, setAgentNote] = useState<string | null>(null);
  const [logoErr, setLogoErr] = useState('');

  const iframeRef = useRef<HTMLIFrameElement>(null);
  const reloadTimer = useRef<number | undefined>(undefined);
  const agentNoteTimer = useRef<number | undefined>(undefined);
  const loadSeq = useRef(0);   // monotonic token so only the most-recently-issued load() applies its (possibly slower) result

  async function load() {
    const myId = ++loadSeq.current;
    try {
      const [d, o] = await Promise.all([rpc<DocModelDto>('getDocModel', 50), rpc<DocOutline>('getDocOutline')]);
      if (myId !== loadSeq.current) return;   // a newer load started (getDocModel awaits VertiPaq → variable latency) — drop this stale snapshot
      setDto(d); setOutline(o); setErr(null);
    } catch (e) { if (myId === loadSeq.current) setErr(String((e as Error).message ?? e)); }
    finally { if (myId === loadSeq.current) setLoading(false); }
  }

  useEffect(() => {
    void load();
    const off = onDidChange(() => {
      window.clearTimeout(reloadTimer.current);
      reloadTimer.current = window.setTimeout(() => void load(), 300);   // coalesce bursts (e.g. a multi-edit)
    });
    return () => { off(); window.clearTimeout(reloadTimer.current); window.clearTimeout(agentNoteTimer.current); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Reflect Claude authoring narrative on the same session (onDidChange also reloads; this surfaces an attribution).
  // Auto-dismiss like the "Copied" indicator so the chip doesn't sit stale once Claude has moved on.
  useClaudeReflection('set_doc_section', (e) => {
    setAgentNote(`AI Assistant added “${e.query ?? 'context'}” to ${e.target ?? 'the model'}`);
    window.clearTimeout(agentNoteTimer.current);
    agentNoteTimer.current = window.setTimeout(() => setAgentNote(null), 4000);
  });

  // Merge config with any newly-added engine flags so an older persisted config never drops a section silently.
  const cfg = useMemo<DocConfig>(() => ({ ...DEFAULT_DOC_CONFIG, ...config }), [config]);
  // Defer branding so typing in a branding field doesn't reload the whole preview iframe (+ re-run the dagre SVG) on
  // every keystroke — the heavy structural render still tracks dto/cfg immediately; branding catches up a tick later.
  const deferredBranding = useDeferredValue(branding);
  const rendered = useMemo(() => (dto ? renderDoc(dto, cfg, deferredBranding) : null), [dto, cfg, deferredBranding]);

  const docName = (dto?.header.name || 'model').replace(/[^\w.-]+/g, '_');

  function doExport() {
    if (!rendered) return;
    if (format === 'markdown') exportDoc('markdown', rendered.markdown, docName);
    else exportDoc('html', rendered.html, docName);
  }
  // window.print() is suppressed inside VS Code's webview host (it silently did nothing) — the host opens
  // the rendered HTML in the SYSTEM browser instead, where Ctrl+P / "Save as PDF" work properly.
  function doPrint() {
    if (rendered) printDoc(rendered.html, docName);
  }

  // The preview iframe is sandboxed WITHOUT scripts, so navigation must be handled from the parent:
  // clicking a TOC fragment link used to navigate the frame away from its srcdoc → a BLANK pane. Intercept
  // clicks on the iframe's document: fragments scroll in place; http(s) links open in the user's browser
  // (the sandbox has no allow-popups, so target=_blank was silently dead too).
  function onPreviewLoad() {
    const doc = iframeRef.current?.contentDocument;
    if (!doc) return;
    doc.addEventListener('click', (e) => {
      const a = (e.target as Element | null)?.closest?.('a');
      if (!a) return;
      const href = a.getAttribute('href') ?? '';
      if (href.startsWith('#')) {
        e.preventDefault();
        doc.getElementById(href.slice(1))?.scrollIntoView({ behavior: 'smooth', block: 'start' });
      } else if (/^https?:\/\//i.test(href)) {
        e.preventDefault();
        openExternal(href);
      } else {
        e.preventDefault();   // anything else would blank the srcdoc frame — refuse the navigation
      }
    });
  }

  if (loading && !dto) return <div className="sem-centered-page p-6 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading model documentation…</div>;
  if (err && !dto) return <div className="sem-centered-page p-4"><div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,var(--sem-bad) 14%, transparent)', color: 'var(--sem-bad)' }}>{err}</div></div>;

  return (
    <div className="sem-centered-page h-full flex min-h-0 text-[12px]">
      {/* LEFT — inclusions + branding */}
      <aside className="w-[260px] shrink-0 overflow-auto border-r p-3 flex flex-col gap-4" style={{ borderColor: 'var(--sem-border)' }}>
        <Section title="Include">
          <div className="flex flex-col gap-1.5">
            {CONFIG_LABELS.map(({ key, label }) => (
              <label key={key} className="flex items-center gap-2 cursor-pointer">
                <input type="checkbox" checked={!!cfg[key]} onChange={(e) => setConfig({ ...cfg, [key]: e.target.checked })} />
                <span>{label}</span>
              </label>
            ))}
          </div>
        </Section>

        <Section title="Branding">
          <div className="flex flex-col gap-2">
            <TextField label="Title" value={branding.title} placeholder={dto?.header.name || 'Model name'} onChange={(v) => setBranding({ ...branding, title: v })} />
            <TextField label="Subtitle" value={branding.subtitle} onChange={(v) => setBranding({ ...branding, subtitle: v })} />
            <TextField label="Company" value={branding.companyName} onChange={(v) => setBranding({ ...branding, companyName: v })} />
            <TextField label="Author" value={branding.author} onChange={(v) => setBranding({ ...branding, author: v })} />
            <TextField label="Date" value={branding.date} placeholder="e.g. June 2026" onChange={(v) => setBranding({ ...branding, date: v })} />
            <TextField label="Footer" value={branding.footerText} onChange={(v) => setBranding({ ...branding, footerText: v })} />
            <div className="flex items-center gap-2">
              <span className="w-14 shrink-0" style={{ color: 'var(--sem-muted)' }}>Accent</span>
              <input type="color" value={/^#[0-9a-f]{6}$/i.test(branding.accentColor) ? branding.accentColor : '#4c6ef5'} onChange={(e) => setBranding({ ...branding, accentColor: e.target.value })} />
              <select value={branding.theme} onChange={(e) => setBranding({ ...branding, theme: e.target.value as 'light' | 'dark' })} style={selStyle}>
                <option value="light">Light</option>
                <option value="dark">Dark</option>
              </select>
            </div>
            <div className="flex items-center gap-2">
              <span className="w-14 shrink-0" style={{ color: 'var(--sem-muted)' }}>Logo</span>
              <input type="file" accept="image/*" onChange={(e) => onLogo(e, (uri) => setBranding((b) => ({ ...b, logoDataUri: uri })), setLogoErr)} className="min-w-0" />
            </div>
            {logoErr && <span style={{ color: 'var(--sem-warn)', fontSize: 11 }}>{logoErr}</span>}
            {branding.logoDataUri && (
              <div className="flex items-center gap-2">
                <img src={branding.logoDataUri} alt="logo" style={{ maxHeight: 28, maxWidth: 80, border: '1px solid var(--sem-border)', borderRadius: 4 }} />
                <button onClick={() => setBranding({ ...branding, logoDataUri: '' })} style={btnGhost}>Remove</button>
              </div>
            )}
            <button onClick={() => { setConfig(DEFAULT_DOC_CONFIG); setBranding(DEFAULT_DOC_BRANDING); }} style={btnGhost}>Reset to defaults</button>
          </div>
        </Section>
      </aside>

      {/* CENTRE — live preview */}
      <main className="flex-1 min-w-0 flex flex-col">
        <div className="flex items-center gap-2 px-3 py-2 border-b" style={{ borderColor: 'var(--sem-border)' }}>
          <div className="inline-flex rounded-md overflow-hidden border" style={{ borderColor: 'var(--sem-border)' }}>
            <SegBtn active={format === 'html'} onClick={() => setFormat('html')}>HTML</SegBtn>
            <SegBtn active={format === 'markdown'} onClick={() => setFormat('markdown')}>Markdown</SegBtn>
          </div>
          {format === 'html' && <button onClick={doPrint} style={btn} title="Opens the documentation in your browser, then print or “Save as PDF” there">Print / PDF</button>}
          <button onClick={doExport} style={btnAccent} title="Save the documentation to a file">Export…</button>
          {agentNote && <span className="ml-2 truncate" style={{ color: 'var(--sem-accent)' }}>{agentNote}</span>}
          <span className="ml-auto" style={{ color: 'var(--sem-muted)' }}>{dto?.header.name}</span>
        </div>
        <div className="flex-1 min-h-0" style={{ background: 'var(--sem-surface-2)' }}>
          {format === 'html' ? (
            // sandboxed: no allow-scripts (the doc's inline search is a no-op here by design). allow-same-origin
            // lets the PARENT drive navigation (onPreviewLoad): TOC clicks scroll in place instead of blanking the
            // srcdoc frame, and external links route to the system browser. Print happens host-side (printDoc).
            <iframe ref={iframeRef} title="Documentation preview" srcDoc={rendered?.html ?? ''} sandbox="allow-same-origin"
              onLoad={onPreviewLoad}
              style={{ width: '100%', height: '100%', border: 'none', background: '#fff' }} />
          ) : (
            <pre className="h-full overflow-auto p-4 m-0 whitespace-pre-wrap" style={{ fontFamily: 'ui-monospace,Consolas,monospace', fontSize: 12 }}>{rendered?.markdown ?? ''}</pre>
          )}
        </div>
      </main>

      {/* RIGHT — narrative authoring */}
      <NarrativePane dto={dto} outline={outline} onSaved={load} />
    </div>
  );
}

// ---- narrative authoring pane ---------------------------------------------------------------------

function NarrativePane({ dto, outline, onSaved }: { dto: DocModelDto | null; outline: DocOutline | null; onSaved: () => void }) {
  const [sel, setSel] = useState<string>('model');

  // The narrative-capable objects (model + tables + measures), with their current authored content from the dto.
  const objects = useMemo(() => {
    const items: { ref: string; name: string; kind: string }[] = [{ ref: 'model', name: dto?.header.name || 'Model', kind: 'model' }];
    if (outline) for (const it of outline.items) if (it.kind !== 'model') items.push({ ref: it.ref, name: it.name, kind: it.kind });
    return items;
  }, [outline, dto]);

  const narrativeFor = (ref: string): DocNarrative | null | undefined => {
    if (ref === 'model') return dto?.modelNarrative;
    if (ref.startsWith('table:')) return dto?.tables.find((t) => t.ref === ref)?.narrative;
    if (ref.startsWith('measure:')) return dto?.measures.find((m) => m.ref === ref)?.narrative;
    return null;
  };

  const current = objects.find((o) => o.ref === sel) ?? objects[0];
  const narr = current ? narrativeFor(current.ref) : null;
  const keys = useMemo(() => {
    const std = SECTION_KEYS[current?.kind ?? 'table'] ?? SECTION_KEYS.table;
    const existing = (narr?.sections ?? []).map((s) => s.key);
    return Array.from(new Set([...std, ...existing]));
  }, [current, narr]);

  return (
    <aside className="w-[320px] shrink-0 overflow-auto border-l p-3 flex flex-col gap-3" style={{ borderColor: 'var(--sem-border)' }}>
      <Section title="Narrative: additional context">
        <div className="mb-1" style={{ color: 'var(--sem-muted)' }}>
          Add business context that merges into the docs, separate from each object's Description. Edits are shared with the AI Assistant live and are undoable.
        </div>
        <select value={current?.ref} onChange={(e) => setSel(e.target.value)} style={{ ...selStyle, width: '100%' }}>
          {objects.map((o) => {
            const has = (narrativeFor(o.ref)?.sections.length ?? 0) > 0;
            return <option key={o.ref} value={o.ref}>{o.kind === 'model' ? '◆ ' : o.kind === 'table' ? '▤ ' : 'ƒ '}{o.name}{has ? ' •' : ''}</option>;
          })}
        </select>
      </Section>
      {current && keys.map((key) => (
        <SectionEditor key={current.ref + '::' + key} objRef={current.ref} objName={current.name} sectionKey={key}
          value={narr?.sections.find((s) => s.key === key)?.markdown ?? ''} onSaved={onSaved} />
      ))}
    </aside>
  );
}

function SectionEditor({ objRef, objName, sectionKey, value, onSaved }: { objRef: string; objName: string; sectionKey: string; value: string; onSaved: () => void }) {
  const [draft, setDraft] = useState(value);
  const [saving, setSaving] = useState(false);
  const [copied, setCopied] = useState(false);
  const baseline = useRef(value);
  const draftRef = useRef(draft); draftRef.current = draft;
  const valueRef = useRef(value); valueRef.current = value;

  // Adopt an external change (Claude or another door wrote this section) only when we have no un-saved local edit,
  // so live updates flow in without clobbering what the user is typing.
  useEffect(() => {
    if (draft === baseline.current) { setDraft(value); baseline.current = value; }
    else baseline.current = value;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value]);

  // Auto-save an unsaved draft when this editor unmounts (e.g. switching the narrative object in the picker), so a
  // one-click object switch never silently discards typed-but-unsaved narrative. objRef/sectionKey are stable per mount
  // (they're part of the React key). A no-op setDocSection (draft === value) is skipped engine-side, so this is cheap.
  useEffect(() => () => {
    if (draftRef.current !== (valueRef.current ?? '')) void rpc('setDocSection', objRef, sectionKey, draftRef.current, 'human');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const dirty = draft !== (value ?? '');
  async function save() {
    setSaving(true);
    try { await rpc('setDocSection', objRef, sectionKey, draft, 'human'); onSaved(); }
    finally { setSaving(false); }
  }
  async function ask() {
    const prompt = `Use the set_doc_section tool to write the "${sectionKey}" documentation narrative for ${objRef} (the ${objName} ${objRef === 'model' ? 'model' : ''}). This is ADDITIONAL business context for the exported docs: concise, accurate, and separate from the object's Description.`;
    const ok = await copyText(prompt);
    setCopied(ok); window.setTimeout(() => setCopied(false), 1800);
  }

  return (
    <div className="rounded-md border p-2" style={{ borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2 mb-1">
        <span className="font-medium">{prettyKey(sectionKey)}</span>
        {dirty && <span style={{ color: 'var(--sem-warn)' }}>•</span>}
        <button onClick={ask} className="ml-auto" style={btnGhost} title="Copy a ready prompt for your AI Assistant">{copied ? 'Copied ✓' : 'Ask AI'}</button>
      </div>
      <textarea value={draft} onChange={(e) => setDraft(e.target.value)} rows={4} placeholder="Markdown… (**bold**, lists, `code`)"
        className="w-full resize-y" style={{ background: 'var(--sem-input-bg, var(--sem-surface-2))', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '6px 8px', fontFamily: 'ui-monospace,Consolas,monospace', fontSize: 12 }} />
      <div className="flex items-center gap-2 mt-1">
        <button onClick={save} disabled={!dirty || saving} style={dirty ? btnAccent : btn}>{saving ? 'Saving…' : 'Save'}</button>
        {draft && <button onClick={() => setDraft('')} style={btnGhost}>Clear</button>}
      </div>
    </div>
  );
}

// ---- small presentational helpers -----------------------------------------------------------------

function onLogo(e: React.ChangeEvent<HTMLInputElement>, set: (uri: string) => void, onError: (msg: string) => void) {
  const input = e.target;
  const f = input.files?.[0];
  input.value = '';                                // always reset so re-picking the SAME file fires a change event
  if (!f) return;
  if (f.size > 512 * 1024) { onError('Logo must be under 512 KB'); return; }   // reject WITHOUT clearing an existing logo
  onError('');
  const r = new FileReader();
  r.onload = () => set(typeof r.result === 'string' ? r.result : '');
  r.readAsDataURL(f);
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div>
      <div className="font-semibold mb-2" style={{ color: 'var(--sem-fg)' }}>{title}</div>
      {children}
    </div>
  );
}
function TextField({ label, value, placeholder, onChange }: { label: string; value: string; placeholder?: string; onChange: (v: string) => void }) {
  return (
    <label className="flex items-center gap-2">
      <span className="w-14 shrink-0" style={{ color: 'var(--sem-muted)' }}>{label}</span>
      <input value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} className="flex-1 min-w-0"
        style={{ background: 'var(--sem-input-bg, var(--sem-surface-2))', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '3px 6px', fontSize: 12 }} />
    </label>
  );
}
function SegBtn({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return <button onClick={onClick} className="px-2.5 py-1 text-[11px]" style={{ background: active ? 'var(--sem-accent-soft)' : 'transparent', color: active ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>{children}</button>;
}

const btn: React.CSSProperties = { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 5, fontSize: 11, padding: '3px 9px', cursor: 'pointer' };
const btnAccent: React.CSSProperties = { ...btn, background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' };
const btnGhost: React.CSSProperties = { background: 'transparent', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)', borderRadius: 5, fontSize: 11, padding: '2px 7px', cursor: 'pointer' };
const selStyle: React.CSSProperties = { background: 'var(--sem-input-bg, var(--sem-surface-2))', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, fontSize: 11, padding: '2px 6px' };
