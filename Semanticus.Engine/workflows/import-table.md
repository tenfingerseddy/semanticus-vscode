---
name: import-table
title: Import a table and prove it loaded correctly
description: Bring in an import table, type every column, tame datetime cardinality, hide the plumbing, lay the incremental-refresh groundwork, then PROVE the load against a control total from the source before you trust a single number.
whenToUse: "Bring a table into the model (fact or dimension) and prove it loaded correctly against a known source figure. Use it whenever a broken fold, a dropped filter, or a coerced type could silently lose or duplicate rows. To connect two tables you have already loaded, use add-relationship."
version: 1
strictness: hard
triggers: [create_import_table, set_column_data_type, set_incremental_refresh_policy]
---

## Step 1: Import the table

Create one query per entity with `create_import_table`, named as the final table name.
Stage shared logic in referenced queries with load disabled. Select only the
columns you will report on and filter unneeded rows as early as possible: the cheapest
VertiPaq win is data that never arrives.

## Step 2: Type every column deliberately

Never trust type inference. Walk every column with `list_columns` and set
its type with `set_column_data_type`: whole number vs decimal, Fixed Decimal for money (float
sums drift), integer keys where possible, no numeric data stored as text. Keep steps foldable
so incremental refresh still works. Name the table below so the gate can check it.

```yaml gate
inputs:
  - name: target
    question: "The ref of the table you are hardening (e.g. table:Sales)."
    type: objectRef
    required: required
  - name: typesConfirmed
    question: "Have you set a deliberate data type on every column (money = Fixed Decimal, keys = integer)?"
    type: text
    required: answer-or-decline
verify:
  - kind: bpa_clean
    scope: model
```

## Step 3: Split datetime columns

A datetime column's cardinality explodes VertiPaq dictionaries. Split each
datetime into a separate date column (365/yr) and a time-of-day column (86,400 max), or drop
the time entirely if it is never reported. Separate columns compress far better and join to
Date/Time dimensions. `vpaq_scan` surfaces the high-cardinality symptom so you prioritise.

## Step 4: Hide the plumbing and set summarization

Hide every key column (FK/PK), RLS/sort-by helper, and any fact column exposed through a
measure with `set_column_hidden`: the field list should show only what a report author
should touch. Set `SummarizeBy = None` on keys, years, and IDs with
`set_summarize_by` so nothing silently SUMs; prefer explicit measures over implicit
aggregation.

```yaml gate
verify:
  - kind: bpa_clean
    scope: model
```

## Step 5: Prepare incremental-refresh plumbing

Even if the policy comes later, add the plumbing now. Create `RangeStart` and
`RangeEnd` datetime parameters with `create_named_expression` plus the filter step that folds;
a broken fold silently moves work to the mashup engine and breaks incremental refresh. When
ready, apply the rolling window with `set_incremental_refresh_policy` and confirm partitions
with `list_partitions`.

```yaml gate
inputs:
  - name: irDecision
    question: "Will this table use incremental refresh? If yes, is the RangeStart/RangeEnd filter confirmed foldable?"
    type: text
    required: answer-or-decline
```

## Step 6: Prove the load (HARD GATE)

A loaded table is unproven until a control total confirms it, fact or dimension. Author
a control measure over the table with `create_measure`: a `COUNTROWS` for a row count, or a `SUM` over
an additive column, and give `target` its ref. Ask the business for the matching figure from the
SOURCE system: a row count or a control total they computed independently, NOT a number this model
derived. The hard gate probes the measure against that value; if it differs, the load is wrong (a
filter dropped rows, a fold broke, a join fanned out, or a type coerced), so fix it and re-probe. The
gate holds on this step until the number matches or you decline the check.

```yaml gate
strictness: hard
ops: [create_measure, probe_measure]
inputs:
  - name: controlMeasure
    question: "The ref of the control measure you created to check the load (e.g. measure:Sales/Order Line Count)."
    type: objectRef
    required: required
  - name: controlTotal
    question: "A control total or row count from the SOURCE system, supplied by the user, not derived by the model (e.g. 'the OLTP system reports 1,204,882 order lines for 2024')."
    type: verification
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.controlTotal.answered
    probe: controlTotal
```
