// ---- display folders (TE2-style) — PURE logic, no vscode import --------------------------------
// A display folder has no existence of its own in the model — it is just the DisplayFolder string on each
// measure/column/hierarchy ("Parent\Child" for nesting), so an empty folder cannot persist. The Model tree
// groups the engine's FLAT fan into 'dfolder:' nodes client-side; every folder gesture writes through the
// engine's batch ops (setDisplayFolder / renameDisplayFolder) so one gesture is ONE undo step.
//
// This module holds the folder math the tree RENDER path and the reveal-by-ref path both use — kept vscode-free
// so there is ONE folder-resolution (not a parallel one) and it can be unit-tested outside the VS Code host
// (test/folders.test.mjs imports the compiled out/folders.js).

/** The subset of a tree node the folder math needs — structurally identical to extension.ts's TreeNode. */
export interface FolderNode { ref: string; name: string; kind: string; hasChildren: boolean; displayFolder?: string; }

/** Normalize a DisplayFolder path for comparison/refs: trim whitespace + stray leading/trailing separators. */
export function normFolder(f?: string): string {
    return (f ?? '').trim().replace(/^\\+|\\+$/g, '').trim();
}

/** 'dfolder:Sales/KPIs\Core' -> { table: 'Sales', path: 'KPIs\Core' } (the path may itself contain '/'). */
export function folderParts(ref: string): { table: string; path: string } {
    const rest = ref.slice('dfolder:'.length);
    const slash = rest.indexOf('/');
    return { table: rest.slice(0, slash), path: rest.slice(slash + 1) };
}

/** The enclosing folder of a folder path ('KPIs\Growth' -> 'KPIs'), or '' when it sits at the table root. */
export function parentFolderPath(path: string): string {
    const cut = path.lastIndexOf('\\');
    return cut >= 0 ? path.slice(0, cut) : '';
}

/** The leaf segment of a folder path ('KPIs\Growth' -> 'Growth'). */
export function leafFolderName(path: string): string {
    return path.slice(path.lastIndexOf('\\') + 1);
}

/** The dfolder ref for a table + normalized folder path. */
export function folderRef(table: string, path: string): string {
    return `dfolder:${table}/${path}`;
}

/**
 * One level of the folder hierarchy: the subfolder nodes at `prefix` (first-seen casing, case-insensitive
 * sort), then the members filed exactly AT `prefix` in the engine's order ('' = the table root, where members
 * with no folder live). Folder names compare case-insensitively throughout (AS names are case-insensitive).
 */
export function groupFolderLevel<T extends FolderNode>(kids: T[], table: string, prefix: string): (T | FolderNode)[] {
    const prefixLower = prefix.toLowerCase();
    const nestedLower = prefixLower ? prefixLower + '\\' : '';
    const folders = new Map<string, FolderNode>();
    const direct: T[] = [];
    for (const k of kids) {
        const df = normFolder(k.displayFolder);
        const dfLower = df.toLowerCase();
        if (dfLower === prefixLower) { direct.push(k); continue; }                    // filed exactly here
        if (prefixLower ? !dfLower.startsWith(nestedLower) : false) continue;         // a different branch
        const rest = prefixLower ? df.slice(prefix.length + 1) : df;
        const seg = rest.split('\\')[0];
        const key = seg.toLowerCase();
        if (!folders.has(key)) {
            const path = prefix ? `${prefix}\\${seg}` : seg;
            folders.set(key, { ref: folderRef(table, path), name: seg, kind: 'dfolder', hasChildren: true });
        }
    }
    // sensitivity 'base': the SORT is case-insensitive too, matching the case-insensitive grouping above
    // (default localeCompare is case-sensitive in most locales — 'Zebra' would beat 'alpha').
    const subfolders = [...folders.values()].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
    return [...subfolders, ...direct];
}
