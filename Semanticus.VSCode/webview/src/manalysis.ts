// M autocomplete + hover-types, driven by Microsoft's MIT @microsoft/powerquery-language-services
// Analysis — entirely in-webview, no language server. Everything here is DEFENSIVE: any failure (a settings-shape
// mismatch, a parse that throws, an unexpected result) degrades to "no completions / no hover" and never crashes
// the editor. The library is the vendored M standard library (866 built-ins — Table.*, List.*, Text.*, Sql.Database,
// …) from mstdlib.ts, so completions/hovers now cover built-in functions on top of keywords + in-scope locals.
import * as PQP from '@microsoft/powerquery-parser';
import { AnalysisUtils, textDocument, TypeStrategy, Library, LibrarySymbolUtils, validate, type AnalysisSettings, type ValidationSettings } from '@microsoft/powerquery-language-services';
import STANDARD_LIBRARY_SYMBOLS from './mstdlib';

// Build the standard-library ILibrary once. createLibrary always returns a usable library even on partial symbol
// conversion (Error branch still carries `.library`); on any thrown failure we fall back to NoOpLibrary so the
// editor keeps working with keywords + locals only. Memoized — the 866-symbol conversion runs at most once.
let libraryMemo: Library.ILibrary | null = null;
function library(): Library.ILibrary {
  if (libraryMemo) return libraryMemo;
  try {
    const r = LibrarySymbolUtils.createLibrary(STANDARD_LIBRARY_SYMBOLS, () => new Map(), undefined);
    libraryMemo = PQP.ResultUtils.isOk(r) ? r.value : r.error.library;
  } catch {
    libraryMemo = Library.NoOpLibrary;
  }
  return libraryMemo;
}

// Inspection settings = parser defaults + the standard library + Primitive type strategy. Shared by the Analysis
// (completions/hover) AND the validator (diagnostics) so both see the same library + parse behaviour.
function inspectionSettings() {
  return {
    ...PQP.DefaultSettings,
    isWorkspaceCacheAllowed: false,
    library: library(),
    eachScopeById: undefined,
    typeStrategy: TypeStrategy.Primitive,
  };
}

// AnalysisSettings wraps the inspection settings with a no-op trace manager. Provider factories are omitted on
// purpose — AnalysisBase falls back to its built-in Language/Library/LocalDocument providers.
function analysisSettings(): AnalysisSettings {
  return {
    inspectionSettings: inspectionSettings(),
    isWorkspaceCacheAllowed: false,
    initialCorrelationId: undefined,
    traceManager: PQP.Trace.NoOpTraceManagerInstance,
  };
}

let version = 0;
function open(text: string) {
  return AnalysisUtils.analysis(textDocument('inmemory://semanticus.pq', ++version, text), analysisSettings());
}

// CompletionItemKind (LSP, stable numeric spec) → a CodeMirror completion `type` (drives the icon).
const CM_TYPE: Record<number, string> = {
  2: 'method', 3: 'function', 4: 'function', 5: 'property', 6: 'variable', 7: 'class', 8: 'interface',
  9: 'namespace', 10: 'property', 12: 'constant', 13: 'enum', 14: 'keyword', 21: 'constant', 22: 'class', 25: 'type',
};

// Pull plain text out of an LSP documentation field (string | MarkupContent | undefined).
function docText(d: unknown): string {
  if (typeof d === 'string') return d;
  if (d && typeof d === 'object' && 'value' in (d as object)) return String((d as { value?: unknown }).value ?? '');
  return '';
}

export interface MCompletion {
  label: string;
  score: number;
  cmType: string;
  detail?: string;          // cheap, shown inline (the type-kind, e.g. "function")
  info: () => string | null; // lazy full signature + documentation, computed only when the item is focused
}

// Completions at (line, character) — 0-based LSP position. Sorted best-match-first via the Jaro–Winkler score.
// The list build is cheap (label + kind); the full signature (TypeUtils.nameOf, which recurses a function type)
// is deferred into the per-item `info` thunk so it runs only for the highlighted item, not all ~860 each keystroke.
export async function mCompletions(text: string, line: number, character: number): Promise<MCompletion[]> {
  try {
    const a = open(text);
    const r = await a.getAutocompleteItems({ line, character });
    a.dispose?.();
    if (r.kind !== PQP.ResultKind.Ok || !r.value) return [];
    return r.value
      .filter((it) => it.label)
      .map((it) => {
        const kind = typeof it.kind === 'number' ? it.kind : undefined;
        const pqType = it.powerQueryType;
        return {
          label: it.label,
          score: it.jaroWinklerScore ?? 0,
          cmType: (kind !== undefined && CM_TYPE[kind]) || 'variable',
          detail: pqType ? (() => { try { return PQP.Language.TypeUtils.nameOfTypeKind(pqType.kind); } catch { return undefined; } })() : undefined,
          info: () => {
            try {
              const sig = pqType ? PQP.Language.TypeUtils.nameOf(pqType, PQP.Trace.NoOpTraceManagerInstance, undefined) : '';
              const doc = docText(it.documentation);
              const head = sig ? `${it.label}: ${sig}` : it.label;
              return [head, doc].filter(Boolean).join('\n\n') || null;
            } catch { return null; }
          },
        };
      })
      .sort((x, y) => y.score - x.score);
  } catch { return []; }
}

// Hover markdown/plaintext at (line, character) — the inferred type + any doc. null when nothing to show.
// Library functions surface as e.g. "[library function] List.Sum: (list: list, …) => any".
export async function mHover(text: string, line: number, character: number): Promise<string | null> {
  try {
    const a = open(text);
    const r = await a.getHover({ line, character });
    a.dispose?.();
    if (r.kind !== PQP.ResultKind.Ok || !r.value) return null;
    const c = r.value.contents as unknown;
    if (typeof c === 'string') return c || null;
    if (Array.isArray(c)) return c.map((x) => (typeof x === 'string' ? x : (x as { value?: string })?.value ?? '')).filter(Boolean).join('\n') || null;
    if (c && typeof c === 'object' && 'value' in (c as object)) return (c as { value?: string }).value || null;
    return null;
  } catch { return null; }
}

// A diagnostic for the inline squiggles — 0-based LSP positions + LSP severity (1 error · 2 warn · 3 info · 4 hint).
export interface MDiag { fromLine: number; fromCh: number; toLine: number; toCh: number; severity: number; message: string; }

// Validate the M (syntax + duplicate-identifier checks) and return positioned diagnostics. checkUnknownIdentifiers
// and checkInvokeExpressions are OFF on purpose: a single partition/expression legitimately references things we
// don't see here (cross-query shared expressions, the RangeStart/RangeEnd parameters, a step from another query),
// so those checks would fire false "unknown identifier" / arity errors. Defensive — any failure yields no squiggles.
export async function mDiagnostics(text: string): Promise<MDiag[]> {
  try {
    const validationSettings: ValidationSettings = {
      ...inspectionSettings(),
      cancellationToken: undefined,
      checkDiagnosticsOnParseError: true,
      checkForDuplicateIdentifiers: true,
      checkInvokeExpressions: false,
      checkUnknownIdentifiers: false,
      isWorkspaceCacheAllowed: false,
      library: library(),
      source: 'semanticus',
    };
    const r = await validate(textDocument('inmemory://semanticus.pq', ++version, text), analysisSettings(), validationSettings);
    if (r.kind !== PQP.ResultKind.Ok || !r.value) return [];
    return r.value.diagnostics
      .filter((d) => d && d.range)
      .map((d) => ({
        fromLine: d.range.start.line, fromCh: d.range.start.character,
        toLine: d.range.end.line, toCh: d.range.end.character,
        severity: typeof d.severity === 'number' ? d.severity : 1,
        message: d.message || 'Problem',
      }));
  } catch { return []; }
}
