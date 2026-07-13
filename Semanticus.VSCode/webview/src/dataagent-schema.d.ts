export interface DataAgentElement {
  display_name?: string;
  type?: string;
  is_selected?: boolean;
  description?: string;
  children?: DataAgentElement[];
  [key: string]: unknown;
}

export function parseElementTree(json: string | null | undefined): DataAgentElement[];
export function isEditableElementTree(json: string | null | undefined): boolean;
export function updateElementSelection(json: string, path: number[], selected: boolean): string;
