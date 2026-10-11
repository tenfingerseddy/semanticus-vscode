# query.summarize replay: does Jev read a data question into the right parts?

The evidence run that measures how well Jev reads data questions (Jev question-to-DAX, Kane 26 Sep). Whether Jev
reads them is Kane's call (`JevQueryReader.JevReads`, true since 28 Sep), not this run's. Results go in `docs/redesign/jev-query-replay.md`, aggregate numbers only.

Plain Node 26 for the scoring. No packages. Nothing in this folder holds a name from a real model: Northstar is the
UAT fixture and Contoso is built in `Semanticus.Tests/JevQueryReplay.cs`.

## What is measured

Each labelled question is asked through the product's own path (`ask_data`, with `run = false`): the engine reads
the model's values through a fake connection, matches the words on this computer, asks Jev once with the real
request, and reads the answer back through every check. A reading is right when its measures, its splits, its
filters (column, operator and the set of values) and its top N are exactly the label's. The order is not scored.
A decline item is right when nothing is settled. The word match (`JevQueryWordArm`) is scored on the same items.

## The bar

Kane's switch-on bar for every Jev decision (26 Sep): Jev is right more often than the best non-Jev baseline with an
exact McNemar test on the discordant items at p < 0.05, and it makes no more harmful mistakes (a sure, wrong reading
that would run and return a plausible number). An item Jev was unsure of is not right, and never harmful. Each
version is committed before the run it scores, on a set nothing was tuned on: version 2 (`bar.json`) scored run 3 on
the old 62, version 3 the first fresh 80, version 4 the second fresh 160, version 5 (`fresh3-bar.json`) the third
fresh 168, on the reader that ships. Version 1 (commit 12925fa3: right at least 80%, wrong and would run at most 5%,
not below the word match) was committed before the first run and scored runs 1 and 2.

**Where it stands:** version 5 was not cleared (p = 0.142), but Kane decided on 28 September that Jev stays on for
data questions and the replays are measurements, not a gate, so `JevQueryReader.JevReads` is true. See
`docs/redesign/jev-query-replay.md`.

## The steps

```
JEV_REPLAY_LIVE=1 JEV_REPLAY_FIXTURES=<the UAT fixtures folder> dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevQueryReplay"
node tools/jev-replay/query/score.mjs
```

`JEV_REPLAY_SET=fresh3` on both commands replays and scores that held-out set against `fresh3-bar.json` instead.

Without `JEV_REPLAY_LIVE=1` the live fact is reported skipped, never passed. The key is read from
`~/.config/typesafe/env` into the test process only and is never printed or written. `JEV_REPLAY_FIXTURES` must name
the UAT fixtures folder (the one holding `northstar/` and `data/`); the live fact refuses to start without it, and the
Northstar project is copied to scratch.

| file | job |
|---|---|
| `labels.json` | the labelled questions: the right parts of each |
| `bar.json` | the committed bar |
| `fresh-labels.json`, `fresh2-labels.json`, `fresh3-labels.json` | the held-out sets, with `fresh-bar.json`, `fresh2-bar.json`, `fresh3-bar.json` beside them |
| `fresh3-near-repeats.json` | the 20 fresh3 questions that ask for the same parts as an earlier one, a word or two apart; flagged, not removed |
| `natural/` | the natural set: 176 natural questions, every reasonable reading accepted, split into a tuning half and a held-out half before any run (`JEV_REPLAY_SET=natural-tune` or `natural-held`, with `JEV_REPLAY_BARE=1`); `score-natural.mjs` scores it |
| `northstar-data/` | the values the Northstar tables hold, and the script that writes them |
| `score.mjs` | the arithmetic and the verdict, aggregate only |
| `test/query.test.mjs` | self-test: labels, bar, scorer |

## The two models a set is written across

`northstar` is the UAT fixture opened from disk. It is **partly described, not raw**: 4 of its 10 visible measures
(Net Sales, Total Cost, Margin %, Units), 6 of its 51 visible columns and 3 of its tables carry a description, and one
table (Sales) has synonyms ("sales", "transactions"); no measure or column has one. An earlier version of this file
called it raw, from a grep for "description" that cannot see TMDL's `///` descriptions.
`The_Northstar_replay_model_is_partly_described_and_names_one_table_synonym` opens the fixture and asserts exactly
that (it needs `JEV_REPLAY_FIXTURES`), and the live replay asserts it again before asking anything.
`contoso-ai` is the Contoso-style model built in `JevQueryReplay.cs` with a description on every table, column and
measure and the words a person would use for each: a model **described throughout**. The reader reads descriptions
(they go in every call) and synonyms (matched on this computer and never sent). `fresh3-labels.json` is the first set
written across it. `contoso` is the same model without any of that metadata, kept because the older sets were scored
on it. The two fresh3 arms differ in more than their metadata (different models, and contoso-ai's questions are
phrased in its own synonyms), so their per-model numbers are a lead, not a raw-against-prepared result; see
`docs/redesign/jev-query-replay.md`.

## The value fixture

The replay answers the engine's own DISTINCT reads from CSV files in `JEV_REPLAY_FIXTURES/data`, one per table. The
files the first runs used were never in any repository, so `northstar-data/` holds a **smaller universe that holds
every labelled value** (for example 10 customers where the originals have 1,848), and
`node tools/jev-replay/query/northstar-data/make.mjs` rewrites it. It is not the original universe: on the 100 fresh3
Northstar questions the baselines read the same under both, and the value matches Jev is asked about differ on 15,
always by the originals adding false matches (the record has the table). Assemble the fixtures folder with:

```
F=~/.cache/jev-replay-fixtures
mkdir -p "$F/northstar" "$F/data"
tar xzf <the northstar-pristine fixture archive> -C "$F/northstar"
cp tools/jev-replay/query/northstar-data/*.csv "$F/data/"
```

`node --test tools/jev-replay/query/test/query.test.mjs` runs with no network and no key.
