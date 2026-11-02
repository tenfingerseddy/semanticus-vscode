// The model this page is showing, as the ENGINE names it (SessionInfo.ModelIdentity), for the one thing that must not depend on anyone remembering it: every request the page sends
// carries it (bridge.ts rpc), and the engine refuses a write whose page is not on the model that is open (C1, drafts-sol round 2; Semanticus.Engine/ModelFence.cs).
// It has no imports, so the bridge and the draft store can both use it without one importing the other.
let identity: string | undefined;

/** Only an identity in the engine's own shape (`file:`, `live:`, `desktop:`, `unsaved:`) is worth naming: a fallback key built by the page would make the engine refuse every write. Kept equal to src/modelFence.ts. */
export const engineShaped = (key: unknown): string | undefined => (typeof key === 'string' && /^(file|live|desktop|unsaved):/.test(key) ? key : undefined);

export const setPageModelIdentity = (key: unknown): void => { identity = engineShaped(key); };
export const pageModelIdentity = (): string | undefined => identity;

/** Send ONE request as an action on `key`, the model the action itself just opened; every other request keeps naming the model the page shows. The page names the
 *  new model only once it reads the new session, and an action that opened a model on purpose and then writes for it cannot wait for that: the address form's
 *  environment was refused as a write for the model the page showed before the open (gap 1, fleet6 and fleet8 rechecks). `send` must post at once (bridge.ts rpc
 *  does). The host's twin is rebindAction in src/modelFence.ts. */
export function namingModel<T>(key: unknown, send: () => T): T {
  const was = identity;
  const named = engineShaped(key);
  if (named) identity = named;
  try { return send(); } finally { identity = was; }
}
