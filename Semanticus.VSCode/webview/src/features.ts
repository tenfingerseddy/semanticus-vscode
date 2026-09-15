// The Free and Pro line, by FEATURE (Kane, 2026-09-15). Pro is four whole features, not a list of big
// buttons: if a feature is Pro, its tab is Pro and every tool behind it is Pro, reads included, on both
// doors. Everything else is free, including every one-click bulk apply that used to be the moat.
//
// This file is the page's half of that vocabulary and nothing more. The ENGINE is the authority: it keys
// its own gate on the IEngine method, and it decides what an entitlement grants. The page uses this map to
// choose what to RENDER, so a locked tool shows a preview instead of loading paid data. A page that
// disagreed with the engine would only be wrong on screen; it could never open a door the engine shut.

export type ProFeature = 'modelCreate' | 'tests' | 'publishedAdvanced' | 'workflows';

/** The four, in the order the entitlement lists them. An entitlement that carries no features array at all
 *  falls back to this whole list for a paid tier, because a token minted before the claim existed must not
 *  quietly become a new, emptier product tier. */
export const PRO_FEATURES: ProFeature[] = ['modelCreate', 'tests', 'publishedAdvanced', 'workflows'];

/** Which tool belongs to which feature. A tool that is not here is FREE, and that is the whole rule.
 *  `deploy` is deliberately absent: Publish, Promote, compare and restore points stay free, and only the
 *  Advanced mode inside that page is gated, by deploy.tsx itself. `dataagent` is the legacy route to that
 *  same Advanced mode, so it is gated here as well and has no side door. */
export const FEATURE_OF_TOOL: Record<string, ProFeature> = {
  spec: 'modelCreate',
  advmodels: 'modelCreate',
  mcode: 'modelCreate',
  docs: 'modelCreate',
  knowledge: 'modelCreate',
  tests: 'tests',
  evidence: 'tests',
  dataagent: 'publishedAdvanced',
  workflows: 'workflows',
};

/** The one place a caller asks "is this tool gated". Nobody rebuilds the map. */
export function featureOfTool(tool: string): ProFeature | null {
  return FEATURE_OF_TOOL[tool] ?? null;
}

/** What the feature is called on screen. Plain words, the same words the tabs use. */
export const FEATURE_NAME: Record<ProFeature, string> = {
  modelCreate: 'Create in Model',
  tests: 'Tests',
  publishedAdvanced: 'Advanced publishing',
  workflows: 'Workflows',
};

/** What a person on the free plan still has. Said on every preview, so a locked page is never a dead end. */
export const FEATURE_STILL_FREE: Record<ProFeature, string> = {
  modelCreate: 'Editing measures, columns and relationships stays free.',
  tests: 'Model quality and AI understanding still check your whole model for free.',
  publishedAdvanced: 'Publish, Promote, compare and restore points stay free.',
  workflows: 'The app still shows any run already going and anything waiting for you.',
};
