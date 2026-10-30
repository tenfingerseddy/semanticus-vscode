import { useEffect, useRef, useState } from 'react';
import { onDidChange, rpc } from './bridge';

interface RatioOffer {
  sessionId: string; revision: number; originalExpression: string; expression: string | null;
  groupedBy?: string; problem?: string;
  rows: { scope: string; before: number | null; after: number | null }[];
}

export function RatioFixChoice({ objectRef, onChanged }: { objectRef: string; onChanged: () => void }) {
  const [offer, setOffer] = useState<RatioOffer | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const generation = useRef(0);
  useEffect(() => onDidChange(() => { generation.current++; setOffer(null); }), []);
  const compare = async () => {
    setBusy(true); setError(null);
    const started = generation.current;
    try {
      const result = await rpc<{ ratioOffer: RatioOffer }>('getFixPrompt', 'AVG-OF-RATIO', objectRef);
      if (started === generation.current) setOffer(result.ratioOffer);
    }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };
  const apply = async () => {
    if (!offer?.expression || offer.problem) return;
    setBusy(true); setError(null);
    try {
      await rpc('setDax', objectRef, offer.expression, 'human', offer.sessionId, offer.revision, offer.originalExpression);
      setOffer(null); onChanged();
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };
  const number = (value: number | null) => value == null ? 'Blank' : value.toLocaleString(undefined, { maximumSignificantDigits: 6 });
  return <div className="sem-text-meta">
    <button className="sem-btn sem-btn-sm" disabled={busy} onClick={() => void compare()}>{busy ? 'Comparing…' : 'Compare totals'}</button>
    {error && <p role="alert">{error}</p>}
    {offer && <div className="mt-2">
      <p>Row averages give each row equal weight. Totals give larger amounts more weight. Choose what the measure should mean.</p>
      {offer.problem ? <p role="status">{offer.problem}</p> : <>
        <p>All data and up to 12 groups{offer.groupedBy ? ` by ${offer.groupedBy}` : ''}. Values use the formula’s raw scale.</p>
        <table className="w-full border-collapse mt-2 mb-2"><thead><tr><th className="text-left py-1 pr-3">Place</th><th className="text-right py-1 px-3">Before</th><th className="text-right py-1 pl-3">After</th></tr></thead>
          <tbody>{offer.rows.map((row, i) => <tr key={i}><td className="py-1 pr-3">{row.scope}</td><td className="sem-num text-right py-1 px-3">{number(row.before)}</td><td className="sem-num text-right py-1 pl-3">{number(row.after)}</td></tr>)}</tbody></table>
      </>}
      {offer.expression && <pre className="whitespace-pre-wrap break-words">{offer.expression}</pre>}
      <div className="flex gap-2 mt-2">
        <button className="sem-btn sem-btn-sm" disabled={busy || !!offer.problem || !offer.expression} onClick={() => void apply()}>Use totals</button>
        <button className="sem-btn sem-btn-sm" disabled={busy} onClick={() => setOffer(null)}>Keep row average</button>
      </div>
    </div>}
  </div>;
}

export function LiveReadinessCheck<T>({ onScan }: { onScan: (card: T) => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const scan = async () => {
    setBusy(true); setError(null);
    try { onScan(await rpc<T>('aiReadinessScanLive')); }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };
  return <div className="sem-text-meta">
    <button className="sem-btn sem-btn-sm" disabled={busy} onClick={() => void scan()}>{busy ? 'Reading counts…' : 'Check live data'}</button>
    <span className="ml-2">Read key counts and text value counts. Nothing changes.</span>
    {error && <p role="alert">{error}</p>}
  </div>;
}
