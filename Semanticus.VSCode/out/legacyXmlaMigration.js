"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.migrateLegacyXmlaEntries = migrateLegacyXmlaEntries;
async function migrateLegacyXmlaEntries(legacy, remember, clearLegacyKey) {
    const records = (legacy ?? []).filter((r) => !!r?.endpoint);
    for (const record of records) {
        const mode = record.authMode === 'interactive' || record.authMode === 'serviceprincipal' || record.authMode === 'azcli'
            ? record.authMode : 'azcli';
        await remember(record, mode);
    }
    // This line is deliberately after the complete import. A partial failure must retain the old key so the
    // idempotent engine writes can be retried without losing the entries that were not reached.
    await clearLegacyKey();
    return records.length;
}
//# sourceMappingURL=legacyXmlaMigration.js.map