import { createContext, forwardRef, useContext, useEffect, useImperativeHandle, useRef, useState } from 'react';
import { EditorState } from '@codemirror/state';
import { EditorView, keymap, lineNumbers, highlightActiveLine, drawSelection, placeholder as cmPlaceholder } from '@codemirror/view';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap } from '@codemirror/autocomplete';
import { syntaxHighlighting, HighlightStyle, bracketMatching } from '@codemirror/language';
import { linter, type Diagnostic as CmDiagnostic } from '@codemirror/lint';
import { tags as t } from '@lezer/highlight';
import { rpc, onDidChange, copyText } from './bridge';
import { daxLanguage, daxCompletionSource, EMPTY_MODEL, type DaxModel, type DaxScope } from './dax';

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
          rpc<{ name: string; table: string }[]>('listMeasures'),
          rpc<{ table: string; name: string }[]>('listColumns'),
        ]);
        if (!alive) return;
        setModel({
          tables: graph.tables.map((x) => x.name),
          measures: ms.map((x) => ({ name: x.name, table: x.table })),
          columns: cs.map((c) => ({ table: c.table, name: c.name })),
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

const daxHighlight = HighlightStyle.define([
  { tag: t.keyword, color: 'var(--sem-accent)' },
  { tag: t.string, color: 'var(--sem-good)' },
  { tag: t.comment, color: 'var(--sem-muted)', fontStyle: 'italic' },
  { tag: t.number, color: 'var(--sem-warn)' },
  { tag: t.typeName, color: '#4ec9b0' },       // 'Table'
  { tag: t.propertyName, color: '#9cdcfe' },    // [Column] / [Measure]
  { tag: t.operator, color: 'var(--sem-fg)' },
]);

// A curated, highly-readable monospace stack for the DAX editors. Leads with purpose-built coding fonts
// (Cascadia Code / JetBrains Mono / SF Mono — large x-height, unambiguous 0/O·1/l/I, and operator ligatures
// that render DAX's <=, >=, <> cleanly) and only then falls back to the user's VS Code editor font and a
// generic monospace. Bumped size + line-height vs. the old cramped 12.5px/auto for legibility on dense DAX.
const DAX_FONT =
  "'Cascadia Code', 'JetBrains Mono', 'SF Mono', 'Fira Code', 'DejaVu Sans Mono', var(--vscode-editor-font-family), Consolas, 'Courier New', monospace";

const daxTheme = EditorView.theme({
  '&': { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: '8px', fontSize: '13.5px', lineHeight: '1.55' },
  '&.cm-focused': { outline: '1px solid var(--sem-accent)' },
  '.cm-content': { fontFamily: DAX_FONT, fontVariantLigatures: 'contextual', caretColor: 'var(--sem-fg)' },
  '.cm-gutters': { fontFamily: DAX_FONT, background: 'transparent', color: 'var(--sem-muted)', border: 'none' },
  '.cm-activeLine': { background: 'color-mix(in srgb, var(--sem-fg) 5%, transparent)' },
  '.cm-activeLineGutter': { background: 'transparent', color: 'var(--sem-fg)' },
  '.cm-selectionBackground, &.cm-focused .cm-selectionBackground, ::selection': { background: 'var(--sem-accent-soft)' },
  '.cm-tooltip': { background: 'var(--sem-surface)', border: '1px solid var(--sem-border)', borderRadius: '6px', color: 'var(--sem-fg)' },
  '.cm-tooltip.cm-tooltip-autocomplete > ul > li[aria-selected]': { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' },
  '.cm-tooltip.cm-tooltip-autocomplete > ul > li': { fontFamily: DAX_FONT },
  '.cm-completionDetail': { color: 'var(--sem-muted)', fontStyle: 'normal', marginLeft: '0.6em' },
  // Themed chrome for the lint (diagnostic) tooltip + the editor font on its text (mirrors meditor.tsx) — inert
  // for editors without the linter, so DAX Lab's query editor is unaffected.
  '.cm-tooltip.cm-tooltip-lint': { background: 'var(--sem-surface)', border: '1px solid var(--sem-border)', borderRadius: '8px', boxShadow: '0 6px 20px rgba(0,0,0,0.35)', overflow: 'hidden' },
  '.cm-diagnostic': { fontFamily: DAX_FONT, fontSize: '12px', padding: '3px 8px' },
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
  // Backbone A: opt-in live validate_dax markers (default off, so DAX Lab's query editor is unchanged) + scope-tuned
  // completion (calc-item / format-string / RLS). onDiagnostics reports the current marker list for a validity pill.
  lint?: boolean; scope?: DaxScope; scopeTable?: string; onDiagnostics?: (diags: CmDiagnostic[]) => void;
}

// A CodeMirror DAX editor: syntax highlighting + model-aware autocomplete + bracket close/match + drop-to-insert
// (a Fields-panel item or any text/plain drag). Imperative handle exposes insert() for the Fields panel.
export const DaxEditor = forwardRef<DaxEditorHandle, DaxEditorProps>(function DaxEditor({ value, onChange, model, minHeight = 120, placeholder, lineNumbers: showLineNumbers = true, lint = false, scope, scopeTable, onDiagnostics }, ref) {
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

  return <div ref={host} className="cm-dax" />;
});

// ===================================================================================================
// <DaxField> — Backbone A. The one DAX-editing surface reused across calc-item expressions, calc-item
// format-string expressions, and RLS row-filters. Wraps DaxEditor with the shared model, live validate_dax
// markers, a scope-tuned autocomplete, a validity pill, and an [Ask AI] escape hatch. It's a controlled
// value/onChange field (survives the host's draft-vs-server reconciliation), and inherits DaxEditor's
// drop-to-insert — so a drag from the object browser lands at the cursor for free.
// ===================================================================================================
export function DaxField({ value, onChange, scope, table, minHeight = 92, placeholder, onValidity, ariaLabel, askContext, model: modelOverride }: {
  value: string; onChange: (v: string) => void;
  scope?: DaxScope; table?: string; minHeight?: number; placeholder?: string;
  onValidity?: (valid: boolean, issues: number) => void; ariaLabel?: string;
  askContext?: string;   // a human phrase for the [Ask AI] prompt, e.g. "a calculation-item expression"
  // Optional completion model. Defaults to the shared LIVE-model context; the Spec tab passes a model built from
  // the SPEC's own tables/columns/measures so IntelliSense works BEFORE the model is built (those symbols exist
  // only in the draft spec, not yet in any live model).
  model?: DaxModel;
}) {
  const ctxModel = useDaxModelContext();
  const model = modelOverride ?? ctxModel;
  const [diags, setDiags] = useState<CmDiagnostic[] | null>(null);   // null = not yet validated
  const [copied, setCopied] = useState(false);
  const errors = (diags ?? []).filter((d) => d.severity === 'error').length;
  const warns = (diags ?? []).filter((d) => d.severity === 'warning').length;
  const valid = errors === 0;
  const validRef = useRef(onValidity); validRef.current = onValidity;
  useEffect(() => { validRef.current?.(valid, errors + warns); }, [valid, errors, warns]);

  const ask = async () => {
    const prompt = `Help me with ${askContext ?? 'this DAX expression'}${table ? ` on the '${table}' table` : ''}.\n\nCurrent expression:\n${value || '(empty)'}\n\n`
      + (diags && diags.length ? `Validation issues:\n${diags.map((d) => `- ${d.severity}: ${d.message}`).join('\n')}\n` : '');
    if (await copyText(prompt)) { setCopied(true); window.setTimeout(() => setCopied(false), 1600); }
  };

  return (
    <div className="flex flex-col gap-1" aria-label={ariaLabel}>
      <DaxEditor value={value} onChange={onChange} model={model} minHeight={minHeight} placeholder={placeholder}
        lineNumbers={false} lint scope={scope} scopeTable={table} onDiagnostics={setDiags} />
      <div className="flex items-center gap-2">
        <ValidityPill checked={diags !== null} empty={!value.trim()} errors={errors} warns={warns} />
        <button onClick={() => void ask()} title="Copy a grounded prompt for the AI Assistant"
          className="text-[10px] px-1.5 py-0.5 rounded-md" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
          {copied ? 'Copied ✓' : 'Ask AI'}
        </button>
      </div>
    </div>
  );
}

// The per-field validity pill fed by the DAX linter: muted while first-checking / empty, green when clean,
// amber for reference warnings, red for a parse error (which blocks a single-edit save upstream).
function ValidityPill({ checked, empty, errors, warns }: { checked: boolean; empty: boolean; errors: number; warns: number }) {
  let text: string, color: string;
  if (empty) { text = 'empty'; color = 'var(--sem-muted)'; }
  else if (!checked) { text = 'checking…'; color = 'var(--sem-muted)'; }
  else if (errors > 0) { text = `${errors} error${errors === 1 ? '' : 's'}`; color = 'var(--sem-bad)'; }
  else if (warns > 0) { text = `${warns} warning${warns === 1 ? '' : 's'}`; color = 'var(--sem-warn)'; }
  else { text = '✓ valid'; color = 'var(--sem-good)'; }
  return (
    <span className="text-[10px] px-1.5 py-0.5 rounded-md tnum" title="Offline DAX check (brackets + table/column/measure references)"
      style={{ color, background: 'color-mix(in srgb,' + color + ' 12%, transparent)' }}>{text}</span>
  );
}
