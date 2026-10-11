// m11/completeness round 5 (verifier round 4, F3): the claim that Jev reads the model's other names for things waits for
// m11/jev-query-tune, and its check must be one that lane's code can pass. Round 4's check wanted a quoted request field
// naming synonyms; that lane sends none. It names a measure's synonym inside the measure's option, and only when the
// question uses that word (JevQueryReader.SaidSynonyms, 29368c52). So the check now reads what that lane builds, and the
// release's words say no more than it sends: "when your question uses one".
// Also (verifier round 4, L5): the plan-statement row catches three paraphrases it missed, and it reads every Help section,
// not only the Jev one (m11/bpa-grade adds a Help section of its own).
import assert from 'node:assert/strict';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const { CLAIMS, surfaces, readsSaidSynonyms } = await import(pathToFileURL(join(repo, 'tools', 'release-claims.mjs')).href);
const failures = [];

const row = CLAIMS.find((c) => c.id === 'jev-reads-other-names');
assert.ok(row, 'the claim list has a jev-reads-other-names row');
if (!/\bm11\/jev-query-tune\b/.test(row.arrivesWith ?? '')) failures.push(`the row waits for ${row.arrivesWith}, not m11/jev-query-tune`);

// The two places m11/jev-query-tune's JevQueryReader.cs (29368c52) names a synonym, as that lane wrote them.
const laneReader = [
  'internal static List<string> SaidSynonyms(string words, string[] synonyms)',
  'var said = SaidSynonyms(words, m.Synonyms);',
  `: " (the model says the question's word '" + said.OrderByDescending(x => x.Length).First() + "' is another name for " + m.Name + ")");`,
].join('\n');
if (!readsSaidSynonyms(laneReader)) failures.push("the check does not pass on m11/jev-query-tune's reader, the lane it waits for");
// A reader that names no synonym does not pass it.
if (readsSaidSynonyms('var option = "the question asks for " + m.Name;')) failures.push('the check passes on a reader that names no synonym');

// Every sentence that makes the claim says it is only when the question uses the word.
for (const s of surfaces())
  for (const p of s.paragraphs)
    for (const sentence of p.sentences)
      if (row.re.test(sentence) && !/\bwhen your (?:data )?question uses (?:one|it|that word)\b/i.test(sentence))
        failures.push(`${s.name} says "${sentence}", more than m11/jev-query-tune sends (a synonym only when the question uses it)`);

const plan = CLAIMS.find((c) => c.id === 'plan-statement');
for (const probe of ['Model quality now shows an A to F grade.', 'Pro fixes every issue with one click.', 'New model from SQL.',
  'Best practice gets an A-to-F letter on the Plan.'])
  if (!plan.re.test(probe)) failures.push(`plan-statement does not catch "${probe}"`);
for (const probe of ['AI readiness gives your model an A to F grade.', 'Apply a safe fix in one click.', 'The Model Spec wizard can draft from a SQL source.'])
  if (plan.re.test(probe)) failures.push(`plan-statement fails on the true sentence "${probe}"`);
const helpAll = surfaces({ allHelp: true }).find((s) => s.name === 'Help, every section');
if (!helpAll || !helpAll.only?.includes('plan-statement')) failures.push('the plan-statement row does not read every Help section');
else if (!helpAll.paragraphs.some((p) => p.sentences.some((x) => /Apply a safe fix, or copy a ready prompt for the rest/.test(x))))   // the AI readiness FAQ (r1 min1 dropped "in one click")
  failures.push("every Help section does not hold a sentence from outside Help's Jev section");
if (surfaces().some((s) => s.name === 'Help, every section')) failures.push('the other rows read every Help section too');

for (const f of failures) console.log('OTHER NAMES: ' + f);
assert.equal(failures.length, 0, `${failures.length} way(s) the other-names claim cannot pass on its lane or says more than it sends, or the plan-statement row misses a paraphrase or a Help section (listed above)`);
console.log('completeness-release-claims-r5: the other-names claim waits for m11/jev-query-tune, its check passes on that lane\'s reader, the words say only what it sends, and the plan-statement row reads its paraphrases and all of Help');
