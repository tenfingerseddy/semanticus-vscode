"use strict";
// A window with no folder keeps no per-window memory of the model that was open, so a person who quit VS Code and came back saw
// "Open a model to begin" while Connections listed the model (round 2 UAT N25). The engine's saved connections do remember, with
// the time each was used, so the last one is offered from there. It is an offer, never an automatic open.
Object.defineProperty(exports, "__esModule", { value: true });
exports.recentModelOffer = recentModelOffer;
/** The model opened most recently, as a reopen target. A running Power BI Desktop copy is not offered: it may be closed by now. */
function recentModelOffer(records, windowHasFolder) {
    if (windowHasFolder)
        return undefined;
    const usable = (records ?? []).filter((r) => (r.kind === 'xmla' || r.kind === 'file') && !!r.endpoint);
    const last = [...usable].sort((a, b) => String(b.lastUsedUtc ?? '').localeCompare(String(a.lastUsedUtc ?? '')))[0];
    if (!last)
        return undefined;
    if (last.kind === 'file')
        return { kind: 'file', path: last.endpoint, modelName: last.modelName };
    return { kind: 'live', endpoint: last.endpoint, database: last.database, tenantId: last.tenantId, authMode: last.authMode, modelName: last.modelName };
}
//# sourceMappingURL=recentModel.js.map