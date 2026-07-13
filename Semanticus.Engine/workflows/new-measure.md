---
name: new-measure
title: Author a verified measure
description: Create a measure with declared intent, verified against a known-good number.
whenToUse: "A single straightforward measure verified against one known-good number: the everyday default. For an edge-heavy or hard measure that must be correct across every filter context, use verified-measure; to rewrite an existing measure for speed or clarity, use optimize-dax."
version: 1
strictness: hard
triggers: [create_measure, update_measure]
---

## Step 1: Capture intent

Ask the user for the business definition. Record what the measure means, at
what grain it is meaningful, and what number it should produce for a context the user can
check independently. Prefer a measure over a calculated column for anything context-dependent;
push row-static values upstream to M/source, where they fold and cost no refresh memory.

```yaml gate
inputs:
  - name: verificationValue
    question: "A known-good number (or per-group matrix) to verify against, from the user, not derived."
    type: verification
    required: answer-or-decline
  - name: intendedFilterContext
    question: "Which dimensions/slicers must this measure respect?"
    type: text
    required: answer-or-decline
  - name: expectedGrain
    question: "At what grain is this measure meaningful?"
    type: text
    required: answer-or-decline
```

## Step 2: Author the DAX

Use `get_grounding` for naming, format, and sibling context. Follow the DAX
floor: DIVIDE over `/`, variables for readability and perf, COUNTROWS over COUNT,
SELECTEDVALUE over VALUES-with-error-trap, no FILTER as a filter argument, fully-qualified
column refs and unqualified measure refs. Run `validate_dax` and `lint_dax` before creating.

## Step 3: Create and verify

Create via `create_measure`; set the format string and description in the same step
(hygiene at authoring time, not cleanup debt). Confirm the number under the
intended filter context. Give `target` the ref of the measure you just
created so the gate can probe it against the user's known-good value.

```yaml gate
strictness: hard
inputs:
  - name: target
    question: "The ref of the measure you just created (e.g. measure:Sales/Total Sales)."
    type: objectRef
    required: required
verify:
  - kind: dax_probe
    when: inputs.verificationValue.answered
    probe: verificationValue
  - kind: bpa_clean
    scope: object
```
