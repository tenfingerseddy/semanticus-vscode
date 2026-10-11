// What the native status bar says, as plain functions so test/sidebar-identity.test.mjs runs them (r1 min4, V1, V2, V4).
//
// The bar used to carry six items and 29 words: the model with its file, measure count, "live" and the account; the
// health chip; the sync chip ("Contoso Retail -> Contoso Sales"); Connect AI Assistant; Publish; the undo label. Every
// fact but the last one repeats something the header, the tree or a tooltip already shows. Now the model item carries
// the model name and nothing else, and the facts that were words on the bar are its tooltip.
//
// The sync chip is not a separate item any more. It was the one place that said the model tests run against does not
// have your unsaved edits, and that is a warning, so the model item takes it: an amber background and a warning icon
// only while that is provably true, and the click then opens the seeded Compare instead of the Model tree.

export interface StatusSession {
    sessionId?: string; modelName?: string; source?: string; measures?: number; hasUnsavedChanges?: boolean; hasUnsavedEdits?: boolean;
    liveBound?: boolean; liveEndpoint?: string; liveDatabase?: string; liveConnected?: boolean; liveKind?: string;
    liveDataSource?: string; currentAccount?: string; currentTenant?: string;
}

/** The tail of a source path, so two copies of one model name stay apart in the tooltip. */
function sourceTail(source?: string, max = 48): string {
    const v = (source || '').replace(/[\\/]+$/, '');
    if (!v) return '';
    return v.length <= max ? v : '…' + v.slice(-(max - 1));
}

export interface SyncVerdict { warn: boolean; detail: string; editing: string; querying: string; connected: boolean }

/** EDITING against QUERYING. It warns only when it can PROVE a divergence: a live query connection with unsaved edits,
 *  the exact case where query results leave out your staged work. It reads the same session info the Studio footer
 *  reads, so there is no second source of truth. It never blocks; it discloses. */
export function syncVerdict(info: StatusSession): SyncVerdict {
    const norm = (x?: string) => (x || '').trim().toLowerCase().replace(/[\\/]+$/, '');
    const short = (p?: string) => { const t = (p || '').replace(/[\\/]+$/, ''); const i = Math.max(t.lastIndexOf('/'), t.lastIndexOf('\\')); return i >= 0 ? t.slice(i + 1) : t; };
    const editing = info.liveBound && info.liveDatabase ? info.liveDatabase : (info.modelName || 'model');
    const connected = !!info.liveConnected;
    // Same live instance? Only claim it when the attached query source provably matches the edited endpoint or source.
    const q = norm(info.liveDataSource);
    const same = connected && !!q && (q === norm(info.liveEndpoint) || q === norm(info.source));
    const querying = !connected ? 'offline'
        : (info.liveBound && info.liveDatabase ? info.liveDatabase : (short(info.liveDataSource) || 'live model'));
    let warn = false, detail: string;
    if (!connected) detail = 'No live connection, so queries are unavailable.';
    else if (info.hasUnsavedEdits ?? info.hasUnsavedChanges) { warn = true; detail = "Unsaved edits are not in the model you're querying."; }
    else if (same) detail = 'Editing and querying the same instance.';
    // A local attach (Power BI Desktop, liveKind 'local') is NOT a published model: do not tell the person their
    // queries "reflect the last deploy" when they hit a live local engine. That disclosure belongs to the XMLA case.
    else if (info.liveKind === 'local') detail = 'Querying an attached local engine (Power BI Desktop).';
    else detail = 'Querying the published model. Reflects the last deploy.';
    return { warn, detail, editing, querying, connected };
}

export interface ModelItem { text: string; tooltip: string; warn: boolean; command: string }

/** The one model item: `{name}`, with a warning icon and tint only while the unsaved edits are not in the model tests use. */
export function modelItem(info: StatusSession): ModelItem {
    const verdict = syncVerdict(info);
    const lines: string[] = [];
    if (info.source) lines.push(sourceTail(info.source, 200));
    if (typeof info.measures === 'number') lines.push(`${info.measures} measure${info.measures === 1 ? '' : 's'}`);
    if (info.currentAccount && (info.liveConnected || info.liveBound)) lines.push(`Signed in as ${info.currentAccount}${info.currentTenant ? ` · ${info.currentTenant}` : ''}`);
    lines.push(`Editing: ${verdict.editing}`, `Querying: ${verdict.connected ? verdict.querying : 'not connected'}`, verdict.detail);
    lines.push(verdict.warn ? "Click to compare what you're editing against what you're querying." : 'Click to show the Model tree.');
    return {
        text: `${verdict.warn ? '$(warning)' : '$(database)'} ${info.modelName || 'Model'}`,
        tooltip: lines.join('\n'),
        warn: verdict.warn,
        command: verdict.warn ? 'semanticus.openCompareSeeded' : 'semanticusModel.focus',
    };
}

/** What the engine reports about the last change's effect on model health (mirrors HealthDelta in extension.ts). */
export interface HealthLike { grade?: string; findings?: number; new?: unknown[]; impact?: number }

/** The health chip's few words: "Health B → C", "Health: 3 new issues" or "Affects 4". The tooltip keeps the full sentence. */
export function healthText(h: HealthLike): string {
    const grade = h.grade && h.grade.includes('->') ? h.grade.split('->') : undefined;
    const issues = h.findings ?? (h.new?.length ?? 0);
    const things = h.impact ?? 0;
    if (grade) return `$(pulse) Health ${grade[0].trim()} → ${grade[1].trim()}`;
    if (issues > 0) return `$(pulse) Health: ${issues} new issue${issues === 1 ? '' : 's'}`;
    return `$(pulse) Affects ${things}`;
}

/** True when VS Code should ask before it closes: the open model holds the person's own unsaved edits. A model nobody edited, or one only marked for
 *  Jev's permission (hasUnsavedEdits false), or one open live (its edits stay in the engine), has nothing to lose. An engine that predates hasUnsavedEdits is read by hasUnsavedChanges. */
export function guardWanted(info: { sessionId?: string; hasUnsavedEdits?: boolean; hasUnsavedChanges?: boolean; liveBound?: boolean } | undefined): boolean {
    // A model open from a live workspace keeps its edits in the engine, which stays running after the window closes, and the model reopens
    // with them (round 2, J4). Nothing is lost, so no tab opens by itself after a run (K2).
    return !!info?.sessionId && !info.liveBound && !!(info.hasUnsavedEdits ?? info.hasUnsavedChanges);
}
