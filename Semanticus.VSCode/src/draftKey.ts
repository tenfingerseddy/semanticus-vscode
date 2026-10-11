// The model identity a held draft is keyed on (join 11, round 3). The ENGINE says which model a session is (SessionInfo.ModelIdentity, one pure function,
// LocalEngine.ModelIdentityOf) and this reads it verbatim: a client never derives identity from fields that do not mean it (a snapshot path rotates on every
// open, a Desktop port and database change on every Desktop start). The same rule as `modelKeyOfSession` in webview/src/drafts.ts: test/drafts-live-key.test.mjs holds the two equal.
// Only when the engine sends no field (an engine from before this field, version skew) does the older derivation apply: a published (live-bound) model is its
// endpoint and database, a local file is its path, never the session id.
export interface DraftKeySession { modelIdentity?: string; source?: string; liveBound?: boolean; liveEndpoint?: string; liveDatabase?: string; modelName?: string }

export function draftModelKeyOf(s?: DraftKeySession | null): string {
    return !s ? ''
        : s.modelIdentity ? s.modelIdentity
        : s.liveBound && s.liveEndpoint ? 'live:' + s.liveEndpoint.trim() + '|' + (s.liveDatabase ?? '')
        : s.source ? s.source
        : s.modelName ? 'name:' + s.modelName : '';
}
