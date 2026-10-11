"use strict";
// The Plan's first-open guidance keeps two kinds of once-flag in context.globalState (Plan-heart contract 10):
// whether the tour has been closed, and whether each What changed card has been seen. globalState, not
// workspaceState, because a person who has taken the tour once has taken it for every project.
//
// The card id is a CONTENT id ("plan-heart-1"), never the package version, so a release that ships no card
// shows nothing and a release that ships one shows it once. The webview names the card it wants; the host
// reads and writes only these two key shapes, and only a card id of the shape below, because the message
// comes from a page and globalState is shared by every window.
Object.defineProperty(exports, "__esModule", { value: true });
exports.PLAN_TOUR_KEY = void 0;
exports.whatChangedKey = whatChangedKey;
exports.readGuideFlags = readGuideFlags;
exports.writeGuideFlag = writeGuideFlag;
exports.PLAN_TOUR_KEY = 'semanticus.guide.planTour';
const WHAT_CHANGED_PREFIX = 'semanticus.guide.whatChanged.';
const CARD_ID = /^[a-z0-9][a-z0-9-]{0,47}$/;
function whatChangedKey(cardId) { return WHAT_CHANGED_PREFIX + cardId; }
function readGuideFlags(state, cardId) {
    const tour = state.get(exports.PLAN_TOUR_KEY);
    const card = typeof cardId === 'string' && CARD_ID.test(cardId) ? state.get(whatChangedKey(cardId)) === true : false;
    return { planTour: !!tour?.done, whatChanged: card };
}
/** Writes the one flag a message names, or nothing when the message is not one of the two shapes. */
async function writeGuideFlag(state, msg, now = () => new Date().toISOString()) {
    if (!msg || typeof msg !== 'object')
        return;
    const { flag, cardId } = msg;
    if (flag === 'planTour' && cardId === undefined) {
        await state.update(exports.PLAN_TOUR_KEY, { done: true, at: now() });
        return;
    }
    if (flag === 'whatChanged' && typeof cardId === 'string' && CARD_ID.test(cardId))
        await state.update(whatChangedKey(cardId), true);
}
//# sourceMappingURL=guideFlags.js.map