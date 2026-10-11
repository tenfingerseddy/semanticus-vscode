# date.table replay: does Jev decide the date table better than the rules?

The evidence run that decides who picks the date table (T268, M4 point 3, `DateTableFamily.JevDecides`).
Results go in `docs/redesign/jev-datetable-replay.md`, aggregate numbers only.

## What is measured

Each labelled model (`labels.json`) is built in a real engine. The product's own family asks Jev once per model:
which table is the date table (one the engine lists, a new one, or none) and whether to mark it. Two rules answer
the same model:

- **the shipped rule**, what the Plan does today: DATE-MARK plus PlanDateMark;
- **the name rule**, the pilot's two-word rule over the engine's own options.

## The bar (committed before the first run)

`bar.json`. Jev decides for Pro only when it is right on at least `jevMin` of the answerable models, strictly more
than the shipped rule, at least as many as the name rule, and with at most `harmfulMax` kept lines that mark the
wrong table. Below the bar the rule stays.

## The steps

**1. Run, on the laptop that holds the key.** The key is read from `~/.config/typesafe/env` into the test process
only, for one client, and is never printed or written.

```
JEV_REPLAY_LIVE=1 dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevDateTableReplay"
```

Without `JEV_REPLAY_LIVE=1` the live fact is reported skipped, never passed. It writes
`~/.cache/jev-replay-datetable/results.json`, outside the repository.

**2. Score.** `node tools/jev-replay/datetable/score.mjs` writes `score.json` and `score.md` beside the results.

## Round 3 (the coordinator's retest rules)

`derive-key.mjs` writes `key.json`: the answer key from each model's structure and declared data, three decisions
apart (needed, which table, mark). `round3-bar.json` is its bar. Run with `JEV_REPLAY_SET=round3`, score with
`node tools/jev-replay/datetable/score-round3.mjs`. Rounds 1 and 2 use `JEV_REPLAY_SET` unset and `heldout`.
Results: `docs/redesign/jev-datetable-replay.md`.

## The population that decides the switch

Rounds 1 to 3 score every labelled model. **The switch is decided on a smaller set**: the models the shipped product
actually asks Jev about, which is where the rule already says a date table is needed (time intelligence is used and
no table is marked) AND the engine can offer at least one table. That is `DateTableFamily.Prepare` returning an ask,
so the set is READ OFF THE ENGINE and never re-implemented here; a second implementation that drifted would be a
false proof about the thing the decision rests on.

The set is written down in `asked-population.json`: 11 of the 64 labelled models on 27 September. A non-live test
builds every labelled model in a real engine, with `key.json`'s declared data as facts as in round 3, and fails when
the engine asks about a different set, printing the set it asks about now:

```
dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevDateTableReplay.The_models_the_product_asks_about"
```

**The rescore** is `rescore/rescore-head.py`. It scores the recorded round 3 answers against the rule as it ships and
against that population, with no new live run. The answers live outside the repository by design, where the live run
writes them (`~/.cache/jev-replay-datetable/round3-results.json`), so it needs them on disk:

```
python3 tools/jev-replay/datetable/rescore/rescore-head.py [round3-results.json]
```

It reads `key.json` and `rescore/head-rule.json`, the joint verifier's dump of what the engine did for each labelled
model on 27 September (whether DATE-MARK fires, the table PlanDateMark names, and whether Prepare asks). Its output on
those answers is `rescore/rescore-head.out`: on the 11 models the product asks, Jev when sure and the shipped rule were
both right 11 times (McNemar p 1.0), which is why the point is off (`docs/redesign/jev-datetable-replay.md`).

## Files

| file | job |
|---|---|
| `labels.json` | the labelled models: tables, relationships, measures, derived facts, and the author's decision |
| `bar.json` | the committed bar and both rules, in words |
| `score.mjs` | the arithmetic and the verdict |
| `asked-population.json` | the labelled models the shipped product asks Jev about: the population that decides the switch |
| `rescore/rescore-head.py` | the rescore of the recorded round 3 answers on that population; `rescore/rescore-head.out` is its output |
| `rescore/head-rule.json` | what the engine did for each labelled model on 27 September, which the rescore reads |
| `test/datetable.test.mjs` | self-test: labels, bar and scorer, no network |
| `../../../Semanticus.Tests/JevDateTableReplay.cs` | the live run; a non-live check that the labels line up with the engine; and a non-live check that the engine asks about exactly `asked-population.json` |
