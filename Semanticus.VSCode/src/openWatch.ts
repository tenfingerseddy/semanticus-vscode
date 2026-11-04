// The window's open watch (cx5, the 1.7.4-rc3 tester, 3 Nov).
//
// At "Step 5 of 5 · The model is open", for 1 to 3 s before the dialog closed, the status bar read "<new model> · 8 measures" (the old
// model's count), Properties read the old model's "Checking 6 of 8" and the tree's top row named the old model over the new model's
// tables. The engine swaps the session and announces it ("Opened <model>") before it connects for queries and answers. The window's
// change handler repainted half the surfaces from the new session and left the rest on the old one.
//
// One rule now: from the press, every per-model surface drops any value whose session is not the one being opened.
//   - The press: the status bar says "Opening <model>...", the tree's row and Properties drop the old model's figures.
//   - A notice that names a session the window does not show: every surface names that model and says "Reading <model>...".
//   - The open's own reads then fill in the new model's values. A stopped or failed open reads the model that stays open again.
// Plain code with no VS Code import, so the node tests run the real thing; extension.ts passes in what each step paints.
import type { SwapModel } from './swapReading';

export interface OpenSurfaces {
    /** The press: no figure of the model being left, one line naming the model being opened. */
    opening(name: string): void;
    /** The engine swapped to a session the window does not show yet: every surface names it and says it is being read. */
    reading(swap: SwapModel): void;
    /** The open did not happen (stopped or failed): read the model that is still open and show it again. */
    restore(): void;
}

const OPEN_METHODS = new Set(['open', 'openLocal', 'openLive']);
/** The window's own opens of a model, the requests the press starts. */
export const isOpenMethod = (method: string): boolean => OPEN_METHODS.has(method);

/** The one line the surfaces show from the press until the engine swaps. */
export const openingLine = (name: string): string => `Opening ${name}...`;

/** The model's name the press can show before the engine answers, from the open's own arguments. */
export function openTargetName(method: string, params: readonly unknown[]): string {
    const text = (v: unknown) => (typeof v === 'string' ? v.trim() : '');
    if (method === 'openLive') {
        const db = text(params[1]);
        if (db) return db;
        const ws = text(params[0]).replace(/[\\/]+$/, '').split(/[\\/]/).pop() ?? '';
        let name = ws;
        try { name = decodeURIComponent(ws); } catch { /* keep the text as typed */ }
        return name ? `the model in ${name}` : 'the model';
    }
    if (method === 'openLocal') return text(params[1]) || 'the Power BI Desktop model';
    // A file open names the file or folder: the model inside it can carry another name, and the press must not guess it.
    const parts = text(params[0]).replace(/[\\/]+$/, '').split(/[\\/]/);
    const leaf = parts.pop() ?? '';
    return (/^definition$/i.test(leaf) ? parts.pop() : leaf) || 'the model';
}

/** The engine labels an open's own change "Opened <model>". */
export function openedNameOf(label: string | undefined | null): string | undefined {
    const m = /^Opened (.+)$/.exec((label ?? '').trim());
    return m ? m[1] : undefined;
}

export class OpenWatch {
    private next = 0;
    private going: { id: number; name: string } | undefined;
    private movedTo: string | undefined;

    /** `shown` is the session the window's surfaces show now (the host's currentSessionId). */
    constructor(private readonly surfaces: OpenSurfaces, private readonly shown: () => string | undefined) {}

    /** The model being opened, while an open the window started runs. */
    get opening(): string | undefined { return this.going?.name; }

    /** The press. Returns the id its end names; a newer press replaces an older one. */
    begin(name: string): number {
        const id = ++this.next;
        this.going = { id, name };
        this.movedTo = undefined;
        this.surfaces.opening(name);
        return id;
    }

    /** A notice or a read names its session. 'moved' when that is not the session the surfaces show: the caller must not paint the
     *  old model's figures with it. The first such notice moves every surface to "Reading <model>..."; later ones change nothing. */
    seen(sessionId: string | undefined | null, name?: string): 'shown' | 'moved' {
        if (!sessionId || sessionId === this.shown()) return 'shown';
        if (this.movedTo === sessionId) return 'moved';
        this.movedTo = sessionId;
        this.surfaces.reading({ sessionId, name: name || this.going?.name || 'the model' });
        return 'moved';
    }

    /** True for a value read for a session that is not the one being opened, while an open the window started runs. */
    drops(sessionId: string | undefined | null): boolean {
        if (!this.going) return false;
        return !sessionId || sessionId !== this.movedTo;
    }

    /** The open answered (ok) or failed or stopped. Only the newest press ends the watch. A failed open, and an answer that moved the window
     *  to no new session, read the model that is open again: the press line must not stay. */
    end(id: number, ok: boolean): void {
        if (this.going?.id !== id) return;
        const moved = this.movedTo !== undefined;
        this.going = undefined;
        this.movedTo = undefined;
        if (!ok || !moved) this.surfaces.restore();
    }
}
