import { createContext, useContext } from 'react';

export type DescriptionDrafts = Map<string, { text: string; base: string }>;
export const newDescriptionDrafts = (): DescriptionDrafts => new Map();

/** What the marks screens need from the page they sit in. Studio passes its bridge; the Properties grid passes its
 *  own message channel, because a page may take the VS Code API only once and the grid already holds it. */
export interface MarksHost {
  /** The page keeps unsaved Descriptions for its model when a measure detail closes. */
  descriptionDrafts?: DescriptionDrafts;
  /** What stops scoring now, when the page already read it (the Checks page). Without it a card reads the page itself. */
  unscored?: string | null;
  rpc<T = unknown>(method: string, ...args: unknown[]): Promise<T>;
  /** Called when the model changes, so a panel reads its marks and Description again. */
  onChange(fn: () => void): () => void;
  /** Go somewhere a check is set up: Connections for a SQL source, or Checks for a measure. 'advice' is the Turn on press of
   *  Pro advice for this model (the permission card); `then` runs once it is on, so the card can check again. */
  open(where: 'connections' | 'checks' | 'advice', ref?: string, then?: () => void): void;
  undo(): void;
  /** The engine's checks/didChange push: the check states that changed, each with its version. A host without it reads again on a change. */
  onCheckState?(fn: (change: unknown) => void): () => void;
  /** Open the measure's formula to fix it (the Filters "Fix the formula" action). */
  editFormula?(ref: string): void;
}

export const MarksHostContext = createContext<MarksHost | null>(null);
export function useMarksHost(): MarksHost {
  const host = useContext(MarksHostContext);
  if (!host) throw new Error('The marks panel needs a host.');
  return host;
}
