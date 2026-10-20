import { useEffect, useRef, useState } from 'react';
import { onProState, openExternal, postProAction, rpc } from './bridge';
import { useProPlan, useTier, upgradeToPro, peekPendingRun, takePendingRun, takeKeepAsked, clearPendingRun } from './pro';
import { navigateInStudio, onEntitlementChange, onReconnect } from './bridge';
import { DecisionSettingsSection } from './decisions-settings';
import { chipOf, countWords, firstWeekOf, jevStateOf, longDate, PLAN_PRICE, proPageView, trialEndingLine, trialSpan, trialViewOf, type TallyW, type TrialW, type BillingPlan, type JevBudgetW, type JevConsentW, type JevStateView, type ProAccount, type ProPageState } from './propage-model';

// The Semanticus Pro page (Kane approved the design on 23 September 2026). A Studio page like Assistant
// permissions: a detour with Done, reached from the header chip, Help and every Unlock Pro.
//
// It holds no licence logic. The host runs every flow it starts, through the SAME code the palette uses
// (extension.ts: upgradeToProFlow, activateLicenseFlow, manageSubscriptionCmd), and sends back where things
// stand as 'proState'. The tier itself is the engine's answer, read the way every other page reads it
// (pro.tsx useTier), so the page and the header chip can never disagree about whether Pro is on.

const LIVE_ADDS: [string, string][] = [
  ['Run the plan tidies your measures and puts your plan in order.', 'Each change lands on your Plan for you to keep or drop.'],
  ['The Plan field:', 'type a task and Semanticus names the page it is done on, and works it out for you when the words alone do not say.'],
];

const Icon = {
  star: <svg viewBox="0 0 12 12" aria-hidden="true"><path d="M6 .8l1.5 3.3 3.6.4-2.7 2.4.8 3.6L6 8.6 2.8 10.5l.8-3.6L.9 4.5l3.6-.4z" fill="currentColor" /></svg>,
  ext: <svg viewBox="0 0 12 12" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.4"><path d="M5 2H2v8h8V7" /><path d="M7 1.5h3.5V5M10.5 1.5L5.5 6.5" /></svg>,
  lock: <svg viewBox="0 0 14 14" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.4"><rect x="2.5" y="6" width="9" height="6.5" rx="1.5" /><path d="M4.5 6V4.5a2.5 2.5 0 015 0V6" /></svg>,
  check: <svg viewBox="0 0 12 12" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="2"><path d="M2.5 6.2l2.3 2.3 4.7-5" /></svg>,
  info: <svg viewBox="0 0 16 16" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.5"><circle cx="8" cy="8" r="6.5" /><path d="M8 7v4.2M8 4.6v.1" /></svg>,
  x: <svg viewBox="0 0 16 16" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.6"><circle cx="8" cy="8" r="6.5" /><path d="M5.7 5.7l4.6 4.6M10.3 5.7l-4.6 4.6" /></svg>,
  clock: <svg viewBox="0 0 16 16" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.5"><circle cx="8" cy="8" r="6.5" /><path d="M8 4.5V8l2.4 1.6" /></svg>,
  okc: <svg viewBox="0 0 16 16" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.6"><circle cx="8" cy="8" r="6.5" /><path d="M5 8.2l2 2 4-4.3" /></svg>,
};

// ---- the header chip ---------------------------------------------------------------------------------

/** Get Pro when free, a quiet Pro when Pro (both hairline header buttons, Instrument), nothing before the
 *  engine answers. Either way it opens the Pro page. */
export function ProChipView({ tier, here, flash, onOpen, planInactive = false, billingUrl, onBilling }: {
  tier: string; here: boolean; flash?: boolean; onOpen: () => void; planInactive?: boolean; billingUrl?: string; onBilling?: () => void;
}) {
  const kind = chipOf(tier);
  if (!kind) return null;
  // The licence is Pro but the Pro service refused it: the chip says so and carries the Billing link, so the screen never says Pro and refuses Pro at once.
  const inactive = kind === 'pro' && planInactive;
  const label = inactive ? 'Pro, plan not active' : kind === 'pro' ? 'Pro' : 'Get Pro';
  const cls = `pro-chip ${kind === 'pro' ? 'pro-chip-is' : 'pro-chip-get'}${inactive ? ' pro-chip-off' : ''}${here ? ' pro-chip-here' : ''}${flash ? ' pro-chip-flash' : ''}`;
  const chip = (
    <button type="button" data-testid="pro-chip" className={cls} onClick={onOpen} title={inactive ? 'Your Pro plan is not active' : kind === 'pro' ? 'Semanticus Pro' : 'Get Pro'}
      aria-label={label} aria-current={here ? 'page' : undefined}>
      {/* The star is always there for Pro, and for Get Pro only on a folded header, where the words go. */}
      <span className="pro-chip-star">{Icon.star}</span><span className="pro-chip-label">{label}</span>
    </button>
  );
  if (!inactive) return chip;
  return <span className="pro-chip-wrap">{chip}
    <button type="button" data-testid="pro-chip-billing" className="pro-chip-billing" onClick={onBilling ?? (() => billingUrl && openExternal(billingUrl))}>Billing</button></span>;
}

export function ProChip({ here }: { here: boolean }) {
  const tier = useTier();
  // The chip turns from Get Pro to Pro in the same moment the page says Pro is on, and says so with one pulse.
  const previous = useRef(tier);
  const [flash, setFlash] = useState(false);
  useEffect(() => {
    if (previous.current !== 'pro' && tier === 'pro' && previous.current !== 'unknown') setFlash(true);
    if (tier !== 'unknown') previous.current = tier;
  }, [tier]);
  useEffect(() => { if (!flash) return; const t = window.setTimeout(() => setFlash(false), 1400); return () => window.clearTimeout(t); }, [flash]);
  const { inactive, manageUrl } = useProPlan();
  return <ProChipView tier={tier} here={here} flash={flash} onOpen={upgradeToPro} planInactive={inactive} billingUrl={manageUrl} />;
}

// ---- the page ------------------------------------------------------------------------------------

export interface ProPageViewProps {
  tier: string;
  state: ProPageState;
  known?: 'free' | 'pro';
  plan: BillingPlan;
  keyOpen: boolean;
  keyValue: string;
  /** Jev's state on this computer, read from the engine. Absent until the reads come back (Checking). */
  jev?: JevStateView;
  onPlan: (plan: BillingPlan) => void;
  onBuy: () => void;
  onReopen: () => void;
  onCancel: () => void;
  onKeyOpen: () => void;
  onKey: (value: string) => void;
  onActivate: () => void;
  onManage: () => void;
  onRemove: () => void;
  keyRef?: React.Ref<HTMLInputElement>;
  /** The free month: the engine's trial fields and the tally counted on this computer. */
  trial?: TrialW | null;
  tally?: TallyW | null;
  trialEmail?: string;
  onTrialEmail?: (value: string) => void;
  onTrialStart?: () => void;
  onTrialCancel?: () => void;
  /** The end card was put away with Not now; the page shows the plain used note instead. */
  endPutAway?: boolean;
  onPutAway?: () => void;
  /** Keep Pro during a trial: the plans and Buy Pro show on the month page. */
  keepOpen?: boolean;
  onKeep?: () => void;
  /** What the person pressed on a locked button, which runs once Pro is on. */
  pendingLabel?: string;
  nowSeconds?: number;
}

// ---- the free month (trial option B) ---------------------------------------------------------------

function Tally({ tally }: { tally?: TallyW | null }) {
  const t = tally ?? {};
  return <div className="pp-tally" data-testid="trial-tally">
    <div><span className="pp-big">{countWords(t.changesApplied)}</span><span className="pp-lbl">changes applied in {countWords(t.steps)} {t.steps === 1 ? 'step' : 'steps'}</span></div>
    <div><span className="pp-big">{countWords(t.jevChoices)}</span><span className="pp-lbl">things chosen for you</span></div>
  </div>;
}

function TrialOffer(p: ProPageViewProps) {
  const sending = p.state.trial?.step === 'sending';
  const err = p.state.trial?.step === 'error' ? p.state.trial.message : undefined;
  return <div className="pp-sheet" data-testid="trial-offer">
    <h2>Try Pro free for 30 days</h2>
    <p>{p.pendingLabel ? <><b>{p.pendingLabel}</b> is part of Pro. It works once Pro is on. </> : null}Type your email and Pro turns on in this app. It switches itself off after 30 days.</p>
    <form className="pp-trialrow" onSubmit={(e) => { e.preventDefault(); if ((p.trialEmail ?? '').trim() && !sending) p.onTrialStart?.(); }}>
      <label htmlFor="trial-email" className="pp-keylabel">Your email</label>
      <div className="pp-keyrow">
        <input id="trial-email" data-testid="trial-email" className={`pp-field${err ? ' pp-bad' : ''}`} type="email" autoComplete="email" spellCheck={false}
          placeholder="you@contoso.com" value={p.trialEmail ?? ''} onChange={(e) => p.onTrialEmail?.(e.target.value)} />
        <button type="submit" data-testid="trial-start" className="sem-btn sem-btn-primary" disabled={!(p.trialEmail ?? '').trim() || sending}>Start free month</button>
      </div>
    </form>
    {err && <div className="pp-result pp-bad" role="status" data-testid="trial-error">{Icon.x}<span>{err}</span></div>}
    <p className="pp-fine">{Icon.info}<span>Email only. No card. We use it for this one free month and to send you a link to press. One free month for each email.</span></p>
  </div>;
}

function TrialWaiting(p: ProPageViewProps) {
  return <div className="pp-waitbox" role="status" data-testid="trial-waiting">
    <div className="pp-l1"><svg className="pp-spin" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="6.5" /></svg>Check your email and press the link. This page updates by itself.</div>
    <div className="pp-l2">{p.state.trial?.email ? <>We sent it to <b>{p.state.trial.email}</b>. </> : null}{p.pendingLabel ? <><b>{p.pendingLabel}</b> runs once Pro is on. </> : null}You can keep working while you wait.</div>
    <div className="pp-acts"><button type="button" className="sem-btn sem-btn-sm" data-testid="trial-cancel" onClick={p.onTrialCancel}>Cancel</button></div>
  </div>;
}

function TrialUsed(p: ProPageViewProps) {
  const span = trialSpan(p.tally?.startedAt, p.trial?.trialEndsAt);
  const changes = p.tally?.changesApplied ?? 0;
  return <div className="pp-note" data-testid="trial-used">
    <h2>Your free month is used</h2>
    <p>{span ? `It ran from ${span}. ` : 'This email already had its free month. '}{changes > 0 ? `In it Pro applied ${countWords(changes)} changes in ${countWords(p.tally?.steps)} steps. ` : ''}Pro is USD 10 a month or USD 100 a year. Pick a plan below.</p>
  </div>;
}

function WhatDifferent() {
  return <ul className="pp-adds">
    <li><b>Run the plan</b> goes back to one change at a time.</li>
    <li><b>Tidying and ordering the plan</b> and the <b>Plan field</b> switch off.</li>
    <li><b>Your models, Plan and every change you made</b> stay exactly as they are. Undo still works.</li>
  </ul>;
}

function TrialEnd(p: ProPageViewProps) {
  const span = trialSpan(p.tally?.startedAt, p.trial?.trialEndsAt);
  return <div className="pp-main" data-testid="trial-end">
    <h1>Your Pro month</h1>
    <p className="pp-status">{span ? `${span}. ` : ''}You are back on Semanticus Free. Nothing you made is lost.</p>
    <Tally tally={p.tally} />
    <div className="pp-note">
      <div className="pp-h3">What is different now</div>
      <WhatDifferent />
      <div className="pp-acts">
        <button type="button" className="sem-btn sem-btn-primary" data-testid="trial-keep" onClick={p.onPutAway}>Keep Pro</button>
        <button type="button" className="sem-btn" data-testid="trial-notnow" onClick={() => navigateInStudio('plan')}>Not now</button>
      </div>
    </div>
  </div>;
}

function FirstWeek({ tally, now }: { tally?: TallyW | null; now: number }) {
  const items = firstWeekOf(tally, now);
  if (!items) return null;
  return <div className="pp-week" data-testid="trial-week">
    <div className="pp-h3">Your first week with Pro</div>
    {items.map((i) => <div key={i.key} className="pp-weekrow" data-done={i.done ? 'yes' : 'no'}>
      <span className={`pp-tick${i.done ? ' pp-tick-on' : ''}`}>{i.done ? Icon.check : null}</span>
      <span className="pp-weektext"><b>{i.title}</b>{!i.done && i.hint ? <span className="pp-m">{i.hint}</span> : null}</span>
      {i.done ? <span className="pp-m">Done</span> : null}
    </div>)}
  </div>;
}

function MonthBody(p: ProPageViewProps & { justOn: boolean }) {
  const ends = p.trial?.trialEndsAt ? longDate(p.trial.trialEndsAt) : null;
  const who = (p.trial?.licensedTo ?? p.state.account?.licensedTo ?? '').trim();
  const ending = trialEndingLine(p.trial);
  const left = p.trial?.trialDaysLeft;
  const days = left != null && left > 0 ? ` ${left === 1 ? '1 day' : `${left} days`} left.` : '';
  return <div className="pp-main" data-testid="trial-month">
    {ending && <div className="trial-line" role="status" data-testid="trial-ending-line"><span className="trial-line-text">{ending}</span>
      {!p.keepOpen && <button type="button" className="sem-btn sem-btn-sm" data-testid="trial-ending-keep" onClick={p.onKeep}>Keep Pro</button>}</div>}
    <h1>Your Pro month</h1>
    {p.justOn
      ? <div className="pp-done" role="status" data-testid="pro-on"><span className="pp-ok">{Icon.check}</span>Pro is on for this computer.<span className="pp-m">{ends ? `Your free month runs until ${ends}.` : ''}</span></div>
      : <p className="pp-status">{who ? `Pro trial for ${who}. ` : 'Pro trial. '}{ends ? `Runs until ${ends}.` : ''}{days}</p>}
    <FirstWeek tally={p.tally} now={p.nowSeconds ?? Math.floor(Date.now() / 1000)} />
    <Tally tally={p.tally} />
    <p className="pp-fine">{Icon.info}<span>Counted on this computer from what you did. Nothing here is sent anywhere.</span></p>
    {p.keepOpen
      ? <><div className="pp-h3">Keep Pro</div><Purchase {...p} waiting={p.state.purchase === 'waiting'} notice={p.state.purchase === 'notOpen' || p.state.purchase === 'error' ? p.state.purchase : null} /></>
      : <div className="pp-acts"><button type="button" className="sem-btn sem-btn-primary" data-testid="trial-keep" onClick={p.onKeep}>Keep Pro</button><span className="pp-m">Optional, any day. Paid Pro starts the day you buy.</span></div>}
    <div className="pp-remove"><button type="button" className="pp-link pp-plain" data-testid="pro-remove" onClick={p.onRemove}>Remove license from this computer</button></div>
    <DecisionSettingsSection />
  </div>;
}

function Adds() {
  return <>
    <div className="pp-h3">What Pro adds</div>
    <ul className="pp-adds">
      {LIVE_ADDS.map(([lead, rest]) => <li key={lead}><b>{lead}</b> {rest}</li>)}
    </ul>
    <div className="pp-stillfree">Everything else in Semanticus stays free.</div>
  </>;
}

function KeyZone(p: ProPageViewProps & { open: boolean }) {
  if (!p.open) return <div className="pp-keyzone"><button type="button" className="pp-link" data-testid="pro-key-open" onClick={p.onKeyOpen}>Have a license key?</button></div>;
  const r = p.state.key?.result;
  const tone = r === 'ok' ? ' pp-good' : r === 'invalid' ? ' pp-bad' : r === 'expired' ? ' pp-warn' : '';
  const checking = r === 'checking';
  return (
    <div className="pp-keyzone">
      <label htmlFor="pro-key" className="pp-keylabel">Have a license key?</label>
      <form className="pp-keyrow" onSubmit={(e) => { e.preventDefault(); if (p.keyValue.trim() && !checking) p.onActivate(); }}>
        <input id="pro-key" ref={p.keyRef} data-testid="pro-key" className={`pp-field${tone}`} type="password" autoComplete="off" spellCheck={false}
          placeholder="Paste your license key" value={p.keyValue} onChange={(e) => p.onKey(e.target.value)} />
        <button type="submit" data-testid="pro-activate" className="sem-btn" disabled={!p.keyValue.trim() || checking}>Activate</button>
      </form>
      {r === 'ok' ? <div className="pp-result pp-good" role="status">{Icon.okc}<span>Pro is on for this computer.{p.state.key?.licensedTo ? <span className="pp-m"> Licensed to {p.state.key.licensedTo}.</span> : null}</span></div>
        : r === 'invalid' ? <div className="pp-result pp-bad" role="status">{Icon.x}<span>That key is not valid. Check it was copied in full.</span></div>
        : r === 'expired' ? <div className="pp-result pp-warn" role="status">{Icon.clock}<span>That license has expired. Renew it or buy Pro again.</span></div>
        : checking ? <div className="pp-keyhint" role="status">Checking the key…</div>
        : <div className="pp-keyhint">Paste the whole key. It turns Pro on for this computer.</div>}
    </div>
  );
}

function Purchase(p: ProPageViewProps & { waiting: boolean; notice: 'notOpen' | 'error' | null }) {
  const chosen = p.state.purchase === 'waiting' && p.state.plan ? p.state.plan : p.plan;
  const card = (id: BillingPlan) => {
    const x = PLAN_PRICE[id];
    return (
      <button type="button" className="pp-plancard" role="radio" aria-checked={chosen === id} data-plan={id} data-testid={`pro-plan-${id}`}
        disabled={p.waiting} onClick={() => p.onPlan(id)}>
        <svg className="pp-radio" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="7.5" /><circle className="pp-radio-dot" cx="8" cy="8" r="4" /></svg>
        <div className="pp-nm">{x.name}</div>
        <div className="pp-price"><b>{x.price}</b><span>{x.per}</span></div>
        <div className="pp-pn">{x.note}</div>
      </button>
    );
  };
  return <>
      <div className={`pp-plans${p.waiting ? ' pp-dim' : ''}`} role="radiogroup" aria-label="Plan">{card('monthly')}{card('yearly')}</div>
      {p.waiting ? (
        <div className="pp-waitbox" role="status" data-testid="pro-waiting">
          <div className="pp-l1"><svg className="pp-spin" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="6.5" /></svg>Finish paying in your browser. This page updates by itself.</div>
          <div className="pp-l2">{PLAN_PRICE[chosen].name}, {PLAN_PRICE[chosen].price} {PLAN_PRICE[chosen].per}. Stripe's page is open in your browser. You can keep working while you wait.</div>
          <div className="pp-acts">
            <button type="button" className="sem-btn sem-btn-sm" data-testid="pro-reopen" onClick={p.onReopen}>{Icon.ext}Open the payment page again</button>
            <button type="button" className="sem-btn sem-btn-sm" data-testid="pro-cancel" onClick={p.onCancel}>Cancel</button>
          </div>
        </div>
      ) : <>
        <div className="pp-buy"><button type="button" className="sem-btn sem-btn-primary pp-btn-lg" data-testid="pro-buy" onClick={p.onBuy}>Buy Pro</button></div>
        <p className="pp-fine">{Icon.lock}<span>Payment opens in your browser on Stripe's secure page. Stripe handles tax. Your license comes back here by itself.</span></p>
        {p.notice === 'notOpen' && <div className="pp-notice" role="status" data-testid="pro-not-open">{Icon.info}<span>Buying in the app is not open yet. If you have a license key, activate it below.</span></div>}
        {p.notice === 'error' && <div className="pp-notice" role="status" data-testid="pro-error">{Icon.info}<span>{p.state.message}</span></div>}
      </>}
  </>;
}

function FreeBody(p: ProPageViewProps & { waiting: boolean; notice: 'notOpen' | 'error' | null; keyOpen: boolean }) {
  const view = trialViewOf(p.tier === 'pro' ? 'pro' : 'free', p.trial, p.state.trial, !!p.endPutAway);
  if (view === 'end') return <TrialEnd {...p} />;
  return <>
    <div className="pp-main">
      <h1>Semanticus Pro</h1>
      <p className="pp-status">You are on Semanticus Free.</p>
      {view === 'offer' && !p.waiting && <TrialOffer {...p} />}
      {view === 'waiting' && <TrialWaiting {...p} />}
      {view === 'used' && <TrialUsed {...p} />}
      {view !== 'waiting' && <Purchase {...p} />}
      <Adds />
      <KeyZone {...p} open={p.keyOpen} />
    </div>
  </>;
}

function ProBody(p: ProPageViewProps & { justOn: boolean }) {
  const a: ProAccount = p.state.account ?? { subscription: false };
  // Jev's real state, read from the engine by ProPage; until it answers, the page says it is checking.
  const jev = p.jev ?? jevStateOf(null, null, false);
  const planWord = a.plan ? (a.plan === 'yearly' ? 'yearly' : 'monthly') : null;
  const ends = a.expiry ? longDate(a.expiry) : null;
  const endsLabel = a.subscription ? 'Renews' : 'Runs until';
  const l2: string[] = [];
  if (a.licensedTo) l2.push(`Licensed to ${a.licensedTo}.`);
  if (ends && !a.graceUntil) l2.push(`${endsLabel} ${ends}.`);
  return <>
    <div className="pp-main">
      <h1>Semanticus Pro</h1>
      {/* The licence card below says Pro is on, so only the moment it turned on gets a line of its own. */}
      {p.justOn && <div className="pp-done" role="status" data-testid="pro-on"><span className="pp-ok">{Icon.check}</span>Pro is on for this computer.<span className="pp-m" data-testid="pro-jev-line">{jev.line}</span></div>}
      <div className="pp-acct">
        <div className="pp-l1">{planWord ? `Semanticus Pro, ${planWord}.` : 'Semanticus Pro.'}</div>
        {l2.length > 0 && <div className="pp-l2">{l2.join(' ')}</div>}
        {a.graceUntil && ends && <div className="pp-l2">Your license ended on {ends}. Pro stays on until {longDate(a.graceUntil)}. Renew it to keep Pro.</div>}
        {/* The old side card said "Jev: On" or "Not reachable". Only a problem is said now; On is the quiet default. The just-turned-on line above already says it, so it is said once (hero20 D13). */}
        {!p.justOn && (jev.state === 'unreachable' || jev.state === 'noConsent') && <div className="pp-l2" data-testid="pro-jev" data-jev={jev.state}>{jev.line}</div>}
        {a.subscription && <div className="pp-acts">
          <button type="button" className="sem-btn" data-testid="pro-manage" onClick={p.onManage}>{Icon.ext}Manage subscription</button>
          <span className="pp-m">Change the plan or card, or cancel, on Stripe.</span>
        </div>}
      </div>
      <div className="pp-remove"><button type="button" className="pp-link pp-plain" data-testid="pro-remove" onClick={p.onRemove}>Remove license from this computer</button></div>
      <DecisionSettingsSection />
    </div>
  </>;
}

/** The page as a pure function of what it is handed, so every state can be rendered without a host. */
export function ProPageView(p: ProPageViewProps) {
  const v = proPageView(p.tier, p.state, p.known);
  if (v.body === 'checking') {
    return <div className="pp" data-testid="pro-page"><div className="pp-main"><h1>Semanticus Pro</h1><p className="pp-status">Checking your Pro access…</p></div></div>;
  }
  return (
    <div className="pp" data-testid="pro-page" data-state={v.body === 'pro' ? (v.justOn ? 'on' : 'pro') : v.waiting ? 'waiting' : p.state.purchase}>
      {v.body === 'pro'
        ? (p.trial?.isTrial ? <MonthBody {...p} justOn={v.justOn || p.state.trial?.step === 'on'} /> : <ProBody {...p} justOn={v.justOn} />)
        : <FreeBody {...p} waiting={v.waiting} notice={v.notice} keyOpen={p.keyOpen || v.forceKeyOpen} />}
    </div>
  );
}

/** Jev's real state for the Pro page: the engine's route and whether the relay answered (get_decision_budget),
 *  and whether this model has permission (get_consent). Read on Pro only, since a free session never asks Jev.
 *  A consent read that throws means no model is open, which jevStateOf reads as "permission is per model". */
function useJevState(tier: string): JevStateView | undefined {
  const [jev, setJev] = useState<JevStateView | undefined>(undefined);
  useEffect(() => {
    if (tier !== 'pro') { setJev(undefined); return; }
    let live = true;
    void Promise.all([
      rpc<JevBudgetW>('getDecisionBudget').catch(() => null),
      rpc<JevConsentW>('getConsent').catch(() => null),
    ]).then(([budget, consent]) => { if (live) setJev(jevStateOf(budget, consent, true)); });
    return () => { live = false; };
  }, [tier]);
  return jev;
}

/** The engine's trial fields and the tally, re-read whenever the tier moves or the licence changes. */
function useTrialState(tier: string): { trial: TrialW | null; tally: TallyW | null } {
  const [value, setValue] = useState<{ trial: TrialW | null; tally: TallyW | null }>({ trial: null, tally: null });
  const [bump, setBump] = useState(0);
  useEffect(() => {
    const a = onEntitlementChange(() => setBump((n) => n + 1));
    const b = onReconnect(() => setBump((n) => n + 1));
    return () => { a(); b(); };
  }, []);
  useEffect(() => {
    if (tier !== 'pro' && tier !== 'free') return;
    let live = true;
    void Promise.all([
      rpc<TrialW>('getEntitlement').catch(() => null),
      rpc<TallyW>('getTrialTally').catch(() => null),
    ]).then(([trial, tally]) => { if (live) setValue({ trial, tally }); });
    return () => { live = false; };
  }, [tier, bump]);
  return value;
}

/** The page with its hooks: the engine's tier, the host's state, and the few things only the page holds (the
 *  plan picked but not yet bought, whether the key field is open, and the key while it is being typed). */
export function ProPage() {
  const tier = useTier();
  const [state, setState] = useState<ProPageState>({ purchase: 'idle' });
  const [plan, setPlan] = useState<BillingPlan>('yearly');
  const [keyOpen, setKeyOpen] = useState(false);
  const [keyValue, setKeyValue] = useState('');
  const known = useRef<'free' | 'pro' | undefined>(undefined);
  if (tier === 'free' || tier === 'pro') known.current = tier;
  const keyRef = useRef<HTMLInputElement>(null);
  const focusKey = useRef(false);
  const jev = useJevState(tier);
  const { trial, tally } = useTrialState(tier);
  const [trialEmail, setTrialEmail] = useState('');
  const [endPutAway, setEndPutAway] = useState(false);
  const [keepOpen, setKeepOpen] = useState(() => takeKeepAsked());
  const pendingLabel = peekPendingRun()?.label;
  // A pending run belongs to this visit. Leaving with nothing in flight drops it, so a buy days later never
  // replays a press the person has forgotten.
  const inFlight = useRef(false);
  inFlight.current = state.purchase === 'waiting' || state.trial?.step === 'waiting' || state.trial?.step === 'sending';
  useEffect(() => () => { if (!inFlight.current) clearPendingRun(); }, []);
  // The thing pressed on a locked button runs once, as soon as the engine says Pro, and Studio goes back to it.
  useEffect(() => {
    if (tier !== 'pro') return;
    const pending = takePendingRun();
    if (!pending) return;
    if (pending.tab) navigateInStudio(pending.tab);
    window.setTimeout(() => { try { pending.run(); } catch { /* the page it came from reports its own failure */ } }, 0);
  }, [tier]);

  useEffect(() => {
    const off = onProState((next) => {
      setState(next);
      // Not open yet sends the person to the key: open it and put the cursor in it.
      if (next.purchase === 'notOpen') { setKeyOpen(true); focusKey.current = true; }
      // A key the engine accepted has done its job; it does not sit in the field.
      if (next.key?.result === 'ok') setKeyValue('');
    });
    postProAction('hello');
    return off;
  }, []);
  useEffect(() => {
    if (!focusKey.current || !keyOpen) return;
    focusKey.current = false;
    keyRef.current?.focus();
  });

  return <ProPageView tier={tier} state={state} known={known.current} plan={plan} keyOpen={keyOpen} keyValue={keyValue} keyRef={keyRef} jev={jev}
    trial={trial} tally={tally} trialEmail={trialEmail} onTrialEmail={(value) => { setTrialEmail(value); if (state.trial?.step === 'error') setState({ ...state, trial: undefined }); }}
    onTrialStart={() => postProAction('trialStart', { email: trialEmail.trim() })}
    onTrialCancel={() => postProAction('trialCancel')}
    endPutAway={endPutAway} onPutAway={() => setEndPutAway(true)}
    keepOpen={keepOpen} onKeep={() => setKeepOpen(true)} pendingLabel={pendingLabel}
    onPlan={setPlan}
    onBuy={() => postProAction('buy', { plan })}
    onReopen={() => postProAction('reopen')}
    onCancel={() => postProAction('cancel')}
    onKeyOpen={() => { setKeyOpen(true); focusKey.current = true; }}
    onKey={(value) => { setKeyValue(value); if (state.key && state.key.result !== 'checking') setState({ ...state, key: undefined }); }}
    onActivate={() => postProAction('activate', { token: keyValue.trim() })}
    onManage={() => postProAction('manage')}
    onRemove={() => postProAction('remove')} />;
}
