# relationship.join replay: does Jev join the right columns?

The evidence run that decides whether Jev decides relationships (M4 point 2, `RelationshipFamily.JevDecides`).
Results go in `docs/redesign/jev-relationships-replay.md`, aggregate numbers only.

Plain Node 26 for the scoring. No packages. Nothing here holds a name from a real model.

## What is measured

Each labelled model is opened with every relationship removed. The product's own family run asks Jev about every
candidate column (names, types and flags only; with no data connected nothing is measured, which is how the UAT
lanes run too), and the engine resolves which relationships can be active. The name rule
(`RelationshipFamily.RuleTarget`, written down before the first run) is run on the same stripped model through the
same resolution. A proposal is a line that arrives kept. It matches a label exactly when the from column, the to
column, active, direction and cardinality all agree.

## The bar (committed before the first run)

`bar.json`: Jev's exact F1 at least 0.85, Jev's join precision at least 0.90, and Jev's exact F1 strictly above the
rule's. Otherwise the rule stays and nobody is asked.

## The labelled set

`labels.json`, 8 models and 75 relationships:

- `adventureworks`: the vendored Tabular Editor sample (`external/TabularEditor/TOMWrapperTest/TestData/AdventureWorks.bim`),
  labels are its own 27 relationships (the replay's non-live test checks they still match).
- `northstar`, `contoso-mini`: the UAT fixtures, transcribed as names and types, labels are their own relationships.
- `fabrikam-retail`, `tailspin-orders`, `wingtip-tickets`, `adatum-banking`, `litware-hr`: placeholder models written
  with their labels, in the naming styles real models use (dim and fact prefixes, Id columns, number and code
  columns, a bridge that filters both ways, snake case, role-playing dates).

## Steps

**1. Run, on the laptop that holds the key.**

```
JEV_REPLAY_LIVE=1 dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevRelationshipReplay"
```

Without `JEV_REPLAY_LIVE=1` the live fact is reported skipped, never passed. The key is read from
`~/.config/typesafe/env` into the test process only and is never printed or written. Raw results land in
`~/.cache/jev-replay-relationships/results.json`, outside the repository.

**2. Score.** `node tools/jev-replay/relationships/score.mjs` writes `score.json` and `score.md` beside the results.

`node --test tools/jev-replay/relationships/test/relationships.test.mjs` runs with no network and no key.

## Run 2 and the fresh set

Run 1 (the set above) failed the bar and found an engine defect: a column's key list was cut at sixteen keys in
table order, so AdventureWorks' `SalesTerritoryKey` never saw Sales Territory's key. After the fix (keys the
column's name points at come first), `labels-fresh.json` was written: four placeholder models (22 relationships)
Jev had never been asked about, committed before run 2. The SAME `bar.json` decides run 2 on the fresh set alone;
run 1's set is re-run beside it for comparison only, because it has been seen.

```
JEV_REPLAY_LIVE=1 JEV_REPLAY_LABELS=labels-fresh.json JEV_REPLAY_RESULTS=results-fresh.json dotnet test ...
JEV_REPLAY_RESULTS=results-fresh.json node tools/jev-replay/relationships/score.mjs
```
