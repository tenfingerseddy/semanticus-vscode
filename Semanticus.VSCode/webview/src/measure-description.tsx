import { useEffect, useRef, useState } from 'react';
import { onDidChange, rpc } from './bridge';

export type DescriptionProposal = {
  id: string; state: string; message: string; text?: string; deadline?: string; requestId?: string;
};
export type MeasureDescription = {
  ref: string; name: string; description: string; oneLine: string; provenance: string;
  match: { score: number | null; level: string; label: string; reason?: string };
  proposal: DescriptionProposal | null;
};

export function useMeasureDescription(reference: string) {
  const [view, setView] = useState<MeasureDescription | null>(null);
  const [proposal, setProposal] = useState<DescriptionProposal | null>(null);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const generation = useRef(0);
  const inFlight = useRef(false);
  const receive = (value: MeasureDescription) => {
    setView(value); setProposal(value.proposal); setNote(value.proposal?.message ?? '');
  };
  useEffect(() => {
    const at = ++generation.current;
    setView(null); setProposal(null); setNote(''); setBusy(false); inFlight.current = false;
    let reading = 0;
    const refresh = () => {
      const sequence = ++reading;
      void rpc<MeasureDescription>('getMeasureDescription', reference).then(value => {
        if (generation.current === at && sequence === reading) receive(value);
      }).catch(error => { if (generation.current === at && sequence === reading) setNote(String(error.message ?? error)); });
    };
    refresh();
    const stop = onDidChange(change => {
      if (change.replay || !change.deltas?.length || change.deltas.some(d => d.ref === reference || d.kind === 'structure')) refresh();
    });
    return () => { stop(); ++generation.current; };
  }, [reference]);
  useEffect(() => {
    if (proposal?.state !== 'waiting') return;
    const at = generation.current;
    const timer = setTimeout(() => {
      void rpc<MeasureDescription>('getMeasureDescription', reference).then(value => {
        if (generation.current === at) receive(value);
      }).catch(error => {
        if (generation.current === at) {
          setNote(String(error.message ?? error)); setProposal(null);
        }
      });
    }, 500);
    return () => clearTimeout(timer);
  }, [reference, proposal]);
  const request = async () => {
    if (inFlight.current || proposal?.state === 'waiting') return;
    const at = generation.current;
    inFlight.current = true; setBusy(true); setNote('Asking your assistant.');
    try {
      const value = await rpc<DescriptionProposal>(view?.description?.trim() ? 'proposeMeasureDescription' : 'draftMeasureDescription', reference);
      if (generation.current === at) { setProposal(value); setNote(value.message); }
    } catch (error) {
      if (generation.current === at) setNote(error instanceof Error ? error.message : 'The Description could not be requested.');
    } finally { if (generation.current === at) { inFlight.current = false; setBusy(false); } }
  };
  const keep = async () => {
    if (inFlight.current || proposal?.state !== 'ready') return;
    const at = generation.current;
    inFlight.current = true; setBusy(true);
    try {
      await rpc('keepMeasureDescription', proposal.id);
      const value = await rpc<MeasureDescription>('getMeasureDescription', reference);
      if (generation.current === at) receive(value);
    } catch (error) {
      if (generation.current === at) setNote(error instanceof Error ? error.message : 'The Description could not be kept.');
    } finally { if (generation.current === at) { inFlight.current = false; setBusy(false); } }
  };
  return { view, proposal, note, busy, request, keep };
}

// The marks lane owns the one-line layout. This is the existing card's proposal control.
export function MeasureDescriptionEditor({ reference, description = '' }: { reference: string; description?: string }) {
  const { view, proposal, note, busy, request, keep } = useMeasureDescription(reference);
  const text = view ? view.description : description;
  return <div data-testid="measure-description">
    {text && <p style={{ whiteSpace: 'pre-wrap' }}>{text}</p>}
    {view && <p className="va-faint">{view.provenance === 'ai' ? 'Drafted by AI' : view.provenance === 'person' ? 'Written by a person' : 'Source not recorded'} · {view.match.label}</p>}
    {view?.match.reason && <p>{view.match.reason}</p>}
    <button type="button" className="sem-btn" disabled={!view || busy || proposal?.state === 'waiting'} onClick={() => void request()}>
      {view?.description?.trim() ? 'Propose a better Description' : 'Draft a Description'}
    </button>
    {note && <p role="status" aria-live="polite">{note}</p>}
    {proposal?.state === 'ready' && <div>
      <p style={{ whiteSpace: 'pre-wrap' }}>{proposal.text}</p>
      <button type="button" className="sem-btn" disabled={busy} onClick={() => void keep()}>Keep Description</button>
    </div>}
  </div>;
}
