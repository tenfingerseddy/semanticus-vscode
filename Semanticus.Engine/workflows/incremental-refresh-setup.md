---
name: incremental-refresh-setup
title: Set up rolling refresh
description: Check folding prerequisites, configure a rolling window, inspect partitions and record an optional control check.
whenToUse: "Use for a large fact table that needs only recent data refreshed. For a normal small-table import, use Import a table; for a date calendar, use Set up a calendar."
version: 1
strictness: hard
triggers: [set_incremental_refresh_policy]
---

## Step 1: Choose the fact and rolling window

Name the fact table, archive years and trailing refresh days. The RangeStart and RangeEnd filter must fold
to the source. The engine can create the parameters and policy but cannot prove folding offline; use Power BI
Desktop View Native Query or an equivalent source check and record that external result honestly.

```yaml gate
inputs:
  - name: factTable
    question: "Which large fact table gets incremental refresh?"
    type: text
    required: required
  - name: storeYears
    question: "How many years of history should remain stored?"
    type: number
    required: required
  - name: refreshDays
    question: "How many trailing days should refresh on each run?"
    type: number
    required: required
```

## Step 2: Add the parameters and policy

Create or repair RangeStart and RangeEnd, bind the half-open date filter in the partition M, then call
`set_incremental_refresh_policy` and read it back with `get_incremental_refresh_policy`. Show the returned
window and any prerequisites. Do not claim that metadata setup refreshed data.

```yaml gate
ops: [create_named_expression, set_partition_m, set_incremental_refresh_policy, get_incremental_refresh_policy]
```

## Step 3: Inspect partitions and optionally check one window

Call `list_partitions` and compare the archive and incremental buckets with the chosen window. A single
recent `refresh_partition` is an optional live operation and requires the user's explicit go-ahead. When an
independent source count or additive total is available, create a control measure and probe it with `target`;
otherwise decline `verificationValue`. Folding, a live refresh and the scalar proof are separate evidence
items, so one cannot stand in for another.

```yaml gate
strictness: hard
ops: [list_partitions, refresh_partition, create_measure, probe_measure]
inputs:
  - name: target
    question: "Optional control measure ref for the selected window; leave blank when no control measure is being checked."
    type: objectRef
    required: optional
  - name: verificationValue
    question: "An independent source count or total for the same window; decline when unavailable."
    type: verification
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.verificationValue.answered
    probe: verificationValue
```

## Step 4: Save the policy

Call `save_model`. Record the window, folding evidence, partition shape and any refresh or control result.
If the model needs a hybrid table, list it as a separate follow-up; it is not silently created here.

```yaml gate
ops: [save_model]
```
