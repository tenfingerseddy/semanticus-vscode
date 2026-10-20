// The Semanticus Pro page's decisions, as plain functions, so test/pro-in-app.test.mjs runs them.
//
// The shapes mirror src/proPage.ts, which is where the host decides them. They are repeated rather than
// imported because the webview bundle never reaches into the extension's source tree.

export type BillingPlan = 'monthly' | 'yearly';
export type PurchaseState = 'idle' | 'waiting' | 'done' | 'notOpen' | 'error';
export type KeyResult = 'checking' | 'ok' | 'invalid' | 'expired';
export interface ProAccount { licensedTo?: string; expiry?: number; subscription: boolean; plan?: BillingPlan; graceUntil?: number }
export interface ProPageState {
  purchase: PurchaseState;
  plan?: BillingPlan;
  message?: string;
  key?: { result: KeyResult; licensedTo?: string };
  account?: ProAccount;
  trial?: TrialStartState;
}
export interface TrialStartState { step: 'sending' | 'waiting' | 'used' | 'error' | 'on'; email?: string; message?: string }

/** The header chip. Nothing at all until the entitlement has answered: a paying customer must not see
 *  Get Pro for a frame, and a free one must not see Pro. */
export function chipOf(tier: string): 'get' | 'pro' | null {
  if (tier === 'pro') return 'pro';
  if (tier === 'unknown' || !tier) return null;
  return 'get';
}

export interface ProPageView {
  body: 'free' | 'pro' | 'checking';
  /** Pro turned on just now, by a purchase or a key. Coming back later is not that moment. */
  justOn: boolean;
  waiting: boolean;
  notice: 'notOpen' | 'error' | null;
  /** Not open yet sends the person to the key, so the field opens by itself. */
  forceKeyOpen: boolean;
}

/** `known` is the last tier the page saw answered. An activation or a purchase restarts the engine, and
 *  for that moment the tier is unknown again; the page keeps what it was showing instead of flashing
 *  "checking" in the middle of the thing the person is watching. */
export function proPageView(tier: string, state: ProPageState, known?: 'free' | 'pro'): ProPageView {
  // The host reports a purchase or a key as done only after the ENGINE said Pro, so while the tier is being
  // re-read that is the better guess than the free page the person just left. An answered tier always wins.
  const verified = state.purchase === 'done' || state.key?.result === 'ok';
  const effective = tier === 'pro' || tier === 'free' ? tier
    : (tier === 'unknown' || !tier ? (verified ? 'pro' : known) : 'free');
  const body = effective ?? 'checking';
  const waiting = body === 'free' && state.purchase === 'waiting';
  return {
    body,
    justOn: body === 'pro' && (state.purchase === 'done' || state.key?.result === 'ok'),
    waiting,
    notice: body === 'free' && (state.purchase === 'notOpen' || state.purchase === 'error') ? state.purchase : null,
    forceKeyOpen: body === 'free' && state.purchase === 'notOpen',
  };
}

// ---- Jev's real state on this computer (honesty lane) -------------------------------------------------

/** get_decision_budget, camelCased: only the fields the Pro page reads. Semanticus.Engine/Decisions/DecisionProtocol.cs. */
export interface JevBudgetW {
  /** "relay" on a Pro licence, "vendor" on a development key, "none" when there is no service. */
  route?: string;
  serviceAvailable?: boolean;
  monthlyUsedInputTokens?: number | null;
  monthlyCapInputTokens?: number | null;
  /** Why the month could not be read, in the engine's own plain words. On the relay route that means the
   *  relay did not answer, so it is the one live sign of whether Jev can be reached. */
  monthlyReason?: string | null;
}
/** get_consent, camelCased: only the field that decides whether Jev may ask about the open model. */
export interface JevConsentW { current?: boolean }

export type JevState = 'checking' | 'on' | 'unreachable' | 'noConsent';
export interface JevStateView { state: JevState; word: string; line: string }

/** What the Pro page says about Jev. It used to say "Jev: On" and "Jev is ready" on every Pro page whether or
 *  not Jev could be reached or had permission. Now it is read from the engine: `read` false means the reads
 *  have not come back yet; a null budget after that means the read failed, which is not a Jev that is on. A
 *  null consent means no model is open to hold a permission, so reachable is On and the line says permission
 *  is given per model. */
export function jevStateOf(budget: JevBudgetW | null | undefined, consent: JevConsentW | null | undefined, read: boolean): JevStateView {
  if (!read) return { state: 'checking', word: 'Checking', line: 'Checking whether Pro planning can be reached.' };
  if (!budget) return { state: 'unreachable', word: 'Not reachable', line: 'Semanticus could not check whether Pro planning can be reached.' };
  if (budget.route === 'none' || budget.serviceAvailable === false) {
    return { state: 'unreachable', word: 'Not reachable', line: 'This computer has no way to reach Pro planning.' };
  }
  if (budget.route === 'relay' && (budget.monthlyUsedInputTokens == null || budget.monthlyCapInputTokens == null)) {
    // One calm sentence, the same words as the Plan and the Ask (Kane, 8 Oct, r1 join). The engine's reason stays in its log
    // and in what the assistant reads (get_decision_budget), not on this card.
    return { state: 'unreachable', word: 'Not reachable', line: 'Pro planning is not available right now.' };
  }
  if (!consent) return { state: 'on', word: 'On', line: 'Pro planning can be reached. It asks about a model only once you give permission for that model.' };
  if (consent.current !== true) {
    return { state: 'noConsent', word: 'No permission', line: 'Pro planning asks nothing about this model until you give permission.' };
  }
  return { state: 'on', word: 'On', line: 'Pro planning is on for this model.' };
}

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
/** "23 September 2027", read in UTC so the machine's zone cannot move the day a licence ends. */
export function longDate(unixSeconds: number): string {
  const d = new Date(unixSeconds * 1000);
  return `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}`;
}

export const PLAN_PRICE: Record<BillingPlan, { name: string; price: string; per: string; note: string; short: string }> = {
  monthly: { name: 'Monthly', price: 'USD 10', per: 'a month', note: 'Stop at the end of any month.', short: 'Monthly, USD 10' },
  yearly: { name: 'Yearly', price: 'USD 100', per: 'a year', note: 'The same as 10 months at the monthly price.', short: 'Yearly, USD 100' },
};

// ---- the free Pro month (option B, 11 Oct 2026) --------------------------------------------------------

/** get_entitlement's trial fields (contract section C), camelCased. */
export interface TrialW { tier?: string; licensedTo?: string; isTrial?: boolean; trialEndsAt?: number; trialDaysLeft?: number; trialEnded?: boolean }
/** get_trial_tally: counted on this computer from the trial token's start. Zeros or null when not a trial. */
export interface TallyW { startedAt?: number; changesApplied?: number; steps?: number; jevChoices?: number; workflowsRun?: number }

export const TRIAL_ENDING_DAYS = 3;

/** Which trial view the Pro page draws.
 *    offer    free, never had the month: the sheet with the email field
 *    waiting  the email went out; the page waits for the link
 *    used     this email had its month (or the end card was put away): plain words and Buy Pro
 *    month    a trial is on: "Your Pro month" with the tally
 *    end      the trial ended: the same page, finished, with what is different now
 *    none     paid Pro, or nothing known yet */
export type TrialView = 'offer' | 'waiting' | 'used' | 'month' | 'end' | 'none';

export function trialViewOf(tier: string, info: TrialW | null | undefined, start: TrialStartState | undefined, endPutAway: boolean): TrialView {
  if (tier === 'pro') return info?.isTrial ? 'month' : 'none';
  if (tier !== 'free') return 'none';
  if (start?.step === 'waiting' || start?.step === 'sending') return 'waiting';
  if (start?.step === 'used') return 'used';
  if (info?.trialEnded) return endPutAway ? 'used' : 'end';
  return 'offer';
}

/** The plain line for the last three days, or null before them. */
export function trialEndingLine(info: TrialW | null | undefined): string | null {
  if (!info?.isTrial || info.tier !== 'pro') return null;
  const left = info.trialDaysLeft ?? 0;
  if (left > TRIAL_ENDING_DAYS) return null;
  if (left <= 0) return 'Pro trial ends today.';
  return left === 1 ? 'Pro trial ends tomorrow.' : `Pro trial ends in ${left} days.`;
}

/** 1240 reads as "1,240". */
export function countWords(n: number | undefined): string {
  return Math.max(0, Math.floor(n ?? 0)).toLocaleString('en-US');
}

/** "11 October to 10 November 2026", or with both years when they differ. */
export function trialSpan(startedAt: number | undefined, endsAt: number | undefined): string | null {
  if (!startedAt || !endsAt) return null;
  const a = longDate(startedAt), b = longDate(endsAt);
  const ay = a.slice(-4), by = b.slice(-4);
  return `${ay === by ? a.slice(0, -5) : a} to ${b}`;
}

export interface FirstWeekItem { key: 'apply' | 'jev'; title: string; hint?: string; done: boolean }

/** The first-week list: two things Pro does, each ticked from the tally. It shows for the first seven days
 *  of the month and leaves once both are done. Null when it should not show. */
export function firstWeekOf(tally: TallyW | null | undefined, nowSeconds: number): FirstWeekItem[] | null {
  if (!tally?.startedAt) return null;
  if (nowSeconds - tally.startedAt > 7 * 86400) return null;
  const items: FirstWeekItem[] = [
    { key: 'apply', title: 'Apply many changes at once', hint: 'Run the plan on the Plan page. One undo takes it all back.', done: (tally.changesApplied ?? 0) > (tally.steps ?? 0) && (tally.steps ?? 0) > 0 },
    { key: 'jev', title: 'Run the plan to tidy your measures', hint: 'Semanticus picks what to fix. You keep or drop each change.', done: (tally.jevChoices ?? 0) > 0 },
  ];
  return items.every((i) => i.done) ? null : items;
}
