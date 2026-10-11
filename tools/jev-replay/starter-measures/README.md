# Starter measures replay: does Jev beat the rule at choosing a column's base measure?

The evidence run that decides who picks starter measures (T268, `StarterMeasuresFamily.JevDecides`).
Results go in `docs/redesign/jev-starter-measures-replay.md`, aggregate numbers only.

Plain Node 26. No packages. Nothing in this folder holds a name from a real model.

## The three steps

**1. Gather, on the machine that holds the connections** (over SSH is fine there: the session bus, and so
the saved sign-in, is reachable). Copy `gather.mjs`, `starter.mjs` and `mcp-client.mjs` to
`~/.cache/jev-replay-starter/` on that machine and run `node gather.mjs`. It starts the installed engine
headless on a scratch workspace, connects read-only to every saved xmla connection, reads metadata with
`run_dmv`, labels each number column on a fact table from the measures that already exist, and measures
each one with one scalar `run_dax` probe that returns counts only. It writes
`~/.cache/jev-replay-starter/starter-facts.json`: names, types, settings, derived shares and option ids.
Measure formulas are read there and never written. Copy that one file back to the same path here.

**2. Ask Jev, on the laptop.**

```
JEV_REPLAY_LIVE=1 node --experimental-strip-types tools/jev-replay/starter-measures/ask.ts
```

One call per column, with the product's own request: the engine's snapshot for the column and the
engine's exact question. It refuses unless `JEV_REPLAY_LIVE` is exactly `1`, refuses paths inside the
repository, and reads the key from `~/.config/typesafe/env` into the process only.

**3. Score.** `node tools/jev-replay/starter-measures/score.mjs` writes `score.json` and `score.md` into the
cache dir: accuracy against the rule, the yes-or-no half, calibration by confidence band, an exact
McNemar test, the verdict, and the rule's own score on every labelled column.

## Files

| file | job |
|---|---|
| `starter.mjs` | the engine's rules, snapshot and question, mirrored; the labels |
| `gather.mjs` | drives the engine over MCP on the machine with the data |
| `mcp-client.mjs` | a copy of the columns replay's MCP stdio client |
| `ask.ts` | posts the calls to Jev |
| `score.mjs` | the verdict, aggregate only |
| `test/starter.test.mjs` | self-test: drift against the C#, labels, privacy shape, aggregate score |

`node --test tools/jev-replay/starter-measures/test/starter.test.mjs` runs with no network and no key.
