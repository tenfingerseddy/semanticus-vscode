"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.LEARN_CHAPTERS = exports.LEARN_KEEP_DAYS = void 0;
exports.isLearnChapter = isLearnChapter;
exports.scratchName = scratchName;
exports.scratchPath = scratchPath;
exports.isScratchCopyPath = isScratchCopyPath;
exports.staleCopies = staleCopies;
exports.removeOldCopies = removeOldCopies;
exports.makeScratchCopy = makeScratchCopy;
// Learn Semanticus, the host half (1.4, contract section 9). The tour works on a scratch copy of the bundled demo model, so
// nothing the person built is touched. This file holds the parts that need no VS Code: where a copy goes, which old copies
// go, and the copying itself. extension.ts adds the glue (open the copy, remember the model that was open, reopen it).
//
// THE PATH RULE. <globalStorage>/learn/<chapter>-<yyyyMMdd-HHmmss>/ (UTC), one folder for each start. The clock only names
// the folder, so two starts in the same second share a name: the second one adds -2, -3 and so on.
const fs = __importStar(require("fs"));
const path = __importStar(require("path"));
exports.LEARN_KEEP_DAYS = 7;
exports.LEARN_CHAPTERS = ['plan', 'ask', 'edit', 'review', 'publish'];
/** The folder name a start makes. Anything else under learn/ was not made here and is never deleted. */
const COPY_NAME = /^(plan|ask|edit|review|publish)-\d{8}-\d{6}(-\d+)?$/;
const DAY_MS = 24 * 60 * 60 * 1000;
function isLearnChapter(id) {
    return typeof id === 'string' && exports.LEARN_CHAPTERS.includes(id);
}
const two = (n) => String(n).padStart(2, '0');
function scratchName(chapter, now) {
    const day = `${now.getUTCFullYear()}${two(now.getUTCMonth() + 1)}${two(now.getUTCDate())}`;
    const time = `${two(now.getUTCHours())}${two(now.getUTCMinutes())}${two(now.getUTCSeconds())}`;
    return `${chapter}-${day}-${time}`;
}
/** Where the copy for this start goes. */
function scratchPath(globalStorage, chapter, now) {
    return path.join(globalStorage, 'learn', scratchName(chapter, now));
}
/** True for a path inside a tour's demo copy (<globalStorage>/learn/<chapter>-<stamp>/...). The demo is throwaway: nothing offers it as a person's own model or folder. */
function isScratchCopyPath(p) {
    if (typeof p !== 'string' || p.length === 0)
        return false;
    const parts = p.split(/[\\/]+/);
    for (let i = 0; i + 1 < parts.length; i++)
        if (parts[i] === 'learn' && COPY_NAME.test(parts[i + 1]))
            return true;
    return false;
}
/** The copies old enough to delete: made here (by name), and last changed more than seven days before `nowMs`. */
function staleCopies(entries, nowMs) {
    return entries.filter((e) => COPY_NAME.test(e.name) && nowMs - e.mtimeMs > exports.LEARN_KEEP_DAYS * DAY_MS).map((e) => e.name);
}
function copyFolder(from, to) {
    fs.mkdirSync(to, { recursive: true });
    for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
        const source = path.join(from, entry.name);
        const target = path.join(to, entry.name);
        if (entry.isDirectory())
            copyFolder(source, target);
        else
            fs.copyFileSync(source, target);
    }
}
/** Deletes the old copies. Only direct children of learn/ whose names match the rule are touched. */
function removeOldCopies(globalStorage, nowMs) {
    const root = path.join(globalStorage, 'learn');
    let entries;
    try {
        entries = fs.readdirSync(root, { withFileTypes: true }).filter((e) => e.isDirectory())
            .map((e) => ({ name: e.name, mtimeMs: fs.statSync(path.join(root, e.name)).mtimeMs }));
    }
    catch {
        return [];
    }
    const gone = [];
    for (const name of staleCopies(entries, nowMs)) {
        try {
            fs.rmSync(path.join(root, name), { recursive: true, force: true });
            gone.push(name);
        }
        catch { /* in use, try next start */ }
    }
    return gone;
}
/** Makes the scratch copy of the demo model for one start and returns its path. Old copies are removed first. */
function makeScratchCopy(assetsDir, globalStorage, chapter, now = new Date()) {
    removeOldCopies(globalStorage, now.getTime());
    const demo = path.join(assetsDir, 'learn', 'contoso-shop');
    if (!fs.existsSync(demo))
        throw new Error('The demo model is missing from this install.');
    let target = scratchPath(globalStorage, chapter, now);
    for (let n = 2; fs.existsSync(target); n++)
        target = scratchPath(globalStorage, chapter, now) + '-' + n;
    copyFolder(demo, target);
    return target;
}
//# sourceMappingURL=learn.js.map