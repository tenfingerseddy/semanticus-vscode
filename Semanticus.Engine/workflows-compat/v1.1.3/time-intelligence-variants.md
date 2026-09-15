---
name: time-intelligence-variants
title: Expand one base measure into verified time-intelligence variants
description: Take ONE base measure and author its period-over-period variants (PY, YoY %, YTD, QTD, MAT) as a consistent, well-named set (calendar-aware where calendars exist), then verify a representative PY and a representative YTD against known-good numbers before you trust the set. The single-measure counterpart to refactor-to-calculation-group, which collapses the SAME variants across MANY base measures into one calculation group.
whenToUse: "You have ONE base measure and want its standard period-over-period variants (PY, YoY %, YTD, QTD, MAT) authored consistently and verified. To collapse the same variants across MANY base measures into one reusable calculation group, use refactor-to-calculation-group; for a single non-time measure use new-measure."
version: 1
strictness: hard
triggers: [generate_time_intelligence, create_measure]
---

## Step 1: Name the base measure and pick the variants

Name the ONE base measure these variants wrap, and confirm it is itself correct: a wrong base makes
every variant wrong in lockstep. Choose exactly which variants you need rather than
generating all of them by reflex: PY (prior year), YoY % (growth), YTD / QTD / MTD (to-date), MAT
(moving annual total). Check the date table with `list_calendars`: if calendars exist, PREFER the
calendar-name overloads (e.g. `TOTALYTD([Base], 'Fiscal')`) so the variants shift hierarchically and
respect fiscal/ISO/4-4-5 shapes; otherwise use the classic date-column patterns against the marked
date table. Decide the naming convention for the set up front so the variants read as a family.

```yaml gate
inputs:
  - name: baseMeasure
    question: "The single base measure to expand (e.g. measure:Sales/Total Sales), and confirmation it is already correct."
    type: objectRef
    required: required
  - name: variants
    question: "Which variants to author (e.g. PY, YoY %, YTD, MAT), the naming convention for the set, and whether calendars exist to author against (list_calendars)."
    type: text
    required: required
```

## Step 2: Author the variants consistently

Author each variant with `create_measure` (or `generate_time_intelligence` to seed the set, then
refine). Keep them a coherent family: the naming convention from Step 1, a format string per variant
set in the same step (a YoY % renders as a percentage; a YTD inherits the base measure's format), and
a description carrying what the variant means. Follow the time-intelligence floor: variables, the
right to-date function, calendar-name overloads where calendars exist, and DATEADD/SAMEPERIODLASTYEAR
against the marked date table where they do not. Do not leave any variant unformatted or unnamed;
hygiene at authoring time, not cleanup debt.

```yaml gate
ops: [create_measure, generate_time_intelligence, set_measure_format, set_description, list_calendars]
```

## Step 3: Verify a representative PY (HARD GATE)

A whole variant set can be wrong from one systematic error: a mis-mapped calendar, a wrong to-date
boundary, an off-by-one on the year shift. The year-shift family (PY, YoY %) and the to-date family
(YTD, QTD, MAT) fail in DIFFERENT ways, so one check can't clear both: this step proves a representative
PY and Step 4 proves a representative YTD, two distinct checks against two distinct measures. Verify the
PY here: give `target` the PY variant and a known-good figure at a context the business can confirm
independently (last year's full-year total), then probe it. The gate holds here until the PY matches or
you decline.

```yaml gate
strictness: hard
ops: [probe_measure, pivot_measure]
inputs:
  - name: target
    question: "The PY variant to verify (e.g. measure:Sales/Sales PY)."
    type: objectRef
    required: required
  - name: pyVerification
    question: "A known-good PY figure at a context you can check independently (e.g. 'FY2023 full-year revenue = 3.8M')."
    type: verification
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.pyVerification.answered
    probe: pyVerification
```

## Step 4: Verify a representative YTD (HARD GATE)

Now prove the to-date logic independently of the year shift: a PY that matched but a YTD that does not
localises the bug to the to-date boundary, not the year shift, which is exactly why the PY check above
is not enough on its own. Give `ytdTarget` the YTD (or QTD/MAT) variant and a known-good YTD figure at a
context you can check (a specific month's YTD), then probe it. The probe targets the LATEST variant you
name, so naming the YTD here points this second check at it. The gate holds here until the YTD matches or
you decline.

```yaml gate
strictness: hard
ops: [probe_measure, pivot_measure]
inputs:
  - name: ytdTarget
    question: "The YTD (or QTD/MAT) variant to verify (e.g. measure:Sales/Sales YTD): the second, distinct check, on a different measure than the PY."
    type: objectRef
    required: required
  - name: ytdVerification
    question: "A known-good YTD figure at a context you can check independently (e.g. 'FY2024 YTD to March = 1.1M')."
    type: verification
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.ytdVerification.answered
    probe: ytdVerification
```

## Step 5: Save the family

Confirm the set is consistent (same naming convention, every variant formatted and described), then
persist with `save_model` so the variants land in the TMDL beside the base measure. Note which two
figures you verified against, so a future refresh or base-measure change has a known re-check point.

```yaml gate
ops: [save_model]
```
