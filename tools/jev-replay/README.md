# jev-replay

The replay gate for Jev decision points, from `docs/redesign/jev-plan.md` section 3.4.

It is plain Node. No packages, no build step, no network on the default path, and no secret of
any kind in this folder. Node 26 is what it was written and run against.

## How to run it

From the root of the repository:

```
node tools/jev-replay/replay.mjs ci
```

That is the whole gate. It imports pilot one, reproduces pilot one's own published numbers from
the imported records, runs the self-test, prints a short summary, and exits 0 or non-zero. Three
runs on the machine it was written on took 4.2, 2.9 and 4.4 seconds. Most of that is the
self-test starting a fresh node for each command-line refusal it checks.

The separate commands, if you want one at a time:

```
node tools/jev-replay/replay.mjs import    --fixture pilot1
node tools/jev-replay/replay.mjs reproduce --fixture pilot1
node tools/jev-replay/replay.mjs score     --fixture pilot1 --mode development
node --test tools/jev-replay/replay.test.mjs
```

Generated files land in `tools/jev-replay/.work/`. Nothing there is source. It can be deleted
and rebuilt at any time by running the import again. That path needs a line in the repository's
`.gitignore`.

If you change a bar in `manifest.json`, reseal it and commit both together:

```
node tools/jev-replay/seal-manifest.mjs
```

## What is in here

| file | what it does |
|---|---|
| `replay.mjs` | the command line: import, reproduce, score, ci |
| `manifest.json` | the numeric bars per decision point, committed before a run |
| `import-pilot1.mjs` | turns `docs/redesign/jev-pilot/verdict.json` into harness records |
| `seal-manifest.mjs` | recomputes the manifest's own digest after a deliberate change |
| `replay.test.mjs` | the self-test, recorded answers only |
| `lib/record.mjs` | the record shape, and the writer that refuses a bad record |
| `lib/digest.mjs` | canonical JSON, the oracle key and the offer digest |
| `lib/offer.mjs` | the seeded option order and the neutral-option check |
| `lib/split.mjs` | held out by whole model and by ask family, never by row |
| `lib/manifest.mjs` | loads the bars and refuses a point that has none |
| `lib/score.mjs` | the general scorer and its report |
| `lib/pilot1-score.mjs` | an exact port of the pilot's own scorer, frozen |
| `lib/oracle-recorded.mjs` | the recorded answer store |
| `lib/oracle-live.mjs` | the hand-run live oracle, which nothing here imports |

## The two digests, and why there are two

Every record gets two numbers.

The **oracle key** is what the recorded answer store is keyed on. It is a SHA-256 over the whole
of the evidence, which means the fixture, model and source hashes, the task, the knowable
context, the full allowed request, the candidate options both as a sorted set **and in the order
they were actually offered**, and the point, schema, prompt and served-model versions.

The order is in the key because it is in the engine's key. `DecisionDigest.cs:49` writes each
question's options in order, and that file's rule 3 says plainly that a different option order
is a different question. If the harness dropped the order, the harness and the engine would be
evaluating different things under one name.

That has a cost, and it is the right cost: **replaying one recording after a shuffle does not
test order bias.** It misses, and a miss is an error. A real order-bias test needs an answer
recorded for each order that was offered, which is a recording job and not something a replay
can conjure.

The **offer digest** is the same evidence with only the order actually offered. It is written
down and never looked up. It is the short number a reviewer can compare across records to see
which of them were shown the same layout.

An oracle is never keyed on the question wording or a hash of the question. Two models can be
asked the same words about different objects, and a question-only key would hand back the other
model's answer.

## Reading an answer the way the engine reads one

The scorer does not read a reply its own way. `lib/score.mjs` ports
`Semanticus.Core/Decisions/DecisionAnswerReader.cs`, rule by rule, with the C# line cited beside
each one: the shape has to be the shape that was asked, the choice has to be one of the options
that were sent, probabilities have to name real options and sit in 0 to 1, and a confidence that
is not sent is read as the probability of the option that was picked.

The one that mattered most is the noul confidence. The harness used to read a noul of 0.9 as a
confidence of 0.8, from `abs(p - 0.5) * 2`, which is what pilot one's `score.py` did. The engine
reads it as 0.9, from `max(p, 1 - p)`, at `DecisionAnswerReader.cs:201`. At the 0.85 threshold
those two disagree about whether to answer at all: the gate abstained where the engine advises.
The scorer now uses the engine's rule. The frozen pilot-one port in `lib/pilot1-score.mjs` keeps
`score.py`'s rule, because its job is to reproduce a published run exactly, not to judge.

A reply the engine's reader would refuse is counted as **not decided**: never served, never
correct, reported with its reason, because that is what the engine does with one. It makes the
whole ask undecided and the caller takes the fallback.

One declared gap: the engine also reads a "score" ladder (`DecisionAnswerReader.cs:90-171` and
`ReadScoreProbabilities`): the expected rung as a number counted from zero, with a legend and
probabilities keyed by rung position, as pilot one's 44 score answers and a live plan.rank reply
(26 Sep) show. The harness has not ported that reading, so it refuses such a point by name.

A test feeds the same requests and answers to a table copied from the C#, and a second test
re-reads those exact lines of the C# and fails if any of them has moved or changed. That is the
drift alarm: the engine cannot be run from here, because this harness is node only by design.

## What it refuses

Every one of these is a refusal with a message, not a warning, and every one has a test that
fails when the refusal is taken out.

- **A record missing a required field.** The writer names the field and writes nothing.
- **A sequence point with no selected action, executed action, engine result, accepted diff or
  final outcome.**
- **A record missing any of the six case families.** They are carried on every record, set to
  false when they do not apply, so a fixture with none of a family can still be counted.
- **A label written by the system being scored.** If the label author names the producing system
  or is the served model, the import stops.
- **An allowed request that lists the options as an array**, because that is a second copy of
  the option order. The order lives on `candidates.offeredOrder` and nowhere else.
- **An offered order that is not the same options as the candidate set**, since the key carries
  both and two lists that can disagree would key one and show a reviewer the other.
- **Option text that says where a candidate came from**, such as "suggested by Jev" or
  "baseline". The refusal names the word it found.
- **An option order with no seed**, because an unseeded order is not reproducible.
- **A record whose split membership is not the one the policy gives** for its model and family.
  Otherwise a record asserts its own side of the split and a model reserved for the gate can be
  scored in the tuning half by relabelling one field.
- **A gate run that touches a development fixture.** Held-out means held out.
- **A gate run with no split policy**, because then nothing checks the records are held out.
- **A gate run against bars whose status is `proposed`.** Only ratified, sealed bars can fail a
  point. Proposed bars still score in development mode, and every report says which it read.
- **A `--mode` that is neither `gate` nor `development`.** A mistyped mode used to become a
  development run, which reads afterwards as a gate that passed.
- **A scoring run for a point with no bar in `manifest.json`.** The refusal names the point.
- **A manifest whose recorded digest does not match its contents**, so a bar cannot be moved
  after it was committed and still read as committed.
- **A recorded answer that is not in the store.** A miss is an error naming the key. There is no
  fallback answer and no silent skip, because a fallback would turn a broken fixture green.
- **The live oracle**, unless it is switched on by hand. See below.

## Labels, and why rule agreement is a separate number

Every record carries two different things. `label` is the answer a reviewer says is right, with
the author who wrote it and the evidence behind it. `ruleAnswer` is what today's rule says.

The scorer never uses `ruleAnswer` as truth. Rule agreement is computed and reported next to
correctness, as its own number. If the rules were the answer key, a point that simply copied the
rules would score perfectly and prove nothing, which is the whole reason the new judgment exists.

**An honest warning about pilot one.** Its labels are the values stored in the published model
files. No person reviewed them blind. The pilot's own scorer names three of them as plainly
wrong and two more as unanswerable, and those are marked on the records. That is good enough for
a regression fixture and not good enough for a gate. A held-out gate needs labels a reviewer
wrote, with the provenance, the allowed alternatives and the exclusions frozen before the run.

## Splits

Records are held out by whole model and by whole ask family, never by row. Two rows from the same
model share almost everything, so splitting by row puts near-copies of a held-out question into
the half the harness was tuned against, and the held-out number then reads as proof of
generalisation when it is really a memory test.

`manifest.json` names which models are held out. Pilot one used the two public sample models, and
both of them were already read while the questions were being written, so pilot one is marked
development on every record and can never be the gate.

## Pilot one

`import-pilot1.mjs` reads `docs/redesign/jev-pilot/verdict.json`, which is the fixture of record,
and writes four files into `.work/pilot1/`: the run-one records, the run-two records, the
recorded answers keyed on the oracle key, and the labels on their own.

`replay.mjs reproduce --fixture pilot1` then recomputes the pilot's published numbers from those
records alone and compares them to `verdict.json`: all 413 answer rows field by field, all 14
sections including every band, the pooled calibration, and the run-to-run stability. Floats are
compared to a tolerance of 1e-12 and the worst difference seen is printed. Nothing in
`verdict.json` is ever adjusted to make a comparison pass.

Two honest gaps, both written onto every record rather than smoothed over:

1. `verdict.json` holds the answers, not the request that was sent. The file that held the model
   state, `calls.json`, is not committed. So the allowed request on each record carries the exact
   instruction text and criteria from `prep.py` plus the names of the state fields that were
   sent, and says plainly that the state values are not included.
2. The model files themselves are not committed, so the source hash on each record is an
   explicit `none:` note with a reason, never a digest of something else standing in for one.

## The secret policy

The harness holds no key and reads no key on the default path.

`lib/oracle-live.mjs` exists so a person can re-record answers against the real service by hand.
Nothing in this repository imports it, and the self-test proves that by reading the source of
every file here. It refuses to run unless:

- `JEV_REPLAY_LIVE` is exactly `1`, not "true" and not "yes", so it cannot be switched on by an
  accident of spelling;
- `JEV_REPLAY_KEY_FILE` points at a key file;
- that key file is outside this repository, because a key inside the tree is one `git add -A`
  away from being published;
- `JEV_REPLAY_ENDPOINT` names where to send the request.

The key is read inside a closure, used for one header, and never stored on any object, never
logged, and never put into an error message. When a call fails, only the status code is
reported, because a response body can echo the request back.

`replay.mjs ci` runs the self-test with `JEV_REPLAY_LIVE` cleared.
