# queries.chart_kind replay: does Jev pick a better chart than the rules?

The evidence run that decides whether Jev picks the chart a Queries result opens as (D6; `JevChartPick.JevPicks` in
`Semanticus.Engine/Query/JevChartPick.cs`). The bar is `docs/queries-chart-pick-bar.md`; `bar.json` is that bar as
numbers, committed before any run. Plain Node for the scoring, no packages. Placeholder names only.

## The point stays OFF

The point is built and OFF. `JevChartPick.JevPicks` is `false`, so `pick_chart_kind` (MCP) and `pickChartKind` (RPC)
return the rules' pick, by `rules`, with the note "Jev's chart pick is off until it clears its bar." Nothing is sent.
Only Kane switches it on, after one run against this bar passes and its numbers are in the change log.

## What is measured

Each labelled question has its words, the result's shape, every chart a reader would accept, and any chart that would
mislead. A pick is right when it is one of the acceptable charts, and harmful when it is one of the harmful ones.
The rules arm is `chartRulePick` from `Semanticus.VSCode/webview/src/chartpick.mjs`, imported as it is. Where Jev did
not pick (unsure, the neutral "the rules' pick is fine", a refused answer), the item scores as the rules' pick, since
that is what the product shows.

## The bar

All three, on `labels.json`, run once: Jev is acceptable on more items than the rules; an exact two-sided McNemar test
on the discordant items gives p < 0.05; Jev's harmful count is no higher than the rules'. Held out by whole model:
Northstar, MSSEC and Smartsheets.

## The rules baseline (measured, 2026-09-28)

`node tools/jev-replay/chart/score.mjs` on 153 items: the rules are acceptable on 126 of 153 (82.4%), harmful 12.
For Jev to pass it must be acceptable on more than 126, significantly, with harmful no higher than 12.

## The labels

153 questions (Northstar 53, MSSEC 50, Smartsheets 50), weighted toward intent words that change the best chart on
the same shape (share, trend, mix, compared, best, against). Written by hand by the D6 builder agent acting as
labeller, never by running `chartpick.mjs` and never from a Jev answer (none has been asked). They have not yet been
reviewed by a person; that review comes before the one gate run, never after it.

## The steps

```
node --test tools/jev-replay/chart/test/*.mjs          self-test: labels, bar, scorer, McNemar
node tools/jev-replay/chart/score.mjs                  the rules baseline, offline
node tools/jev-replay/chart/score.mjs <results.json>   a Jev run against the bar (not run yet)
```

A results file is `{ "arm": "jev", "picks": { "<id>": { "kind": "line", "by": "jev" } } }`, one per item, from the
product's own `pick_chart_kind` path with the point switched on in a test build. No live run has been made.

| file | job |
|---|---|
| `labels.json` | the labelled questions |
| `bar.json` | the committed bar |
| `score.mjs` | the arithmetic and the verdict, aggregate only |
| `test/chart.test.mjs` | self-test |
