// Review > Copilot test: the one line about changes the published model does not have yet, and its one press, "Publish, then test".
// The test grades each answer by running the student's DAX on the PUBLISHED model, so a rename or a new measure that is not published makes false misses
// (UAT 17 Oct: the assistant stopped there, and the page offered nothing). The press is the person's own yes, and the publish is seen: it says what went
// out, it keeps a restore point, and the line carries Undo publish. A removal on the service is never part of this press; it stays a choice on Publish.
import { useCallback, useEffect, useRef, useState } from 'react';
import { navigateInStudio, onActivity, onDidChange, rpc } from './bridge';
import { useConnection } from './connection';
import { compareSeedFromSession } from './contextbar';
import type { ApplyDiffResult, ModelDiff, ModelRef } from './compare';
import { isRemoval, plainPublishError, publishableRows, publishedLine, undoPublishResult, clockTime } from './publishcopy';
import { PUBLISH_THEN_TEST, gapAfterPublish, unpublishedLine } from './copilot-test-model';

type Gap = { left: ModelRef; right: ModelRef; refs: string[]; removals: number; destination: string };
type Line = { tone: 'good' | 'bad'; text: string; undo?: { id: string; when: string } };

/** What the published model lacks: the ticked-by-default changes (never removals) and how many removals wait. Null when nothing differs or the destination cannot be read. */
function useGap(): { gap: Gap | null; setGap: (gap: Gap | null) => void; reread: () => void } {
  const { session, conn, context } = useConnection();
  const [gap, setGap] = useState<Gap | null>(null);
  const seq = useRef(0);
  const reread = useCallback(async () => {
    const mine = ++seq.current;
    const seed = session ? compareSeedFromSession(session, conn, context) : null;
    if (!seed?.right || seed.right.kind !== 'workspace') { setGap(null); return; }
    try {
      const d = await rpc<ModelDiff>('compareModels', seed.left, seed.right);
      if (mine !== seq.current) return;
      if (d.error) { setGap(null); return; }
      const rows = publishableRows(d.items.filter((i) => i.action !== 'Equal'));
      let refs = rows.filter((r) => !isRemoval(r)).map((r) => r.ref);
      const removals = rows.length - refs.length;
      // One dry run names what Power BI would refuse (the Publish page does the same), so the line counts only what this press can publish.
      if (refs.length > 0) {
        try {
          const dry = await rpc<ApplyDiffResult>('applyDiff', seed.left, seed.right, refs, false, 'human');
          if (mine !== seq.current) return;
          const refused = new Set(dry.failedRefs ?? []);
          refs = refs.filter((r) => !refused.has(r));
        } catch { /* the write itself says no in plain words; a failed rehearsal must not hide the line */ }
      }
      setGap(refs.length > 0 ? { left: seed.left, right: seed.right, refs, removals, destination: seed.right.label ?? 'the published model' } : null);
    } catch { if (mine === seq.current) setGap(null); /* the page works without this line */ }
  }, [session?.sessionId, conn?.connected, context?.publishing?.endpoint, context?.publishing?.database, session?.liveEndpoint, session?.liveDatabase]);   // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    void reread();
    let timer: number | undefined;
    const again = () => { window.clearTimeout(timer); timer = window.setTimeout(() => void reread(), 1500); };
    const offChange = onDidChange(again);
    const offActivity = onActivity((e) => { if (e.kind === 'apply_model_diff' || e.kind === 'deploy_live' || e.kind === 'rollback_push') again(); });
    return () => { offChange(); offActivity(); window.clearTimeout(timer); };
  }, [reread]);
  return { gap, setGap, reread: () => void reread() };
}

export function PublishThenTest({ onPublished }: { onPublished: () => Promise<void> }) {
  const { gap, setGap, reread } = useGap();
  const [busy, setBusy] = useState<'publish' | 'undo' | null>(null);
  const [line, setLine] = useState<Line | null>(null);

  const publish = async () => {
    if (!gap || busy) return;
    const when = clockTime(new Date());
    setBusy('publish'); setLine(null);
    try {
      const r = await rpc<ApplyDiffResult>('applyDiff', gap.left, gap.right, gap.refs, true, 'human');
      if (r.error) { setLine({ tone: 'bad', text: plainPublishError(r.error) }); return; }
      if (!r.applied) { setLine({ tone: 'bad', text: `Nothing was published to ${gap.destination}. Open Publish to see why.` }); return; }
      const left = r.failedRefs?.length ?? 0;
      const said = publishedLine(r.count, gap.destination) + (left > 0 ? ` ${left} could not go. Open Publish to see why.` : '');
      setLine({ tone: 'good', text: said, undo: r.restorePointId && r.count > 0 ? { id: r.restorePointId, when } : undefined });
      setGap(gapAfterPublish(gap, r)); reread();   // the box counts what is left now, not after the test ends
      await onPublished();   // the test is asked for only once the publish has landed
    } catch (e) { setLine({ tone: 'bad', text: String((e as Error)?.message ?? e) }); }
    finally { setBusy(null); reread(); }
  };
  const undo = async (point: { id: string; when: string }) => {
    setBusy('undo');
    try {
      const r = await rpc<{ applied: boolean; error?: string; failedRefs?: string[] | null }>('rollbackPush', point.id, true, null, 'human');
      setLine(undoPublishResult(r, gap?.destination ?? 'The published model', point.when));
    } catch (e) { setLine({ tone: 'bad', text: String((e as Error)?.message ?? e) }); }
    finally { setBusy(null); reread(); }
  };

  if (!gap && !line) return null;
  return <div className="ct-gap" data-testid="ct-gap">
    {gap && <div className="ct-row">
      <span className="sem-text-meta" data-testid="ct-gap-line">{unpublishedLine(gap.refs.length, gap.removals)}</span>
      <span className="ct-push" />
      <button type="button" className="sem-btn sem-btn-primary" disabled={busy !== null} onClick={() => void publish()} data-testid="publish-then-test">{busy === 'publish' ? 'Publishing...' : PUBLISH_THEN_TEST}</button>
    </div>}
    {line && <div className="ct-row sem-text-meta" role={line.tone === 'bad' ? 'alert' : 'status'} data-testid="ct-gap-result">
      <span className={`sem-chip ${line.tone === 'good' ? 'sem-chip-ok' : 'sem-chip-bad'}`} aria-hidden="true" />
      <span>{line.text}</span>
      {line.undo && <button type="button" className="sem-btn sem-btn-sm" disabled={busy !== null} onClick={() => void undo(line.undo!)} data-testid="ct-undo-publish">Undo publish</button>}
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => navigateInStudio('deploy')}>Open Publish</button>
    </div>}
  </div>;
}
