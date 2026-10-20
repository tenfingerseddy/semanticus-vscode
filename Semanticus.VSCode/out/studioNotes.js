"use strict";
// What the assistant says to the Studio (1.4, Claude drives; contract sections 1, 4 and 6).
//
// The engine broadcasts studio/show, studio/suggest, studio/nextStep and assistant/call. The Studio webview may not be mounted when they
// arrive (it was closed, or is still loading), so this buffer holds them and posts them in order once the Studio says
// 'studioReady'. It is the same pattern as pendingNav and flushNav in extension.ts, with two differences: it keeps up to
// 20 messages, not one, and it may open a closed Studio.
//
// It never moves a screen. The webview's driver store decides that. The host's only decision here is whether a note is
// worth opening a closed Studio for: only in Claude drives mode, and only while the person has not taken the wheel.
Object.defineProperty(exports, "__esModule", { value: true });
exports.StudioNotes = exports.STUDIO_BUFFER_LIMIT = void 0;
/** How many messages wait for a Studio that is not ready. The oldest are dropped first; the engine keeps the notes anyway. */
exports.STUDIO_BUFFER_LIMIT = 20;
class StudioNotes {
    constructor(host) {
        this.host = host;
        this.pending = [];
        this.driver = { mode: 'claudeDrives', state: 'idle' };
    }
    /** The Studio's latest report of who drives (relayed from the webview's driverState message). */
    setDriver(report) { this.driver = report; }
    getDriver() { return this.driver; }
    /** A broadcast arrived. Post it now when the Studio is ready, else keep it and, for a note, maybe open the Studio. */
    push(message) {
        if (this.host.isReady()) {
            this.host.post(message);
            return;
        }
        // A call signal is only meaningful live ("the next prompt"); replayed later it would read as a new prompt.
        if (message.type === 'assistantCall')
            return;
        // One current suggestion: a newer one replaces a waiting one.
        if (message.type === 'studioSuggest')
            this.pending = this.pending.filter((m) => m.type !== 'studioSuggest');
        // Likewise one current answer: the Ask page shows the latest the assistant asked for.
        if (message.type === 'studioAnswer')
            this.pending = this.pending.filter((m) => m.type !== 'studioAnswer');
        // And one current Next line (studio/nextStep, 1.5): only the latest view matters.
        if (message.type === 'studioNextStep')
            this.pending = this.pending.filter((m) => m.type !== 'studioNextStep');
        this.pending.push(message);
        if (this.pending.length > exports.STUDIO_BUFFER_LIMIT)
            this.pending.splice(0, this.pending.length - exports.STUDIO_BUFFER_LIMIT);
        if (message.type === 'studioShow' && !this.host.isOpen() && this.mayOpen())
            this.host.open();
    }
    /** The Studio posted 'studioReady': post everything that waited, in order. */
    flush() {
        if (!this.host.isReady())
            return;
        const waiting = this.pending;
        this.pending = [];
        for (const m of waiting)
            this.host.post(m);
    }
    /** How many messages are waiting (for tests). */
    get waiting() { return this.pending.length; }
    mayOpen() {
        return this.driver.mode === 'claudeDrives' && this.driver.state !== 'personHasWheel';
    }
}
exports.StudioNotes = StudioNotes;
//# sourceMappingURL=studioNotes.js.map