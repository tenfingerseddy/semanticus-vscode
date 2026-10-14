import { useEffect, useState } from 'react';
import { onEntitlementChange, onReconnect, rpc } from './bridge';
import { openKeepPro, useTier } from './pro';
import { trialEndingLine, type TrialW } from './propage-model';

/** The plain line for the last three days of the free month (trial option A's line, kept in option B): one
 *  sentence and Keep Pro, above whichever page is open. Nothing before the last three days, and nothing on
 *  paid Pro or Free. Keep Pro opens the Pro page with the plans showing, the same checkout as Buy Pro. */
export function TrialEndingLine({ hidden }: { hidden?: boolean }) {
  const tier = useTier();
  const [info, setInfo] = useState<TrialW | null>(null);
  const [bump, setBump] = useState(0);
  useEffect(() => {
    const a = onEntitlementChange(() => setBump((n) => n + 1));
    const b = onReconnect(() => setBump((n) => n + 1));
    return () => { a(); b(); };
  }, []);
  useEffect(() => {
    if (tier !== 'pro') { setInfo(null); return; }
    let live = true;
    void rpc<TrialW>('getEntitlement').then((v) => { if (live) setInfo(v); }).catch(() => { if (live) setInfo(null); });
    return () => { live = false; };
  }, [tier, bump]);
  const line = trialEndingLine(info);
  if (!line || hidden) return null;
  return <div className="trial-line trial-band" role="status" data-testid="trial-ending-line">
    <span className="trial-line-text">{line}</span>
    <button type="button" className="sem-btn sem-btn-sm" data-testid="trial-ending-keep" onClick={openKeepPro}>Keep Pro</button>
  </div>;
}
