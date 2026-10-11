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
}

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
  if (!read) return { state: 'checking', word: 'Checking', line: 'Checking whether Jev can be reached.' };
  if (!budget) return { state: 'unreachable', word: 'Not reachable', line: 'Semanticus could not check whether Jev can be reached.' };
  if (budget.route === 'none' || budget.serviceAvailable === false) {
    return { state: 'unreachable', word: 'Not reachable', line: 'This computer has no way to reach Jev.' };
  }
  if (budget.route === 'relay' && (budget.monthlyUsedInputTokens == null || budget.monthlyCapInputTokens == null)) {
    // One calm sentence, the same words as the Plan and the Ask (Kane, 8 Oct, r1 join). The engine's reason stays in its log
    // and in what the assistant reads (get_decision_budget), not on this card.
    return { state: 'unreachable', word: 'Not reachable', line: 'Jev is not available right now.' };
  }
  if (!consent) return { state: 'on', word: 'On', line: 'Jev can be reached. It asks about a model only once you give permission for that model.' };
  if (consent.current !== true) {
    return { state: 'noConsent', word: 'No permission', line: 'Jev asks nothing about this model until you give permission.' };
  }
  return { state: 'on', word: 'On', line: 'Jev is on for this model.' };
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
