export interface WorkflowDocument {
  name: string;
  library: 'user' | 'stock';
  path: string;
  exactText: string;
  byteHash: string;
  metadata: {
    schemaVersion: number; title: string; version: number; stepIds: string[]; explicitIds: boolean;
    parses: boolean; parseError?: string | null;
  };
}
export interface WorkflowDocumentEditResult {
  name: string; changed: boolean; reason?: string | null; byteHash: string; document: WorkflowDocument;
}
export interface DocumentBuffer { base: WorkflowDocument; latest: WorkflowDocument; text: string }
export interface WorkflowUpgradeResult {
  name: string; dryRun: boolean; changed: boolean; canApply: boolean; reason?: string | null;
  document: WorkflowDocument; proposedText: string | null; diff: string | null; parseError?: string | null;
  addedLines: number;
}
export function canPreviewUpgrade(buffer: DocumentBuffer | null): boolean;
export function canApplyUpgrade(buffer: DocumentBuffer | null, preview: WorkflowUpgradeResult | null): boolean;
export function receiveDocument(previous: DocumentBuffer | null | undefined, document: WorkflowDocument): DocumentBuffer;
export function rebaseDocument(previous: DocumentBuffer | null, reviewed: WorkflowDocument): DocumentBuffer;
export function acceptDocumentSave(previous: DocumentBuffer, document: WorkflowDocument, submittedText: string, changed: boolean): DocumentBuffer;
export function applyTextareaChange(exactText: string, nextValue: string): string;
export function createDocumentLoader(options: {
  read: (name: string) => Promise<WorkflowDocument>;
  onDocument: (document: WorkflowDocument) => void;
  onError: (error: unknown) => void;
}): { load: (name: string) => Promise<WorkflowDocument | null>; invalidate: () => void };
