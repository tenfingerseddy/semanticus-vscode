const clone = (value) => value == null ? value : structuredClone(value);

const snapshot = (draft) => ({
  sourceText: draft.sourceText,
  editModel: clone(draft.editModel),
  operations: clone(draft.operations),
  label: 'workflow draft',
});

const restore = (draft, value, undo, redo) => ({
  ...draft,
  sourceText: value.sourceText,
  editModel: clone(value.editModel),
  operations: clone(value.operations),
  undo,
  redo,
  preview: null,
});

export function createWorkflowDraft(document, sessionId = null) {
  return {
    base: document,
    latest: document,
    sourceText: document?.exactText ?? '',
    editModel: clone(document?.editModel ?? null),
    operations: [],
    undo: [],
    redo: [],
    preview: null,
    sessionId,
  };
}

export function workflowDraftDirty(draft) {
  return !!draft && (draft.operations.length > 0 || draft.sourceText !== (draft.base?.exactText ?? ''));
}

export function receiveWorkflowDocument(previous, document, sessionId = previous?.sessionId ?? null) {
  if (!previous || !workflowDraftDirty(previous))
    return createWorkflowDraft(document, sessionId);
  return { ...previous, latest: document };
}

function findTarget(definition, key) {
  if (!definition) return null;
  if (definition.key === key) return { kind: 'workflow', value: definition };
  const slot = (definition.slots ?? []).find((item) => item.key === key);
  if (slot) return { kind: 'slot', value: slot, owner: definition.slots };
  for (const step of definition.steps ?? []) {
    if (step.key === key) return { kind: 'step', value: step, owner: definition.steps };
    const input = (step.gate?.inputs ?? []).find((item) => item.key === key);
    if (input) return { kind: 'input', value: input, owner: step.gate.inputs, step };
    const verify = (step.gate?.verify ?? []).find((item) => item.key === key);
    if (verify) return { kind: 'verify', value: verify, owner: step.gate.verify, step };
  }
  return null;
}

function assignField(target, field, value) {
  if (target.kind !== 'step' || !field.includes('.')) {
    target.value[field] = clone(value);
    return;
  }
  const [container, nested] = field.split('.');
  if (container === 'gate') {
    target.value.gate ??= { strictness: null, inputs: [], verify: [] };
    target.value.gate[nested] = clone(value);
  } else {
    if (!target.value[container]) throw new Error(`The ${container} section is not enabled.`);
    target.value[container][nested] = clone(value);
  }
}

function stableId(title, used) {
  const words = String(title ?? '').toLowerCase().match(/[a-z0-9]+/g) ?? [];
  let basis = words.length ? words.join('-') : 'untitled';
  if (!/^[a-z]/.test(basis)) basis = `do-${basis}`;
  let result = basis;
  for (let suffix = 2; used.has(result) || /^step-[0-9]+$/.test(result); suffix++) result = `${basis}-${suffix}`;
  used.add(result);
  return result;
}

function stabilize(model) {
  const steps = model.definition.steps ?? [];
  const used = new Set(steps.filter((step) => step.idKind === 'explicit-stable').map((step) => step.id));
  for (const step of steps) {
    if (step.idKind === 'explicit-stable') continue;
    step.id = stableId(step.title, used);
    step.idKind = 'explicit-stable';
  }
}

function renumber(steps) {
  steps.forEach((step, index) => { step.number = index + 1; });
}

function itemList(target, field) {
  if (target.kind === 'workflow' && field === 'slots') return target.value.slots;
  if (target.kind === 'step' && field === 'gate.inputs') {
    target.value.gate ??= { strictness: null, inputs: [], verify: [] };
    return target.value.gate.inputs;
  }
  if (target.kind === 'step' && field === 'gate.verify') {
    target.value.gate ??= { strictness: null, inputs: [], verify: [] };
    return target.value.gate.verify;
  }
  throw new Error(`The ${field} list cannot be edited here.`);
}

function insertAfter(items, item, after) {
  const index = after == null ? 0 : items.findIndex((value) => value.key === after) + 1;
  if (after != null && index === 0) throw new Error('The insertion point is no longer available.');
  items.splice(index, 0, item);
}

function applyLocal(model, operation) {
  const definition = model?.definition;
  if (!definition) throw new Error('This workflow has no structured edit model.');
  if (operation.op === 'stabilize_step_ids') { stabilize(model); return; }
  if (operation.op === 'set_field') {
    const target = findTarget(definition, operation.target);
    if (!target) throw new Error('The edited field is no longer available.');
    assignField(target, operation.field, operation.value);
    return;
  }
  if (operation.op === 'set_map_entry' || operation.op === 'remove_map_entry') {
    const target = findTarget(definition, operation.target);
    if (!target) throw new Error('The edited entry is no longer available.');
    const list = operation.field === 'provenance' ? target.value.provenance : target.value.call?.with;
    if (!list) throw new Error('The edited map is no longer available.');
    const index = list.findIndex((entry) => entry.name === operation.name);
    if (operation.op === 'remove_map_entry') { if (index >= 0) list.splice(index, 1); return; }
    if (index >= 0) list[index].value = operation.value;
    else list.push({ key: `new:map:${operation.name}`, name: operation.name, value: operation.value });
    return;
  }
  if (operation.op === 'insert_item') {
    const target = findTarget(definition, operation.target);
    if (!target) throw new Error('The edited list is no longer available.');
    insertAfter(itemList(target, operation.field), { key: operation.tempKey, fingerprint: '', ...clone(operation.value) }, operation.after);
    return;
  }
  if (operation.op === 'move_item') {
    const target = findTarget(definition, operation.target);
    if (!target?.owner) throw new Error('The edited item is no longer available.');
    const index = target.owner.indexOf(target.value);
    target.owner.splice(index, 1);
    insertAfter(target.owner, target.value, operation.after);
    return;
  }
  if (operation.op === 'remove_item') {
    const target = findTarget(definition, operation.target);
    if (!target?.owner) throw new Error('The edited item is no longer available.');
    target.owner.splice(target.owner.indexOf(target.value), 1);
    return;
  }
  const steps = definition.steps;
  if (operation.op === 'add_step') {
    insertAfter(steps, {
      key: operation.tempKey, fingerprint: '', idKind: 'explicit-stable', number: 0,
      when: null, forEach: null, call: null, gate: null, ...clone(operation.value),
    }, operation.after);
    renumber(steps);
    return;
  }
  if (operation.op === 'copy_step') {
    const source = steps.find((step) => step.key === operation.target);
    if (!source) throw new Error('The copied step is no longer available.');
    insertAfter(steps, { ...clone(source), key: operation.tempKey, fingerprint: '', id: operation.newId, idKind: 'explicit-stable' }, operation.after);
    renumber(steps);
    return;
  }
  const index = steps.findIndex((step) => step.key === operation.target);
  if (index < 0) throw new Error('The edited step is no longer available.');
  if (operation.op === 'move_step') {
    const [moving] = steps.splice(index, 1);
    insertAfter(steps, moving, operation.after);
    renumber(steps);
    return;
  }
  if (operation.op === 'remove_step') { steps.splice(index, 1); renumber(steps); return; }
  throw new Error(`Unknown workflow draft operation '${operation.op}'.`);
}

function needsStabilization(model) {
  return (model?.definition?.steps ?? []).some((step) => step.idKind !== 'explicit-stable');
}

export function applyWorkflowOperation(draft, operation, structural = false) {
  const editModel = clone(draft.editModel);
  const operations = draft.operations.slice();
  if (structural && needsStabilization(editModel) && !operations.some((item) => item.op === 'stabilize_step_ids')) {
    const stabilizeOperation = { op: 'stabilize_step_ids' };
    operations.unshift(stabilizeOperation);
    applyLocal(editModel, stabilizeOperation);
  }
  operations.push(clone(operation));
  applyLocal(editModel, operation);
  return { ...draft, editModel, operations, undo: [...draft.undo, snapshot(draft)], redo: [], preview: null };
}

export function editWorkflowSource(draft, sourceText) {
  if (sourceText === draft.sourceText) return draft;
  return {
    ...draft, sourceText, editModel: null, operations: [],
    undo: [...draft.undo, snapshot(draft)], redo: [], preview: null,
  };
}

export function installWorkflowPreview(draft, preview, keepUndo = true) {
  const next = {
    ...draft,
    sourceText: preview.proposedText ?? draft.sourceText,
    editModel: clone(preview.editModel ?? null),
    operations: [],
    preview,
    redo: [],
  };
  return keepUndo ? { ...next, undo: [...draft.undo, snapshot(draft)] } : next;
}

export function setWorkflowPreview(draft, preview) { return { ...draft, preview }; }

export function undoWorkflowDraft(draft) {
  if (!draft.undo.length) return draft;
  const value = draft.undo.at(-1);
  return restore(draft, value, draft.undo.slice(0, -1), [snapshot(draft), ...draft.redo]);
}

export function redoWorkflowDraft(draft) {
  if (!draft.redo.length) return draft;
  const [value, ...redo] = draft.redo;
  return restore(draft, value, [...draft.undo, snapshot(draft)], redo);
}

export function discardWorkflowDraft(draft, document = draft.latest) {
  return createWorkflowDraft(document, draft.sessionId);
}

export function acceptWorkflowSave(draft, document) {
  return createWorkflowDraft(document, draft.sessionId);
}

export function workflowPreviewArgs(draft, create = false) {
  return [
    draft.base?.name ?? draft.editModel?.definition?.name,
    create ? null : draft.base?.byteHash,
    create ? null : draft.base?.path,
    JSON.stringify(draft.operations),
    draft.sourceText,
    create,
    draft.sessionId,
  ];
}

export function restrictionFor(model, target, field) {
  return (model?.restrictions ?? []).find((item) => item.target === target && (item.field == null || item.field === field)) ?? null;
}

export function structuredUnavailable(model) {
  if (!model) return 'The source must parse before Steps or Canvas can open.';
  const whole = (model.restrictions ?? []).find((item) => ['parse_error', 'unsupported_version', 'source_map_mismatch'].includes(item.code));
  return whole?.message ?? null;
}

// A draft-only key for an item that does not exist in the saved file yet. The patcher maps it to the
// real key when the preview is applied, so every add control must mint one through here.
export function newTempKey() {
  return `new:${crypto.randomUUID()}`;
}

export function uniqueStepId(title, model) {
  return stableId(title, new Set((model?.definition?.steps ?? []).map((step) => step.id)));
}

export function boxesFromEditModel(model, titleOf = (name) => name) {
  return (model?.definition?.steps ?? []).map((step, order) => ({
    id: step.id,
    key: step.key,
    order,
    number: step.number ?? order + 1,
    title: step.title ?? '',
    instructions: step.instructions ?? '',
    ops: step.ops ?? [],
    when: step.when || null,
    idKind: step.idKind === 'explicit-stable' ? 'explicit' : 'positional',
    idLabel: step.idKind === 'explicit-stable' ? `id: ${step.id}` : `${step.id} (made stable when steps move)`,
    forEach: step.forEach ? {
      in: step.forEach.source?.kind === 'input' ? `inputs.${step.forEach.source.name}` : (step.forEach.source?.values ?? []).join(', '),
      as: step.forEach.as ?? '', maxIterations: step.forEach.maxIterations ?? 0,
    } : null,
    call: step.call ? {
      workflow: titleOf(step.call.workflow) || step.call.workflow,
      with: Object.fromEntries((step.call.with ?? []).map((entry) => [entry.name, entry.value])),
      returns: step.call.returns ?? [],
    } : null,
  }));
}

// The catalog ARRIVES filed and ordered by the engine's OpTaxonomy (question, then shelf, then name),
// so grouping preserves received order at every level and never re-sorts: the order is ratified data.
// An op the taxonomy has not filed yet still has to be reachable, so it lands in UNFILED_OPS, which the
// engine already sorts last. Restored from the 1.1.3 picker (T215) after the 1.2.0 flat list dropped it.
export const UNFILED_OPS = 'Not yet filed';

export function groupOpCatalog(catalog, options = {}) {
  const exclude = new Set(options.exclude ?? []);
  const present = options.present ?? ((op) => ({ label: op.name, description: op.description ?? '' }));
  const needle = (options.filter ?? '').trim().toLowerCase();
  const questions = [];
  const byQuestion = new Map();
  let total = 0;
  let first = null;
  for (const op of catalog ?? []) {
    if (exclude.has(op.name)) continue;
    const shown = present(op);
    const haystack = `${op.name} ${shown.label ?? ''} ${shown.description ?? ''}`.toLowerCase();
    if (needle && !haystack.includes(needle)) continue;
    const questionName = op.question || UNFILED_OPS;
    const shelfName = op.shelf || UNFILED_OPS;
    let question = byQuestion.get(questionName);
    if (!question) { question = { question: questionName, count: 0, shelves: [], byShelf: new Map() }; byQuestion.set(questionName, question); questions.push(question); }
    let shelf = question.byShelf.get(shelfName);
    if (!shelf) { shelf = { shelf: shelfName, ops: [] }; question.byShelf.set(shelfName, shelf); question.shelves.push(shelf); }
    shelf.ops.push({ name: op.name, label: shown.label || op.name, description: shown.description ?? '' });
    question.count += 1;
    total += 1;
    first ??= op.name;
  }
  return { total, first, questions: questions.map(({ question, count, shelves }) => ({ question, count, shelves })) };
}

// A soft budget, never a limit: the same threshold the 1.1.3 designer used, because the agent re-reads
// the instructions on every run and long steps cost context. Warning only; nothing here blocks a save.
export const INSTRUCTIONS_WORD_BUDGET = 150;

export function countWords(text) {
  const trimmed = (text ?? '').trim();
  return trimmed ? trimmed.split(/\s+/).length : 0;
}

export function mapLayoutPositions(positions, keyChanges) {
  const mapped = { ...positions };
  for (const change of keyChanges ?? []) {
    const oldId = change.oldStepId ?? change.oldKey;
    const newId = change.newStepId ?? change.newKey;
    if (!(oldId in mapped)) continue;
    mapped[newId] = mapped[oldId];
    delete mapped[oldId];
  }
  return mapped;
}
