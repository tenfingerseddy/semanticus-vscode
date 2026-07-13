---
name: incremental-refresh-setup
title: Set up incremental refresh and prove the window loads
description: Add the RangeStart/RangeEnd plumbing that MUST fold, apply a rolling-window policy on a large fact table, verify the partitions it produced, then PROVE the loaded window against a source control total before you trust it.
version: 1
strictness: hard
triggers: [set_incremental_refresh_policy]
---

## Step 1: Define the rolling window and check the fold

Incremental refresh partitions a large fact table so only recent data reloads. Decide the
window first: how many years to STORE and how many trailing days to REFRESH on each run. The plumbing is
two datetime parameters, `RangeStart` and `RangeEnd`, and a filter step on the fact's date column bounded
by them. THE folding caveat is decisive: that filter step MUST fold to the source, or
the refresh silently degrades to the mashup engine and incremental refresh breaks outright.
Be honest about the limit: folding introspection is NOT available offline; you cannot confirm the fold
from here. Instruct the user to verify "View Native Query" resolves on the filter step in Power BI Desktop
before relying on the policy.

```yaml gate
inputs:
  - name: factTable
    question: "Which fact table gets incremental refresh (e.g. Sales)?"
    type: text
    required: required
  - name: storeYears
    question: "How many years of history to STORE in the model (the archive window, e.g. 5)?"
    type: number
    required: required
  - name: refreshDays
    question: "How many trailing days to REFRESH on each run (the hot window, e.g. 10)?"
    type: number
    required: required
```

## Step 2: Plumb the parameters and apply the policy

Create the `RangeStart` and `RangeEnd` datetime parameters with `create_named_expression`, then bind the
fact's partition M to filter its date column between them with `set_partition_m`; filters early so the
step folds. Apply the rolling-window policy with `set_incremental_refresh_policy` using the
store/refresh answers from Step 1, then read it back with `get_incremental_refresh_policy` to confirm the
window landed as intended. Optionally enable detect-data-changes so only changed partitions reload.

```yaml gate
ops: [create_named_expression, set_partition_m, set_incremental_refresh_policy, get_incremental_refresh_policy]
```

## Step 3: Verify the partitions it produced

A policy is not proof: inspect what it generated. Run `list_partitions` AFTER applying the
policy: you should see the archive partitions plus the incremental buckets matching the window you set. If
the shape is wrong, the policy or the fold is off; fix it before any refresh. Refresh with
`refresh_partition` ONLY on a live model and ONLY with the user's explicit go-ahead; on a large fact table
a full refresh is expensive, so refresh a single recent partition first to confirm the mechanics.

```yaml gate
ops: [list_partitions, refresh_partition]
```

## Step 4: Prove the loaded window with a control total

A loaded window is unproven until a number confirms it. Author a control measure (a
`COUNTROWS` or `SUM` over the fact within the window) with `create_measure`, and give `target` its ref.
Ask the business for the matching figure from the SOURCE system over the same window (a row count or a
control total they computed independently, not a number derived by this model). The hard gate probes the
measure against that value; a mismatch means the filter dropped rows, the fold broke, or the window is
misaligned: fix and re-probe. The gate holds on this step until the number matches or you decline.

```yaml gate
strictness: hard
ops: [create_measure]
inputs:
  - name: verificationValue
    question: "A row count or control total from the SOURCE system over the refresh window, supplied by the user, not derived by the model (e.g. 'the OLTP reports 842,110 rows in the last 10 days')."
    type: verification
    required: answer-or-decline
  - name: target
    question: "The ref of the control measure you created to check the window (e.g. measure:Sales/Windowed Row Count)."
    type: objectRef
    required: required
verify:
  - kind: dax_probe
    when: inputs.verificationValue.answered
    probe: verificationValue
```

## Step 5: Save and note the hybrid option

Persist with `save_model` so the parameters, partition M, and policy land in the TMDL beside the model.
Record the store/refresh window and what the control measure verifies against, so the load can be
re-checked after any refresh. If near-real-time freshness matters, note the option of a HYBRID table:
import partitions plus exactly one DirectQuery partition for the latest data. Hybrid
is Premium-family only (incl. PPU / Embedded / F SKUs) and pairs best with Dual dimensions; flag it as a
follow-up, not part of this policy.

```yaml gate
ops: [save_model]
```
