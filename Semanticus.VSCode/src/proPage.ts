// The Semanticus Pro page, host half: the decisions the page's state rests on, as plain functions.
//
// The page itself is a Studio webview page (webview/src/propage.tsx). It owns no licence logic at all: it
// shows what this file hands it and posts the person's clicks back. The flows that act on those clicks are
// the ones that already existed in extension.ts (upgradeToProFlow, activateLicenseFlow,
// manageSubscriptionCmd), so SecretStorage, the mutation lock, the conflict machinery, the checkout nonce
// and the return link are reused rather than copied. What lives here is only the part that can be judged
// without VS Code: which plan a click named, what a failed checkout start means, what the engine's verdict
// on a pasted key was, and what the page may say about the licence in hand.
//
// No vscode import, so test/pro-in-app.test.mjs runs every one of these for real.
import type { BillingPlan } from './proBilling';
import { BillingError } from './proBilling';

/// Where a purchase stands. `notOpen` is its own state because it is not a failure of the person's: the
/// billing service is not reachable, which today means buying in the app has not opened yet.
export type PurchaseState = 'idle' | 'waiting' | 'done' | 'notOpen' | 'error';

/// What the engine said about a pasted key, once it had restarted on it. `checking` is the wait in between.
export type KeyResult = 'checking' | 'ok' | 'invalid' | 'expired';

/// What the page may say about the licence in hand. Every field is optional because the page shows only
/// what is known: an old licence has no subscription, a licence bought on another computer has no plan
/// this computer ever saw, and a perpetual one has no end date.
export interface ProAccount {
    licensedTo?: string;
    /// Unix seconds. Absent for a perpetual licence.
    expiry?: number;
    /// True when the licence names a Stripe subscription, which is the only kind that renews.
    subscription: boolean;
    /// Only when this computer bought it and the subscription still matches.
    plan?: BillingPlan;
    /// Unix seconds, when the licence has ended but Pro is still on for its grace days.
    graceUntil?: number;
}

export interface ProPageState {
    purchase: PurchaseState;
    /// The plan the waiting or finished purchase is for.
    plan?: BillingPlan;
    /// A plain sentence for the `error` state. Never shown for any other state.
    message?: string;
    key?: { result: KeyResult; licensedTo?: string };
    account?: ProAccount;
}

export function initialProPageState(): ProPageState { return { purchase: 'idle' }; }

/// The plan a message from the page names, or undefined for anything else. The page is webview content,
/// so this is the whole allow-list between a click and the price the checkout is opened for.
export function planFromMessage(value: unknown): BillingPlan | undefined {
    return value === 'monthly' || value === 'yearly' ? value : undefined;
}

/// What a failure to START a checkout means for the page. Only "the billing service could not be
/// reached" (a transport failure, status 0) and "nothing is there" (404) mean buying has not opened yet.
/// A service that answered with anything else is open and broken, and saying "not open yet" about it
/// would be untrue, so that is an error with a plain sentence instead.
export function checkoutStartFailure(e: unknown): { purchase: 'notOpen' } | { purchase: 'error'; message: string } {
    if (e instanceof BillingError && (e.status === 0 || e.status === 404)) return { purchase: 'notOpen' };
    return { purchase: 'error', message: "Stripe's page could not be opened just now. Try again in a minute, or activate a license key below." };
}

/// The engine's verdict on a key, read from the entitlement it reports after restarting on that key.
/// The engine is the only thing that verifies a licence; this only sorts its answer into the three lines
/// the page has. Its expiry reason is the one it words as "license expired <date>" (LicenseEntitlement.cs).
export function keyResultOf(info: { tier?: string; reason?: string } | undefined): 'ok' | 'invalid' | 'expired' {
    if ((info?.tier ?? '').toLowerCase() === 'pro') return 'ok';
    return /^license expired\b/i.test((info?.reason ?? '').trim()) ? 'expired' : 'invalid';
}

/// The plan this computer remembers buying, keyed on the subscription it bought, so a licence activated
/// later for a DIFFERENT subscription is never labelled with a plan it did not choose.
export interface RememberedPlan { sid: string; plan: BillingPlan }

/// What the page may say about the licence, from the engine's entitlement and the plan this computer
/// remembers. `sid` is the subscription the licence names ('' when it names none).
export function accountOf(
    info: { tier?: string; licensedTo?: string; expiry?: number; reason?: string } | undefined,
    sid: string,
    remembered: RememberedPlan | undefined,
    nowSeconds: number,
): ProAccount | undefined {
    if ((info?.tier ?? '').toLowerCase() !== 'pro') return undefined;
    const account: ProAccount = { subscription: !!sid };
    const who = (info?.licensedTo ?? '').trim();
    // The dev switch reports a licensee that is a note to an engineer, not a person.
    if (who && !/SEMANTICUS_DEV_PRO/.test(who)) account.licensedTo = who;
    if (info?.expiry && info.expiry > 0) {
        account.expiry = info.expiry;
        // Pro with an expiry already behind it is the grace window, and the engine words its end date.
        const grace = /in grace until (\d{4}-\d{2}-\d{2})/i.exec(info.reason ?? '');
        if (info.expiry < nowSeconds && grace) account.graceUntil = Math.floor(Date.parse(`${grace[1]}T00:00:00Z`) / 1000);
    }
    if (sid && remembered && remembered.sid === sid) account.plan = remembered.plan;
    return account;
}
