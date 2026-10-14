// What the assistant says to the Studio (1.4, Claude drives; contract sections 1, 4 and 6).
//
// The engine broadcasts studio/show, studio/suggest, studio/nextStep and assistant/call. The Studio webview may not be mounted when they
// arrive (it was closed, or is still loading), so this buffer holds them and posts them in order once the Studio says
// 'studioReady'. It is the same pattern as pendingNav and flushNav in extension.ts, with two differences: it keeps up to
// 20 messages, not one, and it may open a closed Studio.
//
// It never moves a screen. The webview's driver store decides that. The host's only decision here is whether a note is
// worth opening a closed Studio for: only in Claude drives mode, and only while the person has not taken the wheel.

export type StudioMessageType = 'studioShow' | 'studioSuggest' | 'studioAnswer' | 'assistantCall' | 'studioNextStep';
export interface StudioMessage { type: StudioMessageType; payload: unknown }

/** Who drives, as the Studio last reported it. The Studio reports on every change; until then, a fresh Studio's start. */
export interface DriverReport { mode: 'claudeDrives' | 'iDrive'; state: 'idle' | 'claudeDriving' | 'personHasWheel' }

export interface StudioNotesHost {
    /** The Studio webview has mounted and posted 'studioReady', so its listeners are live. */
    isReady(): boolean;
    /** A Studio panel exists (it may still be mounting). */
    isOpen(): boolean;
    post(message: StudioMessage): void;
    /** Open the Studio. Called only in Claude drives mode, when the person has not taken the wheel. */
    open(): void;
}

/** How many messages wait for a Studio that is not ready. The oldest are dropped first; the engine keeps the notes anyway. */
export const STUDIO_BUFFER_LIMIT = 20;

export class StudioNotes {
    private pending: StudioMessage[] = [];
    private driver: DriverReport = { mode: 'claudeDrives', state: 'idle' };

    constructor(private readonly host: StudioNotesHost) {}

    /** The Studio's latest report of who drives (relayed from the webview's driverState message). */
    setDriver(report: DriverReport): void { this.driver = report; }
    getDriver(): DriverReport { return this.driver; }

    /** A broadcast arrived. Post it now when the Studio is ready, else keep it and, for a note, maybe open the Studio. */
    push(message: StudioMessage): void {
        if (this.host.isReady()) { this.host.post(message); return; }
        // A call signal is only meaningful live ("the next prompt"); replayed later it would read as a new prompt.
        if (message.type === 'assistantCall') return;
        // One current suggestion: a newer one replaces a waiting one.
        if (message.type === 'studioSuggest') this.pending = this.pending.filter((m) => m.type !== 'studioSuggest');
        // Likewise one current answer: the Ask page shows the latest the assistant asked for.
        if (message.type === 'studioAnswer') this.pending = this.pending.filter((m) => m.type !== 'studioAnswer');
        // And one current Next line (studio/nextStep, 1.5): only the latest view matters.
        if (message.type === 'studioNextStep') this.pending = this.pending.filter((m) => m.type !== 'studioNextStep');
        this.pending.push(message);
        if (this.pending.length > STUDIO_BUFFER_LIMIT) this.pending.splice(0, this.pending.length - STUDIO_BUFFER_LIMIT);
        if (message.type === 'studioShow' && !this.host.isOpen() && this.mayOpen()) this.host.open();
    }

    /** The Studio posted 'studioReady': post everything that waited, in order. */
    flush(): void {
        if (!this.host.isReady()) return;
        const waiting = this.pending;
        this.pending = [];
        for (const m of waiting) this.host.post(m);
    }

    /** How many messages are waiting (for tests). */
    get waiting(): number { return this.pending.length; }

    private mayOpen(): boolean {
        return this.driver.mode === 'claudeDrives' && this.driver.state !== 'personHasWheel';
    }
}
