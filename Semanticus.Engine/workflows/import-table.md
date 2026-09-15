---
name: import-table
title: Import a table
description: Bring in one table, make its fields deliberate, and record an independent load check when available.
whenToUse: "Use when adding a fact or dimension table from a source. For a loaded table that needs a relationship, use Connect tables; for a large table needing a rolling window, use Set up rolling refresh."
version: 1
strictness: hard
triggers: [create_import_table, set_column_data_type, set_incremental_refresh_policy]
---

## Step 1: Choose the source and load the table

Name the source entity and the final model table. Call `create_import_table` for that entity, selecting
only fields needed for reporting and filtering rows early when the source query can fold. If the source or
table cannot be reached, leave the load as unavailable and say what must be supplied; do not report a load
as reconciled from the model's own row count.

```yaml gate
ops: [create_import_table]
inputs:
  - name: sourceTable
    question: "Which source entity should be imported, and what final table name should it have?"
    type: text
    required: required
```

## Step 2: Review types and exposed fields

Use `list_columns` after the table exists. Set deliberate types with `set_column_data_type`, hide keys and
other plumbing with `set_column_hidden`, and set `SummarizeBy = None` for keys, years and identifiers with
`set_summarize_by`. Split a datetime only when reporting needs separate date and time; do not turn every
small import into an incremental-refresh design. Hand a large rolling fact to Set up rolling refresh.

```yaml gate
ops: [list_columns, set_column_data_type, set_column_hidden, set_summarize_by]
```

## Step 3: Check a source control total

When a source row count or additive control total is available, create a small control measure and give
`target` its ref. The scalar probe compares the model result at the stated context with the independent
source value. A mismatch points to filtering, folding, joins or coercion and must be fixed before saving.
When no source number or query access exists, decline `controlTotal` with that reason; the result remains
an honest load that was not independently reconciled.

```yaml gate
strictness: hard
ops: [create_measure, probe_measure]
inputs:
  - name: target
    question: "The control measure ref, for example measure:Product/Product Row Count."
    type: objectRef
    required: required
  - name: controlTotal
    question: "A row count or additive total from the source system for the same scope; decline when no independent value or source access is available."
    type: verification
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.controlTotal.answered
    probe: controlTotal
```

## Step 4: Save the imported table

Call `save_model` and record the selected source fields, deliberate types and control result. A missing
control total stays visible as an unresolved check. Do not imply that incremental refresh or a live source
refresh happened in this workflow.

```yaml gate
ops: [save_model]
```
