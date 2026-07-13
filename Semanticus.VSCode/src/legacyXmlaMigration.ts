export interface LegacyRecentXmla {
    endpoint: string;
    database?: string;
    authMode?: string;
    modelName?: string;
}

type AuthMode = 'interactive' | 'serviceprincipal' | 'azcli';

export async function migrateLegacyXmlaEntries(
    legacy: LegacyRecentXmla[],
    remember: (record: LegacyRecentXmla, mode: AuthMode) => Promise<unknown>,
    clearLegacyKey: () => PromiseLike<void>,
): Promise<number> {
    const records = (legacy ?? []).filter((r) => !!r?.endpoint);
    for (const record of records) {
        const mode: AuthMode = record.authMode === 'interactive' || record.authMode === 'serviceprincipal' || record.authMode === 'azcli'
            ? record.authMode : 'azcli';
        await remember(record, mode);
    }
    // This line is deliberately after the complete import. A partial failure must retain the old key so the
    // idempotent engine writes can be retried without losing the entries that were not reached.
    await clearLegacyKey();
    return records.length;
}
