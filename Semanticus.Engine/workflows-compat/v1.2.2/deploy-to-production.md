---
name: deploy-to-production
title: Publish changes
description: Choose the configured destination, preview the actual change, publish under its current permission gate, and record what happened.
version: 1
strictness: hard
whenToUse: "Use when reviewed model changes are ready for a configured test or production destination. For a risky edit that still needs impact review, use Review a risky change; for a report-dependent rename, use Rename with impact review."
triggers: [deploy_stage, deploy_live, preview_deploy]
---

## Step 1: Choose the destination and change set

Call `model_diff` once and review the changed objects and the verified-edit trail. Name the actual target
workspace, pipeline stage or live model. Do not invent a dev-to-test-to-prod route when this project has no
such configured pipeline. A wrong destination or an unexpected local edit stops the run here.

```yaml gate
ops: [model_diff, list_verified_edits]
inputs:
  - name: targetStage
    question: "Which configured workspace, pipeline stage or live model is the intended destination?"
    type: text
    required: required
```

## Step 2: Preview the relevant checks

Confirm the connection and run `preview_deploy` plus `deploy_gate` against the named target. Review parameter
rebinding, creates, updates and any blocking findings. If the model has roles, call `list_roles` and perform
the available role check; if it has none, record that RLS was not applicable. `run_dax` cannot impersonate a
role, so a simulation is not a published-role proof. Do not call an old readiness or BPA warning a new
failure without reading the target-specific gate result.

```yaml gate
ops: [connection_status, preview_deploy, deploy_gate, list_roles, run_dax]
inputs:
  - name: previewReviewed
    question: "What did the target preview and deploy gate show, and which RLS path applies: published role test, offline simulation, or no roles?"
    type: text
    required: required
```

## Step 3: Publish with the configured permission

Use the operation that matches the chosen destination: `deploy_stage` for a pipeline stage, `deploy_live`
for XMLA metadata writeback, or `cicd_publish` for a configured full-definition publish. These operations
are dry-run or human-gated as described by their results. Keep the change local when the preview is wrong,
the gate blocks, or the required target permission is absent. A successful metadata publish is not a data
refresh or usage-monitoring result.

```yaml gate
ops: [deploy_stage, deploy_live, cicd_publish]
```

## Step 4: Record the actual result

Read `deployment_history` when a pipeline was used and call `save_model` when the local recovery point should
be captured. Record the target, operation, result, permission or human token used, and any required post-
publish role or refresh check. Mention monitoring only when an actual monitoring result exists; do not imply
that a history row proved refresh health.

```yaml gate
ops: [deployment_history, save_model]
```
