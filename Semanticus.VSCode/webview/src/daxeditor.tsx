import { createContext, forwardRef, useContext, useEffect, useImperativeHandle, useRef, useState } from 'react';
import { EditorState } from '@codemirror/state';
import { EditorView, keymap, lineNumbers, highlightActiveLine, drawSelection, placeholder as cmPlaceholder } from '@codemirror/view';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap } from '@codemirror/autocomplete';
import { syntaxHighlighting, HighlightStyle, bracketMatching } from '@codemirror/language';
import { linter, type Diagnostic as CmDiagnostic } from '@codemirror/lint';
import { tags as t } from '@lezer/highlight';
import { rpc, onDidChange } from './bridge';
import { daxLanguage, daxCompletionSource, EMPTY_MODEL, type DaxModel, type DaxScope } from './dax';
import { validityCanReveal, validityLabel } from './daxValidity';

// Live model symbols for completion (tables/measures/columns). Metadata reads — work on a file model too, no
// live query engine needed. Refreshes on model change.
export function useDaxModel(): DaxModel {
  const [model, setModel] = useState<DaxModel>(EMPTY_MODEL);
  useEffect(() => {
    let alive = true;
    async function load() {
      try {
        const [graph, ms, cs] = await Promise.all([
          rpc<{ tables: { name: string }[] }>('getModelGraph'),
          rpc<{ name: string; table: string; formatString?: string; description?: string }[]>('listMeasures'),
          rpc<{ table: string; name: string; sortByColumn?: string; description?: string }[]>('listColumns'),
        ]);
        if (!alive) return;
        setModel({
          tables: graph.tables.map((x) => x.name),
          measures: ms.map((x) => ({ name: x.name, table: x.table, formatString: x.formatString, ...(x.description ? { description: x.description } : {}) })),
          columns: cs.map((c) => ({ table: c.table, name: c.name, ...(c.sortByColumn ? { sortByColumn: c.sortByColumn } : {}), ...(c.description ? { description: c.description } : {}) })),
          functions: [],
        });
      } catch { /* no model open yet — built-in functions still complete */ }
    }
    void load();
    let timer: number | undefined;
    const off = onDidChange(() => { window.clearTimeout(timer); timer = window.setTimeout(() => void load(), 400); });
    return () => { alive = false; off(); window.clearTimeout(timer); };
  }, []);
  return model;
}

// One model fetch shared by every editor in the Studio (DAX Query + the Pivot/Lab expression inputs).
const DaxModelContext = createContext<DaxModel>(EMPTY_MODEL);
export function DaxModelProvider({ children }: { children: React.ReactNode }) {
  const model = useDaxModel();
  return <DaxModelContext.Provider value={model}>{children}</DaxModelContext.Provider>;
}
export function useDaxModelContext() { return useContext(DaxModelContext); }

// The shared code palette (--xe-code-* in styles.css; see mlang.ts): readable in all three themes, no accent.
const daxHighlight = HighlightStyle.define([
  { tag: t.keyword, color: 'var(--sem-fg)', fontWeight: '600' },
  { tag: t.string, color: 'var(--xe-code-string)' },
  { tag: t.comment, color: 'var(--sem-muted)', fontStyle: 'italic' },
  { tag: t.number, color: 'var(--xe-code-number)' },
  { tag: t.typeName, color: 'var(--xe-code-type)' },       // 'Table'
  { tag: t.propertyName, color: 'var(--xe-code-name)' },    // [Column] / [Measure]
  { tag: t.operator, color: 'var(--sem-fg)' },
]);

// A curated, highly-readable monospace stack for the DAX editors. Leads with purpose-built coding fonts
// (Cascadia Code / JetBrains Mono / SF Mono — large x-height, unambiguous 0/O·1/l/I, and operator ligatures
// that render DAX's <=, >=, <> cleanly) and only then falls back to the user's VS Code editor font and a
// generic monospace. Bumped size + line-height vs. the old cramped 12.5px/auto for legibility on dense DAX.
const DAX_FONT =
  "'Cascadia Code', 'JetBrains Mono', 'SF Mono', 'Fira Code', 'DejaVu Sans Mono', var(--vscode-editor-font-family), Consolas, 'Courier New', monospace";

const daxTheme = EditorView.theme({
  '&': { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)', fontSize: 'var(--sem-text-body)', lineHeight: 'var(--sem-lh-body)' },
  '&.cm-focused': { outline: '1px solid var(--sem-focus)' },
  // The muted text colour measures 4.4:1 on the editor's own fill in Light Modern, so the hint sits a step darker.
  '.cm-placeholder': { color: 'color-mix(in srgb, var(--sem-fg) 82%, var(--sem-bg))' },
  '.cm-content': { fontFamily: DAX_FONT, fontVariantLigatures: 'contextual', caretColor: 'var(--sem-fg)' },
  '.cm-gutters': { fontFamily: DAX_FONT, background: 'transparent', color: 'var(--sem-muted)', border: 'none' },
  '.cm-activeLine': { background: 'color-mix(in srgb, var(--sem-fg) 5%, transparent)' },
  '.cm-activeLineGutter': { background: 'transparent', color: 'var(--sem-fg)' },
  '.cm-selectionBackground, &.cm-focused .cm-selectionBackground, ::selection': { background: 'var(--sem-accent-soft)' },
  '.cm-tooltip': { background: 'var(--sem-surface)', border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)', color: 'var(--sem-fg)' },
  '.cm-tooltip.cm-tooltip-autocomplete > ul > li[aria-selected]': { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' },
  '.cm-tooltip.cm-tooltip-autocomplete > ul > li': { fontFamily: DAX_FONT },
  '.cm-completionDetail': { color: 'var(--sem-muted)', fontStyle: 'normal', marginLeft: 'var(--sem-space-2)' },
  // Themed chrome for the lint (diagnostic) tooltip + the editor font on its text (mirrors meditor.tsx) — inert
  // for editors without the linter, so DAX Lab's query editor is unaffected.
  '.cm-tooltip.cm-tooltip-lint': { background: 'var(--sem-surface)', border: '1px solid var(--sem-border)', borderRadius: 'var(--sem-radius-card)', boxShadow: '0 6px 20px rgba(0,0,0,0.35)', overflow: 'hidden' },
  '.cm-diagnostic': { fontFamily: DAX_FONT, fontSize: 'var(--sem-text-meta)', padding: 'var(--sem-space-1) var(--sem-space-2)' },
}, { dark: true });

// Offline DAX validation as a CodeMirror linter (squiggles + hover messages). Calls the engine's validate_dax
// (the same conservative offline check the McpSmoke exercises) and converts its 1-based line/col diagnostics to
// clamped CM document offsets, so a stale range can never throw. Debounced, like the M editor's linter.
interface DaxValidation { valid: boolean; diagnostics: { severity: string; message: string; line: number; column: number }[] }
function makeDaxLinter(onDiagnostics?: () => (d: CmDiagnostic[]) => void) {
  return linter(async (view): Promise<CmDiagnostic[]> => {
    const doc = view.state.doc;
    let res: DaxValidation;
    try { res = await rpc<DaxValidation>('validateDax', doc.toString()); }
    catch { return []; }   // no engine / transient — don't paint stale squiggles
    const offset = (line1: number, col1: number) => {
      const ln = doc.line(Math.min(Math.max(line1, 1), doc.lines));
      return Math.min(ln.from + Math.max(col1 - 1, 0), ln.to);
    };
    const diags = (res.diagnostics ?? []).map((d) => {
      const from = offset(d.line, d.column);
      const to = Math.min(from + 1, doc.length);
      return { from, to, severity: (d.severity === 'error' ? 'error' : 'warning') as CmDiagnostic['severity'], message: d.message, source: 'DAX' };
    });
    onDiagnostics?.()(diags);
    return diags;
  }, { delay: 350 });
}

export interface DaxEditorHandle { insert: (text: string) => void; focus: () => void; }

interface DaxEditorProps {
  value: string; onChange: (v: string) => void; model: DaxModel; minHeight?: number; placeholder?: string; lineNumbers?: boolean;
  // Fill the box it is given and scroll inside it, instead of growing with its text. Queries' editor card has a set
  // height, and a 14-line question drew over the result below it (verify-queries-redesign D3).
  fill?: boolean;
  // Backbone A: opt-in live validate_dax markers (default off, so DAX Lab's query editor is unchanged) + scope-tuned
  // completion (calc-item / format-string / RLS). onDiagnostics reports the current marker list for a validity pill.
  lint?: boolean; scope?: DaxScope; scopeTable?: string; onDiagnostics?: (diags: CmDiagnostic[]) => void;
}

// A CodeMirror DAX editor: syntax highlighting + model-aware autocomplete + bracket close/match + drop-to-insert
// (a Fields-panel item or any text/plain drag). Imperative handle exposes insert() for the Fields panel.
export const DaxEditor = forwardRef<DaxEditorHandle, DaxEditorProps>(function DaxEditor({ value, onChange, model, minHeight = 120, placeholder, lineNumbers: showLineNumbers = true, lint = false, scope, scopeTable, onDiagnostics, fill = false }, ref) {
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const onChangeRef = useRef(onChange); onChangeRef.current = onChange;
  const modelRef = useRef(model); modelRef.current = model;
  const onDiagRef = useRef(onDiagnostics); onDiagRef.current = onDiagnostics;

  useImperativeHandle(ref, () => ({
    insert(text: string) {
      const v = view.current; if (!v) return;
      const s = v.state.selection.main;
      v.dispatch({ changes: { from: s.from, to: s.to, insert: text }, selection: { anchor: s.from + text.length } });
      v.focus();
    },
    focus() { view.current?.focus(); },
  }), []);

  useEffect(() => {
    if (!host.current) return;
    const state = EditorState.create({
      doc: value,
      extensions: [
        showLineNumbers ? lineNumbers() : [], highlightActiveLine(), drawSelection(), history(), bracketMatching(), closeBrackets(),
        EditorView.lineWrapping,
        keymap.of([...closeBracketsKeymap, ...defaultKeymap, ...historyKeymap, ...completionKeymap, indentWithTab]),
        daxLanguage, syntaxHighlighting(daxHighlight),
        autocompletion({ override: [daxCompletionSource(() => modelRef.current, { scope, table: scopeTable })], activateOnTyping: true, icons: false }),
        lint ? makeDaxLinter(() => (d) => onDiagRef.current?.(d)) : [],
        daxTheme,
        EditorView.theme({ '.cm-scroller': { minHeight: minHeight + 'px' } }),
        fill ? EditorView.theme({ '&': { height: '100%' }, '.cm-scroller': { overflow: 'auto' } }) : [],
        placeholder ? cmPlaceholder(placeholder) : [],
        EditorView.updateListener.of((u) => { if (u.docChanged) onChangeRef.current(u.state.doc.toString()); }),
        EditorView.domEventHandlers({
          drop(e, v) {
            const text = e.dataTransfer?.getData('text/plain');
            if (!text) return false;
            e.preventDefault();
            const pos = v.posAtCoords({ x: e.clientX, y: e.clientY }) ?? v.state.selection.main.head;
            v.dispatch({ changes: { from: pos, insert: text }, selection: { anchor: pos + text.length } });
            v.focus();
            return true;
          },
        }),
      ],
    });
    const v = new EditorView({ state, parent: host.current });
    view.current = v;
    return () => { v.destroy(); view.current = null; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Sync an external value change (e.g. a persisted load) that the editor doesn't already reflect.
  useEffect(() => {
    const v = view.current;
    if (v && value !== v.state.doc.toString()) {
      v.dispatch({ changes: { from: 0, to: v.state.doc.length, insert: value } });
    }
  }, [value]);

  return <div ref={host} className="cm-dax" style={fill ? { height: '100%', minHeight: 0 } : undefined} />;
});

// ===================================================================================================
// <DaxField> — Backbone A. The one DAX-editing surface reused across calc-item expressions, calc-item
// format-string expressions, and RLS row-filters. Wraps DaxEditor with the shared model, live validate_dax
// markers, a scope-tuned autocomplete, and a validity pill that shows only on a problem. It's a controlled
// value/onChange field (survives the host's draft-vs-server reconciliation), and inherits DaxEditor's
// drop-to-insert — so a drag from the object browser lands at the cursor for free.
// ===================================================================================================
export function DaxField({ value, onChange, scope, table, minHeight = 92, placeholder, onValidity, ariaLabel, model: modelOverride }: {
  value: string; onChange: (v: string) => void;
  scope?: DaxScope; table?: string; minHeight?: number; placeholder?: string;
  onValidity?: (valid: boolean, issues: number) => void; ariaLabel?: string;
  askContext?: string;   // kept for callers that still pass it; the per-field assistant link is gone
  // Optional completion model. Defaults to the shared LIVE-model context; the Spec tab passes a model built from
  // the SPEC's own tables/columns/measures so IntelliSense works BEFORE the model is built (those symbols exist
  // only in the draft spec, not yet in any live model).
  model?: DaxModel;
}) {
  const ctxModel = useDaxModelContext();
  const model = modelOverride ?? ctxModel;
  const [diags, setDiags] = useState<CmDiagnostic[] | null>(null);   // null = not yet validated
  const errors = (diags ?? []).filter((d) => d.severity === 'error').length;
  const warns = (diags ?? []).filter((d) => d.severity === 'warning').length;
  const valid = errors === 0;
  const validRef = useRef(onValidity); validRef.current = onValidity;
  useEffect(() => { validRef.current?.(valid, errors + warns); }, [valid, errors, warns]);

  return (
    <div className="flex flex-col gap-1" aria-label={ariaLabel}>
      <DaxEditor value={value} onChange={onChange} model={model} minHeight={minHeight} placeholder={placeholder}
        lineNumbers={false} lint scope={scope} scopeTable={table} onDiagnostics={setDiags} />
      {/* The state shows only when there is something to say: a clean or empty field shows nothing. The assistant door is
          one "Give to your assistant" on each page that uses this field, not one link under every field. */}
      {diags !== null && value.trim() && (errors > 0 || warns > 0) && (
        <ValidityPill checked empty={false} errors={errors} warns={warns}
          messages={(diags ?? []).map((d) => d.message)} />
      )}
    </div>
  );
}

// The per-field validity pill fed by the DAX linter: muted while first-checking / empty, green when clean,
// amber for reference warnings, red for a parse error (which blocks a single-edit save upstream).
// A count with issues is a button so a click or keyboard can read the reason, not only a hover.
function ValidityPill({ checked, empty, errors, warns, messages }: {
  checked: boolean; empty: boolean; errors: number; warns: number; messages?: string[];
}) {
  const [open, setOpen] = useState(false);
  const text = validityLabel(empty, checked, errors, warns);
  let color: string;
  if (empty || !checked) color = 'var(--sem-muted)';
  else if (errors > 0) color = 'var(--sem-bad)';
  else if (warns > 0) color = 'var(--sem-warn)';
  else color = 'var(--sem-good)';
  const canReveal = validityCanReveal(empty, checked, errors, warns) && (messages?.length ?? 0) > 0;
  const chip = (
    // Instrument: the state is a word with a dot in its colour, not a tinted pill.
    <span className="text-(length:--sem-text-label) sem-num xe-dot" style={{ '--xe-dot': color } as React.CSSProperties}>{text}</span>
  );
  return (
    <div className="flex flex-col gap-1">
      {canReveal ? (
        <button type="button" onClick={() => setOpen((v) => !v)} aria-expanded={open}
          aria-label={text + '. Show details'}
          className="text-left bg-transparent border-0 p-0 cursor-pointer">
          {chip}
        </button>
      ) : chip}
      {open && canReveal && (
        <ul className="m-0 pl-4 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
          {messages!.map((m, i) => <li key={i}>{m}</li>)}
        </ul>
      )}
    </div>
  );
}
