// The model this page is showing, as the ENGINE names it (SessionInfo.ModelIdentity), for the one thing that must not depend on anyone remembering it: every request the page sends
// carries it (bridge.ts rpc), and the engine refuses a write whose page is not on the model that is open (C1, drafts-sol round 2; Semanticus.Engine/ModelFence.cs).
// It has no imports, so the bridge and the draft store can both use it without one importing the other.
let identity: string | undefined;

/** Only an identity in the engine's own shape (`file:`, `live:`, `desktop:`, `unsaved:`) is worth naming: a fallback key built by the page would make the engine refuse every write. Kept equal to src/modelFence.ts. */
export const engineShaped = (key: unknown): string | undefined => (typeof key === 'string' && /^(file|live|desktop|unsaved):/.test(key) ? key : undefined);

export const setPageModelIdentity = (key: unknown): void => { identity = engineShaped(key); };
export const pageModelIdentity = (): string | undefined => identity;
