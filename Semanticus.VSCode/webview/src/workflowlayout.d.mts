import type { WorkflowLayout, WorkflowPositions } from './bridge';
export function workflowLayoutSession(options: {
  name: string;
  rpc: (method: string, ...params: unknown[]) => Promise<WorkflowLayout>;
  subscribe: (fn: (layout: WorkflowLayout) => void) => () => void;
  apply: (positions: WorkflowPositions) => void;
  error: (message: string) => void;
}): { ready: Promise<void>; refresh: () => Promise<void>; save: (positions: WorkflowPositions) => Promise<void>; dispose: () => void };
