---
name: refactor-to-calculation-group
title: Refactor repeated time-intelligence measures into one calculation group
description: Collapse a family of copy-pasted time-intel measures (PY, YTD, MAT, %growth…) into a single calculation group, and prove, item by item, that not one number changed. A structural refactor most people are scared to do by hand.
version: 1
strictness: hard
whenToUse: "When several base measures each repeat the SAME time-intelligence variants (Sales PY, Sales YTD, Sales MAT; Margin PY, Margin YTD…), dozens of near-identical measures. Use this to collapse them into one reusable calculation group without changing a single reported value; it is also the home for building a fresh calculation group of time-intelligence items. To expand ONE base measure into its variants, use time-intelligence-variants; not for a one-off single measure."
triggers: [create_calculation_group, create_calculation_item]
---

## Step 1: Inventory the repeated pattern

List the base measures (e.g. [Sales Amount], [Total Cost], [Margin]) and the time-intelligence VARIANTS
that repeat across them (PY, YTD, MAT, YoY %…). The calculation group will express each variant ONCE with
`SELECTEDMEASURE()`; every base measure then inherits all of them. Record the base measures and the exact
variant measures this refactor will replace; that list is your retirement scope and your proof checklist.

```yaml gate
inputs:
  - name: baseMeasures
    question: "The base measures the variants apply to (e.g. Sales Amount, Total Cost, Margin)."
    type: text
    required: required
  - name: variantMeasures
    question: "Every existing variant measure this will replace (e.g. Sales PY, Sales YTD, Margin YTD…): the full retirement list."
    type: text
    required: required
```

## Step 2: Freeze the ground truth

Before creating anything, capture what the current variant measures produce. For each variant, `run_dax` it
over a representative matrix (a couple of base measures × Year × Month) and keep the numbers. This frozen
vector is the ONLY evidence of "what it used to compute"; the calc group must reproduce it exactly.

```yaml gate
inputs:
  - name: baselineMatrix
    question: "The frozen before-values: each variant measure × the representative Year×Month matrix, from run_dax."
    type: text
    required: required
```

## Step 3: Build the calculation group and its items

`create_calculation_group`, then one `create_calculation_item` per variant, each written against
`SELECTEDMEASURE()` (e.g. PY = `CALCULATE ( SELECTEDMEASURE (), DATEADD ( 'Date'[Date], -1, YEAR ) )`). Set a
sensible `set_calc_group_precedence` if it must combine with other groups, and `set_calc_item_format_string`
where the variant changes the format (e.g. a % growth item). Keep the item names human ("PY", "YTD").

## Step 4: Prove every item reproduces the frozen numbers (the gate)

For each (base measure × calculation item) pair, `run_dax` the base measure with the item applied and compare
to the matching frozen value from Step 2. Every cell must match. If any differs, fix the item's DAX and
re-check; do NOT proceed on a mismatch (a calc group that changes a number is a silent data incident). Record
the full comparison; confirm all pairs matched.

```yaml gate
strictness: hard
inputs:
  - name: allItemsReproduceBaseline
    question: "Confirm EVERY (base × item) pair matched its Step-2 frozen value (paste the comparison). If any differed, you have not finished; fix and re-check."
    type: text
    required: required
```

## Step 5: Retire the redundant measures and re-verify the model

With equivalence proven, remove the now-redundant variant measures (`delete_object`, or hide them first if
reports still bind them by name; check `get_dependents` before deleting). Then a model-scope `bpa_clean` gate
confirms the refactor introduced no new best-practice violations and left the model consistent.

```yaml gate
strictness: hard
verify:
  - kind: bpa_clean
    scope: model
```
