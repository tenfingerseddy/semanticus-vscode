export const EMITTER_FRONTMATTER_KEYS: string[];
export const EMITTER_STEP_KEYS: string[];
export const EMITTER_INPUT_KEYS: string[];
export const EMITTER_VERIFY_KEYS: string[];
export const EMITTER_GATE_KEYS: string[];
export const LOSSY_DEF_KEYS: string[];
export const LOSSY_STEP_KEYS: string[];
export const CONTRACT_INPUT_KEYS: string[];
export const CONTRACT_VERIFY_KEYS: string[];

export function lossyReasons(def: unknown): string[];
export function isLossy(def: unknown): boolean;

export interface BoxesStepView {
  id: string;
  /** The two tags (lane W2), read from the engine's definition by Author; absent on a box the draft alone made. */
  tags?: ('you' | 'asst')[];
  key?: string;
  order: number;
  number: number;
  title: string;
  instructions: string;
  ops: string[];
  /** Format 3: who does the step. */
  performer?: 'semanticus' | 'assistant' | 'person' | null;
  /** Format 3: the one action Semanticus runs, as the engine's id. */
  action?: string | null;
  /** Format 3: how many questions and parts the step holds. */
  questions?: number;
  blanks?: number;
  /** How many checks the step holds, from either format. */
  checks?: number;
  when: string | null;
  idKind: 'explicit' | 'positional';
  idLabel: string;
  forEach: { in: string; as: string; maxIterations: number } | null;
  call: { workflow: string; with: Record<string, string>; returns: string[] } | null;
}
export interface BoxesView {
  steps: BoxesStepView[];
  connectors: string;
  notRunMeaning: string;
  lossy: string[];
}
export function boxesView(def: unknown): BoxesView;
export function plainWords(value: unknown): string;
export function stepSummary(box: Pick<BoxesStepView, 'ops'> & Partial<BoxesStepView>): { who: 'semanticus' | 'assistant' | 'person'; whoWords: string; action: string | null; detail: string | null; checks: number };
export function canvasCounts(boxes: Pick<BoxesStepView, 'ops'>[] & Partial<BoxesStepView>[]): { steps: number; actions: number; checks: number };
