// The Workflows page's fake engine (lane W2), for tools/uishot/workflows-drive.mjs.
//
// It answers the workflow calls the one page makes the way the engine does, as a function of each call's
// params and of what earlier calls did, so a click's effect is measured and not assumed: Use copies a starter
// into your workflows (instantiate_workflow_template), Run starts a run whose Plan row walks the steps
// (start_workflow, submit_workflow_step, skip_workflow_step), the finished run lands in History as a run entry
// (get_plan_history), and Make it reusable saves a copy that the list marks "From a run" (save_workflow). The
// harness's own Author machinery (projectedWorkflowDocument, previewWorkflowEdit) reads the same two objects this
// file hands it as window.__UISHOT__.workflows2 and .workflowDefs, so there is one library, not two.
//
// Placeholder names only (Contoso). Nothing here came from a real tenant.
//
// Every function is plain JavaScript with no imports: the driver inlines this file into the page, because a
// function does not survive JSON.

const info = (o) => Object.assign({ description: '', version: 1, source: 'stock', stepCount: 0, gated: true, triggers: [], error: null,
  enabled: true, active: true, activeReason: null, required: false, replacesRequired: false }, o, { starter: (o.source || 'stock') === 'stock' });

// The edit model and saved document the engine sends for a definition (WorkflowDocument.cs), in the shape the
// harness's own projection builds, so a save hands Author back a document it can keep editing.
const CHOICES = {
  strictness: ['hard', 'warn', 'off'], kinds: ['workflow', 'template'],
  inputTypes: ['verification', 'text', 'enum', 'number', 'objectRef', 'planItem'], slotTypes: ['verification', 'text', 'enum', 'number', 'objectRef', 'planItem'],
  inputRequired: ['answer-or-decline', 'required', 'optional'], inputScopes: ['iteration', 'run'], daxPurity: ['no-bare-measures'], slotRequired: ['required', 'optional'],
  verifyKinds: ['dax_probe', 'dax_equivalence', 'bpa_clean'], verifyScopes: ['object', 'model'], verifyIntents: ['change', 'rename', 'remove', 'restructure'],
  shapeIds: ['grand_total'], openMismatch: ['countersign'], defaultMaxIterations: 100, maxIterations: 1000,
};
function editModelOf(def) {
  const steps = (def.steps || []).map((step, index) => {
    const key = 'step:' + step.id;
    const gate = step.gate ? { strictness: step.gate.strictness || null,
      inputs: (step.gate.inputs || []).map((i, n) => ({ key: key + '/input:' + i.name, fingerprint: 'input-' + n, name: i.name, question: i.question || '', type: i.type || 'text', required: i.required || 'answer-or-decline', daxPurity: null, scope: null })),
      verify: (step.gate.verify || []).map((v, n) => ({ key: key + '/verify:' + v.kind + ':' + n, fingerprint: 'verify-' + n, kind: v.kind, when: null, probe: null, scope: null, intent: null, pinnedShapes: [], openShapes: [], openShapesFrom: null, openMismatch: null, anchors: null })) } : null;
    return { key, fingerprint: 'step-' + index, id: step.id, number: index + 1, idKind: 'explicit-stable', title: step.title || '', instructions: step.instructions || '', ops: step.ops || [], when: null, forEach: null, call: null, gate };
  });
  return { format: 'v2', restrictions: [], choices: JSON.parse(JSON.stringify(CHOICES)), definition: { key: 'workflow', schemaVersion: 2, name: def.name, kind: 'workflow', title: def.title, description: def.description || '',
    whenToUse: def.whenToUse || null, version: def.version || 1, strictness: def.strictness || null, triggers: def.triggers || [], tags: def.tags || [], provenance: [], slots: [], steps } };
}
function documentOf(def, n) {
  const exactText = ['---', 'schemaVersion: 2', 'name: ' + def.name, 'title: ' + def.title, 'version: ' + (def.version || 1), '---', ''].join(String.fromCharCode(10));
  return { name: def.name, library: def.source || 'stock', path: (def.source === 'user' ? '/project/.semanticus/workflows/' : '/engine/workflows/') + def.name + '.md',
    exactText, byteHash: 'sha256:' + String(n || 0).padStart(64, '0'), editModel: editModelOf(def),
    metadata: { schemaVersion: 2, title: def.title, version: def.version || 1, stepIds: (def.steps || []).map((x) => x.id), explicitIds: true, parses: true, parseError: null } };
}

const guided = (id, n, title, instructions, extra) => Object.assign({ id, number: n, title, instructions, ops: [], hasExplicitId: true, performer: 'assistant' }, extra || {});

export function makeWorkflowsEngine(opts) {
  const o = opts || {};
  const now = o.now || new Date();
  const iso = (d) => new Date(d).toISOString();
  const day = (n) => iso(new Date(now.getTime() - n * 86400000));

  const LIST = [
    info({ name: 'verified-measure', title: 'Test a complex measure', description: 'Check a hard measure against numbers you already trust, at every level.', whenToUse: 'Check a hard measure against numbers you already trust, at every level.', version: 7, stepCount: 4 }),
    info({ name: 'optimize-dax', title: 'Fix a slow measure', whenToUse: 'Try a faster formula and keep it only if every number still matches.', stepCount: 4 }),
    info({ name: 'deploy-to-production', title: 'Publish changes', whenToUse: 'Preview what will change, check the release window, then publish.', stepCount: 4 }),
    info({ name: 'governed-rename', title: 'Rename with impact review', whenToUse: 'See every report and measure a rename touches before it happens.', version: 2, stepCount: 5 }),
    info({ name: 'secure-with-rls', title: 'Set up data access rules', whenToUse: 'Decide who sees which rows, then test allowed and denied people.', stepCount: 5 }),
    info({ name: 'check-blast-radius', title: 'Review a risky change', whenToUse: 'Assess a risky change and record what remains, without applying it.', stepCount: 4 }),
    // The three stock workflows that wait on the describe-it test (contract 21.2). new-measure and model-hygiene-pass
    // were drawn here until lane W3 parked them; the stock list is now the nine that ship.
    info({ name: 'incremental-refresh-setup', title: 'Set up rolling refresh', whenToUse: 'Check folding, set a rolling window, and inspect the partitions.', stepCount: 4 }),
    info({ name: 'reconcile-saved-tests-sql', title: 'Reconcile saved tests with SQL', whenToUse: 'Compare saved checks with SQL and list what does not match.', stepCount: 3 }),
    info({ name: 'refactor-to-calculation-group', title: 'Replace repeated measures with a calculation group', whenToUse: 'Turn a family of look-alike measures into one calculation group.', stepCount: 5 }),
    // Yours.
    info({ name: 'test-a-complex-measure', title: 'Test a complex measure', description: 'The team copy, with Return rate as the example.', source: 'user', stepCount: 4, version: 2 }),
    info({ name: 'import-returns', title: 'Import Returns and relate it to Sales and Date', source: 'user', stepCount: 5 }),
    info({ name: 'publish-to-test', title: 'Publish to the test workspace with checks', source: 'user', stepCount: 3 }),
    info({ name: 'old-hygiene', title: 'Weekly tidy (older format)', source: 'user', stepCount: 2 }),
  ];
  if (o.empty) for (let i = LIST.length - 1; i >= 0; i--) if (LIST[i].source === 'user') LIST.splice(i, 1);
  // offWithRule: a workflow turned off that still has a saved "Only when" rule. The engine hides the rule's reason
  // while it is off (ResolveActivation answers "turned off for this project" first) and shows it again once it is
  // turned back on, until the rule is cleared.
  const OFF_REASON = 'turned off for this project';
  const RULE_REASON = 'shown by a project rule while its condition holds';
  if (o.offWithRule) { const w = LIST.find((x) => x.name === 'test-a-complex-measure'); w.enabled = false; w.active = false; w.activeReason = OFF_REASON; w.rule = 'model.tableCount < 50'; }

  const DEFS = {
    'test-a-complex-measure': {
      name: 'test-a-complex-measure', title: 'Test a complex measure', description: 'The team copy, with Return rate as the example.',
      version: 2, schemaVersion: 3, source: 'user', triggers: [], strictness: 'hard', tags: [],
      assistantNotes: 'House style for every part your assistant writes here. Measure names in sentence case. Descriptions say what the number means to a finance reader.',
      inputs: [{ name: 'measure', question: 'Measure name', type: 'text', by: 'you' }, { name: 'trusted', question: 'Numbers you already trust', type: 'text', by: 'you' }],
      provenance: { template: 'verified-measure' },
      steps: [
        { id: 'define', number: 1, title: 'Define the measure', instructions: 'Name the measure and say which numbers you already trust.', ops: [], hasExplicitId: true, performer: 'person', phase: 'change',
          questions: [{ name: 'name', question: 'What should the measure be called?', type: 'text', by: 'you' }] },
        { id: 'write', number: 2, title: 'Write the formula', instructions: 'Adds one measure to the Returns folder with the name and format you choose. Nothing is added until the checks in step 3 pass.',
          ops: [], hasExplicitId: true, performer: 'semanticus', phase: 'change', action: { op: 'create_measure' },
          questions: [{ name: 'format', question: 'How should it be shown?', type: 'enum', by: 'you', values: ['Percent, 1 decimal', 'Whole number'] }],
          blanks: [{ name: 'formula', kind: 'dax', fills: 'expression', by: 'assistant' }],
          brief: 'Compare returned units to units sold for the same period. Count each return once, even when it has several refund lines.',
          checks: [{ kind: 'valid_dax', phase: 'written' }, { kind: 'refs_within', phase: 'written', of: ['Returns', 'Sales'] }, { kind: 'result_range', phase: 'after', min: '0', max: '1' }] },
        { id: 'hard-cases', number: 3, title: 'Check the hard cases', instructions: 'Compare the new measure with the numbers you trust at every level.', ops: [], hasExplicitId: true, performer: 'semanticus', phase: 'change', action: { op: 'verify_dax_equivalence' } },
        { id: 'report', number: 4, title: 'Report what was checked', instructions: 'Export the report of what was checked.', ops: [], hasExplicitId: true, performer: 'semanticus', phase: 'after', action: { op: 'export_workflow_evidence' } },
      ],
    },
    'import-returns': {
      name: 'import-returns', title: 'Import Returns and relate it to Sales and Date', description: 'Made from a run on 24 September.', version: 1, schemaVersion: 3, source: 'user', triggers: [],
      provenance: { derived_from: 'wfr-2' },
      steps: ['Import the Returns table', 'Relate Returns to Sales', 'Relate Returns to Date', 'Set data types on 4 columns', 'Load the Returns data'].map((t, i) => (
        { id: `s${i + 1}`, number: i + 1, title: t, instructions: '', ops: [], hasExplicitId: true, performer: 'semanticus', phase: i === 4 ? 'after' : 'change', action: { op: 'create_import_table' } })),
    },
    'publish-to-test': {
      name: 'publish-to-test', title: 'Publish to the test workspace with checks', description: '', version: 1, schemaVersion: 3, source: 'user', triggers: [],
      steps: [
        { id: 'preview', number: 1, title: 'Preview what changes', instructions: '', ops: [], hasExplicitId: true, performer: 'semanticus', phase: 'change', action: { op: 'preview_deploy' } },
        { id: 'window', number: 2, title: 'Confirm the release window', instructions: '', ops: [], hasExplicitId: true, performer: 'person', phase: 'change', questions: [{ name: 'ok', question: 'Is the release window open?', by: 'you' }] },
        { id: 'publish', number: 3, title: 'Publish', instructions: '', ops: [], hasExplicitId: true, performer: 'semanticus', phase: 'after', action: { op: 'deploy_stage' } },
      ],
    },
    'old-hygiene': {
      name: 'old-hygiene', title: 'Weekly tidy (older format)', description: 'A format 1 file.', version: 1, schemaVersion: 1, source: 'user', triggers: [],
      steps: [guided('step-1', 1, 'Scan the model', 'Run the scan.', { gate: { strictness: 'hard', inputs: [], verify: [{ kind: 'bpa_clean' }] } }), guided('step-2', 2, 'Save', 'Save the model.')],
    },
    'optimize-dax': {
      name: 'optimize-dax', title: 'Fix a slow measure', description: 'Freeze a measure, compare a rewrite across useful contexts, and keep it only when the evidence supports it.', version: 1, source: 'stock', triggers: [],
      steps: [
        guided('freeze', 1, 'Freeze the measure', 'Record its current numbers.', { gate: { strictness: 'hard', inputs: [{ name: 'measure', question: 'Which measure is slow?', type: 'objectRef', required: 'required' },
          // F12: an answer Semanticus reads as data; the run names it in assistantFills, as WorkflowRunner.AssistantFills does.
          { name: 'equivalenceGrid', question: "Machine field: the comma-separated qualified display columns, for example 'Date'[Year].", type: 'text', required: 'optional' }],
          verify: [], assistantFills: ['equivalenceGrid'] } }),
        guided('baseline', 2, 'Record the baseline', 'Benchmark it.', { gate: { strictness: 'hard', inputs: [], verify: [{ kind: 'dax_probe' }] } }),
        guided('rewrite', 3, 'Write a faster formula', 'Rewrite it.', {}),
        guided('compare', 4, 'Keep it only if every number matches', 'Compare.', { gate: { strictness: 'hard', inputs: [], verify: [{ kind: 'dax_equivalence' }] } }),
      ],
    },
    'verified-measure': {
      name: 'verified-measure', title: 'Test a complex measure', description: 'Define the hard cases for a measure, compare a candidate with independent expected values, and report checked and unresolved cases.', version: 7, source: 'stock', triggers: [],
      steps: ['Fix the specification', 'Lock the expected values', 'Write the candidate', 'Reconcile and finish'].map((t, i) => guided(`step-${i + 1}`, i + 1, t, '', { gate: { strictness: 'hard', inputs: [{ name: `a${i}`, question: 'Record it', type: 'text', required: 'answer-or-decline' }], verify: [] } })),
    },
  };
  for (const w of LIST) if (!DEFS[w.name]) DEFS[w.name] = { name: w.name, title: w.title, description: w.whenToUse || '', version: w.version, source: w.source, triggers: [],
    steps: Array.from({ length: w.stepCount }, (_, i) => guided(`step-${i + 1}`, i + 1, `Step ${i + 1} of ${w.title}`, '', {})) };

  const TEMPLATES = [
    { name: 'month-end-close', title: 'Month-end close for {{period_label}}', whenToUse: 'Refresh, reconcile to the source and sign off the finance numbers.', version: 1, source: 'stock', slotCount: 2, slots: ['period_label', 'finance_owner'] },
    { name: 'hard-measure', title: 'Author "{{measure_goal}}" and reconcile it at every grain', whenToUse: 'A measure where a wrong number would be costly.', version: 5, source: 'stock', slotCount: 2, slots: ['measure_goal', 'grain'] },
  ];
  const TEMPLATE_DEFS = {
    'month-end-close': { name: 'month-end-close', title: 'Month-end close for {{period_label}}', source: 'stock', markdown: '', slots: [
      { name: 'period_label', question: 'Which period is this for?', type: 'text', required: 'required', example: 'September 2026' },
      { name: 'finance_owner', question: 'Who signs off the numbers?', type: 'text', required: 'optional', example: 'Finance lead', hint: 'A role, not a person.' },
    ] },
  };

  const policy = {
    activeProfile: o.rules ? 'team-standard' : 'standard', enforcement: o.checksOff ? 'off' : null,
    workflows: LIST.map((w) => ({ name: w.name, enabled: w.enabled, active: w.active, activeReason: null, gated: true, requiredForOps: [] })),
    bindings: [], lints: [],
  };
  const requireIt = (op, names, mode) => {
    const b = policy.bindings.find((x) => x.op === op);
    if (!names.length || mode === 'off') { policy.bindings = policy.bindings.filter((x) => x.op !== op); }
    else if (b) { b.require = names; b.mode = mode; } else policy.bindings.push({ op, require: names, mode, userDisablable: true });
    for (const w of policy.workflows) w.requiredForOps = policy.bindings.filter((x) => x.require.includes(w.name)).map((x) => x.op);
    for (const w of LIST) w.required = policy.workflows.find((p) => p.name === w.name).requiredForOps.length > 0;
  };
  if (o.rules) requireIt('create_measure', ['test-a-complex-measure'], 'hard');
  let enforcement = { mode: o.checksOff ? 'off' : 'default', enforced: !o.checksOff, note: null };
  const profiles = () => [
    { name: 'standard', title: 'No team rules', description: 'Nothing is required.', effects: ['Clears every required workflow.'], pro: false, selected: policy.activeProfile === 'standard' },
    { name: 'team-standard', title: 'Team standard', description: 'New measures are tested first.', effects: ['New measures go through Test a complex measure, flagged when skipped.'], pro: true, selected: policy.activeProfile === 'team-standard' },
    { name: 'consulting-delivery', title: 'Consulting delivery', description: 'New measures and relationships go through their workflows.', effects: ['New measures go through Test a complex measure.', 'New relationships go through Connect tables.'], pro: true, selected: false },
  ];

  // ---- runs, History ----
  const HISTORY = [
    { id: 'h-run-1', kind: 'run', when: day(1), by: 'workflow', title: 'Import Returns and relate it to Sales and Date', report: { runId: 'wfr-2', workflow: 'import-returns', title: 'Import Returns and relate it to Sales and Date', status: 'completed', verdict: 'Verified', steps: [] } },
    { id: 'h-run-2', kind: 'run', when: day(2), by: 'workflow', title: 'Test a complex measure', report: { runId: 'wfr-1', workflow: 'test-a-complex-measure', title: 'Test a complex measure', status: 'completed', verdict: 'Unknown', steps: [] } },
  ];
  const RUNS = {};
  let nextRun = 7;
  const runView = (r) => JSON.parse(JSON.stringify(r));
  const newRun = (name) => {
    const def = DEFS[name];
    const steps = def.steps.map((s) => ({ stepId: s.id, title: s.title, status: 'pending', answers: {}, verifyResults: [], note: null, effectiveStrictness: 'hard' }));
    steps[0].status = 'in_progress';
    const r = { runId: `wfr-${nextRun++}`, workflow: name, title: def.title, workflowVersion: def.version || 1, status: 'active', startedUtc: iso(now), modelName: 'Contoso Retail',
      stepIndex: 0, totalSteps: steps.length, steps, currentStep: current(def, 0) };
    RUNS[r.runId] = r; return r;
  };
  function current(def, i) {
    const s = def.steps[i];
    const q = (s.gate && s.gate.inputs) || [];
    const fills = (s.gate && s.gate.assistantFills) || null;
    return { stepId: s.id, title: s.title, instructions: s.instructions || '', ops: s.ops || [], questions: q, verifyKinds: ((s.gate && s.gate.verify) || []).map((v) => v.kind), effectiveStrictness: 'hard',
      ...(fills ? { assistantFills: fills } : {}) };
  }
  const advance = (r, status, note) => {
    const def = DEFS[r.workflow];
    r.steps[r.stepIndex].status = status; r.steps[r.stepIndex].note = note || null;
    r.stepIndex++;
    if (r.stepIndex >= r.totalSteps) {
      r.status = 'completed'; r.currentStep = null; r.finishedUtc = iso(now);
      HISTORY.unshift({ id: `h-${r.runId}`, kind: 'run', when: iso(now.getTime() + 60000), by: 'workflow', title: r.title,
        report: { runId: r.runId, workflow: r.workflow, title: r.title, status: 'completed', verdict: r.steps.every((s) => s.status === 'passed') ? 'Verified' : 'Unknown', steps: r.steps.map((s) => ({ stepId: s.stepId, title: s.title, outcome: s.status, submittedBy: 'you' })) } });
    } else { r.steps[r.stepIndex].status = 'in_progress'; r.currentStep = current(def, r.stepIndex); }
    return runView(r);
  };

  const DRAFTS = {};
  const calls = [];
  const note = (name, params) => calls.push({ call: name, params });
  const fail = (message) => ({ __uishotError: message });
  const LIST_FAILS = "Required workflow 'verified-measure' is pinned to stock revision 'sha256:unavailable', but that revision is unavailable. Restore the shipped workflow compatibility files or select the binding/profile again.";

  const handlers = {
    // UAT-S02-j3-004: the engine could not read the library (a required workflow pinned to a revision this install
    // no longer ships took every read down with it).
    listWorkflows: () => o.listFails ? fail(LIST_FAILS) : LIST.slice(),
    getWorkflow: (p) => DEFS[p[0]] || fail(`Workflow '${p[0]}' not found.`),
    listWorkflowTemplates: () => TEMPLATES,
    getWorkflowTemplate: (p) => TEMPLATE_DEFS[p[0]] || { name: p[0], slots: [], markdown: '' },
    getWorkflowPolicy: () => o.policyFails ? fail(LIST_FAILS) : JSON.parse(JSON.stringify(policy)),
    getWorkflowEnforcement: () => enforcement,
    setWorkflowEnforcement: (p) => { note('setWorkflowEnforcement', p); enforcement = { mode: p[0], enforced: p[0] !== 'off', note: null }; policy.enforcement = p[0] === 'off' ? 'off' : null;
      // UAT-S02-2-004 / j7-004: the setting was written, then the call failed on its way back.
      return o.enforcementFailsAfterWrite ? fail(LIST_FAILS) : enforcement; },
    setWorkflowBinding: (p) => { note('setWorkflowBinding', p); requireIt(p[0], p[1] || [], p[2]); return LIST.slice(); },
    setWorkflowEnabled: (p) => { note('setWorkflowEnabled', p); const w = LIST.find((x) => x.name === p[0]);
      if (w) { w.enabled = p[1]; if (w.rule !== undefined) { w.active = !!p[1]; w.activeReason = !p[1] ? OFF_REASON : w.rule ? RULE_REASON : null; } }
      return LIST.slice(); },
    setWorkflowActivation: (p) => { note('setWorkflowActivation', p); const w = LIST.find((x) => x.name === p[0]);
      if (w && w.rule !== undefined) w.rule = p[1] || null;
      if (w && w.enabled === false) return LIST.slice();
      if (w) { if (!p[1]) { w.active = true; w.activeReason = null; } else { w.active = p[2] === 'on'; w.activeReason = p[2] === 'on' ? 'shown by a project rule while its condition holds' : 'hidden by a project rule'; } }
      return LIST.slice(); },
    listWorkflowProfiles: () => profiles(),
    activateWorkflowProfile: (p) => { note('activateWorkflowProfile', p); policy.activeProfile = p[0];
      if (p[0] === 'standard') for (const b of policy.bindings.slice()) requireIt(b.op, [], 'off');
      if (p[0] === 'team-standard') requireIt('create_measure', ['test-a-complex-measure'], 'warn');
      return { activeProfile: p[0], workflows: LIST.slice(), note: `Team rules applied: ${profiles().find((x) => x.name === p[0]).title}.` }; },
    instantiateWorkflowTemplate: (p) => {
      note('instantiateWorkflowTemplate', p);
      const src = p[0]; const tmpl = TEMPLATE_DEFS[src];
      const w = LIST.find((x) => x.name === src);
      if (w && w.required) return [w];
      const name = p[1] || src;
      if (LIST.some((x) => x.name === name && x.source === 'user')) return fail(`A workflow named '${name}' already exists. Pick a different name.`);
      const values = p[2] ? JSON.parse(p[2]) : {};
      const title = tmpl ? tmpl.title.replace('{{period_label}}', values.period_label || '...') : (w ? w.title : name);
      const base = DEFS[src] || { steps: [guided('step-1', 1, 'Refresh', ''), guided('step-2', 2, 'Reconcile to the source', ''), guided('step-3', 3, 'Sign off', '')] };
      DEFS[name] = Object.assign(JSON.parse(JSON.stringify(base)), { name, title, source: 'user', provenance: { template: src } });
      const made = info({ name, title, source: 'user', stepCount: DEFS[name].steps.length });
      const at = LIST.findIndex((x) => x.name === name); if (at >= 0) LIST.splice(at, 1);
      LIST.push(made);
      policy.workflows.push({ name, enabled: true, active: true, activeReason: null, gated: true, requiredForOps: [] });
      window.dispatchEvent(new MessageEvent('message', { data: { type: 'workflowLibraryDidChange', payload: LIST.slice() } }));
      return [made];
    },
    saveWorkflow: (p) => {
      note('saveWorkflow', p);
      const [name, text] = p;
      if (LIST.some((x) => x.name === name && x.source === 'user')) return fail(`A workflow named '${name}' already exists. It was not overwritten.`);
      const title = (/^title: (.*)$/m.exec(text) || [])[1];
      const run = (/derived_from: (.*)$/m.exec(text) || [])[1];
      const from = run && Object.values(RUNS).find((r) => r.runId === run.trim());
      const fromDraft = run && DRAFTS[run.trim()];
      const base = from ? DEFS[from.workflow] : fromDraft && DEFS[fromDraft.source] ? DEFS[fromDraft.source] : { steps: [], schemaVersion: 3 };
      DEFS[name] = Object.assign(JSON.parse(JSON.stringify(base)), { name, title: title ? JSON.parse(title.startsWith('"') ? title : JSON.stringify(title)) : name, source: 'user', provenance: run ? { derived_from: run.trim() } : {} });
      LIST.push(info({ name, title: DEFS[name].title, source: 'user', stepCount: DEFS[name].steps.length }));
      policy.workflows.push({ name, enabled: true, active: true, activeReason: null, gated: true, requiredForOps: [] });
      window.dispatchEvent(new MessageEvent('message', { data: { type: 'workflowLibraryDidChange', payload: LIST.slice() } }));
      return { name };
    },
    editWorkflowDocument: (p) => {
      note('editWorkflowDocument', p);
      const [name, , text] = p;
      const title = (/^title: (.*)$/m.exec(text) || [])[1];
      if (title && DEFS[name]) { DEFS[name].title = title.replace(/^"|"$/g, ''); const w = LIST.find((x) => x.name === name); if (w) w.title = DEFS[name].title; }
      const document = documentOf(DEFS[name], calls.length);
      return { name, changed: true, reason: null, byteHash: document.byteHash, document };
    },
    deleteWorkflow: (p) => { note('deleteWorkflow', p); const i = LIST.findIndex((x) => x.name === p[0]); if (i >= 0) LIST.splice(i, 1); return LIST.slice(); },
    checkWorkflow: (p) => { note('checkWorkflow', p); return { name: p[0], findings: [], ok: true }; },
    replayCheckWorkflow: (p) => { note('replayCheckWorkflow', p); return { name: p[0], admissible: true, rehearsed: 3, rehearsedFailed: 0, replaySkipped: false, note: null, rows: [] }; },
    upgradeWorkflow: (p) => { note('upgradeWorkflow', p); return { name: p[0], dryRun: p[1], changed: !p[1], canApply: !!p[1], reason: null, document: { byteHash: 'sha256:up', path: '/project/.semanticus/workflows/' + p[0] + '.md' }, proposedText: '', diff: '+schemaVersion: 2\n+```yaml step\n+id: step-1\n+```', addedLines: 7 }; },
    // runs
    startWorkflow: (p) => { note('startWorkflow', p); return runView(newRun(p[0])); },
    getWorkflowRun: (p) => RUNS[p[0]] ? runView(RUNS[p[0]]) : fail(`Run '${p[0]}' not found.`),
    submitWorkflowStep: (p) => { note('submitWorkflowStep', p); const r = RUNS[p[0]]; const v = advance(r, 'passed'); window.dispatchEvent(new MessageEvent('message', { data: { type: 'workflowDidChange', payload: v } })); return v; },
    skipWorkflowStep: (p) => { note('skipWorkflowStep', p); const r = RUNS[p[0]]; const v = advance(r, 'skipped', p[2]); window.dispatchEvent(new MessageEvent('message', { data: { type: 'workflowDidChange', payload: v } })); return v; },
    abortWorkflow: (p) => { note('abortWorkflow', p); const r = RUNS[p[0]]; r.status = 'aborted'; r.abortReason = p[1]; r.currentStep = null; return runView(r); },
    exportWorkflowEvidence: (p) => { note('exportWorkflowEvidence', p); return { html: '<h1>Workflow report</h1><p>Every step passed.</p>', json: '{}', markdown: '# Workflow report' }; },
    getPlanHistory: () => ({ rows: HISTORY.slice(), more: false }),
    // Describe it in words (Kane's UAT, 25 Sep): the engine cuts the words into steps for Author and stores nothing.
    // The mock knows the words the scenes type; the engine's own matching is held by DescribedStepsTests.
    describeWorkflowSteps: (p) => {
      note('describeWorkflowSteps', p);
      // Kane's own words on Pro once Jev drafts (lane L5), as the engine answers them after Jev chose each step's
      // templates and the engine proved every pick (JevDraftBuilderTests holds the engine side). The sentence is the
      // engine's (DraftCopy.DescribeChosen), never the page's.
      if (/meaure/iu.test(String(p[0] || ''))) return {
        words: p[0], when: null, jevAsked: true, jevNote: null, sentence: '2 steps from your words. Jev chose what each step may use.',
        steps: [
          { title: 'Create a meaure', op: 'create_measure', stepName: 'Add a measure', matched: true, ops: ['create_measure'], performer: 'assistant', target: 'Sales', chosenBy: 'jev', reason: null },
          { title: 'Test its performance', op: 'benchmark_dax', stepName: 'Test performance', matched: true, ops: ['benchmark_dax', 'profile_dax'], performer: 'assistant', target: null, chosenBy: 'jev', reason: null },
        ],
      };
      if (/check its correct/iu.test(String(p[0] || ''))) return {
        words: p[0], when: null, jevAsked: true, jevNote: null,
        sentence: '2 steps from your words. Jev chose what each step may use.',
        steps: [
          { title: 'Refresh the sales model', op: 'refresh_partition', stepName: 'Refresh a table', matched: true, ops: ['refresh_partition'], performer: 'semanticus', target: 'Sales', chosenBy: 'jev', reason: null },
          { title: 'Check its correct', op: 'run_tests', stepName: 'Run the saved checks', matched: true, ops: ['run_tests'], performer: 'semanticus', target: null, chosenBy: 'jev', reason: null },
        ],
      };
      const known = { refresh: 'refresh_partition', check: 'run_tests', publish: 'deploy_stage', hide: 'set_column_hidden' };
      let when = null;
      const steps = String(p[0] || '').split(/[.;\n]+|,|\b(?:then|and)\b/iu).map((c) => c.trim()).filter(Boolean).flatMap((c) => {
        const lead = /^every\s+\w+/iu.exec(c);
        if (lead) { when = when || lead[0]; c = c.slice(lead[0].length).trim(); }
        if (!c) return [];
        const hit = Object.keys(known).find((k) => new RegExp('(^|\\W)' + k, 'iu').test(c));
        return [{ title: c[0].toUpperCase() + c.slice(1), op: hit ? known[hit] : null, matched: !!hit }];
      });
      const open = steps.filter((s) => !s.matched).length;
      return { words: p[0], steps, when, sentence: `${steps.length} steps from your words${open ? `; ${open} to fill in.` : '.'}` };
    },
    // drafts
    composeTask: (p) => {
      note('composeTask', p);
      // Words alone: the menu draft (lane L5a), no steps until one is picked, the engine's line in place of Jev.
      if (p[0] && !p[1]) {
        const m = { draftId: 'dr-5e6f7a8b', title: p[0], words: p[0], source: 'From your words', state: 'needs_you', planWord: 'needs_you', planNote: 'Pick the steps.', approved: false,
          summary: 'Pick the steps. The ones your words name come first.', menuLine: 'Pick the steps. The ones your words name come first.', notes: [], questions: [], blanks: [], steps: [], after: [],
          menu: [{ id: 'refresh', title: 'Refresh a table', changes: 'Loads 1 table', matched: true }, { id: 'publish', title: 'Publish the model', changes: 'Publishes 1 model', matched: true }, { id: 'describe', title: 'Describe an object', changes: 'Sets 1 description', matched: false }],
          undoSteps: 0, partsWaiting: 0, sentence: 'Drafted. Nothing has changed yet.' };
        DRAFTS[m.draftId] = m; return JSON.parse(JSON.stringify(m));
      }
      const d = { draftId: 'dr-0a1b2c3d', title: DEFS[p[1]] ? DEFS[p[1]].title : 'Draft', source: p[1], state: 'needs_you', planWord: 'needs_you', planNote: 'Answer the questions, then it runs by itself.', approved: false,
        summary: 'You answer 2 questions and your assistant writes 1 part, then it runs by itself.', notes: ['1 check happens after it runs; if it fails, one Undo takes the change back.'],
        questions: [{ name: 'name', question: 'What should the measure be called?', type: 'text', by: 'you' }, { name: 'format', question: 'How should it be shown?', type: 'enum', by: 'you', values: ['Percent, 1 decimal', 'Whole number'] }],
        blanks: [{ step: 2, stepId: 'write', name: 'formula', kind: 'dax', state: 'empty', line: 'Waiting to be written', attemptsLeft: 3 }],
        steps: [{ id: 'define', number: 1, title: 'Define the measure', changes: 'Asks 1 question', tags: ['you'], kept: true },
          { id: 'write', number: 2, title: 'Add the measure Return rate', object: 'Return rate', changes: 'Adds 1 measure; Sets 1 format', tags: ['you', 'asst'], check: { phase: 'Checked once written', text: 'A valid formula against the model as the draft leaves it.' }, kept: true, status: 'Waiting' },
          { id: 'hard-cases', number: 3, title: 'Check the hard cases', changes: 'Checks 24 numbers', tags: [], kept: true }],
        after: [{ id: 'report', number: 4, title: 'Report what was checked', changes: 'Writes the report', tags: [], kept: true, status: 'After the change' }],
        undoSteps: 1, partsWaiting: 0, sentence: 'Drafted. Nothing has changed yet.' };
      DRAFTS[d.draftId] = d; return JSON.parse(JSON.stringify(d));
    },
    getDraft: (p) => DRAFTS[p[0]] ? JSON.parse(JSON.stringify(DRAFTS[p[0]])) : fail('No draft.'),
    editDraft: (p) => {
      note('editDraft', p);
      const d = DRAFTS[p[0]];
      if (p[1] === 'answer') { const q = d.questions.find((x) => x.name === p[4]); if (q) q.answer = p[6]; }
      if (p[1] === 'write') { const b = d.blanks.find((x) => x.name === p[3]); if (b) { b.state = 'self'; b.text = p[6]; b.line = `Written by you, checked: ${p[6]}`; b.filledBy = 'you'; } }
      if (p[1] === 'drop' || p[1] === 'keep') { const s = d.steps.concat(d.after).find((x) => x.id === p[2]); if (s) s.kept = p[1] === 'keep'; }
      if (d.questions.every((q) => q.answer) && d.blanks.every((b) => b.state === 'self' || b.state === 'filled')) {
        d.state = 'ready'; d.planWord = 'ready'; d.summary = 'Ready. Press Approve and run, or tell your assistant to run it.';
      }
      return JSON.parse(JSON.stringify(d));
    },
    runDraft: (p) => { note('runDraft', p); const d = DRAFTS[p[0]]; d.approved = true;
      if (d.state === 'ready') { d.state = 'done'; d.planWord = 'done'; d.summary = 'Done. One Undo takes back steps 1 to 3.'; d.notes = ['1 check ran after it; it passed.'];
        d.record = { blanks: [{ blank: 'Add the measure Return rate: formula', filledBy: 'You, with Write it yourself', checks: 'Passed its checks' }] }; }
      return JSON.parse(JSON.stringify(d)); },
    approveDraft: (p) => handlers.runDraft(p),
    addPlanRepeat: (p) => { note('addPlanRepeat', p); return {}; },
  };
  return { handlers, calls, LIST, DEFS, RUNS, DRAFTS, HISTORY };
}
