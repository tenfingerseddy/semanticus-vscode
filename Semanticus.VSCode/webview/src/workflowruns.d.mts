// Type surface for the framework-free run-map reducer (workflowruns.mjs). The run view shape is owned by
// workflows.tsx (the wire mirror of Semanticus.Engine/Workflow.cs); we import it type-only here so the reducer
// stays a plain ES module the node test can execute, while the webview keeps full type-checking.
import type { WorkflowRunView, WorkflowRunFrame, StepResult, AnswerValue } from './workflows';

export type RunMapState = { runs: WorkflowRunView[]; sessionId: string | null };
/** Fold options: `seed` = an initial snapshot that only fills gaps (never overwrites a live entry); `reconcile` =
 *  an authoritative by-id refetch after a pipe reconnect (rank-checked update; with `notFound` it evicts a
 *  locally-active ghost the engine no longer holds); `sessionId` = the generation the fold was requested under,
 *  dropped if it disagrees with the map's session (a model swap). */
export type FoldOpts = { seed?: boolean; reconcile?: boolean; notFound?: boolean; sessionId?: string | null };

export const MAX_RUNS: number;
export function isActive(run: WorkflowRunView | null | undefined): boolean;
export function emptyRuns(sessionId?: string | null): RunMapState;
export function runRank(run: WorkflowRunView | null | undefined): number;
export function reduceRuns(state: RunMapState, incoming: WorkflowRunView | null | undefined, opts?: FoldOpts): RunMapState;
export function liveRuns(state: RunMapState): WorkflowRunView[];
export function terminalRuns(state: RunMapState): WorkflowRunView[];
export function mostRecentLive(state: RunMapState): WorkflowRunView | null;
export function mostRecentTerminal(state: RunMapState): WorkflowRunView | null;
export function runById(state: RunMapState, runId: string | null | undefined): WorkflowRunView | null;
export function runForWorkflow(state: RunMapState, workflowName: string | null | undefined): WorkflowRunView | null;
export function focusedRun(state: RunMapState, selectedRunId: string | null | undefined): WorkflowRunView | null;
export function stepGateSkipped(effectiveStrictness: string | null | undefined): boolean;
export function isRunNotFound(outcome: unknown): boolean;
export type RunRow = { result: StepResult; n: number };
export type RunFrameItem = { frame: WorkflowRunFrame; frameIndex: number; rows: RunRow[]; sections: RunSection[] };
export type RunSection = { kind: 'step'; key: string; row: RunRow }
  | { kind: 'frames'; key: string; stepId: string; frameKind: 'iteration' | 'call'; items: RunFrameItem[] };
export function runSections(run: WorkflowRunView): RunSection[];
export function frameProgress(item: RunFrameItem, currentStepId?: string | null): string;
export function frameIsCurrent(item: RunFrameItem, currentStepId?: string | null): boolean;
export type AnswerDraft = { vals: Record<string, string>; declined: Record<string, boolean>; reasons: Record<string, string> };
export function providedAnswerDraft(provided?: Record<string, AnswerValue> | null, previous?: AnswerDraft, edited?: Set<string>): AnswerDraft;
