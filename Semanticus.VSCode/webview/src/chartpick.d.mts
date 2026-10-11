// Type surface for chartpick.mjs.

export type ChartKind = 'matrix' | 'table' | 'bar' | 'line' | 'area' | 'card' | 'scatter';
export interface ChartShape { dims: { name: string; isDate: boolean; ordered?: boolean; distinct: number }[]; measures: number; rows: number }
export interface ChartPick { pick: ChartKind; reason: string; fits: ChartKind[]; misfits: { viz: ChartKind; reason: string }[] }
export declare function chartRulePick(shape: ChartShape): ChartPick;
export declare function engineShape(shape: ChartShape): { dims: { name: string; isDate: boolean; distinct: number }[]; measures: number; rows: number };
export declare function offeredKinds<K extends string>(all: K[], rule: ChartPick | null): K[];
export declare function twoSplitAxes(dims: { isDate: boolean; ordered?: boolean; distinct: number }[], measures: number, viz: string): { axis: 0 | 1; series: 0 | 1 } | null;
export declare function scatterPoints(names: string[], cells: unknown[][]): { name: string; x: number; y: number }[];
