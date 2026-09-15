export interface WorkflowEditRestriction { target: string; field?: string | null; code: string; message: string }
export interface WorkflowEditMapEntry { key: string; name: string; value: string }
export interface WorkflowEditSlot { key: string; fingerprint: string; name: string; question: string; type: string; required: string; default?: string | null; example?: string | null; hint?: string | null; values: string[] }
export interface WorkflowEditInput { key: string; fingerprint: string; name: string; question: string; type: string; required: string; daxPurity?: string | null; scope?: string | null }
export interface WorkflowEditVerify { key: string; fingerprint: string; kind: string; when?: string | null; probe?: string | null; scope?: string | null; intent?: string | null; pinnedShapes: string[]; openShapes: string[]; openShapesFrom?: string | null; openMismatch?: string | null; anchors?: string | null }
export interface WorkflowEditStep {
  key: string; fingerprint: string; id: string; idKind: 'explicit-stable' | 'explicit-positional' | 'implicit-positional'; number: number; title: string; instructions: string; ops: string[];
  when?: string | null;
  forEach?: { source: { kind: 'literal' | 'input'; values?: string[]; name?: string | null }; as: string; maxIterations: number } | null;
  call?: { workflow: string; with: WorkflowEditMapEntry[]; returns: string[] } | null;
  gate?: { strictness?: string | null; inputs: WorkflowEditInput[]; verify: WorkflowEditVerify[] } | null;
}
export interface WorkflowEditModel {
  format: 'v1' | 'v2';
  definition: { key: string; schemaVersion: number; name: string; kind: string; title: string; description: string; whenToUse?: string | null; version: number; strictness?: string | null; triggers: string[]; tags: string[]; provenance: WorkflowEditMapEntry[]; slots: WorkflowEditSlot[]; steps: WorkflowEditStep[] };
  restrictions: WorkflowEditRestriction[];
  choices: { strictness: string[]; kinds: string[]; inputTypes: string[]; slotTypes: string[]; inputRequired: string[]; inputScopes: string[]; daxPurity: string[]; slotRequired: string[]; verifyKinds: string[]; verifyScopes: string[]; verifyIntents: string[]; shapeIds: string[]; openMismatch: string[]; defaultMaxIterations: number; maxIterations: number };
}
export interface WorkflowDocument { name: string; library: 'user' | 'stock'; path: string; exactText: string; byteHash: string; metadata: { schemaVersion: number; title: string; version: number; stepIds: string[]; explicitIds: boolean; parses: boolean; parseError?: string | null }; editModel?: WorkflowEditModel | null }
export interface WorkflowEditIssue { code: string; severity: 'error' | 'warning' | 'info'; target?: string | null; field?: string | null; relatedTargets: string[]; message: string }
export interface WorkflowEditPreview { name: string; outcome: 'ready' | 'no_change' | 'conflict' | 'invalid' | 'unpreservable' | 'stock_read_only'; canApply: boolean; requiresReview: boolean; reason?: string | null; document?: WorkflowDocument | null; proposedText?: string | null; proposedByteHash?: string | null; diff?: string | null; editModel?: WorkflowEditModel | null; issues: WorkflowEditIssue[]; keyChanges: { oldKey: string; newKey: string; oldStepId?: string | null; newStepId?: string | null }[]; suggested_next_action?: { op: string; args: Record<string, unknown>; why: string } | null }
export interface WorkflowDraftSnapshot { sourceText: string; editModel: WorkflowEditModel | null; operations: WorkflowEditOperation[]; label: 'workflow draft' }
export type WorkflowEditOperation = Record<string, unknown> & { op: string };
export interface WorkflowDefinitionDraft { base: WorkflowDocument; latest: WorkflowDocument; sourceText: string; editModel: WorkflowEditModel | null; operations: WorkflowEditOperation[]; undo: WorkflowDraftSnapshot[]; redo: WorkflowDraftSnapshot[]; preview: WorkflowEditPreview | null; sessionId: string | null }
export function createWorkflowDraft(document: WorkflowDocument, sessionId?: string | null): WorkflowDefinitionDraft;
export function workflowDraftDirty(draft: WorkflowDefinitionDraft | null): boolean;
export function receiveWorkflowDocument(previous: WorkflowDefinitionDraft | null, document: WorkflowDocument, sessionId?: string | null): WorkflowDefinitionDraft;
export function applyWorkflowOperation(draft: WorkflowDefinitionDraft, operation: WorkflowEditOperation, structural?: boolean): WorkflowDefinitionDraft;
export function editWorkflowSource(draft: WorkflowDefinitionDraft, sourceText: string): WorkflowDefinitionDraft;
export function installWorkflowPreview(draft: WorkflowDefinitionDraft, preview: WorkflowEditPreview, keepUndo?: boolean): WorkflowDefinitionDraft;
export function setWorkflowPreview(draft: WorkflowDefinitionDraft, preview: WorkflowEditPreview | null): WorkflowDefinitionDraft;
export function undoWorkflowDraft(draft: WorkflowDefinitionDraft): WorkflowDefinitionDraft;
export function redoWorkflowDraft(draft: WorkflowDefinitionDraft): WorkflowDefinitionDraft;
export function discardWorkflowDraft(draft: WorkflowDefinitionDraft, document?: WorkflowDocument): WorkflowDefinitionDraft;
export function acceptWorkflowSave(draft: WorkflowDefinitionDraft, document: WorkflowDocument): WorkflowDefinitionDraft;
export function workflowPreviewArgs(draft: WorkflowDefinitionDraft, create?: boolean): unknown[];
export function restrictionFor(model: WorkflowEditModel | null, target: string, field?: string | null): WorkflowEditRestriction | null;
export function structuredUnavailable(model: WorkflowEditModel | null): string | null;
export function newTempKey(): string;
export function uniqueStepId(title: string, model: WorkflowEditModel | null): string;
export function boxesFromEditModel(model: WorkflowEditModel | null, titleOf?: (name: string) => string): import('./workflowboxes.mjs').BoxesStepView[];
export interface OpCatalogEntry { name: string; description?: string | null; question?: string | null; shelf?: string | null }
export interface OpCatalogChoice { name: string; label: string; description: string }
export interface OpCatalogShelf { shelf: string; ops: OpCatalogChoice[] }
export interface OpCatalogQuestion { question: string; count: number; shelves: OpCatalogShelf[] }
export interface OpCatalogGrouping { total: number; first: string | null; questions: OpCatalogQuestion[] }
export const UNFILED_OPS: string;
export function groupOpCatalog(catalog: OpCatalogEntry[] | null | undefined, options?: { exclude?: string[]; filter?: string; present?: (op: OpCatalogEntry) => { label?: string; description?: string } }): OpCatalogGrouping;
export const INSTRUCTIONS_WORD_BUDGET: number;
export function countWords(text?: string | null): number;
export function mapLayoutPositions(positions: Record<string, { x: number; y: number }>, keyChanges: WorkflowEditPreview['keyChanges']): Record<string, { x: number; y: number }>;
