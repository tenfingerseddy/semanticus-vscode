import { useEffect, useRef } from 'react';
import { EditorState, Compartment } from '@codemirror/state';
import { EditorView, keymap, lineNumbers, highlightActiveLine, drawSelection, hoverTooltip } from '@codemirror/view';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { syntaxHighlighting, bracketMatching } from '@codemirror/language';
import { closeBrackets, closeBracketsKeymap, autocompletion, completionKeymap, type CompletionContext, type CompletionResult } from '@codemirror/autocomplete';
import { linter, type Diagnostic as CmDiagnostic } from '@codemirror/lint';
import { mLanguage, mHighlight } from './mlang';
import { mCompletions, mHover, mDiagnostics } from './manalysis';

// 0-based LSP (line, character) for a CodeMirror document offset.
const lspPos = (doc: { lineAt: (p: number) => { number: number; from: number } }, pos: number) => {
  const ln = doc.lineAt(pos);
  return { line: ln.number - 1, character: pos - ln.from };
};

// One monospace stack for the editor AND its hover/completion popups — signatures should read as code, not prose.
// Explicit code fonts come first and `Consolas, monospace` anchors the tail, so we never fall through to the webview's
// default serif when --vscode-editor-font-family resolves to a proportional family (the old `var(…, monospace)` did).
const M_FONT = "'Cascadia Code', 'JetBrains Mono', 'SF Mono', 'Fira Code', var(--vscode-editor-font-family), Consolas, monospace";
// Shared inner-content style for the hover tooltip + completion info panel (chrome — bg/border — comes from mTheme).
const TIP_CSS = `max-width:480px;padding:7px 10px;white-space:pre-wrap;font-family:${M_FONT};font-size:12px;line-height:1.5`;

// Autocomplete via the language-services Analysis: keywords + in-scope locals + the M standard library (Table.*,
// List.*, Sql.Database, …). The full signature/docs render lazily in the side popup (`info`) only for the focused
// item, so we never compute ~860 signatures per keystroke.
async function mCompletionSource(ctx: CompletionContext): Promise<CompletionResult | null> {
  const word = ctx.matchBefore(/[#"\w][\w.]*/);
  if (!word && !ctx.explicit) return null;
  const { line, character } = lspPos(ctx.state.doc, ctx.pos);
  const items = await mCompletions(ctx.state.doc.toString(), line, character);
  if (!items.length) return null;
  return {
    from: word ? word.from : ctx.pos,
    options: items.map((c, i) => ({
      label: c.label,
      detail: c.detail,
      type: c.cmType,
      boost: items.length - i,
      info: () => {
        const text = c.info();
        if (!text) return null;
        const dom = document.createElement('div');
        dom.style.cssText = TIP_CSS;
        dom.textContent = text;
        return dom;
      },
    })),
    validFor: /^[#"\w][\w.]*$/,
  };
}

// Inferred-type hover via the Analysis.
const mHoverTooltip = hoverTooltip(async (view, pos) => {
  const { line, character } = lspPos(view.state.doc, pos);
  const md = await mHover(view.state.doc.toString(), line, character);
  if (!md) return null;
  return {
    pos,
    create: () => {
      const dom = document.createElement('div');
      dom.style.cssText = TIP_CSS;
      dom.textContent = md;
      return { dom };
    },
  };
});

// LSP DiagnosticSeverity (1 error · 2 warning · 3 info · 4 hint) → CodeMirror severities.
const SEV: Record<number, CmDiagnostic['severity']> = { 1: 'error', 2: 'warning', 3: 'info', 4: 'hint' };

// Inline diagnostics (squiggles + hover messages) from the language-services validator. Converts the validator's
// 0-based LSP line/character ranges to CodeMirror document offsets, clamped to the document so a stale range can
// never throw. Zero-width ranges get a 1-char span so there's something to underline. Debounced via the linter.
const mLinter = linter(async (view): Promise<CmDiagnostic[]> => {
  const doc = view.state.doc;
  const diags = await mDiagnostics(doc.toString());
  const offset = (line: number, ch: number) => {
    const ln = doc.line(Math.min(Math.max(line, 0), doc.lines - 1) + 1);
    return Math.min(ln.from + Math.max(ch, 0), ln.to);
  };
  return diags.map((d) => {
    const from = offset(d.fromLine, d.fromCh);
    let to = offset(d.toLine, d.toCh);
    if (to <= from) to = Math.min(from + 1, doc.length);
    return { from, to, severity: SEV[d.severity] ?? 'error', message: d.message, source: 'M' };
  });
}, { delay: 350 });

const mTheme = EditorView.theme({
  '&': { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: '8px', fontSize: '13px', lineHeight: '1.5' },
  '&.cm-focused': { outline: '1px solid var(--sem-accent)' },
  '.cm-content': { fontFamily: M_FONT, caretColor: 'var(--sem-fg)' },
  '.cm-gutters': { fontFamily: M_FONT, background: 'transparent', color: 'var(--sem-muted)', border: 'none' },
  '.cm-activeLine': { background: 'color-mix(in srgb, var(--sem-fg) 5%, transparent)' },
  '.cm-activeLineGutter': { background: 'transparent', color: 'var(--sem-fg)' },
  '.cm-selectionBackground, &.cm-focused .cm-selectionBackground, ::selection': { background: 'var(--sem-accent-soft)' },
  // Themed chrome for the hover tooltip, the completion info panel, and the lint (diagnostic) tooltip (their inner
  // content uses TIP_CSS / M_FONT). The autocomplete dropdown list is left to its defaults — it already renders
  // fine. Override CM's light-mode tooltip + give the diagnostic text the editor font.
  '.cm-tooltip.cm-tooltip-hover, .cm-completionInfo, .cm-tooltip.cm-tooltip-lint': {
    background: 'var(--sem-surface-2)', color: 'var(--sem-fg)',
    border: '1px solid var(--sem-border)', borderRadius: '8px',
    boxShadow: '0 6px 20px rgba(0,0,0,0.35)', overflow: 'hidden',
  },
  '.cm-diagnostic': { fontFamily: M_FONT, fontSize: '12px', padding: '3px 8px' },
}, { dark: true });

// A CodeMirror M editor: highlighting + bracket match/close + history + standard-library-aware
// autocomplete & hover-types (the powerquery-language-services Analysis, via manalysis.ts). Editable unless
// readOnly. The parent owns format/validity/save; this component just edits text and reports changes.
export function MEditor({ value, onChange, readOnly = false, minHeight = 260, selection, resizable = false }: { value: string; onChange?: (v: string) => void; readOnly?: boolean; minHeight?: number; selection?: { from: number; to: number; nonce: number }; resizable?: boolean }) {
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const onChangeRef = useRef(onChange); onChangeRef.current = onChange;
  const editable = useRef(new Compartment());

  useEffect(() => {
    if (!host.current) return;
    const state = EditorState.create({
      doc: value,
      extensions: [
        lineNumbers(), highlightActiveLine(), drawSelection(), history(), bracketMatching(), closeBrackets(),
        EditorView.lineWrapping,
        keymap.of([...closeBracketsKeymap, ...defaultKeymap, ...historyKeymap, ...completionKeymap, indentWithTab]),
        mLanguage, syntaxHighlighting(mHighlight),
        autocompletion({ override: [mCompletionSource], activateOnTyping: true, icons: false }),
        mHoverTooltip,
        mLinter,
        mTheme,
        EditorView.theme({
          '&': { height: resizable ? '100%' : 'auto' },
          '.cm-scroller': { minHeight: minHeight + 'px', height: resizable ? '100%' : 'auto' },
        }),
        editable.current.of([EditorView.editable.of(!readOnly), EditorState.readOnly.of(readOnly)]),
        EditorView.updateListener.of((u) => { if (u.docChanged) onChangeRef.current?.(u.state.doc.toString()); }),
      ],
    });
    const v = new EditorView({ state, parent: host.current });
    view.current = v;
    return () => { v.destroy(); view.current = null; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Reflect an external value change (table/partition switch, format, revert) the editor doesn't already hold.
  useEffect(() => {
    const v = view.current;
    if (v && value !== v.state.doc.toString()) v.dispatch({ changes: { from: 0, to: v.state.doc.length, insert: value } });
  }, [value]);

  // Toggle read-only without remounting.
  useEffect(() => {
    view.current?.dispatch({ effects: editable.current.reconfigure([EditorView.editable.of(!readOnly), EditorState.readOnly.of(readOnly)]) });
  }, [readOnly]);

  // Select + scroll a span into view (e.g. clicking an Applied Step selects its binding). The parent bumps
  // `nonce` to re-fire the same span; offsets are clamped so a stale range can never throw.
  useEffect(() => {
    const v = view.current;
    if (!v || !selection) return;
    const len = v.state.doc.length;
    const from = Math.max(0, Math.min(selection.from, len));
    const to = Math.max(from, Math.min(selection.to, len));
    v.dispatch({ selection: { anchor: from, head: to }, effects: EditorView.scrollIntoView(from, { y: 'center' }) });
    v.focus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selection?.nonce]);

  return <div ref={host} style={resizable ? { height: minHeight, minHeight, resize: 'vertical', overflow: 'hidden' } : undefined} />;
}
