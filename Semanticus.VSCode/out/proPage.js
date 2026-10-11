"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.initialProPageState = initialProPageState;
exports.planFromMessage = planFromMessage;
exports.checkoutStartFailure = checkoutStartFailure;
exports.keyResultOf = keyResultOf;
exports.accountOf = accountOf;
const proBilling_1 = require("./proBilling");
function initialProPageState() { return { purchase: 'idle' }; }
/// The plan a message from the page names, or undefined for anything else. The page is webview content,
/// so this is the whole allow-list between a click and the price the checkout is opened for.
function planFromMessage(value) {
    return value === 'monthly' || value === 'yearly' ? value : undefined;
}
/// What a failure to START a checkout means for the page. Only "the billing service could not be
/// reached" (a transport failure, status 0) and "nothing is there" (404) mean buying has not opened yet.
/// A service that answered with anything else is open and broken, and saying "not open yet" about it
/// would be untrue, so that is an error with a plain sentence instead.
function checkoutStartFailure(e) {
    if (e instanceof proBilling_1.BillingError && (e.status === 0 || e.status === 404))
        return { purchase: 'notOpen' };
    return { purchase: 'error', message: "Stripe's page could not be opened just now. Try again in a minute, or activate a license key below." };
}
/// The engine's verdict on a key, read from the entitlement it reports after restarting on that key.
/// The engine is the only thing that verifies a licence; this only sorts its answer into the three lines
/// the page has. Its expiry reason is the one it words as "license expired <date>" (LicenseEntitlement.cs).
function keyResultOf(info) {
    if ((info?.tier ?? '').toLowerCase() === 'pro')
        return 'ok';
    return /^license expired\b/i.test((info?.reason ?? '').trim()) ? 'expired' : 'invalid';
}
/// What the page may say about the licence, from the engine's entitlement and the plan this computer
/// remembers. `sid` is the subscription the licence names ('' when it names none).
function accountOf(info, sid, remembered, nowSeconds) {
    if ((info?.tier ?? '').toLowerCase() !== 'pro')
        return undefined;
    const account = { subscription: !!sid };
    const who = (info?.licensedTo ?? '').trim();
    // The dev switch reports a licensee that is a note to an engineer, not a person.
    if (who && !/SEMANTICUS_DEV_PRO/.test(who))
        account.licensedTo = who;
    if (info?.expiry && info.expiry > 0) {
        account.expiry = info.expiry;
        // Pro with an expiry already behind it is the grace window, and the engine words its end date.
        const grace = /in grace until (\d{4}-\d{2}-\d{2})/i.exec(info.reason ?? '');
        if (info.expiry < nowSeconds && grace)
            account.graceUntil = Math.floor(Date.parse(`${grace[1]}T00:00:00Z`) / 1000);
    }
    if (sid && remembered && remembered.sid === sid)
        account.plan = remembered.plan;
    return account;
}
//# sourceMappingURL=proPage.js.map