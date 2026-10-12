import { useState } from 'react';
import { rpc } from './bridge';
import { recoveryMessage, type FileRecovery } from '../../src/fileRecoveryText';
export type { FileRecovery } from '../../src/fileRecoveryText';

export function FileRecoveryOffer({ copy, sessionId, onResolved }: { copy: FileRecovery; sessionId: string; onResolved: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const resolve = async (restore: boolean) => {
    setBusy(true); setError('');
    try { await rpc('resolveFileRecovery', sessionId, restore); onResolved(); }
    catch (e) { setError(String((e as Error).message || e)); }
    finally { setBusy(false); }
  };
  return <div role="status" className="mx-6 my-3 flex items-center gap-3 rounded border border-[var(--vscode-widget-border)] p-3" data-testid="file-recovery-offer">
    <div className="flex-1">{recoveryMessage(copy)}{error && <p role="alert">{error}</p>}</div>
    <button type="button" className="sem-btn primary" disabled={busy} onClick={() => void resolve(true)}>Restore them</button>
    <button type="button" className="sem-btn" disabled={busy} onClick={() => void resolve(false)}>Discard</button>
  </div>;
}
