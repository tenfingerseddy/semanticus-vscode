// Type surface for the framework-free Diagram reflow module (diagramfit.mjs).

export interface FitCard { id: string; x: number; y: number; w: number; h: number; }
export interface FitSize { w: number; h: number; }

export declare const CARD_GAP: number;
/** How many lines leave a card by each side. */
export interface SideLines { top: number; right: number; bottom: number; left: number; }
export type LinesById = Map<string, SideLines> | Record<string, SideLines>;
export declare function reflowCards(cards: FitCard[], prev: Record<string, FitSize>, gap?: number, keep?: Set<string> | null): Record<string, { x: number; y: number }>;
export declare function clearDropped(cards: FitCard[], dropped: string[], gap?: number, grid?: number, lines?: LinesById | null,
  view?: { x: number; y: number; w: number; h: number } | null): Record<string, { x: number; y: number }>;
export declare const ROOM_PER_LINE: number;
export declare const ROOM_MARKER: number;
export declare const ROOM_EDGE: number;
export declare const ROOM_MIN: number;
export declare function roomBetween(kA: number, kB: number): number;
export declare function makeRoom(cards: FitCard[], lines: LinesById, grid?: number, keep?: Set<string> | null, extra?: Map<string, number> | null): Record<string, { x: number; y: number }>;
export declare const ROOM_PASSING: number;
export declare const ROOM_CLEAR: number;
export declare function gapLoads(cards: FitCard[], routes: Map<string, { pts: { x: number; y: number }[] | null }>): Map<string, number>;
export declare function roomForLoads(loads: Map<string, number>, lines: LinesById, axisOf: (pair: string) => 'x' | 'y', prev?: Map<string, number> | null): Map<string, number>;
export declare const ROOM_ROUNDS: number;
/** One line and the card sides its two ends sit on (frac: how far along the side, 0 to 1). */
export interface RoomLink { id: string; from: { id: string; side: string; frac: number }; to: { id: string; side: string; frac: number }; }
export declare function roomSteps(cards: FitCard[], links: RoomLink[], lines: LinesById, grid?: number, keep?: Set<string> | null): Generator<unknown, Record<string, { x: number; y: number }>>;
export declare function keepBeside(cards: FitCard[], linksFor: (boxes: FitCard[]) => RoomLink[], lines: LinesById, grid?: number, keep?: Set<string> | null): Generator<unknown, Set<string>>;
export declare function linesBySide(anchors: { side: string }[] | undefined): SideLines;
export declare function toggleExpansion(expanded: Record<string, boolean>, name: string): Record<string, boolean>;
export declare function setExpansionFor(expanded: Record<string, boolean>, names: string[], on: boolean): Record<string, boolean>;
export declare function recordsHeight(expanded: boolean, filter: string): boolean;
export declare function filterHeldHeight(expanded: boolean, filter: string, held: number | undefined): number | undefined;
export declare const FIRST_FIT_MIN_ZOOM: number;
export declare function readableFirstView(bounds: { x: number; y: number; width: number; height: number }, width: number, height: number,
  fittedZoom: number, pad: number, minZoom?: number): { x: number; y: number; zoom: number } | null;
export declare function changedPositions(before: Record<string, { x: number; y: number }>, after: Record<string, { x: number; y: number }>): Record<string, { x: number; y: number }>;
export declare function layoutBox(anchorBox: FitSize, measured?: { width?: number; height?: number }, open?: boolean): FitSize;
export declare function boxChanges(cards: { id: string; w: number; h: number }[], last: Map<string, FitSize> | null): { prev: Record<string, FitSize>; changed: boolean };
export declare function isLayoutChange(was: { graph: unknown; activeId: string; membershipKey: string; layout: string } | null, now: { graph: unknown; activeId: string; membershipKey: string; layout: string }): boolean;
export declare function sizesToCompare(was: { graph: unknown; activeId: string; membershipKey: string; layout: string } | null, now: { graph: unknown; activeId: string; membershipKey: string; layout: string }, last: Map<string, FitSize>): Map<string, FitSize>;
export declare function isArrangeChange(was: { graph: unknown; activeId: string; membershipKey: string; layout: string } | null, now: { graph: unknown; activeId: string; membershipKey: string; layout: string }): boolean;
/** True when an announced change can change what the Diagram draws: a structure, add or remove delta, or a walk of the timeline (undo or redo). */
export declare function changeReloadsDiagram(n: { replay?: string; deltas?: { kind: string }[] } | null | undefined): boolean;
/** A card as the spread takes it: its SAVED spot, the size it is drawn at, its closed box and whether it is open. */
export interface SpreadCard { id: string; x: number; y: number; w: number; h: number; bw: number; bh: number; open: boolean; }
export declare function spreadStart(cards: SpreadCard[], keep?: Set<string> | null, gap?: number): { needed: boolean; grown: Record<string, { x: number; y: number }> };
export declare function spreadKey(cards: { id: string; w: number; h: number; open: boolean }[], extra?: string): string;
export declare function savedAfterDrag(saved: { x: number; y: number }, drawnFrom: { x: number; y: number }, droppedAt: { x: number; y: number }): { x: number; y: number };
/** The one holder of every card's saved spot while a spread is drawn; only a page-level event releases it (round 11). */
export interface SavedSpots {
  hold(saved: Record<string, { x: number; y: number }>): void;
  peek(): Record<string, { x: number; y: number }> | null;
  amend(part: Record<string, { x: number; y: number }>): void;
  release(): Record<string, { x: number; y: number }> | null;
}
export declare function savedSpots(): SavedSpots;
