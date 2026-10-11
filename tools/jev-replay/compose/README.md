# compose.draft replay: does Jev draft the right steps from a person's words?

The evidence run that decides whether Jev drafts (composer lane L5, `JevDraftBuilder.JevDrafts`). Results go in
`docs/redesign/jev-compose-replay.md`, aggregate numbers only.

Plain Node 26 for the scoring. No packages. Nothing in this folder holds a name from a real model.

## What is measured

One chooser serves two doors, and both are scored on the same labelled descriptions:

- **Workflows, Describe it in words** (`describe_workflow_steps`): Jev is asked once per step the words name. A
  step is right when the ops Jev chose hold every labelled op and nothing outside the label and `alsoOk`. The word
  match is scored on the same steps.
- **The Plan, the field and Ctrl J** (`compose_task` from words): Jev is asked once per step the words name, as on
  Workflows (the per-clause rule). A draft is right when its kept steps, as a LIST, hold exactly the labelled ops: a
  step asked for twice needs two, a step kept twice is one too many (reads left out: a Plan draft never drafts them).
  The steps are compared as a multiset, not in order, because the engine orders a draft by what depends on what.

## The bar (committed before the first run)

`bar.json`. Jev drafts for Pro only when the Workflows share is at least `describeMin` and not below the word match,
and the Plan share is at least `composeMin`. A step Jev did not decide counts as Jev's miss.

## The steps

**1. Run, on the laptop that holds the key.** It drives the product's own path end to end (the real builder, the
real vendor client, the real engine) against a Contoso-style model, and writes raw results outside the repository:

```
JEV_REPLAY_LIVE=1 dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevComposeReplay"
```

Without `JEV_REPLAY_LIVE=1` the live fact is reported skipped, never passed. The key is read from
`~/.config/typesafe/env` into the test process only, for one client, and is never printed or written.

**2. Score.** `node tools/jev-replay/compose/score.mjs` reads `~/.cache/jev-replay-compose/results.json` and writes
`score.json` and `score.md` beside it: the shares, the word match's share, the counts, latency and the verdict.

## Files

| file | job |
|---|---|
| `labels.json` | the labelled descriptions: per step, the ops it asks for |
| `bar.json` | the committed bar |
| `score.mjs` | the arithmetic and the verdict, aggregate only |
| `test/compose.test.mjs` | self-test: labels against the engine's templates, the bar, the scorer |
| `../../../Semanticus.Tests/JevComposeReplay.cs` | the live run, and a non-live check that each description cuts into as many steps as it labels |

`node --test tools/jev-replay/compose/test/compose.test.mjs` runs with no network and no key.
