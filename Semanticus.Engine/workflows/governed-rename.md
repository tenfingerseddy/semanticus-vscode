---
name: governed-rename
title: Safe rename
description: Assess every known consumer, stage the reference-aware rename for review, preserve representative values, record external-binding decisions, apply through FormulaFixup, replay the safety net, and produce one sealed certificate.
version: 2
strictness: hard
whenToUse: "When renaming an existing measure, column or table that may be referenced by formulas, reports, M text or external consumers. Use Check blast radius when you need the evidence and decision without an apply path."
triggers: [rename_object, add_plan_item]
---

## Step 1: Assess the rename before staging it

Name the exact object and proposed new name. Supply the local PBIR report folders in this review scope, or explicitly
decline the report-path question with the reason the certificate must be model-only. The gate runs T87 with
`intent: rename`, so TOM dependants, supplied reports, saved Tests, Interview questions and free-form binding gaps
are recorded together. An Unknown structural gap is never turned green: it is carried as skipped evidence into the
accountable review in Step 4.

```yaml gate
ops: [impact_assessment]
inputs:
  - name: target
    question: "The exact object ref to rename, for example measure:Sales/Total Sales or column:Sales/Amount."
    type: objectRef
    required: required
  - name: newName
    question: "The proposed new object name."
    type: text
    required: required
  - name: reportPaths
    question: "Local PBIR report folder paths in this review scope, separated by semicolons; or decline with why the certificate must be model-only."
    type: text
    required: answer-or-decline
verify:
  - kind: impact_assessment
    intent: rename
```

## Step 2: Stage the reference-aware preview

Call `add_plan_item` with the declared target, `kind:"rename"` and `after:newName`. Do not approve it yet. Review the
before and after name in Change Plan alongside the Step-1 impact. Record the returned plan item id. The gate reads
the shared Change Plan and passes only when that exact target and name remain proposed, so a pasted description or
premature apply cannot masquerade as a preview.

```yaml gate
ops: [add_plan_item, get_plan]
inputs:
  - name: planItemId
    question: "The proposed rename Change Plan item id returned by add_plan_item."
    type: text
    required: required
verify:
  - kind: plan_item_staged
    probe: planItemId
```

## Step 3: Preserve representative values when live evidence exists

Before applying, run `capture_baseline` on the target with `includeDependents:true` and representative `groupBy`
columns. Record the capture id. The gate requires an engine-held capture for this target with a real grid, no errors
and no truncation. If no live query model or no measurable downstream measure exists, decline with the exact reason;
that gap remains visible in the certificate.

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

## Step 4: Account for bindings FormulaFixup cannot own

FormulaFixup rewrites model DAX and security references. It does not own free-form M text, report definitions,
bookmarks, external scripts or client integrations. Review every Step-1 gap and report hit with the responsible owner.
Record what will be updated, or explicitly decline with why that binding cannot be reviewed now. A decline is residual
risk in the certificate, never a silent safe result.

```yaml gate
inputs:
  - name: externalBindingsReviewed
    question: "What M text, report fields, bookmarks, scripts or external consumers were reviewed, and who owns each required update?"
    type: text
    required: answer-or-decline
  - name: renameDecision
    question: "Proceed, revise or reject this rename based on the assessment, preview and external-binding review."
    type: text
    required: required
```

## Step 5: Apply once, then replay the evidence

Only after the recorded decision says proceed, approve the staged item with `set_plan_item` and apply that one id with
`apply_plan`. This is the reference-aware FormulaFixup path and one undoable mutation. If a value baseline exists,
deploy the local change to the query model used for capture before comparing it, then run `compare_baseline`. Record
that live-sync boundary below. Run the full Tests suite; answer `replayInterview` only when Step 1 scheduled a pack.

The gate proves the exact plan item applied and the renamed ref resolves, compares any recorded baseline, and replays
Tests plus the optional Interview pack. Moved/missing values, Test failures or a confidently wrong interview hold it.

```yaml gate
ops: [set_plan_item, apply_plan, compare_baseline, run_tests, run_interview]
inputs:
  - name: liveSyncConfirmed
    question: "Confirm where the rename was deployed for the baseline comparison, or state that no baseline was captured."
    type: text
    required: required
  - name: replayInterview
    question: "Answer yes when Step 1 scheduled Model Interview questions; otherwise decline and record that no current-model pack was scheduled."
    type: text
    required: answer-or-decline
verify:
  - kind: plan_item_applied
    probe: planItemId
  - kind: baseline_unchanged
    when: inputs.baselineCapture.answered
    probe: baselineCapture
  - kind: tests_replay
  - kind: interview_replay
    when: inputs.replayInterview.answered
```

## Step 6: Produce the rename certificate

Submit this step to complete the run. Then call `export_workflow_evidence` and export the self-contained HTML and
canonical JSON, call `save_evidence(source:"workflow", sourceId:...)` to keep the same sealed pair with the model,
or do both. The certificate carries the assessment verdict, scope, proposed and applied plan evidence, every decline,
baseline comparison and replay result.

```yaml gate
ops: [export_workflow_evidence, save_evidence]
inputs:
  - name: certificateDelivery
    question: "Where should the completed Safe rename certificate go: Save with model, local export, or both?"
    type: text
    required: required
```
