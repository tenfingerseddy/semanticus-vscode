// Learn Semanticus, the host half (1.4, contract section 9). The tour works on a scratch copy of the bundled demo model, so
// nothing the person built is touched. This file holds the parts that need no VS Code: where a copy goes, which old copies
// go, and the copying itself. extension.ts adds the glue (open the copy, remember the model that was open, reopen it).
//
// THE PATH RULE. <globalStorage>/learn/<chapter>-<yyyyMMdd-HHmmss>/ (UTC), one folder for each start. The clock only names
// the folder, so two starts in the same second share a name: the second one adds -2, -3 and so on.
import * as fs from 'fs';
import * as path from 'path';

export const LEARN_KEEP_DAYS = 7;
export const LEARN_CHAPTERS = ['plan', 'ask', 'edit', 'review', 'publish'] as const;
export type LearnChapter = typeof LEARN_CHAPTERS[number];
/** The folder name a start makes. Anything else under learn/ was not made here and is never deleted. */
const COPY_NAME = /^(plan|ask|edit|review|publish)-\d{8}-\d{6}(-\d+)?$/;
const DAY_MS = 24 * 60 * 60 * 1000;

export function isLearnChapter(id: unknown): id is LearnChapter {
    return typeof id === 'string' && (LEARN_CHAPTERS as readonly string[]).includes(id);
}

const two = (n: number) => String(n).padStart(2, '0');
export function scratchName(chapter: LearnChapter, now: Date): string {
    const day = `${now.getUTCFullYear()}${two(now.getUTCMonth() + 1)}${two(now.getUTCDate())}`;
    const time = `${two(now.getUTCHours())}${two(now.getUTCMinutes())}${two(now.getUTCSeconds())}`;
    return `${chapter}-${day}-${time}`;
}

/** Where the copy for this start goes. */
export function scratchPath(globalStorage: string, chapter: LearnChapter, now: Date): string {
    return path.join(globalStorage, 'learn', scratchName(chapter, now));
}

/** The copies old enough to delete: made here (by name), and last changed more than seven days before `nowMs`. */
export function staleCopies(entries: { name: string; mtimeMs: number }[], nowMs: number): string[] {
    return entries.filter((e) => COPY_NAME.test(e.name) && nowMs - e.mtimeMs > LEARN_KEEP_DAYS * DAY_MS).map((e) => e.name);
}

function copyFolder(from: string, to: string): void {
    fs.mkdirSync(to, { recursive: true });
    for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
        const source = path.join(from, entry.name);
        const target = path.join(to, entry.name);
        if (entry.isDirectory()) copyFolder(source, target);
        else fs.copyFileSync(source, target);
    }
}

/** Deletes the old copies. Only direct children of learn/ whose names match the rule are touched. */
export function removeOldCopies(globalStorage: string, nowMs: number): string[] {
    const root = path.join(globalStorage, 'learn');
    let entries: { name: string; mtimeMs: number }[];
    try {
        entries = fs.readdirSync(root, { withFileTypes: true }).filter((e) => e.isDirectory())
            .map((e) => ({ name: e.name, mtimeMs: fs.statSync(path.join(root, e.name)).mtimeMs }));
    } catch { return []; }
    const gone: string[] = [];
    for (const name of staleCopies(entries, nowMs)) {
        try { fs.rmSync(path.join(root, name), { recursive: true, force: true }); gone.push(name); } catch { /* in use, try next start */ }
    }
    return gone;
}

/** Makes the scratch copy of the demo model for one start and returns its path. Old copies are removed first. */
export function makeScratchCopy(assetsDir: string, globalStorage: string, chapter: LearnChapter, now: Date = new Date()): string {
    removeOldCopies(globalStorage, now.getTime());
    const demo = path.join(assetsDir, 'learn', 'contoso-shop');
    if (!fs.existsSync(demo)) throw new Error('The demo model is missing from this install.');
    let target = scratchPath(globalStorage, chapter, now);
    for (let n = 2; fs.existsSync(target); n++) target = scratchPath(globalStorage, chapter, now) + '-' + n;
    copyFolder(demo, target);
    return target;
}
