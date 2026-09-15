import { useEffect, useState } from 'react';
import { manageLicense, showLicense, onReconnect, rpc } from './bridge';
import { FEATURE_OF_TOOL, PRO_FEATURES, featureOfTool, type ProFeature } from './features';

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

interface Entitlement { tier: string; features: ProFeature[] }

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
function readEntitlement(value: { tier?: string; features?: string[] } | null | undefined): Entitlement {
  const tier = value?.tier ?? 'free';
  const listed = Array.isArray(value?.features) ? (value!.features as ProFeature[]) : (isPaid(tier) ? PRO_FEATURES : []);
  return { tier, features: listed };
}
// Which fetch is the current one. A reconnect starts a new one, and an answer from the fetch it replaced
// must be DISCARDED rather than published: the old engine's answer arriving late would otherwise overwrite
// the new engine's, and the page would act on a licence that is no longer there.
let generation = 0;
function fetchTier(): Promise<Entitlement> {
  const mine = generation;
  tierPromise ??= rpc<{ tier?: string; features?: string[] }>('getEntitlement')
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

/** Is this feature granted? `null` means the caller is asking about a free tool, which is always granted,
 *  so a component can call this unconditionally and keep its hook order stable. */
export function useFeature(feature: ProFeature | null): FeatureGrant {
  const value = useEntitlement();
  if (!feature) return 'granted';
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

// The small "Pro" pill worn inside a bulk button. `show` keeps call sites terse (render only for the
// free tier); `variant='onAccent'` keeps it legible on accent/danger-filled primary buttons.
export function ProBadge({ show, variant }: { show: boolean; variant?: 'onAccent' | 'accent' }) {
  if (!show) return null;
  const onAccent = variant === 'onAccent';
  return (
    <span className="text-[9px] uppercase tracking-wide font-bold px-1 py-px rounded ml-1.5"
      style={onAccent
        ? { background: 'color-mix(in srgb, var(--sem-on-accent) 24%, transparent)', color: 'var(--sem-on-accent)' }
        : { background: 'color-mix(in srgb, var(--sem-accent) 20%, transparent)', color: 'var(--sem-accent)' }}>
      Pro
    </span>
  );
}

// One ambient licensing door in the Studio shell. Free users get the upgrade invitation; active subscribers
// get account management (including cancellation) without a new tab or any billing logic in the extension.
export function LicenseButton() {
  const tier = useTier();
  if (tier === 'unknown') return null;
  const isPro = tier === 'pro';
  return (
    <button onClick={isPro ? showLicense : manageLicense} title={isPro ? 'Show your Pro license' : 'See Semanticus Pro and upgrade options'}
      className="sem-btn sem-btn-sm shrink-0"
      style={isPro ? undefined : { background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', borderColor: 'color-mix(in srgb, var(--sem-accent) 42%, var(--sem-border))' }}>
      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M10 13a5 5 0 0 0 7.1.1l2-2a5 5 0 0 0-7.1-7.1l-1.1 1.1" /><path d="M14 11a5 5 0 0 0-7.1-.1l-2 2A5 5 0 0 0 12 20l1.1-1.1" />
      </svg>
      {isPro ? 'Pro options' : 'Upgrade to Pro'}
    </button>
  );
}

// The teaching notice a free click gets instead of an error: accent-tinted (an invitation, not a
// failure) with a dismiss. The copy is written per surface, in plain analyst English.
export function UpsellNotice({ children, onDismiss }: { children: React.ReactNode; onDismiss?: () => void }) {
  return (
    <div className="rounded-lg px-3 py-2 text-[12px] flex items-start gap-2"
      style={{ background: 'color-mix(in srgb, var(--sem-accent) 12%, transparent)', color: 'var(--sem-fg)', border: '1px solid color-mix(in srgb, var(--sem-accent) 34%, transparent)' }}>
      <span className="flex-1 min-w-0">{children}</span>
      <button onClick={manageLicense} className="sem-btn sem-btn-sm sem-btn-primary shrink-0">Upgrade to Pro →</button>
      {onDismiss && (
        <button onClick={onDismiss} title="Dismiss" className="shrink-0 text-[12px] px-1 rounded" style={{ color: 'var(--sem-muted)' }}>✕</button>
      )}
    </div>
  );
}
