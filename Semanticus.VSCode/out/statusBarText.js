"use strict";
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
Object.defineProperty(exports, "__esModule", { value: true });
exports.syncVerdict = syncVerdict;
exports.modelItem = modelItem;
exports.healthText = healthText;
exports.guardWanted = guardWanted;
/** The tail of a source path, so two copies of one model name stay apart in the tooltip. */
function sourceTail(source, max = 48) {
    const v = (source || '').replace(/[\\/]+$/, '');
    if (!v)
        return '';
    return v.length <= max ? v : '…' + v.slice(-(max - 1));
}
/** EDITING against QUERYING. It warns only when it can PROVE a divergence: a live query connection with unsaved edits,
 *  the exact case where query results leave out your staged work. It reads the same session info the Studio footer
 *  reads, so there is no second source of truth. It never blocks; it discloses. */
function syncVerdict(info) {
    const norm = (x) => (x || '').trim().toLowerCase().replace(/[\\/]+$/, '');
    const short = (p) => { const t = (p || '').replace(/[\\/]+$/, ''); const i = Math.max(t.lastIndexOf('/'), t.lastIndexOf('\\')); return i >= 0 ? t.slice(i + 1) : t; };
    const editing = info.liveBound && info.liveDatabase ? info.liveDatabase : (info.modelName || 'model');
    const connected = !!info.liveConnected;
    // Same live instance? Only claim it when the attached query source provably matches the edited endpoint or source.
    const q = norm(info.liveDataSource);
    const same = connected && !!q && (q === norm(info.liveEndpoint) || q === norm(info.source));
    const querying = !connected ? 'offline'
        : (info.liveBound && info.liveDatabase ? info.liveDatabase : (short(info.liveDataSource) || 'live model'));
    let warn = false, detail;
    if (!connected)
        detail = 'No live connection, so queries are unavailable.';
    else if (info.hasUnsavedEdits ?? info.hasUnsavedChanges) {
        warn = true;
        detail = "Unsaved edits are not in the model you're querying.";
    }
    else if (same)
        detail = 'Editing and querying the same instance.';
    // A local attach (Power BI Desktop, liveKind 'local') is NOT a published model: do not tell the person their
    // queries "reflect the last deploy" when they hit a live local engine. That disclosure belongs to the XMLA case.
    else if (info.liveKind === 'local')
        detail = 'Querying an attached local engine (Power BI Desktop).';
    else
        detail = 'Querying the published model. Reflects the last deploy.';
    return { warn, detail, editing, querying, connected };
}
/** The one model item: `{name}`, with a warning icon and tint only while the unsaved edits are not in the model tests use. */
function modelItem(info) {
    const verdict = syncVerdict(info);
    const lines = [];
    if (info.source)
        lines.push(sourceTail(info.source, 200));
    if (typeof info.measures === 'number')
        lines.push(`${info.measures} measure${info.measures === 1 ? '' : 's'}`);
    if (info.currentAccount && (info.liveConnected || info.liveBound))
        lines.push(`Signed in as ${info.currentAccount}${info.currentTenant ? ` · ${info.currentTenant}` : ''}`);
    lines.push(`Editing: ${verdict.editing}`, `Querying: ${verdict.connected ? verdict.querying : 'not connected'}`, verdict.detail);
    lines.push(verdict.warn ? "Click to compare what you're editing against what you're querying." : 'Click to show the Model tree.');
    return {
        text: `${verdict.warn ? '$(warning)' : '$(database)'} ${info.modelName || 'Model'}`,
        tooltip: lines.join('\n'),
        warn: verdict.warn,
        command: verdict.warn ? 'semanticus.openCompareSeeded' : 'semanticusModel.focus',
    };
}
/** The health chip's few words: "Health B → C", "Health: 3 new issues" or "Affects 4". The tooltip keeps the full sentence. */
function healthText(h) {
    const grade = h.grade && h.grade.includes('->') ? h.grade.split('->') : undefined;
    const issues = h.findings ?? (h.new?.length ?? 0);
    const things = h.impact ?? 0;
    if (grade)
        return `$(pulse) Health ${grade[0].trim()} → ${grade[1].trim()}`;
    if (issues > 0)
        return `$(pulse) Health: ${issues} new issue${issues === 1 ? '' : 's'}`;
    return `$(pulse) Affects ${things}`;
}
/** Older engines need a close question for the person's unsaved edits. Current owners keep dirty work for the next window.
 *  An engine that predates hasUnsavedEdits is read by hasUnsavedChanges. */
function guardWanted(info) {
    // Keep the legacy fallback for engines that cannot retain local work after the window closes.
    return !!info?.sessionId && !info.keepsUnsavedWork && !info.liveBound && !!(info.hasUnsavedEdits ?? info.hasUnsavedChanges);
}
//# sourceMappingURL=statusBarText.js.map