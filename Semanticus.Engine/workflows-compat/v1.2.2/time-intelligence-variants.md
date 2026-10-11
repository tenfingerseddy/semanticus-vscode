---
name: time-intelligence-variants
title: Add time comparisons
description: Select the needed variants for one trusted measure, generate them, check a representative context and save.
whenToUse: "Use when one base measure needs selected prior-period or to-date measures. Set up a calendar first when date support is missing; for the same variants across many measures, use Replace repeated measures with a calculation group."
version: 1
strictness: hard
triggers: [generate_time_intelligence, create_measure]
---

## Step 1: Choose the base and variants

Name one trusted base measure and the variants that are actually needed, such as PY, YoY %, YTD, QTD,
MTD or MAT. Call `list_calendars` and prefer its calendar-name overload when a calendar exists; otherwise
use the marked date column. Choose the naming convention before creating the family.

```yaml gate
inputs:
  - name: baseMeasure
    question: "The single trusted base measure to expand."
    type: objectRef
    required: required
  - name: variants
    question: "Which variants and naming convention should be created, and which calendar/date support does the model have?"
    type: text
    required: required
```

## Step 2: Generate only the selected variants

Use `generate_time_intelligence` with the selected variants or author them with `create_measure`. Review
the expressions, format strings and descriptions; do not create the full default suite when the job needs
only one or two comparisons.

```yaml gate
ops: [list_calendars, generate_time_intelligence, create_measure, set_measure_format, set_description]
```

## Step 3: Check one representative boundary

Choose the generated variant that exposes the likely risk: PY at a year boundary or YTD at a period end.
Give `target` its ref and provide an independent expected value for that context, or decline when none is
available. This scalar probe is representative evidence for the selected family; it does not prove every
generated variant. Use `pivot_measure` for additional contexts when a live connection is available.

```yaml gate
strictness: hard
ops: [probe_measure, pivot_measure]
inputs:
  - name: target
    question: "The generated variant selected for the representative check."
    type: objectRef
    required: required
  - name: verificationValue
    question: "An independently known result for the selected variant and boundary context; decline when unavailable."
    type: verification
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.verificationValue.answered
    probe: verificationValue
```

## Step 4: Save the family

Call `save_model` and record the selected variants, calendar choice and representative result. Link a
many-base-measure repetition to the calculation-group workflow rather than expanding this run.

```yaml gate
ops: [save_model]
```
