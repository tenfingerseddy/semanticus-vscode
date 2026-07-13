# ProBench operator protocol (v1)

How an assistant session runs one arm. The point of this file is that anyone can re-run the
benchmark and get comparably produced candidates: same prompts, same tool policy, same logging.

## Invariants (all arms)

- **One task = one fresh assistant session.** No memory of other tasks or other arms.
- **Byte-identical task text.** Every arm's session receives exactly:
  1. The arm system prompt below (differs ONLY in the tool-policy paragraph).
  2. The model orientation block: the output of `get_model_summary` + `list_objects` for tables
     and columns (generated once, reused verbatim for every arm and task).
  3. The task's `prompt` field from `tasks.json`, verbatim.
- The session must END by emitting the final DAX expression in a fenced block tagged `FINAL`.
- The operator saves per task: `runs/<arm>/<taskId>.json` = `{ "task": id, "dax": final,
  "attempts": n, "transcript": path }`, plus the full session transcript beside the results.
- The reference DAX is never shown to any session, any arm, ever.

## Arm system prompts

### FREE
> You are writing a DAX measure for the Power BI model described below. Respond with your single
> best measure expression and nothing else, in a fenced block tagged FINAL. You have no tools.

### FREE+
> You are writing a DAX measure for the Power BI model described below. You may use `validate_dax`
> and `run_dax` to check your work and you may revise after seeing raw results or errors, up to
> {attemptCap} candidate expressions in total. When satisfied (or out of attempts), emit your final
> measure expression in a fenced block tagged FINAL.

### PRO
> You are writing a DAX measure for the Power BI model described below. Work under the verified-edit
> loop: draft, then `probe_measure` across contexts (grand total, single member, multi member, empty,
> boundary) and judge the behavior against the task's intent; use `verify_dax_equivalence` when you
> refactor a passing candidate; if more than one candidate behaves correctly, pick the faster one
> with `benchmark_dax_coldwarm`. You may revise on this engine-computed evidence, up to {attemptCap}
> candidate expressions in total. When satisfied (or out of attempts), emit your final measure
> expression in a fenced block tagged FINAL.

## Attempt counting

An "attempt" is a distinct candidate expression the session commits to checking (or, in FREE's
case, its single answer). Tool calls that merely inspect the model do not count; each revised
expression does. The cap is `config.json: attemptCap` for FREE+ and PRO alike; FREE is always 1.

## Ordering & hygiene

- Run order interleaves arms per task (task 1: free, freeplus, pro; task 2: pro, free, freeplus; …)
  so warm caches never systematically favor one arm. (Correctness scoring is cache-independent;
  this matters for the perf comparisons.)
- The operator never edits a candidate: what the session emits under FINAL is what gets scored,
  verbatim, including any mistakes.
- Sessions that fail to emit a FINAL block within the cap are recorded as `"dax": null` and score
  zero cells — reported, not dropped.
