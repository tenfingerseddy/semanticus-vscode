import { useEffect, useRef, useState } from 'react';
import { leaves, rowTitle, type HistoryRow } from '../../plan-history-model';
import { UndoRewind, type RewindChange } from './components';
import { historyTransition } from './state.mjs';

type Transition = { changes: RewindChange[]; fromRevision?: number; toRevision?: number; direction: 'undo' | 'redo'; key: number };

/** History's confirmed undone flags work for both writers. A failed undo produces no transition. */
export function HistoryMotion({ rows, sessionId, partial = false }: { rows: HistoryRow[]; sessionId?: string | null; partial?: boolean }) {
  const previous = useRef<{ rows: HistoryRow[]; sessionId?: string | null } | null>(null);
  const [transition, setTransition] = useState<Transition | null>(null);
  useEffect(() => {
    const before = previous.current;
    previous.current = { rows, sessionId };
    if (!before || before.sessionId !== sessionId) { setTransition(null); return; }
    const old = leaves(before.rows).filter(row => row.kind === 'edit' && row.sessionId === sessionId);
    const next = leaves(rows).filter(row => row.kind === 'edit' && row.sessionId === sessionId);
    const change = historyTransition(old, next);
    if (!change) return;
    setTransition({ ...change, changes: change.changes.map(row => ({ id: row.id, text: rowTitle(row) })), key: performance.now() });
  }, [rows, sessionId]);
  return transition ? <UndoRewind {...transition} partial={partial} eventKey={transition.key} /> : null;
}
