import { useEffect, useRef, useState } from 'react';
import { onProState, openExternal, postProAction, rpc } from './bridge';
import { useProPlan, useTier, upgradeToPro } from './pro';
import { DecisionSettingsSection } from './decisions-settings';
import { chipOf, jevStateOf, longDate, PLAN_PRICE, proPageView, type BillingPlan, type JevBudgetW, type JevConsentW, type JevStateView, type ProAccount, type ProPageState } from './propage-model';

// The Semanticus Pro page (Kane approved the design on 23 September 2026). A Studio page like Assistant
// permissions: a detour with Done, reached from the header chip, Help and every Unlock Pro.
//
// It holds no licence logic. The host runs every flow it starts, through the SAME code the palette uses
// (extension.ts: upgradeToProFlow, activateLicenseFlow, manageSubscriptionCmd), and sends back where things
// stand as 'proState'. The tier itself is the engine's answer, read the way every other page reads it
// (pro.tsx useTier), so the page and the header chip can never disagree about whether Pro is on.

const LIVE_ADDS: [string, string][] = [
  ['Jev tidies your measures and ranks your plan.', 'Each change lands on your Plan for you to keep or drop.'],
  ['The Plan field:', 'type a task and Semanticus names the page it is done on, asking Jev when the words alone do not say.'],
  ['Workflows:', 'work you repeat becomes steps the app checks as you go.'],
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

function FreeBody(p: ProPageViewProps & { waiting: boolean; notice: 'notOpen' | 'error' | null; keyOpen: boolean }) {
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
    <div className="pp-main">
      <h1>Semanticus Pro</h1>
      <p className="pp-status">You are on Semanticus Free.</p>
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
        {/* The old side card said "Jev: On" or "Not reachable". Only a problem is said now; On is the quiet default. */}
        {(jev.state === 'unreachable' || jev.state === 'noConsent') && <div className="pp-l2" data-testid="pro-jev" data-jev={jev.state}>{jev.line}</div>}
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
        ? <ProBody {...p} justOn={v.justOn} />
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
