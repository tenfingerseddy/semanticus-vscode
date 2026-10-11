import { useDeferredValue, useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onDidChange, exportDoc, printDoc, openExternal } from './bridge';
import { GiveToAssistant, docsTask } from './sendtask';
import { DraftMark, EditMovedBanner, sameValue, useEditBase } from './editbase';
import { draftModel, draftStore, fenceFor, heldDraft } from './drafts';
import { OrphanDrafts } from './orphandrafts';
import { useAutoGrow, useFillHeight, usePersistedState } from './hooks';
import { useClaudeReflection } from './activity';
import {
  renderDoc, DEFAULT_DOC_CONFIG, DEFAULT_DOC_BRANDING,
  type DocModelDto, type DocConfig, type DocBranding, type DocNarrative,
} from './docrender';
import { DOC_FONTS } from './docfonts';

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
  { key: 'perTableDetail', label: 'Tables' },
  { key: 'columnsDetail', label: 'Columns' },
  { key: 'daxExpressions', label: 'Formulas' },
  { key: 'measuresIndex', label: 'Measures' },
  { key: 'relationships', label: 'Relationships' },
  { key: 'diagram', label: 'Relationship diagram' },
  { key: 'hierarchies', label: 'Drill paths' },
  { key: 'calcGroups', label: 'Calculation groups' },
  { key: 'kpis', label: 'KPIs' },
  { key: 'rls', label: 'Roles and security' },
  { key: 'perspectives', label: 'Perspectives' },
  { key: 'lineage', label: 'Where the data comes from' },
  { key: 'storageStats', label: 'Storage' },
  { key: 'readinessScorecard', label: 'AI readiness grade' },
  { key: 'narrative', label: 'Authored narrative' },
];

// The document follows the reader's light or dark setting. In the preview the reader is the editor, so the preview
// takes the editor's theme (VS Code puts it on the body as a class) and keeps up when the person changes it.
function useEditorTheme(): 'light' | 'dark' {
  const read = () => (document.body.classList.contains('vscode-light') || document.body.classList.contains('vscode-high-contrast-light') ? 'light' : 'dark');
  const [theme, setTheme] = useState<'light' | 'dark'>(read);
  useEffect(() => {
    const mo = new MutationObserver(() => setTheme(read()));
    mo.observe(document.body, { attributes: true, attributeFilter: ['class'] });
    return () => mo.disconnect();
  }, []);
  return theme;
}

// The subtitle an older version saved as its default is not a choice the person made. Drop it so the new header reads right.
const OLD_DEFAULT_SUBTITLE = 'Semantic Model Documentation';

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

  // The two columns that stay as tall as the window while the page scrolls. Their height is measured rather than
  // set in CSS because the page's own top is not a fixed number of pixels down the window. The `dto` key matters:
  // until the model is read this page renders "Loading model documentation…" and neither column exists, so a
  // measurement taken then finds no box and the columns would sit at their floor for ever.
  const [leftRef, leftH] = useFillHeight<HTMLElement>(240, 0, !!dto);
  const [previewRef, previewH] = useFillHeight<HTMLElement>(240, 0, !!dto);

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
    setAgentNote(`Your assistant added “${e.query ?? 'context'}” to ${e.target ?? 'the model'}`);
    window.clearTimeout(agentNoteTimer.current);
    agentNoteTimer.current = window.setTimeout(() => setAgentNote(null), 4000);
  });

  // Merge config with any newly-added engine flags so an older persisted config never drops a section silently.
  const cfg = useMemo<DocConfig>(() => ({ ...DEFAULT_DOC_CONFIG, ...config }), [config]);
  // Defer branding so typing in a branding field doesn't reload the whole preview iframe (+ re-run the dagre SVG) on
  // every keystroke — the heavy structural render still tracks dto/cfg immediately; branding catches up a tick later.
  const deferredBranding = useDeferredValue(branding);
  const theme = useEditorTheme();
  const rendered = useMemo(() => {
    if (!dto) return null;
    const b: DocBranding = { ...DEFAULT_DOC_BRANDING, ...deferredBranding };
    if (b.subtitle === OLD_DEFAULT_SUBTITLE) b.subtitle = '';
    return renderDoc(dto, cfg, b, { fonts: DOC_FONTS, theme });
  }, [dto, cfg, deferredBranding, theme]);

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

  if (loading && !dto) return <div className="h-full w-full flex items-center justify-center p-6 text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>Loading model documentation…</div>;
  if (err && !dto) return <div className="h-full w-full flex items-center justify-center p-4"><div role="alert" className="sem-card flex items-start gap-2"><span className="sem-chip sem-chip-bad shrink-0" aria-hidden="true" />{err}</div></div>;

  return (
    // Three cards on the 12-column grid: what goes in (3), the live preview (6), the narrative (3). The first two
    // are as tall as the window and stay put; the narrative column is as tall as what is written in it, so the PAGE
    // scrolls. See .sem-docs-page for why that is not the other way round.
    <div className="sem-page sem-docs-page sem-text-meta" style={{ color: 'var(--sem-fg)' }}>
      {/* LEFT — inclusions + branding */}
      {/* The space rule (27 September 2026): the long list of Include toggles is what scrolls when the column is short,
          and it takes whatever room Branding leaves. The whole column used to scroll instead, which put the Branding
          fields and Reset to defaults below the fold at 1440 x 900 with no sign that they were there. */}
      <aside ref={leftRef} className="sem-card sem-docs-col sem-span-3 overflow-auto flex flex-col gap-4 min-h-0"
        style={{ '--sem-docs-col-h': `${leftH}px` } as React.CSSProperties}>
        <Section title="Include" className="flex flex-col min-h-0 flex-1">
          <div className="flex flex-col gap-2 min-h-0 flex-1 overflow-y-auto">
            {CONFIG_LABELS.map(({ key, label }) => (
              <label key={key} className="flex items-center gap-2 cursor-pointer">
                <input type="checkbox" checked={!!cfg[key]} onChange={(e) => setConfig({ ...cfg, [key]: e.target.checked })} />
                <span>{label}</span>
              </label>
            ))}
          </div>
        </Section>

        <Section title="Branding" className="shrink-0">
          <div className="flex flex-col gap-2">
            <TextField label="Title" value={branding.title} placeholder={dto?.header.name || 'Model name'} onChange={(v) => setBranding({ ...branding, title: v })} />
            <TextField label="Subtitle" value={branding.subtitle} onChange={(v) => setBranding({ ...branding, subtitle: v })} />
            <TextField label="Company" value={branding.companyName} onChange={(v) => setBranding({ ...branding, companyName: v })} />
            <TextField label="Author" value={branding.author} onChange={(v) => setBranding({ ...branding, author: v })} />
            <TextField label="Date" value={branding.date} placeholder="e.g. June 2026" onChange={(v) => setBranding({ ...branding, date: v })} />
            <TextField label="Footer" value={branding.footerText} onChange={(v) => setBranding({ ...branding, footerText: v })} />
            <div className="flex items-center gap-2">
              <span className="w-14 shrink-0" style={{ color: 'var(--sem-muted)' }}>Logo</span>
              <input type="file" accept="image/*" onChange={(e) => onLogo(e, (uri) => setBranding((b) => ({ ...b, logoDataUri: uri })), setLogoErr)} className="min-w-0" />
            </div>
            {logoErr && <span className="sem-chip sem-chip-warn">{logoErr}</span>}
            {branding.logoDataUri && (
              <div className="flex items-center gap-2">
                <img src={branding.logoDataUri} alt="logo" style={{ maxHeight: 28, maxWidth: 80, border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)' }} />
                <button onClick={() => setBranding({ ...branding, logoDataUri: '' })} className="sem-btn sem-btn-sm">Remove</button>
              </div>
            )}
            <button onClick={() => { setConfig(DEFAULT_DOC_CONFIG); setBranding(DEFAULT_DOC_BRANDING); }} className="sem-btn sem-btn-sm self-start">Reset to defaults</button>
          </div>
        </Section>
      </aside>

      {/* CENTRE — live preview */}
      <section ref={previewRef} className="sem-card sem-card-flush sem-docs-col sem-span-6 flex flex-col"
        style={{ '--sem-docs-col-h': `${previewH}px` } as React.CSSProperties}>
        <div className="sem-card-head">
          <h2 className="sem-card-title">Preview</h2>
          <div className="sem-seg" role="group" aria-label="Preview format">
            <SegBtn active={format === 'html'} onClick={() => setFormat('html')}>HTML</SegBtn>
            <SegBtn active={format === 'markdown'} onClick={() => setFormat('markdown')}>Markdown</SegBtn>
          </div>
          {format === 'html' && <button onClick={doPrint} className="sem-btn sem-btn-sm" title="Opens the documentation in your browser, then print or “Save as PDF” there">Print / PDF</button>}
          <button onClick={doExport} className="sem-btn sem-btn-sm sem-btn-primary" title="Save the documentation to a file">Export…</button>
          {agentNote && <span className="ml-2 truncate" style={{ color: 'var(--sem-muted)' }}>{agentNote}</span>}
          <span className="ml-auto truncate" style={{ color: 'var(--sem-muted)' }}>{dto?.header.name}</span>
        </div>
        <div className="flex-1 min-h-0">
          {format === 'html' ? (
            // sandboxed: no allow-scripts (the doc's inline search is a no-op here by design). allow-same-origin
            // lets the PARENT drive navigation (onPreviewLoad): TOC clicks scroll in place instead of blanking the
            // srcdoc frame, and external links route to the system browser. Print happens host-side (printDoc).
            <iframe ref={iframeRef} title="Documentation preview" srcDoc={rendered?.previewHtml ?? ''} sandbox="allow-same-origin"
              onLoad={onPreviewLoad}
              style={{ width: '100%', height: '100%', border: 'none', background: 'var(--sem-bg)' }} />
          ) : (
            <pre className="h-full overflow-auto p-4 m-0 whitespace-pre-wrap" style={{ fontFamily: 'var(--sem-font-num)', fontSize: 'var(--sem-text-meta)' }}>{rendered?.markdown ?? ''}</pre>
          )}
        </div>
      </section>

      {/* RIGHT — narrative authoring */}
      <NarrativePane dto={dto} outline={outline} onSaved={load} />
    </div>
  );
}

// ---- narrative authoring pane ---------------------------------------------------------------------

export function NarrativePane({ dto, outline, onSaved }: { dto: DocModelDto | null; outline: DocOutline | null; onSaved: () => void }) {
  const [sel, setSel] = useState<string>('model');
  // A section editor copies its words when it opens. Words moved onto a section whose editor is already open must be read by it, or its next keystroke replaces them (Sol join 12, A8-1): a counted key opens that one editor again, on the moved words and their base.
  const [moved, setMoved] = useState<Record<string, number>>({});

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

  // Every held section id (`<object ref>|<section key>`) that is there now. A held scope for an object the assistant renamed or deleted is none of these: it is listed below (join 12, J-D1).
  const liveIds = useMemo(() => outline ? objects.flatMap((o) => {
    const std = SECTION_KEYS[o.kind] ?? SECTION_KEYS.table;
    const existing = (narrativeFor(o.ref)?.sections ?? []).map((s) => s.key);
    return Array.from(new Set([...std, ...existing])).map((k) => `${o.ref}|${k}`);
  }) : null, [objects, outline]);   // eslint-disable-line react-hooks/exhaustive-deps
  const sectionLabel = (id: string) => { const [ref, key] = [id.slice(0, id.lastIndexOf('|')), id.slice(id.lastIndexOf('|') + 1)]; return `${ref.replace(/^(table|measure):/, '')} / ${prettyKey(key)}`; };

  return (
    // No scroller of its own. Every section here carries its own Save, Clear, Copy and Give to your assistant, so
    // they cannot be lifted out of a box that scrolls; the column grows with what is written in it instead and the
    // page scrolls, which is the space rule's own answer for content that is long.
    <aside className="sem-card sem-span-3 flex flex-col gap-3">
      <Section title="Narrative: additional context">
        <div className="mb-2" style={{ color: 'var(--sem-muted)' }}>
          Add business context that merges into the docs, separate from each object's Description. Edits save with the model and are undoable. Your assistant sees them the next time it works here.
        </div>
        <select value={current?.ref} onChange={(e) => setSel(e.target.value)} className="sem-field sem-field-sm w-full">
          {objects.map((o) => {
            const has = (narrativeFor(o.ref)?.sections.length ?? 0) > 0;
            return <option key={o.ref} value={o.ref}>{o.kind === 'model' ? '◆ ' : o.kind === 'table' ? '▤ ' : 'ƒ '}{o.name}{has ? ' •' : ''}</option>;
          })}
        </select>
      </Section>
      {/* Words typed into a section of an object that is not listed now (the assistant renamed it, or deleted it; a ref is its name): kept, and asked about. */}
      <OrphanDrafts prefix="docs|" live={liveIds} what="documentation section" onMoved={(to) => setMoved((m) => ({ ...m, [to]: (m[to] ?? 0) + 1 }))}
        nameOf={sectionLabel}
        targetsFor={(fields, id) => { const key = id.slice(id.lastIndexOf('|') + 1); return (liveIds ?? []).filter((t) => t.slice(t.lastIndexOf('|') + 1) === key).map((t) => ({ id: t, label: sectionLabel(t) })); }} />
      {current && keys.map((key) => (
        <SectionEditor key={current.ref + '::' + key + '::' + (moved[`${current.ref}|${key}`] ?? 0)} objRef={current.ref} objName={current.name} sectionKey={key}
          value={narr?.sections.find((s) => s.key === key)?.markdown ?? ''} onSaved={onSaved} />
      ))}
    </aside>
  );
}

export function SectionEditor({ objRef, objName, sectionKey, value, onSaved }: { objRef: string; objName: string; sectionKey: string; value: string; onSaved: () => void }) {
  // Words typed here and not saved are held above this editor (drafts.ts, the drafts class): a tab switch, another object, a restarted engine and a reload keep them,
  // and a section that moved meanwhile is asked about (Keep mine / Use the new one) when the editor opens on them. They are NOT dropped when the editor unmounts.
  const draftScope = `docs|${objRef}|${sectionKey}`;
  const heldAtMount = useRef(heldDraft<string>(draftScope)).current;
  const modelAtMount = useRef(draftModel()).current;
  const [draft, setDraft] = useState(heldAtMount ? heldAtMount.value : value);
  const growRef = useAutoGrow(draft);
  const [saving, setSaving] = useState(false);
  const draftRef = useRef(draft); draftRef.current = draft;
  const valueRef = useRef(value); valueRef.current = value;

  // Adopt an external change (Claude or another door wrote this section) only when we have no un-saved local edit,
  // so live updates flow in without clobbering what the user is typing. A typed draft that is still here when the
  // section moved is asked about, never saved over the other change (the stale-copy class, UAT 2 Oct).
  // Save reads THIS section from the engine (getDocSection: one small read, not the page's whole-document one) and the write names that value as the one
  // it replaces, so the engine compares it inside its own mutation. The page's copy is a moment behind after a change (it reloads 300 ms later), and a
  // Save inside that moment used to compare the page's old copy with its own old start and write over the newer section (Sol join 6, C4).
  const guard = useEditBase<string>({ current: value ?? '', draft, editing: true, reseed: setDraft, persist: { scope: draftScope },
    readNow: async () => (await rpc<string | null>('getDocSection', objRef, sectionKey)) ?? '' });
  const baseNow = useRef(guard.baseNow); baseNow.current = guard.baseNow;

  // Auto-save an unsaved draft when this editor unmounts (e.g. switching the narrative object in the picker), so a
  // one-click object switch never silently discards typed-but-unsaved narrative. objRef/sectionKey are stable per mount
  // (they're part of the React key). A no-op setDocSection (draft === value) is skipped engine-side, so this is cheap.
  // Only while the section is still what the draft started from: a section the other door rewrote is not overwritten here.
  // A section that moved under a typed draft is never written over: the draft stays in the store and the editor asks when it opens again.
  // The page's copy can be a moment behind (it re-reads the whole document model after a change), so the write reads the section
  // itself first: a section the other door rewrote inside that moment is kept for the person, never written over.
  // The draft ends only when the engine took the write; a refused or failed write (a restarting engine) leaves it held.
  useEffect(() => () => {
    const typed = draftRef.current, started = baseNow.current();
    if (typed === (valueRef.current ?? '')) return;
    if (!sameValue(started, valueRef.current ?? '')) return;
    // The read spares a write that would be refused; the write itself names the section it started from, and the engine compares it inside the write, so a
    // section rewritten in the moment between this read and the write is refused rather than written over, and the words stay held.
    void (async () => {
      let live: string | null | undefined;
      try { live = await rpc<string | null>('getDocSection', objRef, sectionKey, fenceFor(modelAtMount)); }
      catch { return; }   // could not look: keep the words rather than write blind
      if (!sameValue(started ?? '', live ?? '')) return;
      // R-A: the write names the model these words were typed for, and the engine refuses it when another is open (the editor can unmount AFTER a model switch).
      // R-B: the draft ends only when the held words are exactly the ones the engine took (the person may have reopened the editor and typed more while this waited).
      try { await rpc('setDocSection', objRef, sectionKey, typed, 'human', started ?? '', fenceFor(modelAtMount)); draftStore.clearIfSubmitted(modelAtMount, draftScope, '', typed); }
      catch { /* refused (another model is open, or the section moved after the read) or failed: the words stay held */ }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const dirty = draft !== (value ?? '');
  async function save() {
    setSaving(true);
    try {
      if (!(await guard.beforeSave())) return;
      try { await rpc('setDocSection', objRef, sectionKey, draft, 'human', guard.confirmed() ?? '', fenceFor(modelAtMount)); }
      catch (e) { if (await guard.refused(e)) return; throw e; }   // the section moved after the read: the banner asks, nothing was written
      guard.saved(draft);   // R-B: the draft ends only if the words held are still exactly the ones the engine took
      onSaved();
    }
    finally { setSaving(false); }
  }
  // The prompt Ask AI used to copy is now the brief of a Plan task for your assistant, with Copy beside it.
  const askPrompt = () => {
    const prompt = `Use the set_doc_section tool to write the "${sectionKey}" documentation narrative for ${objRef} (the ${objName} ${objRef === 'model' ? 'model' : ''}). This is ADDITIONAL business context for the exported docs: concise, accurate, and separate from the object's Description.`;
    return docsTask(objRef, objName, prettyKey(sectionKey), prompt, sectionKey);
  };

  return (
    // A section of the narrative card, split from the next by a hairline rule rather than boxed (a card never
    // holds another card).
    <div className="pt-3" style={{ borderTop: '1px solid var(--sem-rule)' }}>
      <div className="flex items-center gap-2 mb-1">
        <span className="font-semibold">{prettyKey(sectionKey)}</span>
        {dirty && <span className="sem-chip sem-chip-warn">unsaved</span>}
        <DraftMark show={guard.drafted} />
      </div>
      {/* On its own line: the narrative column is narrow, and beside the heading the two buttons wrapped apart. */}
      <div className="mb-1"><GiveToAssistant sendKey={`docs|${objRef}|${sectionKey}`} build={askPrompt} /></div>
      {/* Grows with its text (four rows is the floor), so a long narrative is not a four-line box that scrolls. */}
      <textarea ref={growRef} value={draft} onChange={(e) => setDraft(e.target.value)} rows={4} placeholder="Markdown… (**bold**, lists, `code`)"
        className="sem-field w-full resize-y overflow-hidden" style={{ fontFamily: 'var(--sem-font-num)', fontSize: 'var(--sem-text-meta)' }} />
      {guard.moved && <EditMovedBanner what="section" theirs={guard.theirs} onKeepMine={guard.keepMine} onUseNew={guard.useNew} disabled={saving} />}
      <div className="flex items-center gap-2 mt-1">
        <button onClick={save} disabled={!dirty || saving} className="sem-btn sem-btn-sm">{saving ? 'Saving…' : 'Save'}</button>
        {draft && <button onClick={() => setDraft('')} className="sem-btn sem-btn-sm sem-btn-quiet">Clear</button>}
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

function Section({ title, children, className }: { title: string; children: React.ReactNode; className?: string }) {
  return (
    <div className={className}>
      <h2 className="sem-card-title mb-2">{title}</h2>
      {children}
    </div>
  );
}
function TextField({ label, value, placeholder, onChange }: { label: string; value: string; placeholder?: string; onChange: (v: string) => void }) {
  return (
    <label className="flex items-center gap-2">
      <span className="w-14 shrink-0" style={{ color: 'var(--sem-muted)' }}>{label}</span>
      <input value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} className="sem-field sem-field-sm flex-1 min-w-0" />
    </label>
  );
}
function SegBtn({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return <button type="button" onClick={onClick} aria-pressed={active} className="sem-seg-item">{children}</button>;
}
