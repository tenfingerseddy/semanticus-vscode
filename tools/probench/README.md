# ProBench — the Pro-value benchmark (v1)

Implements the **Verified DAX Benchmark** from [`docs/verified-edits-plan.md`](../../docs/verified-edits-plan.md)
§"Verified DAX Benchmark" at v1 scope. The question it answers, transparently: *does the same AI
assistant, wrapped in the Pro verify/probe/benchmark loop, produce more-correct DAX than the same
assistant retrying on raw error text?*

## The three arms (frozen)

| Arm | What the assistant gets | What it models |
|---|---|---|
| **FREE** | The task prompt + the model summary. ONE response, no tools. | The floor: paste-and-pray. |
| **FREE+** | Same prompt. May run `validate_dax` + `run_dax` and retry on the raw error/result text, up to the attempt cap. No probe, no equivalence proof, no benchmark-pick. | A smart free user iterating by hand. **The honest control.** |
| **PRO** | Same prompt. The enforced loop: `probe_measure` across the filter-context matrix → iterate on engine-computed evidence → `verify_dax_equivalence` where a reference expression exists → `benchmark_dax_coldwarm` pick when multiple candidates pass. Same attempt cap. | The Pro referee. |

**The headline is `PRO − FREE+`, never `PRO − FREE`.** Retrying alone must not be creditable to the
loop. Same assistant, same model, same tasks, same oracle; only the loop varies.

## Scoring (pre-registered, before any arm runs)

- **Correctness**: each task ships a frozen **filter-context matrix** (grains × edge members,
  including empty intersections, zero denominators and year boundaries). A candidate is executed
  across the matrix and scored as the fraction of cells matching the **reference vector**, where
  `BLANK ≠ 0 ≠ ERROR`. `pass@1.0` (all cells) gates everything else; fast-but-wrong never wins.
- **Reference vectors** are produced by executing the task's hand-written reference DAX (reviewed,
  frozen in `tasks.json`) via `run_dax`, dumped to `gold/`. The comparator (`compare.py`) is a
  standalone script that shares **zero code** with the engine's `verify_dax_equivalence`.
- **Attempt budget**: identical across FREE+/PRO (`config.json: attemptCap`). We report `PRO@1`,
  `PRO@cap` and `FREE+@cap`.
- **Perf** (only among `pass@1.0` candidates): cold/warm medians via `benchmark_dax_coldwarm`,
  interleaved arms, reported as relative speedup with IQR.
- **Where Pro does NOT help is a headline, not a footnote**: per-tier deltas are published even
  (especially) when ≈ 0.

## v1 scope, stated plainly

- ~24 tasks (not the full 60), single pinned model, one assistant (Claude, via the operator
  protocol below), no external audit yet. v1 exists to publish an honest first number and to
  wire the machinery; the pre-registered 60-task externally-audited run is the follow-up.
- Contoso-style schemas are in every LLM's training data. v1 mitigates with model-specific
  fiscal-calendar tasks (the pinned model uses a **non-standard fiscal year**) and flags the
  contamination risk in the results; the held-out novel set arrives with v2.

## Anatomy

```
tasks.json        the frozen corpus: id, tier (basic|intermediate|advanced), failureMode,
                  prompt, referenceDax, matrix (the filter-context spec)
config.json       pinned model identity (+hash), attempt cap, arm prompts (byte-identical
                  task text; per-arm tool policy only)
runner.mjs        drives the engine's MCP door: builds matrix queries, executes reference +
                  candidate DAX via run_dax, dumps raw cell vectors to gold/ and runs/
compare.py        the independent comparator: cell-by-cell vector comparison (BLANK/0/ERROR
                  distinct), per-task pass@1.0, per-tier + per-failure-mode rollups
protocol.md       the operator protocol: how an assistant session runs each arm (frozen
                  per-arm system prompts, logging requirements, transcript publication)
results/          raw run logs + the published summary (committed — transparency is the point)
```

## Running

1. Open the pinned model (see `config.json`) in Power BI Desktop, or connect to the pinned
   XMLA workspace. The engine must see it (`connect_local` / `open_live`).
2. `node runner.mjs gold` — executes every task's reference DAX across its matrix, freezes
   `gold/<task>.json`. Do this ONCE per pinned model hash; commit the vectors.
3. Run arms per `protocol.md` (each arm session logs to `runs/<arm>/<task>.json`).
4. `node runner.mjs score <arm>` — executes each candidate across the matrix.
5. `python compare.py` — emits `results/summary.json` + `results/summary.md`.

Nothing here calls an LLM: the engine never runs inference (golden rule #1) and the harness
scripts are deterministic. The assistant sessions are driven by the operator per `protocol.md`,
with transcripts saved alongside the results.
