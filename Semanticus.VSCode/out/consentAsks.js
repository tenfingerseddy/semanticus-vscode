"use strict";
// When Studio may raise the "Let Pro decide for this model?" card by itself. Kept here in the extension host, for the
// window's run, because a Studio webview is rebuilt every time the person closes and reopens it: a memory kept in the
// page forgot "Not now" with it and asked again (verifier r2, O2).
//
// One rule (coordinator, 26 and 27 Sep; changed 13 Oct, hero 20 D10): a model is asked about once in this window's run.
// Answered or dismissed, it is not asked again, even after the person reopens it. The Connections line's "Turn on" is
// always there for someone who changes their mind, and it does not come through here. A model the assistant opened is
// asked about like any other: the old rule that stayed silent after the assistant's swap left Jev quiet until the
// person found Pro > Turn on. The card only asks; nothing is ever turned on by itself.
//
// Not now is also kept in the editor's own store (globalState), so Reload Window and a new window do not ask again about a model the
// person put off (UAT round 2). The list is capped, oldest dropped, so it cannot grow without end.
//
// One instance serves every panel, so the Studio and the standalone Connections hub cannot both ask about one model.
Object.defineProperty(exports, "__esModule", { value: true });
exports.ConsentAsks = void 0;
const ASKED_KEY = 'semanticus.consentAsked';
const ASKED_CAP = 500;
class ConsentAsks {
    constructor() {
        this.asked = new Set();
    }
    /** Give the memory its store: what earlier runs asked about is read back, and every later ask is kept. */
    remember(store) {
        this.store = store;
        try {
            for (const scope of store.get(ASKED_KEY, []))
                if (typeof scope === 'string')
                    this.asked.add(scope);
        }
        catch { /* an unreadable store is an empty one */ }
    }
    /** What this window has asked about, so a reload Semanticus causes can carry it over (the marker and the connect record hold it). */
    snapshot() { return [...this.asked]; }
    /** Scopes a closing window had already asked about: this one does not ask them again. */
    seed(scopes) {
        if (!Array.isArray(scopes))
            return;
        for (const s of scopes)
            if (typeof s === 'string' && s.length > 0 && s.length <= 4096)
                this.asked.add(s);
    }
    /** Whether the card may ask about `scope` now. A yes is also the record that it asked: the card is up. */
    claim(scope) {
        if (typeof scope !== 'string' || scope.length === 0 || scope.length > 4096)
            return false;
        if (this.asked.has(scope))
            return false;
        this.asked.add(scope);
        try {
            void this.store?.update(ASKED_KEY, [...this.asked].slice(-ASKED_CAP));
        }
        catch { /* the window's run still remembers */ }
        return true;
    }
}
exports.ConsentAsks = ConsentAsks;
//# sourceMappingURL=consentAsks.js.map