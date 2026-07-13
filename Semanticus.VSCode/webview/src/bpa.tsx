import { useEffect, useRef, useState } from 'react';
import { rpc, onDidChange } from './bridge';
import { useFixState } from './hooks';
import { GroupedFindings, WaiveControl, WaivedList, type FindingRow } from './findings';
import { useTier, isEntitlementError, ProBadge, UpsellNotice } from './pro';
import { CustomRulesPanel } from './rulesauthor';

interface BpaViolation { ruleId: string; ruleName: string; category: string; severity: number; objectRef: string; objectName: string; message: string; canAutoFix: boolean; custom?: boolean; waived?: boolean; waiverReason?: string; waiverRuleLevel?: boolean; }
interface BpaScorecard { ruleCount: number; violationCount: number; autoFixable: number; waivedCount: number; violations: BpaViolation[]; ruleErrors: string[]; }

export function BpaView({ onReviewAsPlan }: { onReviewAsPlan?: () => void } = {}) {
  const [card, setCard] = useState<BpaScorecard | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [upsell, setUpsell] = useState<string | null>(null);   // a free click on Fix-all teaches, never errors
  const tier = useTier();
  const { state: work, keyOf, fix, ask } = useFixState('bpaFix', 'bpaGetFixPrompt');
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
  const askClaude = (v: BpaViolation) => ask(v.ruleId, v.objectRef);
  async function fixAll() {
    setBusy(true); setUpsell(null);
    try { const r = await rpc<{ scorecard: BpaScorecard; applied: number }>('bpaFixAll'); if (r?.scorecard) setCard(r.scorecard); }
    catch (e) {
      // A free click on the bulk button gets the plain invitation, not a raw exception in a red banner.
      if (isEntitlementError(e)) setUpsell(`Each fix below is free. Apply them one at a time, as many as you like. Pro fixes all ${card?.autoFixable ?? 0} in one undoable step.`);
      else setErr(String((e as Error).message ?? e));
    }
    finally { setBusy(false); }
  }

  if (!card) return <div className="sem-evidence-page sem-centered-page text-[12px]" style={{ color: 'var(--sem-muted)' }}>{err ?? 'Running Best Practice Analyzer…'}</div>;

  const byRef = new Map(card.violations.map((v) => [v.objectRef + '|' + v.ruleId, v]));
  const toRow = (v: BpaViolation): FindingRow => ({
    // Provenance in the rule header: a violation from a model-embedded (user/org) rule says so.
    ruleId: v.ruleId, ruleName: v.ruleName + (v.custom ? ' (custom rule)' : ''), category: v.category, severity: v.severity,
    objectRef: v.objectRef, objectName: v.objectName, message: v.message,
    waived: v.waived, waiverReason: v.waiverReason, waiverRuleLevel: v.waiverRuleLevel,
  });
  const active = card.violations.filter((v) => !v.waived).map(toRow);
  const waivedRows = card.violations.filter((v) => v.waived).map(toRow);
  const fail = (e: unknown) => setErr(String((e as Error).message ?? e));

  const waive = (r: FindingRow, reason: string) => void rpc('waiveFinding', 'bpa', r.ruleId, r.objectRef, reason).then(scan).catch(fail);
  const waiveRule = (ruleId: string, reason: string) => void rpc('waiveFinding', 'bpa', ruleId, '*', reason).then(scan).catch(fail);
  // un-waive routes to the rule-level waiver ('*') when the finding was waived model-wide, else the single instance.
  const unwaive = (r: FindingRow) => void rpc('unwaiveFinding', 'bpa', r.ruleId, r.waiverRuleLevel ? '*' : r.objectRef).then(scan).catch(fail);

  const actions = (r: FindingRow) => {
    const v = byRef.get(r.objectRef + '|' + r.ruleId); if (!v) return null;
    const k = keyOf(v.ruleId, v.objectRef); const st = work[k];
    const fixBtn = v.canAutoFix
      ? (st === 'done'
        ? <span className="text-[10px]" style={{ color: 'var(--sem-good)' }}>fixed ✓</span>
        : <MiniButton disabled={st === 'fixing'} onClick={() => fixOne(v)}>{st === 'fixing' ? '…' : 'Fix'}</MiniButton>)
      : <MiniButton onClick={() => askClaude(v)}>{st === 'copied' ? 'Copied ✓' : 'Ask AI'}</MiniButton>;
    return <span className="flex items-center gap-1">{fixBtn}<WaiveControl onWaive={(reason) => waive(r, reason)} onUnwaive={() => unwaive(r)} /></span>;
  };
  // The rule-header un-waive removes the MODEL-WIDE ('*') waiver — the mirror of "Waive rule" made from the
  // same spot it was made (it was a dead no-op before the 2026-07-07 hook-fix batch).
  const ruleActions = (ruleId: string) => <WaiveControl label="Waive rule" title="Accept every instance of this rule, model-wide (Pro)" onWaive={(reason) => waiveRule(ruleId, reason)} onUnwaive={() => void rpc('unwaiveFinding', 'bpa', ruleId, '*').then(scan).catch(fail)} />;

  return (
    <div className="sem-evidence-page sem-centered-page flex flex-col gap-4">
      <Panel>
        <div className="flex items-center gap-5">
          <div className="flex flex-col items-center justify-center w-20 h-20 rounded-2xl" style={{ background: 'var(--sem-surface-2)', boxShadow: `inset 0 0 0 2px ${card.violationCount === 0 ? 'var(--sem-good)' : 'var(--sem-warn)'}` }}>
            <div className="text-3xl font-bold tnum" style={{ color: card.violationCount === 0 ? 'var(--sem-good)' : 'var(--sem-fg)' }}>{card.violationCount}</div>
            <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>issues</div>
          </div>
          <div className="flex-1 min-w-0">
            <div className="text-[15px] font-semibold">Best Practice Analyzer</div>
            <div className="text-[12px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>
              {card.ruleCount} rules · {card.autoFixable} auto-fixable · the rest fixable by the AI Assistant
              {card.waivedCount > 0 && <span> · <span style={{ color: 'var(--sem-warn)' }}>{card.waivedCount} waived</span></span>}
            </div>
            {card.ruleErrors.length > 0 && <div className="text-[11px] mt-1" style={{ color: 'var(--sem-bad)' }}>{card.ruleErrors.length} rule error(s)</div>}
          </div>
          <Button primary disabled={busy || card.autoFixable === 0} onClick={fixAll}
            title={tier === 'free'
              ? `Pro fixes all ${card.autoFixable} in one undoable step. Each fix below stays free, one at a time.`
              : 'Apply every auto-fixable violation in one undoable step.'}>
            {busy ? 'Fixing…' : `Fix all ${card.autoFixable} auto-fixable`}
            <ProBadge show={tier === 'free'} variant="onAccent" />
          </Button>
          {onReviewAsPlan && card.violationCount > 0 && <Button onClick={onReviewAsPlan} title="Review these fixes as one change plan, then apply in bulk">Review as a plan →</Button>}
          <Button onClick={scan}>Re-scan</Button>
        </div>
      </Panel>

      {err && <Banner color="var(--sem-bad)">{err}</Banner>}
      {upsell && <UpsellNotice onDismiss={() => setUpsell(null)}>{upsell}</UpsellNotice>}
      {card.violationCount === 0 && <Panel><div className="text-[13px]" style={{ color: 'var(--sem-good)' }}>No active best-practice violations. ✓{card.waivedCount > 0 ? ` (${card.waivedCount} waived)` : ''}</div></Panel>}

      {card.violationCount > 0 && <Panel><GroupedFindings rows={active} renderActions={actions} renderRuleActions={(ruleId) => ruleActions(ruleId)} /></Panel>}
      <WaivedList rows={waivedRows} onUnwaive={unwaive} />
      <CustomRulesPanel kind="bpa" onChanged={() => void scan()} />
    </div>
  );
}

// ---- local primitives -----------------------------------------------------------------------
function Panel({ children }: { children: React.ReactNode }) {
  return <div className="rounded-xl border p-4" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>;
}
function Button({ children, onClick, primary, disabled, title }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className="text-[12px] px-3 py-1.5 rounded-lg font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={primary ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
function MiniButton({ children, onClick, disabled }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
      className="text-[11px] px-2 py-0.5 rounded-md font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,' + color + ' 14%, transparent)', color }}>{children}</div>;
}
