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
// applyLicenseToken, the SAME path Activate License and Buy Pro use. Starting a trial needs an email, from the person
// typing it or from the assistant with the person's yes in chat (start_trial, F-293); the assistant also reads the state.
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

// ---- the pending claim, kept across a reload (F-290) -------------------------------------------------------
// The nonce is the only thing that can collect the licence, and the email link works for a day. Closing or
// reloading VS Code between the email and the press used to lose it. The claim is kept in secret storage (the
// nonce collects a licence, so it is not left in plain settings) and picked up again when the extension starts.

/// The email link works for a day (the worker's code lifetime), but the claim outlives it by design (F-290): a link
/// pressed at hour 23 arms the licence, and a window opened at hour 25 must still collect it. The claim is dropped
/// after a week, or sooner when the worker says it is gone (410), which is also how a ticket the worker no longer
/// knows stops being waited for.
export const PENDING_CLAIM_LIFETIME_MS = 7 * 24 * 60 * 60 * 1000;

export interface PendingTrialClaim { nonce: string; email: string; startedAt: number; }

/// One string slot, as vscode SecretStorage gives it. extension.ts supplies the real one; the test a map.
export interface PendingClaimSlot {
    get(): Promise<string | undefined>;
    set(value: string): Promise<void>;
    delete(): Promise<void>;
}

export async function savePendingClaim(slot: PendingClaimSlot, claim: PendingTrialClaim): Promise<void> {
    await slot.set(JSON.stringify(claim));
}

export async function clearPendingClaim(slot: PendingClaimSlot): Promise<void> {
    await slot.delete();
}

/// The saved claim, or undefined when there is none, it is unreadable, or it has run out (and then it is cleared).
export async function loadPendingClaim(slot: PendingClaimSlot, now: number = Date.now()): Promise<PendingTrialClaim | undefined> {
    const raw = await slot.get();
    if (!raw) return undefined;
    let claim: PendingTrialClaim | undefined;
    try {
        const j = JSON.parse(raw);
        if (typeof j?.nonce === 'string' && /^[A-Za-z0-9_-]{22,128}$/.test(j.nonce) && typeof j.email === 'string' && Number.isFinite(j.startedAt))
            claim = { nonce: j.nonce, email: j.email, startedAt: j.startedAt };
    } catch { /* unreadable: dropped below */ }
    if (!claim || now - claim.startedAt > PENDING_CLAIM_LIFETIME_MS || claim.startedAt > now + 60_000) {
        await slot.delete();
        return undefined;
    }
    return claim;
}

/// What a resumed wait ended as. `none` means there was nothing to resume.
export type ResumeOutcome = CollectOutcome | { kind: 'none' };

/// Pick the saved claim up again and wait for the press. The claim goes when the licence is collected or the
/// worker says it is gone (spent, or no longer known). A cancel, a timeout or a network failure keep it, because
/// the link in the email may still be pressed.
export async function resumeTrialClaim(slot: PendingClaimSlot, options: Omit<CollectOptions, 'path' | 'nonce'> & { now?: () => number }): Promise<ResumeOutcome & { claim?: PendingTrialClaim }> {
    const claim = await loadPendingClaim(slot, options.now?.());
    if (!claim) return { kind: 'none' };
    const outcome = await collectTrialToken({ ...options, nonce: claim.nonce });
    if (outcome.kind === 'token' || outcome.kind === 'gone') await clearPendingClaim(slot);
    return { ...outcome, claim };
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
