---
name: refactor-to-calculation-group
title: Replace repeated measures with a calculation group
description: Replace a selected repeated variant family with calculation items, compare representative outputs, and retire only chosen measures.
version: 1
strictness: hard
whenToUse: "Use when many base measures repeat the same time variants. For variants on one base measure, use Add time comparisons; for general cleanup, use Tidy a model."
triggers: [create_calculation_group, create_calculation_item]
---

## Step 1: Choose the repeated family

List the base measures and the exact variant measures that the group is intended to replace. This is the
retirement scope. Inspect dependants before planning deletion; report bindings may still use a variant by
name even when the model has no downstream DAX reference.

```yaml gate
inputs:
  - name: baseMeasures
    question: "Which base measures should receive the calculation items?"
    type: text
    required: required
  - name: variantMeasures
    question: "Which existing variant measures are candidates for replacement?"
    type: text
    required: required
```

## Step 2: Capture a representative before matrix

Use `run_dax` over a small Year by Month or equivalent context matrix for the selected base and variant
measures. Record those before-values and the contexts. This is an authored comparison plan, not an engine
certificate; the current runner has no calculation-group-to-old-measure equivalence verify.

```yaml gate
ops: [run_dax]
inputs:
  - name: baselineMatrix
    question: "The representative before-values and contexts captured with run_dax for each selected base and variant."
    type: text
    required: required
```

## Step 3: Build the calculation group

Create one group and one item for each selected variant using `SELECTEDMEASURE()` and the model's marked
date or calendar. Set precedence and dynamic format strings when other calculation groups or percentage
items require them. Keep item names human and review the generated expressions before continuing.

```yaml gate
ops: [create_calculation_group, create_calculation_item, set_calc_group_precedence, set_calc_item_format_string]
```

## Step 4: Compare outputs and review dependants

Run the same contexts again and compare every selected base by item with the before matrix. A mismatch is a
refactor defect, not a reason to edit the old values to fit. Use `impact_of` and report inspection for each
candidate retirement. Record the comparison and any unresolved report binding. The text matrix is useful
review evidence, but it is not machine-equivalence proof in this engine version.

```yaml gate
strictness: hard
ops: [run_dax, impact_of, analyze_reports]
inputs:
  - name: comparisonResult
    question: "The after matrix against the same contexts, every mismatch, and each remaining model/report dependant."
    type: text
    required: required
verify:
  - kind: bpa_clean
    scope: model
```

## Step 5: Retire only selected replacements and save

Delete or hide a variant only when the review selected it and no report or model dependant still needs its
name. Do not perform a mass deletion. Call `save_model` and record any variants left in place for a later
report migration. A full machine-backed comparison remains an implementation gap; this run must not call
the refactor certified.

```yaml gate
ops: [delete_object, save_model]
inputs:
  - name: retireDecision
    question: "Which selected variants were retired or kept, and why does each remaining dependant make a keep necessary?"
    type: text
    required: required
```
