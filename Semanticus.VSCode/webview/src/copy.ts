/** Convert an engine identifier or enum value into user-facing words. */
export function uiLabel(value: unknown, fallback = 'Not available'): string {
  const raw = String(value ?? '').trim();
  if (!raw) return fallback;
  const words = raw
    .replace(/[_-]+/g, ' ')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/\s+/g, ' ')
    .trim()
    .split(' ')
    .map((word, index) => /^[A-Z0-9]{2,}$/.test(word)
      ? word
      : index === 0
        ? word.charAt(0).toUpperCase() + word.slice(1).toLowerCase()
        : word.toLowerCase())
    .join(' ');
  return words;
}

/** The five Studio areas. Tool ids stay separate because they are part of the compatibility API. */
export const STUDIO_AREA_LABELS = {
  model: 'Model',
  calc: 'Calculations',
  checks: 'Checks',
  changes: 'Changes',
  workflows: 'Workflows',
} as const;

/** Shared explanations for the four local and remote actions people commonly confuse. */
export const EDIT_ACTION_COPY = {
  apply: 'Apply puts the selected proposed changes into your working model. It creates one undoable edit and does not publish anything.',
  save: 'Save keeps the current edit in your working model or writes the current workflow or spec to its local file, depending on the page. It does not publish to a live destination.',
  publish: 'Publish sends reviewed model changes to the chosen live destination after you review and confirm. It changes model definitions, not data, and does not refresh data.',
  restore: 'Restore, called Roll back in Changes, returns a chosen live destination to a saved restore point after you preview and confirm. Local Undo cannot reverse that remote change.',
} as const;

/** Plain next steps shared by page guides and empty states. */
export const SESSION_STATE_COPY = {
  noModel: {
    title: 'Open a model to begin',
    detail: 'Open a local model or connect to a published model to inspect and edit it.',
    action: 'Open model',
  },
  loading: {
    title: 'Loading the current model',
    detail: 'The page is reading the latest session data. Keep this page open while it loads.',
  },
  empty: {
    title: 'Nothing here yet',
    detail: 'This list has no saved items in the current scope. Use the page action to create one, or run the relevant check.',
    action: 'Add the first one',
  },
  success: {
    title: 'Done',
    detail: 'Read the result before moving on. A local model edit appears under Changes > History. A publish or restore result names the live destination that changed.',
  },
  noLiveConnection: {
    title: 'No live connection',
    detail: 'You can edit model structure and formulas here. Connect a live model to run queries, preview rows, measure performance or test real answers.',
    action: 'Choose a test model',
  },
  staleQuery: {
    title: 'The query model may be behind your edits',
    detail: 'Queries use the selected live model. Save or publish the change, then run the query again when you need current results.',
    action: 'Check Connections',
  },
  noPermission: {
    title: 'You do not have permission for this action',
    detail: 'Check the account and destination, or ask an administrator for access. Your local edits stay in place.',
    action: 'Check Connections',
  },
  error: {
    title: 'Something went wrong',
    detail: 'Read the message, fix the named issue, then try again. If the source changed, reload it before repeating the action.',
    action: 'Try again',
  },
  notChecked: {
    title: 'Not checked',
    detail: 'No check has finished for this item, so there is nothing to rely on yet. Run the relevant check before treating it as evidence.',
    action: 'Run the check',
  },
} as const;

export const ASSISTANT_SYNC_COPY =
  'The VS Code view updates at once. Your assistant sees the change on its next call.';

/**
 * The publish safety check, in one set of words, because Changes > History and Help both have to describe it
 * and they used not to describe it at all. Kane read a stack of red boxes on History that said only
 * "Reason: <something an assistant typed months ago>" and asked "what does all this gate stuff mean?". The
 * words below are the answer, and they are the ONLY place the answer is written.
 *
 * What the check really is, so this copy stays true to it: before a live publish commits, the engine runs the
 * deploy gate (LocalEngine.DeployGateAsync) over the model. The gate passes or it returns a set of blockers.
 * A publish past a returned set of blockers is refused unless a HUMAN writes a reason, and that reason plus
 * the blockers is appended to the model's audit trail before the push. Nothing here may promise more.
 */
export const SAFETY_CHECK_COPY = {
  heading: 'Publishes that went ahead with a red safety check',
  explainer: 'Before a publish, Semanticus checks the model. Green means no blocking problem was found. '
    + 'Red means blocking problems remained, and the publish only went ahead because someone wrote a reason.',
  // Only the block on Changes > History may say "here". Help is not where the reasons are.
  keptHere: 'Those reasons are kept here.',
  helpTail: 'A red check does not stop you. It means someone has to write down why the publish should go '
    + 'ahead anyway, and that reason is kept with the model on Changes > History.',
  pill: 'Red check',
  publishTitle: 'Published with a red safety check',
  promoteTitle: 'Moved to the next stage with a red safety check',
  publishWentAhead: 'The publish went ahead with a written reason.',
  promoteWentAhead: 'The move went ahead with a written reason.',
  found: 'The check found',
  destinationLabel: 'Where it went:',
  helpQuestion: 'What is the safety check?',
} as const;

/** What one red-check publish said, read back out of the sentence the engine recorded with it. */
export interface SafetyCheckOverride {
  /** What the check found, in the engine's own words, with the retired vocabulary glossed. */
  problems: string;
  /** Where the publish went: a live destination, or the two pipeline stages for a promotion. */
  destination: string;
  /** A live publish, or a move between deployment stages. */
  kind: 'publish' | 'promote';
}

// The recorded summary is the ONLY place a red-check publish keeps its destination: the record's
// machine-readable evidence carries the gate result and never the endpoint, so the destination has to come
// back out of the sentence. TWO generations of that sentence exist on real models and both are matched here.
// The first is the wording Kane read off his own audit trail; those records are hash-chained and cannot be
// rewritten after the fact, so the only way they ever read plainly is for this reader to understand them.
const OVERRIDE_SUMMARY_SHAPES: { open: string; mid: string; kind: 'publish' | 'promote' }[] = [
  { open: 'gate RED (', mid: '): override accepted to deploy to ', kind: 'publish' },
  { open: 'gate RED (', mid: '): override accepted to promote ', kind: 'promote' },
  { open: 'Red safety check (', mid: '). Published anyway with a written reason, to ', kind: 'publish' },
  { open: 'Red safety check (', mid: '). Moved to the next stage anyway with a written reason, from ', kind: 'promote' },
];

/** Reads a recorded summary as a red-check publish, or null when it is any other kind of record. */
export function readSafetyCheckOverride(summary: unknown): SafetyCheckOverride | null {
  const text = String(summary ?? '').trim();
  for (const shape of OVERRIDE_SUMMARY_SHAPES) {
    if (!text.startsWith(shape.open)) continue;
    // lastIndexOf, because the gate's own blocker text can contain a closing bracket and a full stop of its
    // own. The tail of the sentence is ours; everything between the first bracket and it belongs to the gate.
    const at = text.lastIndexOf(shape.mid);
    if (at < shape.open.length) continue;
    const destination = text.slice(at + shape.mid.length).trim();
    if (!destination) continue;
    return { problems: plainProblems(text.slice(shape.open.length, at)), destination, kind: shape.kind };
  }
  return null;
}

/**
 * What the check found, in words a person can read. Today's engine already writes this in plain words
 * (Semanticus.Analysis/GateBlockerCopy.cs, which names rules and objects), but records written before that
 * carry the old count phrase, and the old phrase is exactly what Kane read on his own model. The gloss is
 * DISPLAY ONLY: the stored record is hash-chained and is never edited.
 */
export function plainProblems(text: unknown): string {
  const out = String(text ?? '').trim()
    .replace(/(\d+) blocking BPA (?:error|warning|finding)\(s\)/gi,
      (_match, count: string) => count + ' blocking problem' + (count === '1' ? '' : 's'))
    .replace(/\bBPA\b/g, 'model quality');
  if (!out) return 'problems that block a publish.';
  return /[.!?]$/.test(out) ? out : out + '.';
}

// The spellings worth hand-writing, where the generic humaniser reads flat. Everything else falls through
// to uiLabel, so no engine id can reach the audit trail (UX-02 / UX-06, D-209).
const OP_LABEL: Record<string, string> = {
  blame_value: 'What moved this number?',
  apply_plan: 'Plan applied',
  'apply_plan-item': 'Plan item applied',
  optimize_measure: 'Measure optimization',
  set_dax: 'DAX change',
  create_measure: 'New measure',
  create_column: 'New column',
  create_relationship: 'New relationship',
  compare_baseline: 'Baseline comparison',
  deploy_live: 'Live deploy',
  save_model: 'Model saved',
};

/**
 * Plain-English titles for the engine's op ids, so a chip a person reads never prints a raw id.
 * The engine's op catalog (getOpCatalog / OpInfo) carries a one-SENTENCE description, not a title, so
 * the short label lives here and the catalog sentence becomes the chip's tooltip.
 */
const OP_TITLE: Record<string, string> = {
  add_plan_item: 'Stage it now',
  ai_readiness_scan: 'Score AI understanding',
  apply_plan: 'Apply the staged changes',
  apply_safe_fixes: 'Apply the safe fixes',
  bpa_scan: 'Check model quality',
  capture_baseline: 'Save a before picture',
  compare_baseline: 'Compare with the before picture',
  connect_local: 'Connect to an open model',
  create_calculated_column: 'Add a calculated column',
  create_calculation_item: 'Add a calculation item',
  create_column: 'Add a column',
  create_measure: 'Add a measure',
  create_relationship: 'Link two tables',
  create_role: 'Add a viewer role',
  create_table: 'Add a table',
  daxlib_search: 'Search the formula library',
  deploy_live: 'Publish it',
  export_test_report: 'Export the test report',
  export_workflow_evidence: 'Evidence report',
  generate_date_table: 'Add a date table',
  get_doc_section: 'Read part of the write-up',
  get_grounding: 'Read the naming rules',
  get_model_summary: 'Summarise the model',
  get_plan: 'Open the change plan',
  git_commit: 'Save a version',
  impact_assessment: 'Check what this affects',
  lint_dax: 'Check the formula style',
  list_format_templates: 'List the number formats',
  list_measures: 'List the measures',
  list_tables: 'List the tables',
  model_diff: 'Compare two versions',
  open_model: 'Open a model',
  optimize_measure: 'Make a measure faster',
  preview_table: 'Preview the rows',
  probe_measure: 'Try the measure',
  run_dax: 'Run a query',
  run_tests: 'Run the tests',
  save_evidence: 'Save the evidence',
  save_model: 'Save the model',
  save_spec: 'Save the model plan',
  set_ai_instructions: 'Write the notes for AI',
  set_dax: 'Change a formula',
  set_description: 'Write a description',
  set_measure_format: 'Set the number format',
  set_partition_m: 'Change where the data comes from',
  set_plan_item: 'Update a staged change',
  start_workflow: 'Start a workflow',
  undo_change: 'Undo the last change',
  update_measure: 'Edit a measure',
  validate_dax: 'Check the formula works',
};

/** What each workflow check actually proves, in plain words. Engine kinds: WorkflowParser.cs VerifyKinds. */
const CHECK_TITLE: Record<string, string> = {
  anchor_coverage: 'Every named field was covered',
  baseline_captured: 'A before picture was saved',
  baseline_exists: 'A before picture exists',
  baseline_unchanged: 'The before picture still matches',
  benchmark_delta: 'Speed was measured before and after',
  bpa_clean: 'Model quality is clean',
  dax_equivalence: 'Both formulas give the same answers',
  dax_probe: 'The formula ran and returned the expected number',
  expected_values: 'The numbers match what you expected',
  impact_assessment: 'What this affects was checked',
  interview_replay: 'The saved questions were asked again',
  plan_item_applied: 'The staged change was applied',
  plan_item_staged: 'The change was staged for review',
  readiness_rescan: 'AI understanding was scored again',
  tests_replay: 'The saved tests were run again',
  workflow_admissible: 'The workflow file is sound',
};

/** The plain name of a workflow check. */
export function checkTitle(kind: unknown): string {
  const raw = String(kind ?? '').trim();
  if (!raw) return 'A check';
  return CHECK_TITLE[raw] ?? uiLabel(raw, 'A check');
}

/** The plain title for an op id, for anything a person reads. */
export function opTitle(op: unknown): string {
  const raw = String(op ?? '').trim();
  if (!raw) return 'An action';
  return OP_TITLE[raw] ?? uiLabel(raw, 'An action');
}

/** The audit trail's name for a model operation. */
export function opLabel(op: unknown): string {
  const raw = String(op ?? '').trim();
  if (!raw) return 'An edit';
  return OP_LABEL[raw] ?? uiLabel(raw, 'An edit');
}

/**
 * The chords this panel handles itself. VS Code never sees them, so the Keyboard Shortcuts editor cannot
 * rebind them, and the cheat sheet has to say so (UX-04, D-211).
 */
export const PANEL_ONLY_CHORDS =
  '?, Esc, Ctrl+Alt+Z and Ctrl+Alt+Shift+Z (model undo and redo), Ctrl+Alt+T, Shift+Alt+F, and the Ctrl+Alt+letter tab jumps';

// ===================================================================================================
// Named SQL sources: the fourth role in Connections.
//
// Every sentence a person reads on that page lives here, so the Connections hub and the Tests page say
// the same words about the same thing. A source is an address and a way to sign in, saved once. It is
// never a password: nothing in this block offers to keep one, because the engine never holds one.
// ===================================================================================================
export const SQL_SOURCE_COPY = {
  /** The rail label and the page title. */
  title: 'SQL sources',
  /** Under the title. Says what the page buys you, in one sentence. */
  lede: 'Save a server and database once. Then pick it by name in a check or a table mapping, instead of typing the address again.',
  /** The first visit. Ratified sentence, do not reword without Kane. */
  empty: 'No SQL sources yet. Save a server and database once, then choose it from any check.',
  loading: 'Loading your SQL sources...',
  /** Lead line when the list itself could not be read. The reason follows it. */
  loadFailed: 'Your SQL sources could not be loaded.',
  add: 'Add SQL source',
  edit: 'Edit',
  test: 'Test connection',
  remove: 'Remove',
  save: 'Save SQL source',
  saveChanges: 'Save changes',
  cancel: 'Cancel',
  newTitle: 'Add a SQL source',
  editTitle: 'Edit this SQL source',
  nameLabel: 'Name',
  namePlaceholder: 'Contoso warehouse',
  serverLabel: 'Server',
  serverPlaceholder: 'contoso-sql.database.windows.net',
  databaseLabel: 'Database',
  databasePlaceholder: 'Warehouse',
  /** Beside the shared account picker. Says when the sign-in is used, so the choice is not a guess. */
  signInHint: 'Every check that uses this source signs in this way.',
  testing: 'Testing...',
  /** What Test connection actually does, so nobody fears it reads their data. */
  testExplains: 'Test connection signs in and asks the database for the number one. It reads none of your tables.',
  /** The confirm before an unused source is removed. */
  removeConfirm: 'Remove this SQL source? Any check you point at it later would have to be set up again.',
  /** The refusal, and what to do about it. The engine supplies the names. */
  refusedLead: 'It is still in use, so nothing was removed.',
  refusedNext: 'Point those at another source, or remove them, then remove this source.',
  refusedChecks: 'Checks that use it',
  refusedTables: 'Table mappings that use it',
  /** The Current setup role card, when nothing is saved yet. */
  roleEmpty: 'No SQL sources saved',
  roleDetail: 'Save a server once, then any check can use it by name',
  roleAction: 'Manage SQL sources',
  /** The device-local promise, matching the wording used for model connections. */
  footnote: 'SQL sources are stored on this device and hold no password. Semanticus signs in when a check runs.',
} as const;

// ---- Who signs in to Fabric ---------------------------------------------------------------------
// Every Fabric call carries a sign-in mode. The values below are the engine's own mode ids, read off
// EntraToken.BuildCredentialWith: serviceprincipal, interactive (aliases entra / entramfa / mfa),
// devicecode, and anything else falls through to the Azure command line. The 'token' mode is left out
// on purpose, because the caller hands the engine a token and there is nothing for a person to choose.
// Labels say what the PERSON does, never what the machinery is called. There is no credential built
// from the VS Code session, so nothing here offers one.

export interface SignInMode { value: string; label: string; detail: string }

export const SIGN_IN_MODES: readonly SignInMode[] = [
  { value: 'interactive', label: 'Sign in in a browser', detail: 'A Microsoft sign-in page opens. Pick the account that can see this model.' },
  { value: 'devicecode', label: 'Use a code on another device', detail: 'You get a short code to type on your phone or another computer.' },
  { value: 'azcli', label: 'Use the Azure command line', detail: 'Uses the account the Azure command line on this computer is already signed in as.' },
  { value: 'serviceprincipal', label: 'Use a service account', detail: 'Uses a set-up robot account rather than a person. Someone has to set it up on this computer first.' },
];

/** The engine's spelling aliases, folded onto the one choice the picker shows for them. */
const SIGN_IN_ALIASES: Record<string, string> = {
  entra: 'interactive', entramfa: 'interactive', mfa: 'interactive', sp: 'serviceprincipal',
};

/**
 * The mode a page should start on. Prefers the way the destination was really opened (the engine reports
 * it on the publish side of the connection context), because that account is the one already proven to
 * reach the model. With nothing known, start at the browser sign-in: it is the only way that works on a
 * computer that has never run the Azure command line, which is how Kane's laptop reported the defect.
 */
export function defaultSignInMode(known?: { authMode?: string | null } | null): string {
  const raw = String(known?.authMode ?? '').trim().toLowerCase();
  const folded = SIGN_IN_ALIASES[raw] ?? raw;
  return SIGN_IN_MODES.some((m) => m.value === folded) ? folded : 'interactive';
}

/** True when this way of signing in puts a sign-in step in front of the person and makes them wait. */
export function isInteractiveSignIn(mode: unknown): boolean {
  const folded = defaultSignInMode({ authMode: mode == null ? null : String(mode) });
  return folded === 'interactive' || folded === 'devicecode';
}

/** What a person can press to get past a failed sign-in. */
export type SignInFix = 'signIn' | 'chooseAccount' | 'askAdmin' | 'none';

export interface SignInProblem {
  /** One plain line saying what went wrong. Always present. */
  lead: string;
  fix: SignInFix;
  /** The words on the button, or null when there is no button to press. */
  fixLabel: string | null;
  /** The words the engine sent back, kept only when we could not explain them. */
  detail: string | null;
}

const KNOWN_SIGN_IN_PROBLEMS: { match: RegExp; lead: string; fix: SignInFix; fixLabel: string | null }[] = [
  {
    match: /azure cli (?:not|could not be) (?:installed|found)|could not find.{0,20}azure cli/i,
    lead: 'This computer does not have the Azure command line, so signing in that way cannot work. Pick another way to sign in.',
    fix: 'chooseAccount', fixLabel: 'Choose a different account',
  },
  {
    match: /az login/i,
    lead: 'This computer is not signed in to the Azure command line, so we could not check who you are.',
    fix: 'signIn', fixLabel: 'Sign in',
  },
  {
    match: /consent/i,
    lead: 'Your account has not been given permission to use this yet. Someone who runs your workspace has to allow it.',
    fix: 'askAdmin', fixLabel: null,
  },
  {
    match: /\b403\b|forbidden|lack the role|insufficient(?:ly)? privileg/i,
    // Three panels read this line now (Promote, Fabric Git, the CI/CD publish), so it cannot name pipelines.
    lead: 'You are signed in, but your account is not allowed to do that here. Ask whoever runs your workspace to add you.',
    fix: 'askAdmin', fixLabel: null,
  },
  {
    match: /AADSTS50020|AADSTS90002|AADSTS500011|does not exist in (?:the )?tenant|not found in the directory|different tenant/i,
    lead: 'Your account belongs somewhere else than this model does, so it was turned away. Try the account that can open this model.',
    fix: 'chooseAccount', fixLabel: 'Choose a different account',
  },
  {
    match: /AZURE_CLIENT_ID|AZURE_CLIENT_SECRET|AZURE_TENANT_ID|client secret/i,
    lead: 'No service account is set up on this computer, so signing in that way cannot work. Pick another way to sign in.',
    fix: 'chooseAccount', fixLabel: 'Choose a different account',
  },
  {
    match: /authentication_canceled|user_cancel|was cancell?ed|sign-?in (?:was )?cancell?ed/i,
    lead: 'The sign-in window closed before it finished.',
    fix: 'signIn', fixLabel: 'Sign in',
  },
  {
    match: /\b401\b|credentialunavailable|no accounts were found in the cache|authenticationfailed|interactive authentication is not supported|sign in again|token.{0,20}expired|expired.{0,20}token/i,
    lead: 'Your sign-in has run out, so we could not check who you are.',
    fix: 'signIn', fixLabel: 'Sign in',
  },
  {
    match: /AADSTS/i,
    lead: 'Microsoft would not accept that sign-in. Try a different account.',
    fix: 'chooseAccount', fixLabel: 'Choose a different account',
  },
];

/**
 * Turn whatever came back off a failed Fabric call into one plain line plus the action that fixes it.
 * A failure we recognise shows the plain line ALONE: repeating the engine's own words under it is what
 * made the Promote panel unreadable. A failure we do not recognise keeps its text, under a lead line,
 * because hiding it would leave a person with nothing to pass on.
 */
export function signInProblem(raw: unknown): SignInProblem {
  const text = String((raw as { message?: string })?.message ?? raw ?? '').trim();
  if (!text) return { lead: 'That did not work, and nothing said why.', fix: 'none', fixLabel: null, detail: null };
  const known = KNOWN_SIGN_IN_PROBLEMS.find((p) => p.match.test(text));
  if (known) return { lead: known.lead, fix: known.fix, fixLabel: known.fixLabel, detail: null };
  return { lead: 'That did not work. Here is what came back:', fix: 'none', fixLabel: null, detail: text };
}
