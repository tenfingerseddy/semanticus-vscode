import { useEffect, useState } from 'react';
import { onEntitlementChange, onReconnect, rpc, navigateInStudio } from './bridge';
import { FEATURE_OF_TOOL, PRO_FEATURES, featureOfTool, isRetiredFeature, type ProFeature, type RetiredProFeature } from './features';

/** Every Unlock Pro in Studio, the header chip and Help all land here: the Semanticus Pro page (Kane approved
 *  the in-app Pro design on 23 September 2026). It used to start the host's quick pick and then the browser,
 *  and the upsell notice went to the website. The page is where the plan is chosen now, and the host still
 *  runs the whole checkout from it, which is what keeps a webview out of a payment path. */
export function upgradeToPro(): void {
  navigateInStudio('pro');
}

// ===================================================================================================
// The shared Free/Pro presentation kit. Two jobs now.
//
// 1. The old one (hook-fix batch, 2026-07-07): a bulk button stays VISIBLE and CLICKABLE on the free
//    tier, wears a small "Pro" pill, and a free click teaches with plain English instead of surfacing a
//    raw engine exception. Most of those buttons are FREE from 2026-09-15, so the kit is used far less.
// 2. The new one: Pro is four whole FEATURES, and the page renders a preview instead of the real tool
//    when a feature is not granted. useFeature is that answer. The engine gate stays the source of
//    truth; a wrong answer here can only mis-draw a page, never open a door the engine shut.
// ===================================================================================================

/** What the page knows about one feature. `unknown` is a real state, not a loading detail: before the
 *  first get_entitlement lands, neither the real page nor the locked preview is the honest thing to draw,
 *  and a paying customer must not be shown an upsell for one frame. */
export type FeatureGrant = 'unknown' | 'granted' | 'denied';

/** What a surface says while the plan has not answered. One sentence, spelled once, because three surfaces
 *  show it (the whole-page boundary, the Overview Checks card and the Advanced body) and a driver searches
 *  for it. It is deliberately not "Loading": what is unknown is the plan, not the page. */
export const PENDING_ACCESS = 'Checking your Pro access…';

interface Entitlement { tier: string; features: ProFeature[]; planInactive?: boolean; manageUrl?: string }

// A tier that is entitled. Grace and dev-Pro both report "pro", so this stays one comparison.
const isPaid = (tier?: string) => tier === 'pro';

// One entitlement fetch shared by every mounted tab. A license activation restarts the engine without remounting
// Studio, so the single reconnect listener invalidates the cache and republishes the new tier to every hook.
let tierPromise: Promise<Entitlement> | null = null;
let entitlement: Entitlement = { tier: 'unknown', features: [] };
let reconnectBound = false;
const tierListeners = new Set<(e: Entitlement) => void>();
function publish(next: Entitlement): Entitlement {
  entitlement = next;
  tierListeners.forEach((listener) => { try { listener(next); } catch { /* isolate a view */ } });
  return next;
}
// The engine adds `features` to get_entitlement in the same slice. An engine that has not shipped it yet
// answers with a tier alone, and a token minted before the claim existed (mint.mjs) carries no features
// either. Both must keep granting all four to a paid tier: an unused optional claim may not silently
// become a new, emptier product tier.
function readEntitlement(value: { tier?: string; features?: string[]; planInactive?: boolean; manageUrl?: string } | null | undefined): Entitlement {
  const tier = value?.tier ?? 'free';
  const listed = Array.isArray(value?.features) ? (value!.features as ProFeature[]) : (isPaid(tier) ? PRO_FEATURES : []);
  // The licence is Pro but the Pro service refused it: the header says so (planInactive) and links to billing (manageUrl).
  return { tier, features: listed, planInactive: isPaid(tier) && value?.planInactive === true, manageUrl: value?.manageUrl };
}
// Which fetch is the current one. A reconnect starts a new one, and an answer from the fetch it replaced
// must be DISCARDED rather than published: the old engine's answer arriving late would otherwise overwrite
// the new engine's, and the page would act on a licence that is no longer there.
let generation = 0;
function fetchTier(): Promise<Entitlement> {
  const mine = generation;
  tierPromise ??= rpc<{ tier?: string; features?: string[]; planInactive?: boolean; manageUrl?: string }>('getEntitlement')
    .then((value) => (mine === generation ? publish(readEntitlement(value)) : entitlement))
    .catch(() => {
      if (mine !== generation) return entitlement;
      tierPromise = null;
      // A FAILED read is not a grant. 'unknown' keeps every paid surface shut, which is the safe direction:
      // the engine is the authority and it has not answered.
      return publish({ tier: 'unknown', features: [] });
    });
  return tierPromise;
}
function bindTierReconnect(): void {
  if (reconnectBound) return;
  reconnectBound = true;
  // INVALIDATE FIRST, then re-ask. Dropping the promise alone left the old grant published, so between a
  // licence lapsing and the new answer landing every Pro page stayed mounted and kept calling paid
  // operations (Astra, 2026-09-15: Workflows called listWorkflows, getWorkflowEnforcement,
  // getWorkflowPolicy and listWorkflowProfiles in that window). The window is real; the grant must not
  // survive it. Bumping the generation also makes the in-flight answer from the OLD engine unpublishable.
  onReconnect(() => {
    generation++;
    tierPromise = null;
    publish({ tier: 'unknown', features: [] });
    void fetchTier();
  });
  // A licence swapped into the running engine (F-242) moves the tier with no reconnect. Same three steps: drop
  // the old grant first, then show what the engine now says.
  onEntitlementChange(() => {
    generation++;
    tierPromise = null;
    publish({ tier: 'unknown', features: [] });
    void fetchTier();
  });
}
function useEntitlement(): Entitlement {
  const [value, setValue] = useState(entitlement);
  useEffect(() => {
    let live = true;
    bindTierReconnect();
    tierListeners.add(setValue);
    void fetchTier().then((e) => { if (live) setValue(e); });
    return () => { live = false; tierListeners.delete(setValue); };
  }, []);
  return value;
}
export function useTier(): string {
  return useEntitlement().tier;
}

/** The Pro plan as the header shows it: refused by the Pro service (cancelled, refunded, unpaid) while the licence itself is valid. */
export function useProPlan(): { inactive: boolean; manageUrl?: string } {
  const e = useEntitlement();
  return { inactive: e.planInactive === true, manageUrl: e.manageUrl };
}

/** Read the plan again without blanking the tier first. The Plan calls this when its order line says the plan is not active (or is
 *  active again), so the header chip changes in the same moment instead of waiting for a reload. */
export function refreshEntitlement(): void {
  const mine = generation;
  void rpc<{ tier?: string; features?: string[]; planInactive?: boolean; manageUrl?: string }>('getEntitlement')
    .then((value) => { if (mine === generation) publish(readEntitlement(value)); })
    .catch(() => { /* the next read says it */ });
}

/** Is this feature granted? `null` means the caller is asking about a free tool, which is always granted,
 *  so a component can call this unconditionally and keep its hook order stable. */
export function useFeature(feature: ProFeature | RetiredProFeature | null): FeatureGrant {
  const value = useEntitlement();
  if (!feature) return 'granted';
  // A feature that was retired on 22 September is free, and free is granted BEFORE the entitlement answers
  // as well as after. Letting it fall through to the tier read would have held a free page on "checking"
  // until get_entitlement landed, for a question the answer to no longer depends on.
  if (isRetiredFeature(feature)) return 'granted';
  if (value.tier === 'unknown') return 'unknown';
  return value.features.includes(feature) ? 'granted' : 'denied';
}

/** Every tool the open plan does not reach, as one set. The tab row marks these and keeps them in place;
 *  the pill is a label, never the gate. Empty while the entitlement is unknown, so nothing is marked on a
 *  guess. */
export function useLockedTools(): Set<string> {
  const value = useEntitlement();
  if (value.tier === 'unknown') return new Set<string>();
  const locked = new Set<string>();
  for (const tool of Object.keys(FEATURE_OF_TOOL)) {
    const feature = featureOfTool(tool);
    if (feature && !value.features.includes(feature)) locked.add(tool);
  }
  return locked;
}

// True when a failed call is the engine's Pro entitlement refusal (EntitlementGuard's stable phrase)
// rather than a real failure — lets a click handler turn the refusal into a plain upsell notice while
// real errors keep their loud red treatment.
export function isEntitlementError(e: unknown): boolean {
  return /Semanticus Pro feature/i.test(String((e as Error)?.message ?? e ?? ''));
}

/**
 * The quiet pill a free page shows where ONE paid control stood.
 *
 * It is not the preview. A preview replaces a whole page that a free session cannot open at all; this
 * stands inside a page that IS free and works, in the one section that is not. The Plan uses that shape
 * for Run the plan, the ask box uses it for the box, and Model notes uses it for the workflow library.
 *
 * Nothing behind it may be CALLED. The pill is what a page shows instead of asking, so a section that
 * renders this and still makes its read has gained an upsell and kept the refusal.
 *
 * `testId` ends in -pro-pill by convention: tools/uishot/cp-pro-drive.mjs finds every pill on a page by
 * that suffix, so a new one is measured without anybody having to remember to register it.
 *
 * plan.tsx and askbox.tsx still carry their own copy of this markup, written before there was one place
 * to put it. They should move here; test/pro-feature-line.test.mjs holds this one either way.
 */
export function ProPill({ line, testId, unlockTestId }: { line: React.ReactNode; testId: string; unlockTestId?: string }) {
  // Instrument: a flat inset, the word Pro in the label size, the line, and the one filled button.
  return (
    <div data-testid={testId} className="sem-inset flex flex-wrap items-center gap-3">
      <span className="sem-label">Pro</span>
      <span className="min-w-0 flex-1 text-xs">{line}</span>
      <button type="button" data-testid={unlockTestId} onClick={upgradeToPro}
        className="sem-btn sem-btn-sm sem-btn-primary shrink-0">Unlock Pro</button>
    </div>
  );
}

// The small "Pro" pill worn inside a bulk button. `show` keeps call sites terse (render only for the
// free tier); `variant='onAccent'` keeps it legible on accent/danger-filled primary buttons.
export function ProBadge({ show, variant }: { show: boolean; variant?: 'onAccent' | 'accent' }) {
  if (!show) return null;
  // Instrument: the word, in the label size and a quieter colour, with no pill behind it.
  return (
    <span className="ml-2 text-(length:--sem-text-label) font-normal"
      style={{ color: variant === 'onAccent' ? 'var(--sem-on-accent)' : 'var(--sem-muted)' }}>
      Pro
    </span>
  );
}

// The teaching notice a free click gets instead of an error: a flat inset (an invitation, not a
// failure) with a dismiss. The copy is written per surface, in plain analyst English.
export function UpsellNotice({ children, onDismiss }: { children: React.ReactNode; onDismiss?: () => void }) {
  return (
    <div className="sem-inset text-xs flex items-start gap-2" style={{ color: 'var(--sem-fg)' }}>
      <span className="flex-1 min-w-0">{children}</span>
      <button onClick={upgradeToPro} className="sem-btn sem-btn-sm sem-btn-primary shrink-0">Unlock Pro</button>
      {onDismiss && (
        <button onClick={onDismiss} title="Dismiss" className="shrink-0 text-xs px-1 rounded-(--sem-radius-control)" style={{ color: 'var(--sem-muted)' }}>✕</button>
      )}
    </div>
  );
}
