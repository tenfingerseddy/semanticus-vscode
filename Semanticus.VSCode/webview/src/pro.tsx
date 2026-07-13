import { useEffect, useState } from 'react';
import { manageLicense, onReconnect, rpc } from './bridge';

// ===================================================================================================
// The shared Free/Pro presentation kit (hook-fix batch, 2026-07-07). One mechanism for every bulk
// button, copied from the Data Agent tab's shipped pattern: the button stays VISIBLE and CLICKABLE on
// the free tier, wears a small "Pro" pill, and a free click teaches with plain English instead of
// surfacing a raw engine exception in a red error banner. The engine gate stays the source of truth —
// the UI never pre-disables, it just softens the refusal.
// ===================================================================================================

// One entitlement fetch shared by every mounted tab. A license activation restarts the engine without remounting
// Studio, so the single reconnect listener invalidates the cache and republishes the new tier to every hook.
let tierPromise: Promise<string> | null = null;
let tierValue = 'unknown';
let reconnectBound = false;
const tierListeners = new Set<(tier: string) => void>();
function publishTier(tier: string): string {
  tierValue = tier;
  tierListeners.forEach((listener) => { try { listener(tier); } catch { /* isolate a view */ } });
  return tier;
}
function fetchTier(): Promise<string> {
  tierPromise ??= rpc<{ tier?: string }>('getEntitlement')
    .then((e) => publishTier(e?.tier ?? 'free'))
    .catch(() => { tierPromise = null; return publishTier('unknown'); });
  return tierPromise;
}
function bindTierReconnect(): void {
  if (reconnectBound) return;
  reconnectBound = true;
  onReconnect(() => { tierPromise = null; void fetchTier(); });
}
export function useTier(): string {
  const [tier, setTier] = useState(tierValue);
  useEffect(() => {
    let live = true;
    bindTierReconnect();
    tierListeners.add(setTier);
    void fetchTier().then((t) => { if (live) setTier(t); });
    return () => { live = false; tierListeners.delete(setTier); };
  }, []);
  return tier;
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
    <button onClick={manageLicense} title={isPro ? 'Open Pro plans and support' : 'See Semanticus Pro and upgrade options'}
      className="shrink-0 whitespace-nowrap flex items-center gap-1.5 text-[12px] px-2.5 py-1 rounded-md font-semibold transition-colors"
      style={isPro
        ? { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }
        : { background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', border: '1px solid color-mix(in srgb, var(--sem-accent) 42%, var(--sem-border))' }}>
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
      <button onClick={manageLicense} className="shrink-0 text-[11px] font-semibold px-2 py-1 rounded-md"
        style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Upgrade to Pro →</button>
      {onDismiss && (
        <button onClick={onDismiss} title="Dismiss" className="shrink-0 text-[12px] px-1 rounded" style={{ color: 'var(--sem-muted)' }}>✕</button>
      )}
    </div>
  );
}
