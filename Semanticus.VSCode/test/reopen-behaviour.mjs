// A behavioural bench for the remember/reopen functions in src/extension.ts.
//
// Technique borrowed from Astra's spot-review-5 probe
// (/tmp/semanticus-joint-review/astra-spot-5/reopen-source-probe.mjs): pull the REAL function declarations
// out of the TypeScript syntax tree, transpile them unchanged, and run them in a VM with the module globals
// they close over supplied as mocks. Nothing here paraphrases the implementation, so a test cannot pass
// against source that would fail in the product. It is still a mock bench: no sign-in, no tenant, no engine.
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(root + '/package.json');
const ts = require('typescript');

// Astra, recheck 6: this list omitted SignInRequiredError and signInAndReopen, so the cold-start case threw
// ReferenceError inside the try, was caught by the same handler, and its three assertions all passed for the
// WRONG REASON. The extraction is the test's contract with the source: anything the exercised paths name has
// to be in here, and `extract` throws when a name is missing rather than letting the gap pass silently.
const REQUIRED = [
  'ReopenMissingError', 'SignInRequiredError', 'reopenLastModel', 'openLiveTarget', 'signInAndReopen',
  'reopenFailureReason', 'reportReopenFailure', 'describeSession', 'rememberLastModel',
  'confirmUnsavedWork', 'confirmAndSendOpen', 'writeLastModel',
  'readLastModel', 'forgetLastModel', 'bindGridAndTree', 'saveCommand', 'savedAuthMode',
];

export function extract(names = REQUIRED) {
  const source = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
  const ast = ts.createSourceFile('extension.ts', source, ts.ScriptTarget.Latest, true);
  const wanted = new Set(names);
  const picked = ast.statements.filter((n) => n.name && wanted.has(n.name.text));
  const found = new Set(picked.map((n) => n.name.text));
  const missing = names.filter((n) => !found.has(n));
  if (missing.length) throw new Error('Source extraction incomplete; missing: ' + missing.join(', '));
  const code = picked.map((n) => n.getText(ast)).join('\n');
  return ts.transpileModule(code, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
}

const LIVE_TARGET = {
  kind: 'live',
  path: '/fixture/semanticus-live/abc123/Contoso.bim',
  endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Review',
  database: 'SM_NFM',
  tenantId: 'tenant.example',
  modelName: 'SM_NFM',
};

// A remembered running Power BI Desktop model. Its endpoint and database both rotate every Desktop restart,
// which is why the descriptor keeps them: a reopen has nothing else to go on.
const LOCAL_TARGET = {
  kind: 'localDesktop',
  path: '/fixture/semanticus-live/def456/Contoso.bim',
  endpoint: 'localhost:51822',
  database: 'SM_NFM',
  modelName: 'SM_NFM',
};

// A session as the engine reports it for a model opened live: LiveBound with its origin coordinates, and a
// Source that points at the temp snapshot the engine exported.
function liveSession(over = {}) {
  return {
    sessionId: 'live-session', revision: 1, modelName: 'SM_NFM', source: LIVE_TARGET.path,
    liveBound: true, liveKind: 'xmla', liveEndpoint: LIVE_TARGET.endpoint, liveDatabase: LIVE_TARGET.database,
    currentTenant: 'tenant.example', ...over,
  };
}

// The same bytes opened as a PLAIN FILE: no LiveOrigin, so the engine reports no live binding at all. This is
// what a cold start sees after it opens a live model's cached snapshot offline.
function fileSessionFromSnapshot(over = {}) {
  return { sessionId: 'file-session', revision: 1, modelName: 'SM_NFM', source: LIVE_TARGET.path, liveBound: false, ...over };
}

export function makeBench({ pathExists = () => false, sessionAfterOpen, records, configuredModelPath } = {}) {
  const state = new Map();
  const calls = [];
  const notices = [];
  const prompts = [];   // modal Save / Discard consent prompts, kept apart from notifications
  const shown = [];
  let lastOpenMethod;

  const workspaceState = {
    get: (key) => state.get(key),
    update: async (key, value) => { if (value === undefined) state.delete(key); else state.set(key, value); },
  };
  const context = { workspaceState };

  const connection = {
    sendRequest: async (method, ...args) => {
      calls.push({ method, args });
      if (method === 'listConnections') {
        if (sandbox.holdListConnections) await sandbox.holdListConnections;   // delayed-reply cases
        return records ?? [{ endpoint: LIVE_TARGET.endpoint, database: LIVE_TARGET.database, authMode: 'devicecode', tenantId: 'tenant.example' }];
      }
      if (method === 'sessionInfo') return sandbox.currentSession;
      if (method === 'save') return { path: LIVE_TARGET.path, format: 'BIM', fileCount: 1 };
      lastOpenMethod = method;
      if (sandbox.openThrows) throw sandbox.openThrows;
      sandbox.currentSession = sessionAfterOpen ? sessionAfterOpen(method, args) : liveSession();
      return { modelName: 'SM_NFM', tables: 3, measures: 4 };
    },
  };

  const sandbox = {
    fs: { existsSync: (p) => pathExists(p) },
    vscode: {
      workspace: { getConfiguration: () => ({ get: () => configuredModelPath }) },
      window: {
        withProgress: async (_opts, work) => work({ report: () => {} }),
        showInformationMessage: (m) => { shown.push(m); },
        showWarningMessage: async (m, ...rest) => {
          // Two different surfaces come through here. A MODAL (second argument is an options object) is the
          // Save / Discard consent prompt; everything else is a notification. Keeping them apart is the point
          // of these cases: the notification is the thing that sits around, and the modal is the thing that
          // must appear before it can act.
          const modal = rest.length > 0 && typeof rest[0] === 'object' && rest[0] !== null;
          const actions = rest.filter((a) => typeof a === 'string');
          if (modal) {
            prompts.push({ message: m, actions });
            return actions.includes(sandbox.unsavedAnswer) ? sandbox.unsavedAnswer : undefined;
          }
          notices.push({ message: m, actions });
          const want = typeof sandbox.answerNotice === 'function' ? sandbox.answerNotice(m, actions) : sandbox.answerNotice;
          return actions.includes(want) ? want : undefined;
        },
      },
      commands: { executeCommand: async (id) => { calls.push({ method: 'command:' + id, args: [] }); } },
      ProgressLocation: { Notification: 15 },
    },
    extCtx: context,
    conn: connection,
    LAST_MODEL_KEY: 'semanticus.lastModel',
    LAST_MODEL_PATH_KEY: 'semanticus.lastModelPath',
    status: {},
    out: { appendLine: () => {}, show: () => {} },
    watchModelDisk: () => {},
    refreshStatus: async () => sandbox.currentSession,
    propGrid: { showModel: async () => {}, showModelIfEmpty: async () => {} },
    rebuildDaxSymbols: () => {},
    refreshOpenDaxEditors: () => {},
    resyncPropertiesFromTree: () => {},
    postToPanels: () => {},
    tree: { refresh: () => {} },
    setTimeout: () => 0,
    pickFirstSaveDestination: async () => ({ path: '/fixture/chosen/Model.bim', format: 'BIM' }),
    // Module-level state the extracted functions close over. `extract` only pulls declarations, so anything
    // they read or MUTATE at module scope has to live here or they fail with a ReferenceError — which is the
    // loud failure we want, rather than a silent gap of the kind Astra found in recheck 6.
    lastModelGeneration: 0,
    currentSession: undefined,
    openThrows: undefined,
    answerNotice: undefined,      // the button a case wants pressed on the reopen notice
    holdListConnections: undefined,   // a promise that gates the saved-record read, for late-reply cases
    unsavedAnswer: undefined,     // what the modal Save / Discard prompt returns
  };

  vm.createContext(sandbox);
  vm.runInContext(extract(), sandbox);

  return {
    sandbox, context, connection, calls, notices, prompts, shown,
    remembered: () => state.get('semanticus.lastModel') ?? null,
    rememberedPath: () => state.get('semanticus.lastModelPath') ?? null,
    seed: (value) => { state.set('semanticus.lastModel', { ...value }); if (value.kind === 'file') state.set('semanticus.lastModelPath', value.path); },
    openMethod: () => lastOpenMethod,
    openCalls: () => calls.filter((c) => c.method === 'open' || c.method === 'openLive' || c.method === 'openLocal'),
  };
}

export { LIVE_TARGET, LOCAL_TARGET, liveSession, fileSessionFromSnapshot };
