export interface DataAgentElement {
  display_name?: string;
  description?: string;
  type?: string;
  is_selected?: boolean;
  children?: DataAgentElement[];
  [key: string]: unknown;
}

export function parseElementTree(json?: string | null): DataAgentElement[];
export function isEditableElementTree(json?: string | null): boolean;
export function updateElementSelection(json: string, path: number[], selected: boolean): string;
