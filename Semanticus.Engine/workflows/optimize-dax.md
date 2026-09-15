---
name: optimize-dax
title: Fix a slow measure
description: Freeze a measure, compare a rewrite across useful contexts, and keep it only when the evidence supports it.
whenToUse: "Use when an existing measure is slow or hard to maintain and its meaning should stay the same. Choose SPEED for measured timing work or READABILITY when the goal is a clearer equivalent expression. For a new measure, use Add a measure; for difficult context semantics, use Test a complex measure."
version: 1
strictness: hard
triggers: [benchmark_dax, profile_dax, update_measure]
---

## Step 1: Freeze the original and choose the goal

Use `get_dax` before editing and paste the exact expression into `originalDax`; the engine has no automatic
field that captures a read result into a later gate. Name the target measure, choose SPEED or READABILITY,
and list the group-by columns that can expose a changed result. For SPEED, run `benchmark_dax` and record
the comparable warm baseline. For READABILITY, decline the timing baseline and skip timing work.

```yaml gate
inputs:
  - name: target
    question: "The measure being rewritten, for example measure:Sales/Net Sales."
    type: objectRef
    required: required
  - name: goal
    question: "Choose SPEED when a comparable timing matters, or READABILITY when the goal is a clearer equivalent expression."
    type: text
    required: required
  - name: originalDax
    question: "The exact expression returned by get_dax before the rewrite."
    type: text
    required: required
  - name: equivalenceGrid
    question: "Qualified group-by columns for the equivalence check, such as 'Date'[Year], 'Product'[Category]."
    type: text
    required: required
  - name: speedBaselineMs
    question: "For SPEED, the pre-rewrite warm median in milliseconds from benchmark_dax; decline for READABILITY or when no live timing is available."
    type: number
    required: answer-or-decline
```

## Step 2: Inspect the measured bottleneck when SPEED is selected

For SPEED, use `profile_dax`, `capture_query_plan` and `clear_cache` only when the connected environment
supports them, then repeat the same benchmark shape. For READABILITY, go straight to the rewrite. A local
or admin XMLA connection is needed for server plans and timings; offline work must say that performance is
unverified rather than inventing a gain.

```yaml gate
ops: [profile_dax, capture_query_plan, clear_cache, benchmark_dax]
```

## Step 3: Rewrite and prove the meaning stayed the same

Apply one focused `update_measure` after `validate_dax` and `lint_dax`, keeping the existing format and
description. The hard equivalence check compares the frozen expression with the current one over the
declared grid. A grand-total match is too thin; if a product or customer context differs, revert with
`update_measure` and keep the original.

```yaml gate
strictness: hard
ops: [validate_dax, lint_dax, update_measure]
verify:
  - kind: dax_equivalence
    probe: originalDax
```

## Step 4: Keep the supported result

For SPEED, rerun the same benchmark and let `benchmark_delta` compare it with `speedBaselineMs`; only a
measured comparable result can support “faster”. For READABILITY, review the proven-equivalent expression
and retain it only when it is actually clearer. If the timing is unavailable or regresses, revert rather
than claiming success. Save after the chosen version is accepted.

```yaml gate
strictness: hard
ops: [benchmark_dax, save_model]
verify:
  - kind: benchmark_delta
    when: inputs.speedBaselineMs.answered
    probe: speedBaselineMs
```
