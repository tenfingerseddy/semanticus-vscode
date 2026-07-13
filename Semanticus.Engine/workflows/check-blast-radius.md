---
name: check-blast-radius
title: Check blast radius
description: Assess a risky model change, preserve representative values when live evidence is available, replay the affected safety checks, record the decision, and produce one sealed evidence certificate.
whenToUse: "Before changing, renaming, removing or restructuring an existing model object when downstream measures, reports or saved checks may depend on it. Use Safe rename for the guided apply path when the intended change is specifically a rename."
version: 1
strictness: hard
triggers: [impact_assessment, capture_baseline, delete_object, rename_object]
---

## Step 1: Assess the declared change

Name the exact object and describe the intended change before editing it. For report-aware coverage, provide the
local PBIR report folders that define this review scope. If those definitions are unavailable, explicitly decline
the report-path question with the reason; the certificate will say model-only and will not imply reports were checked.

The gate runs the engine-owned `impact_assessment`. Known impact is expected and is recorded as NeedsReview.
Unknown coverage or a broken proposal holds the step until the scope or proposal is corrected.

```yaml gate
ops: [impact_assessment]
inputs:
  - name: target
    question: "The exact object ref being changed, for example measure:Sales/Total Sales or column:Sales/Amount."
    type: objectRef
    required: required
  - name: changeIntent
    question: "Describe the intended change and why it is needed."
    type: text
    required: required
  - name: reportPaths
    question: "Local PBIR report folder paths in this review scope, separated by semicolons; or decline with why this certificate must be model-only."
    type: text
    required: answer-or-decline
verify:
  - kind: impact_assessment
```

## Step 2: Preserve representative values where they can be measured

When the target or its dependants include measures and a live query model is available, run `capture_baseline`
before any edit with `includeDependents:true` and representative `groupBy` columns. Record its `captureId` below.
The gate checks the engine-held capture belongs to this target, has a real context grid, and has no errors,
truncation or empty coverage. It never accepts a pasted claim in place of a capture.

If no live connection or no measurable downstream measure exists, decline with the exact reason. That gap remains
part of the certificate and the later decision must account for it.

```yaml gate
ops: [capture_baseline]
inputs:
  - name: baselineCapture
    question: "The captureId returned by a representative capture_baseline run; or decline with why measured value evidence is unavailable."
    type: text
    required: answer-or-decline
verify:
  - kind: baseline_exists
    when: inputs.baselineCapture.answered
    probe: baselineCapture
```

## Step 3: Replay the safety net

Run the complete Tests suite. This is a conservative superset of the affected saved checks named by the assessment:
ambient integrity and security checks run too, so a structural regression outside a narrow ref binding is not missed.
Failures and missing saved-test bindings hold the gate; unverified coverage is disclosed rather than counted as pass.

If the assessment scheduled Model Interview questions, answer `replayInterview` and the engine replays the current
model's pack. If no pack was scheduled, decline with that reason instead of inventing evidence.

```yaml gate
ops: [run_tests, run_interview]
inputs:
  - name: replayInterview
    question: "Answer yes when the assessment scheduled Model Interview questions for replay; otherwise decline and record that no current-model pack was scheduled."
    type: text
    required: answer-or-decline
verify:
  - kind: tests_replay
  - kind: interview_replay
    when: inputs.replayInterview.answered
```

## Step 4: Record the decision and residual risk

Review the known dependants, report scope, value-capture coverage and replay results together. Record whether the
change should proceed, be revised or be rejected. This is a decision on the evidence collected, not a claim that
unseen external bindings are safe.

```yaml gate
inputs:
  - name: decision
    question: "Proceed, revise or reject - and the evidence-based reason for that decision."
    type: text
    required: required
  - name: residualRisk
    question: "List every remaining coverage gap or external binding an owner must review; write none only when the recorded evidence supports it."
    type: text
    required: required
```

## Step 5: Produce the certificate

Submit this final step to complete the run. Then call `export_workflow_evidence` for the run and either export the
self-contained HTML and canonical JSON or call `save_evidence(source:"workflow", sourceId:...)` to place the same
sealed pair in the model's Evidence library. The artifact carries every answer, decline and engine verify result.

```yaml gate
ops: [export_workflow_evidence, save_evidence]
inputs:
  - name: certificateDelivery
    question: "Where should the completed certificate go: Save with model, local export, or both?"
    type: text
    required: required
```
