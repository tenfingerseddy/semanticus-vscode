// The Free and Pro line, by FEATURE. Kane set it on 2026-09-15 as four features, and narrowed it to ONE on
// 2026-09-22 (docs/redesign/ui-hierarchy.md, his answer 3): free is the tool, Pro is the tool that works
// for you. Everything a person can do by hand, or with their own assistant, is free. Advanced modelling,
// Validation, Reports, Model Spec, Docs, Model notes, Power Query and the advanced Publish tools were all
// paid before that date and are free from it. What stays paid is the product DECIDING and DOING: the plan
// ranked, scored and run, Build from a source, Fix in one click, the proposed defaults, the ask box, and
// Workflows as a whole.
//
// This file is the page's half of that vocabulary and nothing more. The ENGINE is the authority: it keys
// its own gate on the IEngine method, and it decides what an entitlement grants. The page uses this map to
// choose what to RENDER, so a locked tool shows a preview instead of loading paid data. A page that
// disagreed with the engine would only be wrong on screen; it could never open a door the engine shut.

// The words box on the Query page (it began as the Ask your data page, 26 September 2026) is Pro. The engine reports it as the
// wire id askData (ProFeatures.WireId). The page itself, with Build and DAX, is free.
export type ProFeature = 'workflows' | 'askData';

/** What a paid entitlement carries. get_entitlement.features will list exactly this (the engine lane).
 *  An entitlement with no features array at all falls back to this whole list for a paid tier, because a
 *  token minted before the claim existed must not quietly become a new, emptier product tier. */
export const PRO_FEATURES: ProFeature[] = ['workflows', 'askData'];

/** Features that USED to be Pro and are free from 22 September 2026.
 *
 *  They are a type and not a map on purpose: a caller that still asks about one gets 'granted', which is
 *  the honest answer, instead of a compile error in a lane that does not own the file. Every name here is
 *  a deletion waiting to happen; remove it the moment its last caller stops asking. Nothing in this list
 *  can ever gate anything, because FEATURE_OF_TOOL below is the only gate and it names none of them. */
export type RetiredProFeature = 'modelCreate' | 'tests' | 'publishedAdvanced';
export const RETIRED_FEATURES: RetiredProFeature[] = ['modelCreate', 'tests', 'publishedAdvanced'];
export function isRetiredFeature(feature: string): feature is RetiredProFeature {
  return (RETIRED_FEATURES as string[]).includes(feature);
}

/** Which tool belongs to which feature. A tool that is not here is FREE, and that is the whole rule.
 *  One entry: Workflows is the only whole tab that is paid. The Query page is free; only its Words mode is Pro (feature askData). `deploy` is free and so is its Advanced mode; `dataagent` is
 *  the legacy route into that same Advanced mode and is free with it. */
export const FEATURE_OF_TOOL: Record<string, ProFeature> = {
  workflows: 'workflows',
};

/** The one place a caller asks "is this tool gated". Nobody rebuilds the map. */
export function featureOfTool(tool: string): ProFeature | null {
  return FEATURE_OF_TOOL[tool] ?? null;
}

/** What the feature is called on screen. Plain words, the same words the tabs use. */
export const FEATURE_NAME: Record<ProFeature, string> = {
  workflows: 'Workflows',
  askData: 'Asking in words',
};

/**
 * Does this tier get the Plan RANKED, SCORED and RUN? (Kane, 21 September, docs/redesign/ui-hierarchy.md
 * item 3: "Free users see the plan's gaps in rule order with a do-it-by-hand jump on each; Pro ranks,
 * scores and runs them.")
 *
 * This one is keyed on the TIER and NOT on a second ProFeature, and that is not a shortcut. The engine
 * gates the ranking on Entitlement.FeatureGrants.IsProTier (Semanticus.Engine/LocalEngine.Plan.cs:842),
 * because a registered decision point is not a Pro tab. get_entitlement.features only ever carries the
 * wire ids of the paid TABS, so an id here would answer "denied" for a paying Pro customer and the page
 * would show them an upsell for something they had already bought.
 *
 * Three answers, because unknown is a real state and not a loading detail: before the first
 * get_entitlement lands, neither the ranked page nor the upsell is the honest thing to draw.
 *
 * Pure, so the page's tests can run it without React. The caller passes what useTier() gave it.
 */
export type PlanRankingGrant = 'unknown' | 'granted' | 'denied';
export function planRankingOf(tier: string): PlanRankingGrant {
  if (tier === 'unknown') return 'unknown';
  return tier === 'pro' ? 'granted' : 'denied';
}

/** What a person on the free plan still has. Said on every preview, so a locked page is never a dead end. */
export const FEATURE_STILL_FREE: Record<ProFeature, string> = {
  workflows: 'The app still shows any run already going and anything waiting for you.',
  askData: 'Build and DAX on the Query page still run any query you write, and your own assistant can still write and run one for you.',
};
