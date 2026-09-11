export function createDefinitionLoader<TDef>(deps: {
  rpc: (method: string, ...args: unknown[]) => Promise<TDef>;
  setDef: (def: TDef | null) => void;
  currentSelection: () => string | null;
}): {
  loadForSelection(name: string | null): () => void;
  loadAfterNotification(): void;
  reload(name: string | null): void;
};

export function onDefinitionArrived<TDef>(
  state: { creating: boolean; dirty: boolean },
  def: TDef | null,
): { liveDef: TDef | null; reseedDraft: boolean };
