export interface HeldEntry<TDraft = unknown> {
  name: string;
  title: string;
  creating: boolean;
  view?: string;
  draft: TDraft;
  at?: number;
}

export function heldKey(name: string, creating: boolean, path?: string | null): string;
export function heldForFile<TDraft = unknown>(
  store: { get(key: string): HeldEntry<TDraft> | null },
  name: string,
  path: string | null | undefined,
): HeldEntry<TDraft> | null;
export type HeldPlace = { kind: 'model'; folder: string } | { kind: 'new'; file: string };
export function heldPlaces(entries: ReadonlyArray<HeldEntry & { key: string }>): Map<string, HeldPlace | null>;
export function heldStateFrom<T>(own: T | null | undefined, kept: T | null | undefined): T | Record<string, never>;
export function draftShape(draft: { sourceText?: string; operations?: unknown[] } | null | undefined): string;
export function worthHolding(
  draft: { sourceText?: string; operations?: unknown[] } | null | undefined,
  state: { creating: boolean; dirty: boolean; seedShape: string | null },
): boolean;
export function createHeldDrafts<TDraft = unknown>(deps?: {
  load?: () => Record<string, HeldEntry<TDraft>> | null | undefined;
  save?: (value: Record<string, HeldEntry<TDraft>>) => void;
  now?: () => number;
}): {
  hold(key: string, entry: HeldEntry<TDraft>): void;
  drop(key: string): void;
  get(key: string): HeldEntry<TDraft> | null;
  list(): (HeldEntry<TDraft> & { key: string })[];
};
