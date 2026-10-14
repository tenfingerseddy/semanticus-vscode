export interface LayoutNode { id: string; group: string }
export interface LayoutLink { source: string; target: string }
export interface Layout {
  readonly positions: Float64Array;
  readonly iteration: number;
  readonly total: number;
  step(budgetMs?: number, now?: () => number): boolean;
}
export function seedPositions(nodes: LayoutNode[], spread?: number): Float64Array;
export function createLayout(o: { nodes: LayoutNode[]; links: LayoutLink[]; iterations?: number; edgeLength?: number; repulsion?: number; cutoff?: number; gravity?: number }): Layout;
export function runLayoutSliced(layout: Layout, onFrame: (positions: Float64Array, done: boolean) => void,
  o?: { sliceMs?: number; frameMs?: number; intermediate?: boolean; yieldFn?: (fn: () => void) => void; now?: () => number }): () => void;
