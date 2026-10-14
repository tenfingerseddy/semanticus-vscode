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
// One instance serves every panel, so the Studio and the standalone Connections hub cannot both ask about one model.

export class ConsentAsks {
    private readonly asked = new Set<string>();

    /** Whether the card may ask about `scope` now. A yes is also the record that it asked: the card is up. */
    claim(scope: unknown): boolean {
        if (typeof scope !== 'string' || scope.length === 0 || scope.length > 4096) return false;
        if (this.asked.has(scope)) return false;
        this.asked.add(scope);
        return true;
    }
}
