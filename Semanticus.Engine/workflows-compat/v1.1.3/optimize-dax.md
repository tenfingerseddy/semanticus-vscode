---
name: optimize-dax
title: Rewrite a measure and prove it unchanged, for speed or clarity
description: Rewrite an existing measure (to make it faster, or just cleaner and more readable), then prove the rewrite returns the IDENTICAL result before you keep it. A hard equivalence gate stands between a rewrite and a silent behaviour change; the benchmark steps apply when the goal is speed and are skipped for a clarity-only cleanup.
whenToUse: "Rewrite an existing measure while proving every number stays identical, whether the goal is SPEED (benchmark, profile, faster form) or CLARITY (VARs, house conventions, de-duplication, readability). To author a new measure from scratch use new-measure or verified-measure; to change what a measure computes use verified-measure."
version: 1
strictness: hard
triggers: [benchmark_dax, profile_dax, update_measure]
---

## Step 1: State the goal and freeze the original

Record the measure's current state BEFORE touching anything. Copy its expression
verbatim with `get_dax`: this frozen text is the only proof of what it used to compute and the
reference the equivalence gate holds the rewrite to. State the goal plainly: SPEED (it is measurably
slow) or CLARITY (a cleaner, more standard, more maintainable expression that returns the same
numbers). If the goal is speed, establish cold/warm timings now with `benchmark_dax_coldwarm` so you
optimise the measured bottleneck, not a guessed one; if the goal is clarity, you can skip the
benchmark. Either way the result must stay identical.

```yaml gate
inputs:
  - name: target
    question: "The measure being rewritten (e.g. measure:Sales/Total Sales)."
    type: objectRef
    required: required
  - name: originalDax
    question: "The CURRENT expression, copied verbatim BEFORE any rewrite (get_dax gives it)."
    type: text
    required: required
  - name: equivalenceGrid
    question: "Comma-separated group-by columns for the per-context equivalence proof (e.g. Date[Year], Product[Category])."
    type: text
    required: answer-or-decline
```

## Step 2: Profile the bottleneck (speed goal only)

For a SPEED rewrite, split the cost with `profile_dax`: formula-engine vs storage-engine time, and
inspect the storage-engine query plans with `capture_query_plan`. Look for
CallbackDataID (FE work pushed into SE scans), excessive materialisation, and repeated scans. Clear
caches between runs with `clear_cache` so timings are honest. Optimise what the plan shows, not
folklore. For a CLARITY rewrite there is no bottleneck to profile, so skip straight to Step 3.

## Step 3: Rewrite the measure

Apply the new expression with `update_measure`, keeping the format string and description. Follow the
DAX floor: name intermediate results with VARs, DIVIDE over `/`, SELECTEDVALUE over
VALUES-with-error-trap, filter columns not whole tables, avoid FILTER as a filter argument,
fully-qualified column refs and unqualified measure refs. Run `validate_dax` and `lint_dax` before you
rely on it. The rewrite is now live on the model, and the next step proves it did not change any number.

## Step 4: Prove equivalence before you trust it

This is the hard gate: a rewrite must be PROVEN equivalent, never
eyeballed. The engine compares the recorded original against the measure's current expression
across the group-by grid you supplied. If any context differs, the gate fails and stays on
this step: revert with `update_measure` and try again, or `skip_workflow_step` with a reason.

```yaml gate
strictness: hard
verify:
  - kind: dax_equivalence
    probe: originalDax
    when: inputs.originalDax.answered
```

## Step 5: Confirm the win and keep it

For a SPEED rewrite, re-run `benchmark_dax_coldwarm` on the proven-equivalent rewrite and compare
against the step-1 baseline; record the gain, and if it is not actually faster keep the original,
because a correct slow measure beats a fast wrong one. For a CLARITY rewrite, confirm the
proven-equivalent version genuinely reads better and still carries its format string and description;
if the "cleaner" form is not actually clearer it is just churn, so keep the original. Either way, if
the equivalence proof was thin (grand-total only, no real grid), do not trust it.
