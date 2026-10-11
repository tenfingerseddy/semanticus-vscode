# ai.schema replay: does Jev choose what Copilot sees better than today's rules?

The evidence run that decides whether the AI data schema family goes live (`AiSchemaFamily.JevDecides`, M4 point 5).
Results go in `docs/redesign/jev-ai-schema-replay.md`, aggregate numbers only. Copilot and data agents framing only.

Plain Node 26 for the scoring. No packages.

## The labelled set and the bar

`labels.json` labels every eligible table, column and measure of three placeholder-named models: the UAT Northstar and
ContosoMini fixtures (archives on the Inspiron under `~/uat-sol/fixtures`, pinned by sha256) and the Tabular Editor
AdventureWorks test model in the vendored submodule. Each is include, exclude or either (not scored), by the guide
written into the file. The labels obey the schema's own rule: nothing an included measure uses is labelled exclude.

The labels were committed before each run. `bar.json` is the verdict rule, and it is now Kane's switch-on bar of 26
September (version 3), set after run 2 and recorded that way: SHIP when the Pro path beats today's readiness rules, an
exact McNemar test gives p below 0.05, and it makes no more wrong exclusions than the rules. The size of the win does not
matter. Versions 1 and 2, each committed before its run, are in git history and in `docs/redesign/jev-ai-schema-replay.md`.

## The steps

**1. Run, on the laptop that holds the key.** It drives the product's own pieces (the family's request builder, the
real vendor client through DecisionService, the family's checks) and writes raw results outside the repository:

```
JEV_REPLAY_LIVE=1 dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevAiSchemaReplay"
```

Without `JEV_REPLAY_LIVE=1` the live fact is reported skipped, never passed. The key is read from
`~/.config/typesafe/env` into the test process only and is never printed or written.

**2. Score.** `node tools/jev-replay/ai-schema/score.mjs` reads `~/.cache/jev-replay-ai-schema/results.json` and writes
`score.json` and `score.md` beside it.

## Files

| file | job |
|---|---|
| `labels.json` | the labelled objects, the guide, the model sources and their sha256 |
| `bar.json` | the bar in force (Kane's, version 3) |
| `score.mjs` | the arithmetic and the verdict, aggregate only |
| `test/ai-schema.test.mjs` | self-test: labels, bar, scorer |
| `../../../Semanticus.Tests/JevAiSchemaReplay.cs` | the live run, and a non-live check that the labels cover every eligible object and obey the schema's rule |
