---
name: new-measure
title: Add a measure
description: Define a measure, check the cases that matter, and save the result with its meaning.
whenToUse: "Use for a straightforward measure with a clear business meaning. For ratios, time logic or other context-sensitive measures, use Test a complex measure; to improve an existing measure, use Fix a slow measure."
version: 1
strictness: hard
triggers: [create_measure, update_measure]
---

## Step 1: Define the question

State what the measure means and where it is useful. Include the filter context or grain that would
expose a likely mistake, such as Net sales by Product category and Customer region. Do not restate table
or column facts the model already provides. Ask for an independent expected value when one is available;
it may be declined when there is no trusted source number.

```yaml gate
inputs:
  - name: meaning
    question: "What business question does this measure answer, and which context or grain must it respect?"
    type: text
    required: required
  - name: verificationValue
    question: "An independently known result for a useful context, including its context if it is not the grand total; decline with why none is available."
    type: verification
    required: answer-or-decline
```

## Step 2: Author and create the measure

Use `get_grounding` for the model's naming and sibling conventions. Write the DAX, run `validate_dax`
and `lint_dax`, then call `create_measure` with the final name, format string and description. The model
holds the new measure ref; do not ask the user to copy table facts into this run.

```yaml gate
ops: [get_grounding, validate_dax, lint_dax, create_measure, set_measure_format, set_description]
```

## Step 3: Check the selected case

Use `probe_measure` or a focused live query for the context from Step 1. Give `target` the new measure
ref. When an independent value was supplied, the engine's scalar probe compares that value; a mismatch
holds the step for correction. Without one, report that the measure was exercised but not reconciled to
an independent number. A grand-total match alone does not prove the stated context.

```yaml gate
strictness: hard
ops: [probe_measure]
inputs:
  - name: target
    question: "The ref of the measure just created, for example measure:Sales/Net Sales."
    type: objectRef
    required: required
verify:
  - kind: dax_probe
    when: inputs.verificationValue.answered
    probe: verificationValue
  - kind: bpa_clean
    scope: object
```

## Step 4: Save the result

Call `save_model`. Record the meaning, checked context and any missing independent answer with the handoff
so the next change knows what was actually checked. The workflow does not claim coverage of every filter
combination.

```yaml gate
ops: [save_model]
```
