import { useEffect, useRef, useState } from 'react';
import { rpc, onDidChange } from './bridge';
import { useFixState } from './hooks';
import { GiveTheRest, GroupedFindings, WaiveControl, WaivedList, type FindingRow } from './findings';
import { CustomRulesPanel } from './rulesauthor';
import { GiveToAssistant, findingBuilder } from './sendtask';
import { RagBars } from './ragbars';
import { gradeBand } from './readinessband.mjs';

interface BpaViolation { ruleId: string; ruleName: string; category: string; severity: number; objectRef: string; objectName: string; message: string; canAutoFix: boolean; custom?: boolean; waived?: boolean; waiverReason?: string; waiverRuleLevel?: boolean; noLongerFires?: boolean; waivedAsTwin?: boolean; coveredBy?: string; coveredReason?: string; wholeRuleRow?: boolean; }
interface BpaScorecard { ruleCount: number; violationCount: number; autoFixable: number; waivedCount: number; violations: BpaViolation[]; ruleErrors: string[]; bestPractice?: string | null; bestPracticeWhy?: string | null; bestPracticeScore?: number; categories?: BpaCategory[]; }
// One category's score from the engine (0 to 100, the same bands as AI readiness) and the objects its rules checked.
interface BpaCategory { category: string; errors: number; warnings: number; objectsChecked: number; score: number; }
// What one press of "Fix all" could NOT clear. The engine verifies the result against a fresh scan, so this is
// the press's honest remainder rather than a guess: the fix ran (or was skipped) and the finding is still here.
interface BpaUnfixed { ruleId: string; ruleName: string; objectRef: string; objectName: string; reason: string; }

export function BpaView({ onReviewAsPlan }: { onReviewAsPlan?: () => void } = {}) {
  const [card, setCard] = useState<BpaScorecard | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [leftOver, setLeftOver] = useState<BpaUnfixed[]>([]);  // what the last press could not clear
  const [waivedOpen, setWaivedOpen] = useState(false);
  const { state: work, keyOf, fix } = useFixState('bpaFix', 'bpaGetFixPrompt');
  const timer = useRef<number | undefined>(undefined);

  async function scan() {
    try { setCard(await rpc<BpaScorecard>('bpaScan')); setErr(null); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
  }
  useEffect(() => {
    void scan();
    const off = onDidChange(() => { window.clearTimeout(timer.current); timer.current = window.setTimeout(() => void scan(), 350); });
    return () => { off(); window.clearTimeout(timer.current); };
  }, []);
  const fixOne = (v: BpaViolation) => fix(v.ruleId, v.objectRef, () => void scan());
  async function fixAll() {
    // Clear both messages BEFORE the attempt: an old refusal (or an old error) must not survive a press that
    // works. Only ever set them from what this press actually saw.
    setBusy(true); setErr(null); setLeftOver([]);
    try {
      const r = await rpc<{ scorecard: BpaScorecard; applied: number; unfixed?: BpaUnfixed[] }>('bpaFixAll');
      if (r?.scorecard) setCard(r.scorecard);
      setLeftOver(r?.unfixed ?? []);
    }
    // Fixing every auto-fixable violation in one step is FREE from 2026-09-15 (Kane's feature line). There is
    // no entitlement refusal left to soften, so a failure here is a real failure and reads as one.
    catch (e) { setErr(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  }

  if (!card) return <div className="sem-page"><div className="sem-text-meta">{err ?? 'Checking the model…'}</div></div>;

  const byRef = new Map(card.violations.map((v) => [v.objectRef + '|' + v.ruleId, v]));
  const toRow = (v: BpaViolation): FindingRow => ({
    // Provenance in the rule header: a violation from a model-embedded (user/org) rule says so.
    ruleId: v.ruleId, ruleName: v.ruleName + (v.custom ? ' (custom rule)' : ''), category: v.category, severity: v.severity,
    objectRef: v.objectRef, objectName: v.objectName, message: v.message,
    waived: v.waived && !v.waivedAsTwin, waiverReason: v.waiverReason, waiverRuleLevel: v.waiverRuleLevel, noLongerFires: v.noLongerFires, waivedAsTwin: v.waivedAsTwin, coveredBy: v.coveredBy, coveredReason: v.coveredReason, wholeRuleRow: v.wholeRuleRow,
  });
  // A finding another acceptance covers is left alone but is not accepted: it stays in the open list, with its own words.
  const accepted = (v: BpaViolation) => !!v.waived && !v.waivedAsTwin;
  const active = card.violations.filter((v) => !accepted(v)).map(toRow);
  const waivedRows = card.violations.filter(accepted).map(toRow);
  const fail = (e: unknown) => setErr(String((e as Error).message ?? e));

  // true only when the engine took the acceptance: the box keeps the person's reason on anything else (R-B).
  const waiveWrite = async (ruleId: string, objectRef: string, reason: string): Promise<boolean> => {
    try { await rpc('waiveFinding', 'bpa', ruleId, objectRef, reason); } catch (e) { fail(e); return false; }
    void scan();
    return true;
  };
  const waive = (r: FindingRow, reason: string) => waiveWrite(r.ruleId, r.objectRef, reason);
  const waiveRule = (ruleId: string, reason: string) => waiveWrite(ruleId, '*', reason);
  // un-waive routes to the rule-level waiver ('*') when the finding was waived model-wide, else the single instance.
  const unwaive = (r: FindingRow) => void rpc('unwaiveFinding', 'bpa', r.ruleId, r.waiverRuleLevel ? '*' : r.objectRef).then(scan).catch(fail);

  const actions = (r: FindingRow) => {
    const v = byRef.get(r.objectRef + '|' + r.ruleId); if (!v) return null;
    const k = keyOf(v.ruleId, v.objectRef); const st = work[k];
    const fixBtn = v.canAutoFix
      ? (st === 'done'
        ? <span className="sem-text-meta">Fixed</span>
        : <MiniButton disabled={st === 'fixing'} onClick={() => fixOne(v)}>{st === 'fixing' ? '…' : 'Fix'}</MiniButton>)
      : <GiveToAssistant sendKey={`bpa|${v.ruleId}|${v.objectRef}`} build={findingBuilder('bpa', v)} />;
    return <span className="flex items-center gap-1">{fixBtn}<WaiveControl onWaive={(reason) => waive(r, reason)} onUnwaive={() => unwaive(r)} /></span>;
  };
  // The rule-header un-waive removes the MODEL-WIDE ('*') waiver — the mirror of "Accept for the whole model" made from the
  // same spot it was made (it was a dead no-op before the 2026-07-07 hook-fix batch).
  const ruleActions = (ruleId: string) => <WaiveControl subtle label="Accept for the whole model" title="Accept every instance of this rule across the model" onWaive={(reason) => waiveRule(ruleId, reason)} onUnwaive={() => void rpc('unwaiveFinding', 'bpa', ruleId, '*').then(scan).catch(fail)} />;

  // One bar per rule category, scored by the engine. The detail is the open findings in that category beside the objects its rules checked.
  const openIn = (name: string) => card.violations.filter((v) => !accepted(v) && v.category === name).length;
  const bars = (card.categories ?? []).map((c) => ({ name: c.category, score: c.score, detail: `${openIn(c.category)} of ${c.objectsChecked}` }));

  return (
    <div className="sem-page">
      <Panel>
        {/* The header wraps: the letter and the issue count stay together, and the words and the buttons drop below them when the page is narrow (a window of 1440 with the
            side bars open leaves under 900 px, and the words were squeezed to one or two words a line; plan statement check 3, defect 5). */}
        <div className="flex flex-wrap items-center gap-x-6 gap-y-3" data-testid="bpa-head">
          {/* The grade is the AI readiness grade: one big letter and the score beside it, in the band colour (readinessband.mjs). */}
          {card.bestPractice && <div className={`flex items-baseline gap-2 shrink-0 sem-rag-${gradeBand(card.bestPractice) ?? 'warn'}`} data-testid="bpa-grade" title={card.bestPracticeWhy ?? undefined}>
            <span className="sem-rag-grade">{card.bestPractice}</span>
            {card.bestPracticeScore != null && <><span className="sem-num sem-rag-score">{card.bestPracticeScore}</span><span className="sem-text-meta">of 100</span></>}
          </div>}
          <div className="flex items-baseline gap-2 shrink-0">
            <span className="sem-num text-(length:--sem-text-page) leading-(--sem-lh-page) font-semibold">{card.violationCount}</span>
            <span className="sem-text-meta">{card.violationCount === 1 ? 'issue' : 'issues'}</span>
          </div>
          <div className="flex-1 min-w-[120px]">
            {/* One short line. The grade's reasons are the letter's tooltip and Help; the accepted count is on the Accepted row below. */}
            {card.bestPractice && <h2 className="sem-card-title">Best practice</h2>}
            <div className="sem-text-meta">{card.ruleCount} checks</div>
            {card.ruleErrors.length > 0 && <div className="sem-chip sem-chip-bad mt-1">{card.ruleErrors.length} rule error(s)</div>}
          </div>
          <div className="flex items-center gap-2 flex-wrap shrink-0">
            {(busy || card.autoFixable > 0) && <Button primary disabled={busy} onClick={fixAll}
              title="Fixes every finding this app can fix, in one undoable step.">
              {busy ? 'Fixing…' : `Fix ${card.autoFixable}`}
            </Button>}
            {/* What the app cannot fix goes to the assistant in one press (D4). */}
            <GiveTheRest system="bpa" items={active.filter((r) => !byRef.get(r.objectRef + '|' + r.ruleId)?.canAutoFix)} />
            {onReviewAsPlan && card.violationCount > 0 && <button type="button" className="sem-btn sem-btn-quiet" onClick={onReviewAsPlan} title="Send these fixes to Pending changes, look at each one, then apply them together.">Review all fixes →</button>}
          </div>
        </div>
      </Panel>

      {bars.length > 0 && (
        <Panel>
          <h2 className="sem-card-title">Categories</h2>
          <RagBars testId="bpa-categories" rows={bars} />
        </Panel>
      )}

      {err && <Banner>{err}</Banner>}
      {leftOver.length > 0 && (
        <Panel>
          <h2 className="sem-card-title flex items-center gap-2"><span className="sem-chip sem-chip-warn" aria-hidden="true" />
            {leftOver.length === 1 ? 'One finding could not be fixed' : `${leftOver.length} findings could not be fixed`}
          </h2>
          <div className="sem-text-meta mt-1">
            Fix all cleared the rest. These are still on the model, and each one says why it stayed. Fix them one at a time, or ask your assistant.
          </div>
          <ul className="mt-2 flex flex-col gap-2">
            {leftOver.map((u) => (
              <li key={u.ruleId + '|' + u.objectRef} className="text-(length:--sem-text-meta)">
                <span className="font-medium">{u.objectName}</span>
                <span style={{ color: 'var(--sem-muted)' }}> · {u.ruleName} · </span>
                <span>{u.reason}</span>
              </li>
            ))}
          </ul>
        </Panel>
      )}
      <Panel>
        {card.violationCount === 0
          ? <div className="sem-text-body">Nothing to fix.</div>
          : <GroupedFindings rows={active} holdAs="bpa" renderActions={actions} renderRuleActions={(ruleId) => ruleActions(ruleId)} />}
        {/* Accepted findings are the last fold row of this card, not a card of their own. */}
        <WaivedList embedded rows={waivedRows} onUnwaive={unwaive} open={waivedOpen} onOpenChange={setWaivedOpen} />
      </Panel>
      <CustomRulesPanel kind="bpa" onChanged={() => void scan()} />
    </div>
  );
}

// ---- local primitives -----------------------------------------------------------------------
function Panel({ children }: { children: React.ReactNode }) {
  return <section className="sem-card">{children}</section>;
}
function Button({ children, onClick, primary, disabled, title }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className={primary ? 'sem-btn sem-btn-primary' : 'sem-btn'}>
      {children}
    </button>
  );
}
function MiniButton({ children, onClick, disabled }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
      className="sem-btn sem-btn-sm">
      {children}
    </button>
  );
}
// A problem is a sentence with a red dot in front of it, not red text on a red wash (Instrument: state is a dot).
function Banner({ children }: { children: React.ReactNode }) {
  return <div role="alert" className="sem-card flex items-start gap-2 sem-text-body"><span className="sem-chip sem-chip-bad shrink-0" aria-hidden="true" />{children}</div>;
}
