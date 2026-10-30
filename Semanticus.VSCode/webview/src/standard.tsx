// Review > Your standard (Kane picked option B, 18 Oct 2026). Three presets (Light, Balanced, Strict), each with its counts on this model; under them only the lines that
// differ from the preset, and "Change one thing"; on the right what applying does here, before anything is written, and one primary, Apply to this model. A count press shows
// the changes before and after (option A's idea). File shows the standard as its file, with "Copy as a page" (option C's idea).
// The page runs nothing of its own: it reads and writes through the same four engine calls the assistant uses (get_standard, set_standard, preview_standard, apply_standard).
// One 12-column grid, tokens only.
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { copyText, navigateInStudio, onActivity, onDidChange, openFileInEditor, rpc } from './bridge';
import {
  EXAMPLES_PER_PART, PRESET_IDS, WHO_WORDS, appliedFromWire, choiceOf, countWords, effectWords, gradeOf, loadError, moreThanShown, overrides, partOf, presetOf, presetPatch, previewFromWire, standardAsPage, viewFromWire, wirePatch, wholeStandard,
  type Applied, type CataloguePart, type PartEffect, type PartId, type PresetId, type Preview, type StandardPatch, type StandardView, type WireApplied, type WireGet, type WirePreview,
} from './standard-model';
import './standard.css';

// ---------------------------------------------------------------- what the page reads
type Previews = Partial<Record<PresetId, Preview>>;

// The Applied card (what was done, the Plan rows, Undo) outlives a visit to the Plan: it stays until the model or the standard changes, so Undo is still here on return.
let keptApplied: Applied | null = null;

function useStandard() {
  const [view, setView] = useState<StandardView | null>(null);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [presets, setPresets] = useState<Previews>({});
  const [error, setError] = useState<string | null>(null);
  const [applied, setApplied] = useState<Applied | null>(keptApplied);
  const [busy, setBusy] = useState(false);
  const alive = useRef(true);
  const seq = useRef(0);

  // The counts are read from the engine for the standard in force and for each preset with the person's own lines kept on top.
  const readCounts = useCallback(async (v: StandardView, mine: number) => {
    // Each preview is a rehearsal on the open model: nothing is written. One that fails leaves a gap in its own count, not in the page.
    const ask = (body: string | null) => rpc<WirePreview>('previewStandard', ...(body ? [body] : [])).then(previewFromWire).catch(() => null);
    const [now, ...each] = await Promise.all([
      ask(null),
      ...PRESET_IDS.map((id) => { const patch = presetPatch(v, id); return patch ? ask(wirePatch(v, patch)) : Promise.resolve(null); }),
    ]);
    if (!alive.current || mine !== seq.current) return;
    setPreview(now);
    if (now && now.now > 0 && keptApplied) { keptApplied = null; setApplied(null); }   // the model moved on (an undo from elsewhere, a new change): the card is stale
    setPresets(Object.fromEntries(PRESET_IDS.map((id, i) => [id, each[i]]).filter(([, p]) => !!p)) as Previews);
  }, []);

  const load = useCallback(async () => {
    const mine = ++seq.current;
    try {
      const v = viewFromWire(await rpc<WireGet>('getStandard'));
      if (!alive.current || mine !== seq.current) return;
      setView(v);
      setError(null);
      await readCounts(v, mine);
    } catch (e) {
      if (alive.current && mine === seq.current) setError(loadError(e));
    }
  }, [readCounts]);

  useEffect(() => {
    alive.current = true;
    void load();
    // Your assistant can change the standard in words; a model change moves the counts. Either way the page reads again.
    const offActivity = onActivity((e) => { if (e.kind === 'set_standard' || e.kind === 'apply_standard') void load(); });
    let timer: number | undefined;
    const offChange = onDidChange(() => { window.clearTimeout(timer); timer = window.setTimeout(() => void load(), 400); });
    return () => { alive.current = false; offActivity(); offChange(); window.clearTimeout(timer); };
  }, [load]);

  /** Change the standard (a patch). The file is written; the model is not. */
  const change = useCallback(async (patch: StandardPatch) => {
    keptApplied = null; setApplied(null);
    try {
      // Pointing a model at Your default needs the file to exist: if there is none yet, the standard in force is saved there first.
      if (patch.use === 'default' && view && !view.userExists) await rpc('setStandard', wirePatch(view, wholeStandard(view)), 'your-default');
      const body = view ? wirePatch(view, patch) : null;
      if (!body) return false;
      await rpc('setStandard', body);
      await load();
      return true;
    } catch (e) { setError(loadError(e)); return false; }
  }, [view, load]);

  const apply = useCallback(async () => {
    setBusy(true);
    try {
      const done = appliedFromWire(await rpc<WireApplied>('applyStandard'));
      keptApplied = done;
      if (alive.current) { setApplied(done); setError(null); }
      await load();
    } catch (e) { if (alive.current) setError(loadError(e)); }
    finally { if (alive.current) setBusy(false); }
  }, [load]);

  const undo = useCallback(async () => {
    try {
      await rpc('undo', 'human');
      keptApplied = null;
      if (alive.current) setApplied(null);
      await load();
    } catch (e) { if (alive.current) setError(loadError(e)); }
  }, [load]);

  return { view, preview, presets, error, applied, busy, change, apply, undo, retry: load };
}

// ---------------------------------------------------------------- small parts
function usePop(): [boolean, (v?: boolean) => void, React.RefObject<HTMLDivElement | null>] {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const down = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', down); document.addEventListener('keydown', key);
    return () => { document.removeEventListener('mousedown', down); document.removeEventListener('keydown', key); };
  }, [open]);
  return [open, (v) => setOpen(v ?? !open), ref];
}

const Caret = () => <svg className="st-caret" width="10" height="10" viewBox="0 0 10 10" aria-hidden="true"><path d="M2 3.5 5 6.5 8 3.5" fill="none" stroke="currentColor" strokeWidth="1.4" /></svg>;

/** A menu of one part's choices. */
function ChoiceMenu({ part, current, onPick, testid, label }: { part: CataloguePart; current: string; onPick: (id: string) => void; testid: string; label?: ReactNode }) {
  const [open, toggle, ref] = usePop();
  const cur = part.choices.find((c) => c.id === current);
  return <div className="st-rel" ref={ref}>
    <button type="button" className="st-value" aria-expanded={open} aria-haspopup="menu" onClick={() => toggle()} data-testid={testid}>{label ?? cur?.short ?? cur?.label ?? current}<Caret /></button>
    {open && <div className="st-pop" role="menu">
      <div className="st-pop-head">{part.ask}</div>
      {part.choices.map((c) => <button key={c.id} type="button" role="menuitemradio" aria-checked={c.id === current} className="st-menuitem" onClick={() => { onPick(c.id); toggle(false); }} data-testid={`${testid}-${c.id}`}>
        <span className="st-tick">{c.id === current ? '✓' : ''}</span><span className="st-menuitem-l">{c.label}</span></button>)}
    </div>}
  </div>;
}

/** Change one thing: pick a part, then pick its choice. */
function ChangeOne({ view, onPick }: { view: StandardView; onPick: (id: PartId, choice: string) => void }) {
  const [open, toggle, ref] = usePop();
  const [part, setPart] = useState<PartId | null>(null);
  const p = part ? partOf(view, part) : null;
  return <div className="st-rel st-push" ref={ref}>
    <button type="button" className="sem-btn sem-btn-sm" aria-expanded={open} aria-haspopup="menu" onClick={() => { setPart(null); toggle(); }} data-testid="st-change-one">Change one thing<Caret /></button>
    {open && <div className="st-pop st-pop-right" role="menu">
      {!p ? view.parts.filter((x) => x.choices.length > 1).map((x) => <button key={x.id} type="button" className="st-menuitem" onClick={() => setPart(x.id)} data-testid={`st-c1-${x.id}`}>
        <span className="st-menuitem-l">{x.name}</span><span className="st-menuitem-n">{choiceOf(view, x.id)?.short ?? choiceOf(view, x.id)?.label}</span></button>)
        : <>
          <button type="button" className="st-pop-head st-back" onClick={() => setPart(null)}>‹ {p.name}</button>
          {p.choices.map((c) => <button key={c.id} type="button" role="menuitemradio" aria-checked={view.picks[p.id] === c.id} className="st-menuitem" onClick={() => { onPick(p.id, c.id); toggle(false); }} data-testid={`st-c1-${p.id}-${c.id}`}>
            <span className="st-tick">{view.picks[p.id] === c.id ? '✓' : ''}</span><span className="st-menuitem-l">{c.label}</span></button>)}
        </>}
    </div>}
  </div>;
}

/** The examples under a count that was pressed: what it changes, before and after. */
function Examples({ e }: { e: PartEffect }) {
  if (!e.examples.length) return <p className="st-faint st-ex-none" data-testid="st-examples">Nothing on this model changes.</p>;
  const more = moreThanShown(e);
  return <div className="st-ex" data-testid="st-examples">
    {e.examples.map((x) => <div key={x.what + x.to} className="st-ex-row"><span className="st-ex-what">{x.what}</span>
      {x.to ? <><span className="st-faint st-ex-from">{x.from || 'nothing'}</span><span className="st-arrow" aria-hidden="true">→</span><span className="st-ex-to">{x.to}</span></> : null}</div>)}
    {more > 0 && <div className="st-faint st-ex-more">and {more} more</div>}
  </div>;
}

/** Where the standard comes from, and the way to move it. */
function Head({ view, file, onFile, onUse }: { view: StandardView; file: boolean; onFile: () => void; onUse: (use: string | null) => Promise<boolean> }) {
  const [open, toggle, ref] = usePop();
  const [path, setPath] = useState('');
  const pathNow = useRef(path); pathNow.current = path;
  const where = view.origin === 'builtin' ? 'Balanced, built in' : view.origin === 'default' ? 'Your default, on this computer' : view.where;
  return <div className="sem-span-12 st-head" data-testid="st-head">
    <h1 className="st-title">Your standard</h1>
    <div className="st-rel" ref={ref}>
      <button type="button" className="sem-btn st-picker" aria-expanded={open} aria-haspopup="menu" onClick={() => toggle()} data-testid="st-picker">{view.name}<Caret /></button>
      {open && <div className="st-pop" role="menu">
        <div className="st-pop-head">Use the standard from</div>
        <button type="button" role="menuitemradio" aria-checked={view.origin === 'model'} className="st-menuitem" onClick={() => { onUse(null); toggle(false); }} data-testid="st-use-model">
          <span className="st-tick">{view.origin === 'model' ? '✓' : ''}</span><span className="st-menuitem-l">This model's own file</span></button>
        <button type="button" role="menuitemradio" aria-checked={view.origin === 'default'} className="st-menuitem" onClick={() => { onUse('default'); toggle(false); }} data-testid="st-use-default">
          <span className="st-tick">{view.origin === 'default' ? '✓' : ''}</span><span className="st-menuitem-l">{view.userExists || view.origin === 'default' ? 'Your default' : 'Save this as your default'}</span></button>
        <div className="st-pop-rule" />
        <form className="st-shared" onSubmit={async (e) => {
          e.preventDefault();
          const sent = path, p = sent.trim();
          if (p && await onUse(p) && pathNow.current === sent) { setPath(''); toggle(false); }
        }}>
          <input className="st-shared-in" value={path} onChange={(e) => setPath(e.target.value)} placeholder="A shared file or web address" aria-label="A shared file or web address" data-testid="st-shared-path" />
          <button type="submit" className="sem-btn sem-btn-sm" data-testid="st-shared-use">Use</button>
        </form>
      </div>}
    </div>
    <span className="st-where" data-testid="st-where">{where}</span>
    <button type="button" className="sem-btn st-push" aria-pressed={file} onClick={onFile} data-testid="st-file">File</button>
  </div>;
}

/** The standard as its file, in place of the presets. */
function FileView({ view }: { view: StandardView }) {
  const [copied, setCopied] = useState<'file' | 'page' | null>(null);
  const copy = (what: 'file' | 'page') => { void copyText(what === 'file' ? view.yaml : standardAsPage(view)).then((ok) => { if (ok) { setCopied(what); window.setTimeout(() => setCopied(null), 1500); } }); };
  const name = view.path ? view.path.split(/[\\/]/).pop() : 'Balanced, built in';
  return <div className="sem-card st-file" data-testid="st-fileview">
    <div className="st-sec-head"><span className="st-sec-name">{name}</span>
      <span className="st-push st-row-gap">
        <button type="button" className="sem-btn sem-btn-sm" onClick={() => copy('file')} data-testid="st-copy-file">{copied === 'file' ? 'Copied' : 'Copy'}</button>
        <button type="button" className="sem-btn sem-btn-sm" onClick={() => copy('page')} data-testid="st-copy-page">{copied === 'page' ? 'Copied' : 'Copy as a page'}</button>
        {view.path && <button type="button" className="sem-btn sem-btn-sm" onClick={() => openFileInEditor(view.path)} data-testid="st-open-file">Open in the editor</button>}
      </span></div>
    <pre className="st-yaml">{view.yaml}</pre>
    {view.hasComments && <p className="st-faint st-line" data-testid="st-comments">Changing the standard on this page rewrites the file without its comments. Edit the file in the editor to keep them.</p>}
  </div>;
}

const Shift = ({ label, tri }: { label: string; tri: [number, number, number] }) => {
  const [from, to, later] = tri;
  return <div className="st-shift"><span className="st-shift-l">{label}</span>
    <span className="st-num">{gradeOf(from)} {from}</span><span className="st-arrow" aria-hidden="true">→</span><span className="st-num st-strong">{gradeOf(to)} {to}</span>
    <span className="st-faint st-shift-later">{later > to ? `${gradeOf(later)} ${later} once the Plan is done` : ''}</span></div>;
};

/** What applying does on this model, before anything is written; then, once applied, what was done and what went to the Plan. */
function Summary({ preview, applied, busy, onApply, onUndo }: { preview: Preview | null; applied: Applied | null; busy: boolean; onApply: () => void; onUndo: () => void }) {
  if (applied) {
    return <div className="sem-card sem-span-4 st-sum" data-testid="st-summary">
      <div className="st-label">Applied</div>
      <p className="st-line" data-testid="st-applied">{applied.changed
        ? <><b className="st-num">{applied.changed}</b> {applied.changed === 1 ? 'change' : 'changes'} made{applied.words ? `: ${applied.words}` : ''}. Undo puts {applied.changed === 1 ? 'it' : 'them all'} back.</>
        : 'The model already meets this standard. Nothing needed changing.'}</p>
      {applied.tasks.length > 0 && <>
        <div className="st-label">On the Plan</div>
        <ul className="st-tasks" data-testid="st-tasks">{applied.tasks.map((t) => <li key={t.part} className="st-task">
          <span className={`st-doer st-doer-${t.doer}`} aria-hidden="true" /><span>{t.text}</span><span className="st-faint st-task-who">{WHO_WORDS[t.doer]}</span></li>)}</ul></>}
      <div className="st-row-gap">
        {applied.planRows > 0 && <button type="button" className="sem-btn sem-btn-primary" onClick={() => navigateInStudio('plan')} data-testid="st-open-plan">Open the Plan</button>}
        {applied.changed > 0 && <button type="button" className="sem-btn" onClick={onUndo} data-testid="st-undo">Undo</button>}
      </div>
    </div>;
  }
  return <div className="sem-card sem-span-4 st-sum" data-testid="st-summary">
    <div className="st-label">On this model</div>
    <div className="st-figs">
      <div><div className="st-fig st-num" data-testid="st-now">{preview ? preview.now : '·'}</div><div className="st-fig-l">changes now</div></div>
      <div><div className="st-fig st-num" data-testid="st-plan">{preview ? preview.tasks.length : '·'}</div><div className="st-fig-l">tasks for the Plan</div></div>
    </div>
    {preview?.words && <p className="st-faint st-meta" data-testid="st-parts">{preview.words}</p>}
    {(preview?.air || preview?.bpa) && <div className="st-shifts" data-testid="st-grades">
      {preview.air && <Shift label="AI readiness" tri={preview.air} />}
      {preview.bpa && <Shift label="Model quality" tri={preview.bpa} />}
    </div>}
    <button type="button" className="sem-btn sem-btn-primary st-apply" disabled={!preview || busy} onClick={onApply} data-testid="st-apply">{busy ? 'Applying' : 'Apply to this model'}</button>
  </div>;
}

// ---------------------------------------------------------------- the page
/** The first example of each part a preset changes, so a count press on a preset shows the kind of change without a long list. */
function presetExamples(view: StandardView, p: Preview): { part: CataloguePart; e: PartEffect }[] {
  return view.parts.map((part) => ({ part, e: p.parts[part.id] })).filter((x) => x.e && x.e.examples.length > 0);
}

export function StandardView() {
  const s = useStandard();
  const [file, setFile] = useState(false);
  const [open, setOpen] = useState<string | null>(null);   // 'preset:<id>' or 'part:<id>': which count shows its examples
  const toggleOpen = (k: string) => setOpen((x) => (x === k ? null : k));

  if (!s.view) {
    return <div className="sem-page st-page" data-testid="st-page">
      <div className="sem-span-12 st-head"><h1 className="st-title">Your standard</h1></div>
      <div className="sem-card sem-span-8 st-note" data-testid="st-note">
        <p className="st-line">{s.error ?? 'Reading your standard.'}</p>
        {s.error && <div><button type="button" className="sem-btn" onClick={() => void s.retry()} data-testid="st-retry">Try again</button></div>}
      </div>
    </div>;
  }
  const v = s.view;
  const base = presetOf(v, v.startsFrom);
  const ov = overrides(v);
  const pick = (id: PartId, choice: string) => void s.change({ picks: { [id]: choice } });
  const left = file ? <FileView view={v} /> : <>
    <div className="st-presets" role="radiogroup" aria-label="Start from" data-testid="st-presets">
      {v.presets.map((p) => {
        const pv = s.presets[p.id];
        const on = v.startsFrom === p.id;
        return <div key={p.id} className="st-preset" data-on={on || undefined}>
          <button type="button" role="radio" aria-checked={on} className="st-preset-pick" onClick={() => { const patch = presetPatch(v, p.id); if (patch && !on) void s.change(patch); }} data-testid={`st-preset-${p.id}`}>
            <span className="st-preset-name">{p.name}</span><span className="st-preset-line">{p.line}</span></button>
          {pv ? <button type="button" className="st-link st-preset-n st-num" aria-expanded={open === `preset:${p.id}`} onClick={() => toggleOpen(`preset:${p.id}`)} data-testid={`st-preset-n-${p.id}`}>{countWords({ now: pv.now, plan: pv.tasks.length })}</button>
            : <span className="st-preset-n st-faint">·</span>}
        </div>;
      })}
    </div>
    {open?.startsWith('preset:') && (() => {
      const id = open.slice(7) as PresetId; const pv = s.presets[id]; if (!pv) return null;
      const rows = presetExamples(v, pv); const shown = rows.reduce((n, r) => n + Math.min(EXAMPLES_PER_PART, r.e.examples.length), 0);
      const more = Math.max(0, pv.now + pv.plan - shown);
      return <div className="sem-card st-exbox" data-testid="st-preset-examples">
        <div className="st-label">{presetOf(v, id)?.name} on this model</div>
        {rows.length === 0 ? <p className="st-faint st-ex-none">Nothing on this model changes.</p>
          : <div className="st-ex">{rows.flatMap(({ part, e }) => e.examples.slice(0, EXAMPLES_PER_PART).map((x) => <div key={part.id + x.what + x.to} className="st-ex-row"><span className="st-ex-what">{part.name}: {x.what}</span>
            {x.to ? <><span className="st-faint st-ex-from">{x.from || 'nothing'}</span><span className="st-arrow" aria-hidden="true">→</span><span className="st-ex-to">{x.to}</span></> : null}</div>))}
            {more > 0 && <div className="st-faint st-ex-more">and {more} more</div>}</div>}
      </div>;
    })()}
    <div className="sem-card st-over" data-testid="st-overrides">
      <div className="st-sec-head"><span className="st-sec-name">Your changes to {base?.name ?? 'the preset'}</span><ChangeOne view={v} onPick={pick} /></div>
      {ov.length === 0 ? <p className="st-faint st-line">None. {base?.name} as it comes.</p>
        : ov.map((p) => {
          const c = choiceOf(v, p.id); const was = p.choices.find((x) => x.id === base?.picks[p.id]); const eff = s.preview?.parts[p.id];
          const key = `part:${p.id}`;
          return <div key={p.id} className="st-item" data-open={open === key || undefined} data-testid={`st-over-${p.id}`}>
            <div className="st-row">
              <span className="st-row-name">{p.name}</span>
              <span className="st-row-gap"><ChoiceMenu part={p} current={v.picks[p.id]} onPick={(id) => pick(p.id, id)} testid={`st-b-${p.id}`} label={c?.short ?? c?.label} />
                {was && <span className="st-faint st-was">was {was.short ?? was.label}</span>}</span>
              {eff && (eff.examples.length > 0
                ? <button type="button" className="st-effect st-link" aria-expanded={open === key} onClick={() => toggleOpen(key)} data-testid={`st-n-${p.id}`}>{effectWords(eff)}</button>
                : <span className="st-effect st-faint">{effectWords(eff)}</span>)}
              {was ? <button type="button" className="st-link st-effect" onClick={() => pick(p.id, was.id)} data-testid={`st-reset-${p.id}`}>Put back</button> : <span />}
            </div>
            {open === key && eff && <Examples e={eff} />}
          </div>;
        })}
    </div>
  </>;
  return <div className="sem-page st-page" data-testid="st-page">
    <Head view={v} file={file} onFile={() => setFile(!file)} onUse={(use) => s.change({ use })} />
    {s.error && <p className="sem-span-12 st-line st-faint" data-testid="st-error">{s.error}</p>}
    {v.notes.length > 0 && <p className="sem-span-12 st-line st-faint" data-testid="st-notes">{v.notes[0]}</p>}
    <div className="sem-span-8 st-col">{left}</div>
    <Summary preview={s.preview} applied={s.applied} busy={s.busy} onApply={() => void s.apply()} onUndo={() => void s.undo()} />
  </div>;
}
