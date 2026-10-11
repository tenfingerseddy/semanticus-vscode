// When Studio may raise the "Let Pro decide for this model?" card by itself. Kept here in the extension host, for the
// window's run, because a Studio webview is rebuilt every time the person closes and reopens it: a memory kept in the
// page forgot "Not now" with it and asked again (verifier r2, O2).
//
// Two rules (coordinator, 26 and 27 Sep):
//   - A model is asked about once in this window's run. Answered or dismissed, it is not asked again, even after the
//     person reopens it. The Connections line's "Turn on" is always there for someone who changes their mind, and it
//     does not come through here.
//   - The assistant's actions never raise the card (O1). After another door's swap (the assistant, or another window)
//     nothing is asked until the person's own next open. The person's own History restore reopens the model they had,
//     so it changes neither rule.
//
// One instance serves every panel, so the Studio and the standalone Connections hub cannot both ask about one model.

export type SwapBy = 'person' | 'otherDoor' | 'restore';

export class ConsentAsks {
    private readonly asked = new Set<string>();
    private afterOtherDoor = false;

    /** Record whose swap just happened. Call it before the panels hear of the swap, so a page that mounts on the new
     *  model and asks at once already gets the right answer. */
    noteSwap(by: SwapBy): void {
        if (by === 'restore') return;
        this.afterOtherDoor = by === 'otherDoor';
    }

    /** Whether the card may ask about `scope` now. A yes is also the record that it asked: the card is up. */
    claim(scope: unknown): boolean {
        if (typeof scope !== 'string' || scope.length === 0 || scope.length > 4096) return false;
        if (this.afterOtherDoor || this.asked.has(scope)) return false;
        this.asked.add(scope);
        return true;
    }
}
