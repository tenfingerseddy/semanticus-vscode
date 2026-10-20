// The Properties grid's two pure halves, kept out of extension.ts so they can be tested without VS Code: merging the per-object property lists of a selection into one
// grid, and turning one grid write into the engine calls it makes (join 11, drafts-sol: R-A, a write names the model and the starting value of EVERY object it was typed over).

export interface PropDesc { name: string; displayName: string; category: string; description: string; kind: string; value: string; options: string[]; readOnly: boolean; varies?: boolean; values?: string[]; suggestions?: string[]; hint?: string; }

/// Merge per-object property descriptors for a (possibly multi-) selection: a single object passes through; a
/// multi-selection keeps only properties present on EVERY object (by name+kind), marking a property `varies` when
/// the objects disagree on its value (so the editor shows a "multiple values" placeholder; a set applies to all).
/// A varying property also carries `values`: each object's own value, in the order of the selection, so the grid can keep what every object held when the words
/// were typed and the engine can compare each one inside the write (a null starting value would skip the check).
export function mergePropDescs(all: PropDesc[][]): PropDesc[] {
    if (all.length === 0) return [];
    if (all.length === 1) return all[0];
    const first = all[0];
    const out: PropDesc[] = [];
    for (const p of first) {
        let onAll = true;
        let varies = false;
        const values = [p.value];
        for (let i = 1; i < all.length; i++) {
            const q = all[i].find((x) => x.name === p.name && x.kind === p.kind);
            if (!q) { onAll = false; break; }
            values.push(q.value);
            if (q.value !== p.value) varies = true;
            if (q.readOnly) p.readOnly = true;   // read-only on any → read-only for the batch
            if (q.suggestions?.length) p.suggestions = [...new Set([...(p.suggestions ?? []), ...q.suggestions])];   // union prefills
        }
        if (onAll) out.push({ ...p, varies, value: varies ? '' : p.value, ...(varies ? { values } : {}) });
    }
    return out;
}

export interface GridSet { name: string; value: string; expected?: string | null; expectedAll?: string[] | null; model?: string; key?: string }
export interface EngineCall { method: string; args: unknown[]; warns: boolean }

/// The identity a write names, only when it has the engine's shape (see fenceFor in webview/src/drafts.ts).
export const fenceOf = (model: unknown): string | null => (typeof model === 'string' && /^(file|live|desktop|unsaved):/.test(model) ? model : null);

/// The engine calls one grid write makes, in order. `expected` is the one value the words were typed over; `expectedAll` is each object's own (a selection whose objects differed).
/// Every call names the model the words were typed for, so the engine refuses it when another model is open (LocalEngine.FenceModelIdentity).
export function gridSetCalls(refs: string[], msg: GridSet): EngineCall[] {
    const expected = typeof msg.expected === 'string' ? msg.expected : null;
    const all = Array.isArray(msg.expectedAll) && msg.expectedAll.length === refs.length && msg.expectedAll.every((v) => typeof v === 'string') ? msg.expectedAll : null;
    const at = (i: number): string | null => (all ? all[i] : expected);
    const identity = fenceOf(msg.model);
    const calls: EngineCall[] = [];
    // Two rows have dedicated single-object engine ops with typed refusals (the format-expression and column-data-type paths); no batch variant, so they stay per-object.
    // Everything else, the vast majority of grid rows, is the generic set_property path, which a multi-selection makes atomic (#140).
    if (msg.name === 'FormatStringExpression') {
        refs.forEach((ref, i) => calls.push({ method: 'setMeasureFormatExpression', args: [ref, msg.value, 'human', at(i), identity], warns: false }));
    } else if (msg.name === 'DataType') {
        // Column data type is a typed op (side effects on format/summarize); columns route there, any non-column ref falls back to the atomic generic write.
        const rest: number[] = [];
        refs.forEach((ref, i) => { if (ref.startsWith('column:')) calls.push({ method: 'setColumnDataType', args: [ref, msg.value, 'human', at(i), identity], warns: false }); else rest.push(i); });
        if (rest.length) calls.push({ method: 'setObjectProperties', args: [rest.map((i) => refs[i]), msg.name, msg.value, 'human', expected, all ? rest.map((i) => all[i]) : null, identity], warns: false });
    } else if (refs.length === 1) {
        // One object → the unchanged single-object path (no batch audit record).
        calls.push({ method: 'setObjectProperty', args: [refs[0], msg.name, msg.value, 'human', at(0), identity], warns: true });
    } else {
        calls.push({ method: 'setObjectProperties', args: [refs, msg.name, msg.value, 'human', expected, all, identity], warns: true });
    }
    return calls;
}

/// A rename the model just made (by the assistant, a script or the tree), as the grid needs it: the selection's refs with the renamed object on its NEW ref, or null when no
/// selected object was renamed. The change carries the new ref, the old name (before) and the new name (after) of a Name update, and '/' is both the ref separator and a legal
/// name character, so the old ref is the new ref with the literal new name at its tail swapped for the old one (UAT 18 Oct N20: Properties kept the old name and "No properties match").
export function followRename(refs: readonly string[], deltas: ReadonlyArray<{ kind: string; ref: string; props?: string[]; before?: string | null; after?: string | null }>): string[] | null {
    let next: string[] | null = null;
    for (const d of deltas) {
        if (d.kind !== 'update' || !d.props?.includes('Name') || !d.before || !d.after || d.before === d.after || !d.ref.endsWith(d.after)) continue;
        const oldRef = d.ref.slice(0, d.ref.length - d.after.length) + d.before;
        const at = (next ?? refs).indexOf(oldRef);
        if (at < 0) continue;
        next = (next ?? refs.slice());
        next[at] = d.ref;
    }
    return next;
}
