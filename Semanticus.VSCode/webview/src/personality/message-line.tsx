import { useEffect, useState, useSyncExternalStore } from 'react';
import { loadState, saveState, onActivity, onAssistantCall, onDidChange, onReconnect, onRpcObservation } from '../bridge';
import { useConnection } from '../connection';
import { getPersonalitySettings, usePersonalitySettings } from './settings';
import { LONG_WAIT_MS, VoiceSession, isTestStage, resultEvent, updateVoiceFacts, type VoiceEvent, type VoiceLine, type VoiceMemory } from './messages';
import './messages.css';

const session = new VoiceSession(loadState<VoiceMemory | null>('personalityVoice', null));
const remember = () => saveState('personalityVoice', session.memory());
const listeners = new Set<() => void>();
let current: VoiceLine | null = null;
let until = 0;
let blocked = false;
let receiptBusy = false;
let receiptJokes = false;
const riskyJobs = new Set<number>();
let publishContext: { stage?: string; removals: boolean } | null = null;
export function setVoicePublishContext(stage?: string, removals = false): void {
  publishContext = stage ? { stage, removals } : null;
}
export function receiptVoice(count: number, plain: string, previous?: VoiceLine | null): VoiceLine | null {
  if (!canVoiceReceipt()) return null;
  const line = previous ? updateVoiceFacts(previous, { count }, plain)
    : session.choose({ id: 'synonym-fields', facts: { count }, plain }, getPersonalitySettings().personality, Date.now());
  remember();
  return line;
}
const notify = () => listeners.forEach((fn) => fn());
const subscribe = (fn: () => void) => { listeners.add(fn); return () => { listeners.delete(fn); }; };
const snapshot = () => current;
export const subscribeReceiptVoice = subscribe;
export const canVoiceReceipt = () => receiptJokes && !blocked && !riskyJobs.size && !document.hidden;
export function clearVoice(muteReceipt = true): void { current = null; if (muteReceipt) receiptJokes = false; notify(); }
export function setVoiceReceiptBusy(value: boolean): void {
  receiptBusy = value;
  if (value) clearVoice(false);
}
export function emitVoice(event: VoiceEvent): void {
  if (document.hidden || blocked || receiptBusy || riskyJobs.size) return;
  if (event.ok === false || event.error || event.warning || event.action === 'delete'
    || (event.action === 'publish' && !isTestStage(event.stage))) { clearVoice(); return; }
  current = session.choose(event, getPersonalitySettings().personality, Date.now());
  remember();
  until = performance.now() + 8_000;
  notify();
}
export function noteTestPublish(stage: string | undefined, plain: string): void {
  if (session.firstTestPublish(stage)) emitVoice({ id: 'first-publish', action: 'publish', stage, plain });
  else if (isTestStage(stage) && new Date().getDay() === 5) {
    // The Friday joke is flagged "Might annoy". Keep only the measured publish receipt.
    emitVoice({ id: 'test-published', action: 'publish', stage, plain });
  }
  remember();
}

/** Existing note strip styles and space. No animation, popup, or extra engine request. */
export function MessageLine({ blocked: noteBusy }: { blocked: boolean }) {
  const settings = usePersonalitySettings();
  const { session: model } = useConnection();
  const line = useSyncExternalStore(subscribe, snapshot, snapshot);
  const [visible, setVisible] = useState(!document.hidden);
  useEffect(() => {
    blocked = noteBusy;
    if (noteBusy) clearVoice();
    return () => { blocked = false; };
  }, [noteBusy]);
  useEffect(() => {
    if (!model?.sessionId) return;
    if ((model.tables ?? 0) >= 400) emitVoice({ id: 'huge-model', facts: { tables: model.tables! } });
    else if (model.measures === 0) emitVoice({ id: 'no-measures' });
    // This is a first-open moment, not each sessionInfo refresh after a write.
  }, [model?.sessionId]);
  useEffect(() => {
    const active = new Map<string, { method: string; at: number; shown?: boolean; stage?: string }>();
    const ownCalls = new Set<number>();
    let agentMethod: string | null = null;
    const methodKey = (name: string) => name.split('_').join('').toLowerCase();
    const failed = (value: unknown) => {
      if (!value || typeof value !== 'object') return false;
      const r = value as { error?: unknown; warning?: unknown; ok?: boolean; ruleErrors?: unknown[]; rejected?: unknown[] };
      return !!r.error || !!r.warning || r.ok === false || !!r.ruleErrors?.length || !!r.rejected?.length;
    };
    let waiting: number | undefined;
    let expiry: number | undefined;
    let waitingKey: string | null = null;
    const stopTimers = () => { window.clearTimeout(waiting); window.clearTimeout(expiry); };
    const expire = () => {
      window.clearTimeout(expiry);
      if (current && !document.hidden) expiry = window.setTimeout(clearVoice, Math.max(0, until - performance.now()));
    };
    const arm = () => {
      window.clearTimeout(waiting);
      if (document.hidden || !active.size) return;
      const pair = [...active].find(([, job]) => !job.shown);
      if (!pair) return;
      const [key, job] = pair;
      waiting = window.setTimeout(() => {
        if (!active.has(key) || document.hidden) return;
        waitingKey = key;
        job.shown = true;
        const elapsedMs = performance.now() - job.at;
        emitVoice(job.stage ? { id: 'test-wait', action: 'publish', stage: job.stage, elapsedMs }
          : job.method === 'refreshPartition' || job.method === 'refresh_partition'
          ? { id: 'refresh-wait', elapsedMs, facts: { seconds: Math.floor(elapsedMs / 1000) } }
          : { id: 'job-wait', elapsedMs });
        arm();
      }, Math.max(0, LONG_WAIT_MS + 1 - (performance.now() - job.at)));
    };
    const risk = (method: string) => /delete|remove|deploy|publish|applyDiff|rollback|approve|permission/i.test(method);
    const long = (method: string) => /^(runDax|run_dax|refreshPartition|refresh_partition|buildStartPlan|build_start_plan|runWorkflow|run_workflow|aiReadinessScanLive|vertiPaqScan)$/.test(method);
    const off = [
      onRpcObservation((e) => {
        const key = `rpc:${e.id}`;
        const dangerous = risk(e.method) && !(e.method === 'applyDiff' && e.params[3] !== true);
        if (dangerous) clearVoice();
        if (e.phase === 'start') {
          ownCalls.add(e.id);
          const testPublish = e.method === 'applyDiff' && e.params[3] === true && isTestStage(publishContext?.stage) && !publishContext?.removals;
          if (testPublish) { active.set(key, { method: e.method, at: performance.now(), stage: publishContext!.stage }); arm(); }
          else if (dangerous) riskyJobs.add(e.id);
          if (long(e.method)) { active.set(key, { method: e.method, at: performance.now() }); arm(); }
          return;
        }
        riskyJobs.delete(e.id);
        if (!ownCalls.delete(e.id)) return;
        active.delete(key); arm();
        if (waitingKey === key) { waitingKey = null; clearVoice(); }
        if (e.error || failed(e.result)) { clearVoice(); return; }
        if (e.method === 'getModelPlan' && e.result && typeof e.result === 'object' && Array.isArray((e.result as { items?: unknown }).items)) {
          const plan = session.observePlan(model?.sessionId ?? '', (e.result as { items: { status?: string }[] }).items);
          if (plan) emitVoice(plan);
        }
        const event = resultEvent(e.method, e.result);
        if (event) emitVoice(event);
      }),
      onAssistantCall((c) => {
        clearVoice();
        agentMethod = c.tool;
        active.delete('agent'); riskyJobs.delete(-1);
        if (risk(c.tool)) riskyJobs.add(-1);
        if (long(c.tool)) { active.set('agent', { method: c.tool, at: performance.now() }); arm(); }
      }),
      onActivity((e) => {
        if (agentMethod && methodKey(agentMethod) === methodKey(e.kind)) {
          active.delete('agent'); riskyJobs.delete(-1); agentMethod = null; arm();
          if (waitingKey === 'agent') { waitingKey = null; clearVoice(); }
        }
        if (!e.ok || e.error || failed(e.result) || risk(e.kind)) { clearVoice(); return; }
        const event = resultEvent(e.kind, e.result);
        if (event) emitVoice(event);
      }),
      onDidChange((n) => {
        if (n.sessionId !== model?.sessionId) return;
        clearVoice();
        if (n.words?.verb === 'added synonyms to' && !n.deltas?.some((d) => d.kind === 'remove')) { receiptJokes = true; notify(); }
        if (n.replay === 'undo' || n.replay === 'redo') emitVoice({ id: n.replay });
      }),
      onReconnect(() => { active.clear(); riskyJobs.clear(); stopTimers(); clearVoice(); }),
      subscribe(expire),
    ];
    const visibility = () => {
      setVisible(!document.hidden);
      stopTimers();
      if (document.hidden) clearVoice();
      else { arm(); expire(); }
    };
    document.addEventListener('visibilitychange', visibility);
    expire();
    return () => { off.forEach((fn) => fn()); riskyJobs.clear(); stopTimers(); document.removeEventListener('visibilitychange', visibility); };
  }, [model?.sessionId]);
  if (!visible || noteBusy || receiptBusy || !line) return null;
  const text = settings.personality === 'quiet' ? line.plain : line.text;
  return text ? <div className="sn-note personality-message" role="status" data-testid="personality-message" data-joke={line.joke && settings.personality === 'on'}>
    <p className="sn-say">{text}</p>
  </div> : null;
}
