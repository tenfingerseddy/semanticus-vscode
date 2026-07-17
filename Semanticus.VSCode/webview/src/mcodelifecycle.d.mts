export type EditorRevision = { text: string; original: string };
export type ReconcileResult =
  | { kind: 'unchanged'; text: string; original: string; profileInvalidated: false }
  | { kind: 'reloaded'; text: string; original: string; serverText: null; profileInvalidated: true }
  | { kind: 'conflict'; text: string; original: string; serverText: string; profileInvalidated: true };

export interface RequestGate {
  begin(): number;
  cancel(): void;
  isCurrent(request: number): boolean;
}

export interface ProfilePartition {
  name?: string | null;
  sourceType?: string | null;
  source?: string | null;
}

export interface ProfileExpression {
  name?: string | null;
  kind?: string | null;
  expression?: string | null;
}

export type BusyOwner<T extends object> = T & { ownerId: number };
export interface BusyOwnerGate<T extends object> {
  begin(operation: T): BusyOwner<T> | null;
  reset(): void;
  current(): BusyOwner<T> | null;
  isOwner(candidate: BusyOwner<T> | null | undefined): boolean;
  release(candidate: BusyOwner<T> | null | undefined): boolean;
}
export type ContextBusyOwner<T extends object> = T & { ownerId: number; contextToken: string };
export interface ContextBusyOwnerGate<T extends object> {
  begin(operation: T, operationContext: string): ContextBusyOwner<T> | null;
  resetContext(nextContext: string): boolean;
  reset(): void;
  current(): ContextBusyOwner<T> | null;
  isOwner(candidate: ContextBusyOwner<T> | null | undefined): boolean;
  release(candidate: ContextBusyOwner<T> | null | undefined): boolean;
}

export function mContextToken(table: string | null | undefined, query: string | null | undefined, revision: number): string;
export function isMContextCurrent(captured: string, current: string): boolean;
export function pollingExpressionForSave(value: string | null | undefined): string | null;
export function serverRevisionMatches(loadedText: string, serverText: string): boolean;
export function reconcileSaveRevision(loadedText: string, savingText: string, serverText: string): 'write' | 'already-saved' | 'conflict';
export function transformErrorKey(table: string | null | undefined, column: string | null | undefined, action: string): string;
export function transformErrorForAction(errors: Record<string, string>, table: string | null | undefined, column: string | null | undefined, action: string): string | null;
export function profileSubjectToken(table: string | null | undefined, partitions: ProfilePartition[] | null | undefined, expressions?: ProfileExpression[] | null): string;
export function reconcileProfileSubject(previousToken: string | null | undefined, table: string | null | undefined, partitions: ProfilePartition[] | null | undefined, expressions?: ProfileExpression[] | null): { token: string; profileInvalidated: boolean };
export function createBusyOwnerGate<T extends object>(): BusyOwnerGate<T>;
export function createContextBusyOwnerGate<T extends object>(initialContext?: string | null): ContextBusyOwnerGate<T>;
export function reconcileExternalM(editor: EditorRevision, serverText: string): ReconcileResult;
export function createRequestGate(): RequestGate;
