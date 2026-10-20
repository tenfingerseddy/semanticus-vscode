// A window with no folder keeps no per-window memory of the model that was open, so a person who quit VS Code and came back saw
// "Open a model to begin" while Connections listed the model (round 2 UAT N25). The engine's saved connections do remember, with
// the time each was used, so the last one is offered from there. It is an offer, never an automatic open.

export interface RecentRecord { kind: string; endpoint?: string; database?: string; modelName?: string; tenantId?: string; authMode?: string; lastUsedUtc?: string }
export interface RecentOffer { kind: 'file' | 'live'; path?: string; endpoint?: string; database?: string; tenantId?: string; authMode?: string; modelName?: string }

/** The model opened most recently, as a reopen target. A running Power BI Desktop copy is not offered: it may be closed by now. */
export function recentModelOffer(records: readonly RecentRecord[] | undefined, windowHasFolder: boolean): RecentOffer | undefined {
    if (windowHasFolder) return undefined;
    const usable = (records ?? []).filter((r) => (r.kind === 'xmla' || r.kind === 'file') && !!r.endpoint);
    const last = [...usable].sort((a, b) => String(b.lastUsedUtc ?? '').localeCompare(String(a.lastUsedUtc ?? '')))[0];
    if (!last) return undefined;
    if (last.kind === 'file') return { kind: 'file', path: last.endpoint, modelName: last.modelName };
    return { kind: 'live', endpoint: last.endpoint, database: last.database, tenantId: last.tenantId, authMode: last.authMode, modelName: last.modelName };
}
