// Types for diagramroute.mjs (a plain ES module so the node test runs the same code the webview ships).
export type RoutePoint = { x: number; y: number };
export type RouteEnd = { x: number; y: number; side: string };
export type RouteBox = { x: number; y: number; w: number; h: number };
export type RouteResult = { pts: RoutePoint[] | null; track: number; margin: number; src: RouteEnd; dst: RouteEnd; held?: boolean };
export type Prepared = { boxes: RouteBox[] };
export type Size = { w: number; h: number };
export declare const MARGIN: number;
export declare const MARGINS: number[];
export declare const OPEN: number;
export declare const NEAR: number;
export declare const RUN: number;
export declare const TIGHT: number;
export declare const HUG: number;
export declare function crowdedRuns(routes: Map<string, RouteResult>, boxes: RouteBox[]): { id: string; k: 'v' | 'h'; at: number; lo: number; hi: number }[];
export declare function segmentHitsBox(a: RoutePoint, b: RoutePoint, r: RouteBox): boolean;
export declare function trackFraction(t: number): number;
export declare function prepareBoxes(boxes: RouteBox[]): Prepared;
export declare function routeOrthogonal(src: RouteEnd, dst: RouteEnd, boxes: RouteBox[] | Prepared, track?: number): RoutePoint[] | null;
export declare function sharedLength(a: RoutePoint[], b: RoutePoint[]): number;
export declare function routeAll(lines: { id: string; src: RouteEnd; dst: RouteEnd }[], boxes: RouteBox[], prev?: Map<string, RouteResult>): Map<string, RouteResult>;
export declare function routeSteps(lines: { id: string; src: RouteEnd; dst: RouteEnd }[], boxes: RouteBox[], prev?: Map<string, RouteResult>): Generator<unknown, Map<string, RouteResult>>;
export declare function demandSteps(lines: { id: string; src: RouteEnd; dst: RouteEnd }[], boxes: RouteBox[], shared?: boolean): Generator<unknown, Map<string, RouteResult>>;
export declare function drain<T>(g: Generator<unknown, T>): T;
export declare function holdRoutes(prev: Map<string, RouteResult> | undefined, lines: { id: string; src: RouteEnd; dst: RouteEnd }[]): Map<string, RouteResult>;
export declare function pathOf(pts: RoutePoint[]): string;
export declare function busOrder(names: string[], links: { many: string; one: string }[]): { dims: string[]; facts: string[]; iso: string[]; parent: Record<string, string> };
export declare function busGrid(dims: Size[], facts: Size[], iso: Size[], aspect: number | undefined,
  o?: { nodeW?: number; nodeH?: number; gap?: number; colGap?: number; rowGap?: number; isoGap?: number }): { dims: RoutePoint[]; facts: RoutePoint[]; iso: RoutePoint[]; cols: number; isoCols: number };
