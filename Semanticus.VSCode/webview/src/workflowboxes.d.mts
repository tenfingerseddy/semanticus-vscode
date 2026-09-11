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
  order: number;
  number: number;
  title: string;
  instructions: string;
  ops: string[];
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
