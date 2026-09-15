---
name: check-blast-radius
title: Review a risky change
description: Assess a risky model change, capture relevant evidence, replay saved checks, and record the remaining risk without applying the change.
whenToUse: "Use before removing, restructuring or otherwise changing an object with downstream use. For a rename that should be applied with a plan, use Rename with impact review; for a quick dependency lookup, use Lineage."
version: 1
strictness: hard
triggers: [impact_assessment, capture_baseline, delete_object, rename_object]
---

## Step 1: Assess the proposed change

Name the exact object and intended change before editing. Supply local PBIR report paths when report-aware
coverage is available; decline them with a reason when this review is model-only. `impact_assessment`
reports known model, report, saved-test and interview use. Known dependants are findings to review, not a
failed proposal; missing scope stays visible.

```yaml gate
ops: [impact_assessment]
inputs:
  - name: target
    question: "The exact object ref being changed, for example measure:Sales/Net Sales or column:Sales/Amount."
    type: objectRef
    required: required
  - name: changeIntent
    question: "What change is being considered and why?"
    type: text
    required: required
  - name: reportPaths
    question: "Local PBIR report folders in scope, separated by semicolons; decline with why the review is model-only when unavailable."
    type: text
    required: answer-or-decline
verify:
  - kind: impact_assessment
```

## Step 2: Capture values when the change can be measured

Before any edit, call `capture_baseline` for the target and its measurable dependants with representative
group-by columns. Record the engine capture id. If there is no live query model or no measurable value,
decline with the exact gap; a pasted number is not a baseline and the later decision must carry the limit.

```yaml gate
ops: [capture_baseline]
inputs:
  - name: baselineCapture
    question: "The captureId from a representative baseline, or a reason measured evidence is unavailable."
    type: text
    required: answer-or-decline
verify:
  - kind: baseline_exists
    when: inputs.baselineCapture.answered
    probe: baselineCapture
```

## Step 3: Replay the saved checks

Run `run_tests` for the current model and export the result with `export_test_report`. The suite's saved
SQL comparisons, relationship checks and static security checks are the reusable safety net. A missing
endpoint, failed comparison or partial coverage remains NotVerifiable or failed in the report; it is not
silently turned into a pass. Inspect the report before deciding.

```yaml gate
ops: [run_tests, export_test_report]
verify:
  - kind: tests_replay
```

## Step 4: Decide and preserve the review record

Record whether to proceed, revise or reject, and list every remaining report binding, live-connection gap,
failed check or unresolved dependency. Export the terminal workflow evidence and save it only when the
project's evidence record calls for it. This workflow remains apply-free; use the appropriate editor or
rename workflow after the decision.

```yaml gate
ops: [export_workflow_evidence, save_evidence]
inputs:
  - name: decision
    question: "Proceed, revise or reject, with the evidence-based reason."
    type: text
    required: required
  - name: residualRisk
    question: "What coverage gaps or external bindings remain for an owner to review?"
    type: text
    required: required
```
