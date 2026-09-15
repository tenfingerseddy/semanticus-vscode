---
name: governed-rename
title: Rename with impact review
description: Review model and report uses, preview one reference-aware rename, apply it once, and record remaining external work.
version: 2
strictness: hard
whenToUse: "Use when renaming a published measure, column or table may affect reports or external text. For a quick model-only rename, use the direct rename action; for review without applying, use Review a risky change."
triggers: [rename_object, add_plan_item]
---

## Step 1: Assess the rename

Name the exact object and proposed new name. Supply local PBIR report folders or decline with why the review
is model-only. `impact_assessment` records TOM dependants, supplied report uses, saved tests, interview
questions and unknown bindings. FormulaFixup cannot prove free-form M, bookmarks, scripts or client bindings.

```yaml gate
ops: [impact_assessment]
inputs:
  - name: target
    question: "The exact object ref to rename, for example measure:Sales/Net Sales or column:Sales/Amount."
    type: objectRef
    required: required
  - name: newName
    question: "The proposed new object name."
    type: text
    required: required
  - name: reportPaths
    question: "Local PBIR report folders in scope, separated by semicolons; decline with why the review is model-only when unavailable."
    type: text
    required: answer-or-decline
verify:
  - kind: impact_assessment
    intent: rename
```

## Step 2: Stage and review the exact rename

Call `add_plan_item` with `kind: rename` and `after: newName`, then inspect it with `get_plan`. Do not apply
until the target and new name match the Step-1 decision. `plan_item_staged` checks the shared plan item,
so a pasted description cannot stand in for the preview.

```yaml gate
ops: [add_plan_item, get_plan]
inputs:
  - name: planItemId
    question: "The staged rename plan item id whose target and proposed name were reviewed."
    type: planItem
    required: required
verify:
  - kind: plan_item_staged
    probe: planItemId
```

## Step 3: Capture representative values when possible

Call `capture_baseline` before applying, including measurable dependants and a useful group-by grid. Record
the capture id or decline with the live-query gap. A model-only rename can continue, but the missing value
evidence remains in the run.

```yaml gate
ops: [capture_baseline]
inputs:
  - name: baselineCapture
    question: "The captureId from the representative baseline, or the exact reason a live value baseline is unavailable."
    type: text
    required: answer-or-decline
verify:
  - kind: baseline_exists
    when: inputs.baselineCapture.answered
    probe: baselineCapture
```

## Step 4: Apply once and replay the relevant evidence

Record the external bindings that need owners, state whether the reviewed rename should proceed, then approve
the exact plan item and call `apply_plan`. If a baseline exists, deploy the local change to the same query
model before `compare_baseline`; the comparison is not valid against an undeployed query model. Run the saved
Tests suite and export its report. Moved or missing values, failed tests and unresolved external bindings
remain review findings.

```yaml gate
ops: [set_plan_item, apply_plan, compare_baseline, run_tests, export_test_report]
inputs:
  - name: externalBindingsReviewed
    question: "Which M text, reports, bookmarks, scripts or clients need follow-up, and who owns each one? Decline with the remaining gap when unknown."
    type: text
    required: answer-or-decline
  - name: renameDecision
    question: "Proceed, revise or reject this rename based on the assessment, plan preview and external-binding review."
    type: text
    required: required
verify:
  - kind: plan_item_applied
    probe: planItemId
  - kind: baseline_unchanged
    when: inputs.baselineCapture.answered
    probe: baselineCapture
  - kind: tests_replay
```

## Step 5: Export the rename record

Export the terminal workflow evidence and save the evidence artifact when the project needs it. The record
contains the impact scope, exact plan item, baseline result, test report and every external gap. A normal
model-only rename can continue through the direct rename action without this workflow.

```yaml gate
ops: [export_workflow_evidence, save_evidence]
```
