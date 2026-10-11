// Verifier V1d on m11/feedback-rules at e8430e99: the Studio door's "Give to your assistant" on a format finding
// that is advice.
//
// Since round 8 a format rule may still RAISE a format that is already there, but its fix is withheld: the finding is
// advice, not a fault. The engine says so on both doors. bpaGetFixPrompt (the Studio door) and get_fix_prompt (the MCP
// door) answer Tool "(advice)" with a brief that begins "It is advice, not a fault to fix" and asks the assistant to
// change the format only if the person agrees (LocalEngine.BpaGetFixPromptAsync). But on Model quality every finding
// without a Fix button, advice included, is given to the assistant through findingBuilder, which drops the Tool and
// titles the Plan task "Fix ... on ...". The title is what the Plan shows the person and what the assistant reads
// first, and it contradicts the brief under it. Every plain number or percentage format the rules advise on now
// reaches the person this way (the verifier's probe: 108 of 1987 measure formats are advised, each with no Fix button).
//
// The request is a pure function in sendtask.tsx, run here with the bridge stubbed to return the engine's own advice
// answer, as send-to-assistant.test.mjs does for its cases.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');

// The engine's advice answer for INTEGER_FORMATTING on a measure shown in millions, word for word as
// LocalEngine.BpaGetFixPromptAsync builds it at e8430e99 (its "Why:" line, the rule's Description, left out).
const ADVICE = {
  objectRef: 'measure:Sales/Revenue M',
  ruleId: 'INTEGER_FORMATTING',
  tool: '(advice)',
  prompt: 'Using the Semanticus MCP, review this Best Practice Analyzer advice on measure [Sales].[Revenue M]. It is advice, not a fault to fix.\n'
    + 'Rule: [Formatting] Whole numbers should be formatted with thousands separators and no decimals (Formatting).\n'
    + 'Its format is already set ("##0,,"), and the rule suggests "#,0". No fix writes over a format that is already there, because a format can mean more than it looks: '
    + 'a comma at the end or just before the decimal point divides by a thousand, an empty section hides values, and dashes or brackets between digits can lay out a code. '
    + 'Tell the person what the current format shows and what the suggestion would show, and change it only if they agree, with set_property on FormatString (objRef "measure:Sales/Revenue M").',
};

const calls = [];
const STUBS = {
  './bridge': {
    rpc: async (method, ...params) => {
      calls.push({ method, params });
      if (method === 'bpaGetFixPrompt') return ADVICE;
      throw new Error('unexpected call ' + method);
    },
    copyText: async () => true,
    navigateInStudio: () => {},
    onReconnect: () => () => {},
    onPlanChange: () => () => {},
  },
};
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => STUBS[name] ?? (name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}

const S = load(join(webview, 'src', 'sendtask.tsx'));

// The row Model quality renders for this finding: no Fix button, because the engine says canAutoFix false.
const row = {
  ruleId: 'INTEGER_FORMATTING',
  ruleName: '[Formatting] Whole numbers should be formatted with thousands separators and no decimals',
  objectRef: 'measure:Sales/Revenue M',
  objectName: 'Revenue M',
  canAutoFix: false,
};
const task = await S.findingBuilder('bpa', row)();

assert.deepEqual(calls.map((c) => c.method), ['bpaGetFixPrompt'], 'the task is built from the engine\'s own answer');
assert.equal(task.brief, ADVICE.prompt, 'the brief is the engine\'s advice, unchanged');
assert.ok(!/^Fix\b/.test(task.words),
  `a format finding the engine calls advice ("It is advice, not a fault to fix") is given to the assistant as a Plan task titled "${task.words}"`);

console.log('verify-format-advice-task: ok');
