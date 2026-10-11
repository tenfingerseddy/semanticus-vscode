// Splits: held-out by whole model and by ask family, never by row.
//
// Why never by row: two rows from the same model share almost everything. Splitting by row puts
// near-copies of a held-out question into the development half, the harness tunes against them,
// and the held-out number is then a memory test that reads as a generalisation test. Astra:
// hold out whole models and ask families before tuning.
//
// assignSplit deliberately takes only a model id and a family. It cannot see a row id, so it
// cannot split by one even by accident.

export class SplitError extends Error {
  constructor(message) {
    super(message);
    this.name = 'SplitError';
  }
}

export const MEMBERSHIPS = ['development', 'held-out'];

/**
 * @param {{modelId: string, family: string}} key
 * @param {{heldOutModels: string[], heldOutFamilies: string[]}} policy
 */
export function assignSplit(key, policy) {
  const modelId = key?.modelId;
  const family = key?.family;
  if (typeof modelId !== 'string' || modelId === '') {
    throw new SplitError('assignSplit needs a model id');
  }
  if (typeof family !== 'string' || family === '') {
    throw new SplitError('assignSplit needs an ask family');
  }
  const models = policy?.heldOutModels ?? [];
  const families = policy?.heldOutFamilies ?? [];
  if (!Array.isArray(models) || !Array.isArray(families)) {
    throw new SplitError('split policy needs heldOutModels and heldOutFamilies arrays');
  }
  return (models.includes(modelId) || families.includes(family)) ? 'held-out' : 'development';
}

/**
 * A gate run scores held-out evidence only.
 * Refuses the moment a development fixture turns up in a gate run, naming the fixture, because a
 * gate that quietly includes the fixtures the harness was built against reports a number nobody
 * should trust.
 */
export function assertGateFixtures(records) {
  for (const record of records) {
    if (record?.fixture?.role === 'development') {
      throw new SplitError(
        `a held-out gate run refuses fixture "${record.fixture.id}" because it is marked `
        + 'role "development". Development fixtures are regression evidence, never the gate.');
    }
    if (record?.split?.membership !== 'held-out') {
      throw new SplitError(
        `a held-out gate run refuses record "${record?.recordId}" because its split membership is `
        + `"${record?.split?.membership}", not "held-out".`);
    }
  }
  return true;
}

/**
 * A whole model lands on one side of the split, always, and a family named as held out is held
 * out everywhere. If one model id shows up in both halves, the split was made by row upstream.
 *
 * A family that is NOT in the held-out list can legitimately appear in both halves, because a
 * held-out model carries every family with it. That is why the family check reads the policy
 * instead of just looking for a family in two places.
 *
 * The first check is the one that matters most: the membership on the record has to be the
 * membership the POLICY gives for that model and family. Without it a record simply asserted
 * its own side of the split, and a model reserved for the gate could be scored in the tuning
 * half by relabelling one field.
 */
export function assertSplitConsistency(records, policy) {
  const heldOutFamilies = policy?.heldOutFamilies ?? [];
  const heldOutModels = policy?.heldOutModels ?? [];
  for (const record of records) {
    if (!MEMBERSHIPS.includes(record?.split?.membership)) {
      throw new SplitError(
        `record "${record?.recordId}" carries split membership `
        + `"${record?.split?.membership}", which is not one of ${MEMBERSHIPS.join(', ')}.`);
    }
  }
  const byModel = new Map();
  for (const record of records) {
    const membership = record.split.membership;
    const modelId = record.split.modelId;

    const declared = assignSplit({ modelId, family: record.split.family }, policy);
    if (declared !== membership) {
      throw new SplitError(
        `record "${record.recordId}" carries membership "${membership}", but the split policy `
        + `puts model "${modelId}" and family "${record.split.family}" in the "${declared}" `
        + 'half. The policy decides the split, never the record.'
        + (heldOutModels.includes(modelId)
          ? ` Model "${modelId}" is reserved for the held-out gate.` : ''));
    }

    const seen = byModel.get(modelId);
    if (seen === undefined) byModel.set(modelId, membership);
    else if (seen !== membership) {
      throw new SplitError(
        `model "${modelId}" appears in both the ${seen} and the ${membership} half. Splits are by `
        + 'whole model, so this split was made by row somewhere upstream.');
    }
    if (heldOutFamilies.includes(record.split.family) && membership !== 'held-out') {
      throw new SplitError(
        `family "${record.split.family}" is named held out by the policy but record `
        + `"${record.recordId}" carries membership "${membership}".`);
    }
  }
  return true;
}
