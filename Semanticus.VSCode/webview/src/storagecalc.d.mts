// Type surface for the framework-free Storage-tab decision module (storagecalc.mjs). The snapshot
// shape is owned by App.tsx (StorageSnap); these functions only read the fields declared here, so the
// module stays a plain ES module the node contract test can execute while the webview keeps typing.

/** The fields the comparison decisions read from a snapshot (App.tsx's StorageSnap satisfies this). */
export interface SnapCoverage {
  known: number;
  attributed: number;
  scannedColumns?: number;
  fingerprintKey?: string | null;
}

export type CompareDecision =
  | { available: false; reason: 'none' | 'fingerprint' }
  | { available: true; structureChanged: boolean; componentsComparable: boolean };

export type StorageMode = 'import' | 'directLake' | 'unknown';
export type ScanIdentityTransition = 'current' | 'pending' | 'resolved' | 'swap';
export type StorageEvidenceLevel = 'currentEditingModel' | 'staleQueryCopy' | 'none';

export function anchorOf(queryEndpoint: string | null | undefined, queryDatabase: string | null | undefined, source: string | null | undefined): string | null;
export function normalizeStorageMode(mode: string | null | undefined): StorageMode;
export function snapKey(anchor: string, storageMode: StorageMode): string;
export function scanMatchesAnchor(scanIdentity: string | null, currentAnchor: string | null): boolean;
export function scanIdentityTransition(scanIdentity: string | null, previousAnchor: string | null, currentAnchor: string | null): ScanIdentityTransition;
export function scanUsableDuringTransition(scanIdentity: string | null, previousAnchor: string | null, currentAnchor: string | null): boolean;
export function storageEvidenceLevel(relationship: string | null | undefined): StorageEvidenceLevel;
export function relationshipAllowsStorageEdits(relationship: string | null | undefined): boolean;
export function relationshipAllowsReverifiedDeletePlan(relationship: string | null | undefined): boolean;
export function coverageBasis(snap: SnapCoverage | null | undefined): string | null;
export function compareDecision(cur: SnapCoverage | null | undefined, prev: SnapCoverage | null | undefined): CompareDecision;
export function shouldStoreAsLast(next: SnapCoverage | null | undefined): boolean;
export function resolvedClaimAllowed(cur: SnapCoverage | null | undefined, prev: SnapCoverage | null | undefined): boolean;
export function introducedClaimAllowed(prev: SnapCoverage | null | undefined): boolean;
export function refIsAmbiguous(table: string | null | undefined, column: string | null | undefined): boolean;
export function identifierLike(name: string | null | undefined, isKey: boolean): boolean;
