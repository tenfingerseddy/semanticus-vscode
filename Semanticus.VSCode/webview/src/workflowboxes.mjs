// Boxes view helpers for the workflow author surface (T2369 / T2395 repair).
// The Outline designer edits a Draft and emits markdown. That emitter is lossy.
// Any file field the Draft/emitter does not keep must stay on the loss list, and
// a present provenance key (including an empty value) blocks the emitter.

export const EMITTER_FRONTMATTER_KEYS = ['name', 'title', 'description', 'version', 'strictness', 'triggers'];
export const EMITTER_STEP_KEYS = ['title', 'instructions', 'ops', 'strictness', 'inputs', 'verify'];
export const EMITTER_INPUT_KEYS = ['name', 'question', 'type', 'required'];
export const EMITTER_VERIFY_KEYS = ['kind', 'when', 'probe', 'scope', 'intent'];

export const EMITTER_GATE_KEYS = ['strictness', 'inputs', 'verify'];

export const LOSSY_DEF_KEYS = ['provenance', 'schemaVersion', 'kind', 'whenToUse', 'tags', 'slots'];
export const LOSSY_STEP_KEYS = ['when', 'forEach', 'call', 'hasExplicitId'];

// The whole supported gate contract, in file order, from Semanticus.Engine/Workflow.cs (`GateInput`,
// `VerifySpec`). Recorded so the emitter-kept lists above can be checked to be a SUBSET of what a valid
// v1 file may carry. The loss check below does not read these lists: it works from the keys actually
// present on the definition, so a field added to the engine before Outline learns it is still caught.
export const CONTRACT_INPUT_KEYS = ['name', 'question', 'type', 'required', 'daxPurity', 'scope'];
export const CONTRACT_VERIFY_KEYS = [
  'kind', 'when', 'probe', 'scope', 'intent',
  'pinnedShapes', 'openShapes', 'openShapesFrom', 'openMismatch', 'anchors',
];

// A field can be PRESENT and still say exactly what its absence says. Treating those as losses would push
// every ordinary v1 file onto read-only Boxes, because the engine serialises its defaults. Null/undefined,
// an empty string, an empty list and an empty bag are default everywhere; a field whose default has a NAME
// declares it here. GateInput.Scope: "Null (or 'iteration') is the default"; VerifySpec.PinnedShapes /
// OpenShapes: "Both empty (the default)".
const INPUT_DEFAULTS = { scope: 'iteration' };
const VERIFY_DEFAULTS = {};

function isSemanticDefault(key, value, named) {
  if (value == null) return true;
  if (typeof value === 'string') return value.length === 0 || named[key] === value;
  if (Array.isArray(value)) return value.length === 0;
  if (typeof value === 'object') return Object.keys(value).length === 0;
  return false;                                  // a number or boolean the emitter cannot keep is a loss
}

// Every key present on `obj` that the emitter does not write, minus the ones holding their default.
// Key-set driven on purpose: a hand-maintained list of lost fields silently falls behind the contract,
// which is exactly how daxPurity and pinnedShapes were being saved away.
function droppedFieldReasons(obj, keptKeys, named, label) {
  if (obj == null || typeof obj !== 'object' || Array.isArray(obj)) return [];
  return Object.keys(obj)
    .filter((key) => !keptKeys.includes(key) && !isSemanticDefault(key, obj[key], named))
    .map((key) => `${label} ${key}`);
}

function gateReasons(gate, n) {
  if (gate == null || typeof gate !== 'object' || Array.isArray(gate)) return [];
  const reasons = droppedFieldReasons(gate, EMITTER_GATE_KEYS, {}, `step ${n} gate`);
  (Array.isArray(gate.inputs) ? gate.inputs : []).forEach((input, k) => {
    const who = input?.name || `#${k + 1}`;
    reasons.push(...droppedFieldReasons(input, EMITTER_INPUT_KEYS, INPUT_DEFAULTS, `step ${n} input ${who}`));
  });
  (Array.isArray(gate.verify) ? gate.verify : []).forEach((v, k) => {
    const who = v?.kind || `#${k + 1}`;
    reasons.push(...droppedFieldReasons(v, EMITTER_VERIFY_KEYS, VERIFY_DEFAULTS, `step ${n} verify ${who}`));
  });
  return reasons;
}

function provenanceReasons(prov) {
  if (prov == null || typeof prov !== 'object' || Array.isArray(prov)) return [];
  // Key-set presence, not a truthy value: an empty string still blocks.
  return Object.keys(prov).map((key) => `provenance key ${key}`);
}

export function lossyReasons(def) {
  if (!def) return [];
  const reasons = [];
  reasons.push(...provenanceReasons(def.provenance));
  if (def.schemaVersion != null && def.schemaVersion !== 1) reasons.push('schemaVersion');
  if (typeof def.kind === 'string' && def.kind.length > 0 && def.kind !== 'workflow') reasons.push('kind');
  if (typeof def.whenToUse === 'string' && def.whenToUse.length > 0) reasons.push('whenToUse');
  if (Array.isArray(def.tags) && def.tags.length > 0) reasons.push('tags');
  if (Array.isArray(def.slots) && def.slots.length > 0) reasons.push('slots');
  (def.steps ?? []).forEach((step, i) => {
    const n = step.number ?? i + 1;
    if (typeof step.when === 'string' && step.when.length > 0) reasons.push(`step ${n} when`);
    if (step.forEach) reasons.push(`step ${n} forEach`);
    if (step.call) reasons.push(`step ${n} call`);
    if (step.hasExplicitId) reasons.push(`step ${n} explicit id`);
    reasons.push(...gateReasons(step.gate, n));
  });
  return reasons;
}

export function isLossy(def) {
  return lossyReasons(def).length > 0;
}

function forEachView(spec) {
  if (!spec) return null;
  const fromList = Array.isArray(spec.inLiteral) ? spec.inLiteral.join(', ') : '';
  const fromInput = spec.inInput ? `inputs.${spec.inInput}` : '';
  return {
    in: fromList || fromInput,
    as: spec.as ?? '',
    maxIterations: spec.maxIterations ?? 25,
  };
}

function callView(spec) {
  if (!spec) return null;
  return {
    workflow: spec.workflow ?? '',
    with: spec.with && typeof spec.with === 'object' ? spec.with : {},
    returns: Array.isArray(spec.returns) ? spec.returns : [],
  };
}

export function boxesView(def) {
  const steps = (def?.steps ?? []).map((step, i) => {
    const explicit = !!step.hasExplicitId;
    return {
      id: step.id || `step-${i + 1}`,
      order: i,
      number: step.number ?? i + 1,
      title: step.title ?? '',
      instructions: step.instructions ?? '',
      ops: step.ops ?? [],
      when: typeof step.when === 'string' && step.when.length > 0 ? step.when : null,
      idKind: explicit ? 'explicit' : 'positional',
      idLabel: explicit ? `id: ${step.id}` : `position ${i + 1}`,
      forEach: forEachView(step.forEach),
      call: callView(step.call),
    };
  });
  return {
    steps,
    connectors: 'file-order-arrows',
    notRunMeaning: 'Not a run. Arrows follow file order. A box with no run mark has not been started, skipped, or failed; this view does not run workflows.',
    lossy: lossyReasons(def),
  };
}
