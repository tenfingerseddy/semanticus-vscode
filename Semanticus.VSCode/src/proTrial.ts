// ===================================================================================================
// The free Pro month, app half (Kane, 11 Oct 2026: "add a try pro for free option that enables pro for a
// month and then switches off"; option B of the trial mockup).
//
// NOTHING IN THIS FILE IMPORTS vscode, on purpose, the same as proBilling.ts: test/pro-trial.test.mjs drives
// it against a stub worker on loopback. extension.ts owns the page messages and the licence storage.
//
// The worker contract (tools/fulfillment/worker, contract section A):
//   POST /trial/start  { email, nonce }  -> 200 {state:"sent"} | 200 {state:"used"} | 400 | 409 | 429
//   GET  /trial/claim?nonce=...          -> 425 pending, 404 unknown, 410 collected, 200 {token} once
// The person confirms by pressing the link in the email. The token that comes back goes through
// applyLicenseToken, the SAME path Activate License and Buy Pro use. Starting a trial is a person action:
// it needs an email the person types, so no assistant door reaches it. The assistant reads the state only.
// ===================================================================================================

import { randomBytes } from 'crypto';
import { BillingError, billingEndpoint, collectProToken, nodeBillingFetch } from './proBilling';
import type { BillingFetch, CollectOptions, CollectOutcome } from './proBilling';

export const TRIAL_DAYS = 30;
/// The plain "ends in N days" line shows from this many days out, and not before.
export const TRIAL_ENDING_DAYS = 3;
/// How many changes a person applies by hand before the one-time offer may appear on the status bar.
export const TRIAL_OFFER_AFTER_HAND_CHANGES = 10;

/// 32 random bytes, base64url: 43 url-safe characters, inside the worker's 22..128 rule.
export function newTrialNonce(): string {
    return randomBytes(32).toString('base64url');
}

/// The email as it is sent, or undefined when it cannot be an address. The worker is the real check; this
/// only keeps an obvious typo on the page instead of a round trip.
export function trialEmailOf(value: unknown): string | undefined {
    const email = typeof value === 'string' ? value.trim() : '';
    return email.length <= 254 && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) ? email : undefined;
}

export type TrialStartAnswer = 'sent' | 'used';

/// Ask the worker to send the confirm email. "used" is an answer, not an error: this email had its month.
export async function startTrial(base: string, email: string, nonce: string, fetchJson: BillingFetch = nodeBillingFetch): Promise<TrialStartAnswer> {
    const url = billingEndpoint(base, '/trial/start');
    const answer = await fetchJson(url, { method: 'POST', body: JSON.stringify({ email, nonce }) });
    if (answer.status === 200) {
        const state = answer.json?.state;
        if (state === 'sent' || state === 'used') return state;
        throw new BillingError('The Semanticus billing service gave an answer this app does not know.', 200);
    }
    if (answer.status === 400) throw new BillingError('That email address does not look right. Check it and try again.', 400);
    if (answer.status === 429) throw new BillingError('An email went to that address a moment ago. Wait a minute, then try again.', 429);
    throw new BillingError(`The free month could not start just now (${answer.status}). Try again in a minute.`, answer.status);
}

/// Wait for the person to press the link in the email, then collect the trial licence once.
export function collectTrialToken(options: Omit<CollectOptions, 'path'>): Promise<CollectOutcome> {
    return collectProToken({ ...options, path: '/trial/claim' });
}

/// What the engine says about a trial (get_entitlement, contract section C).
export interface TrialInfo {
    tier?: string;
    isTrial?: boolean;
    trialEndsAt?: number;
    trialDaysLeft?: number;
    trialEnded?: boolean;
}

/// none: not a trial. on: a trial, more than three days left. ending: three days or fewer. ended: back on Free.
export type TrialPhase = 'none' | 'on' | 'ending' | 'ended';

export function trialPhaseOf(info: TrialInfo | undefined): TrialPhase {
    if (!info) return 'none';
    if (info.trialEnded && (info.tier ?? '').toLowerCase() !== 'pro') return 'ended';
    if (!info.isTrial || (info.tier ?? '').toLowerCase() !== 'pro') return 'none';
    const left = info.trialDaysLeft ?? 0;
    return left > 0 && left <= TRIAL_ENDING_DAYS ? 'ending' : 'on';
}

/// The plain line for the last three days: "Pro trial ends in 3 days", "tomorrow", "today".
export function trialEndsWords(daysLeft: number): string {
    if (daysLeft <= 0) return 'Pro trial ends today.';
    if (daysLeft === 1) return 'Pro trial ends tomorrow.';
    return `Pro trial ends in ${daysLeft} days.`;
}

/// May the one-time status bar offer show? Free, never had a trial, never shown before, and a real count.
export function shouldOfferTrial(s: { tier?: string; trialEnded?: boolean; isTrial?: boolean; trialUsed?: boolean; offered?: boolean; handChanges: number }): boolean {
    if ((s.tier ?? '').toLowerCase() !== 'free') return false;
    if (s.trialEnded || s.isTrial || s.trialUsed || s.offered) return false;
    return s.handChanges >= TRIAL_OFFER_AFTER_HAND_CHANGES;
}

/// The offer's words, with the person's own count.
export function trialOfferWords(handChanges: number): { text: string; tooltip: string } {
    return {
        text: `You applied ${handChanges} changes by hand. Try Pro free for 30 days`,
        tooltip: `You applied ${handChanges} changes by hand today. Pro can apply them together, with one undo. Email only, no card. It switches itself off after 30 days.`,
    };
}

/// How many changes a model notification carries that a person made by hand (not the assistant, not undo).
export function handChangesIn(n: { origin?: string; by?: string | null; replay?: string; deltas?: unknown[] } | undefined): number {
    if (!n || n.replay) return 0;
    const by = n.by ?? n.origin;
    if (by !== 'human') return 0;
    return Math.max(1, Array.isArray(n.deltas) ? n.deltas.length : 0);
}
