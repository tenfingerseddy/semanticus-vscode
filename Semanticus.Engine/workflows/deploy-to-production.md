---
name: deploy-to-production
title: Promote a model to production safely
description: Review the change like a pull request, gate on a clean BPA and a non-regressed readiness grade, dry-run against the target ALWAYS, then promote through separate stages; the live ops stay consent-gated and the run never bypasses them.
version: 1
strictness: hard
triggers: [deploy_stage, deploy_live, preview_deploy]
---

## Step 1: Review the change as a diff

Treat a deploy like a pull request. Run `model_diff` to see the semantic before/after, and
read the accountable record with `list_verified_edits`; `export_verified_edits` captures it for the change
log. Nothing proceeds until you have read exactly what changed and confirmed every change is intended: a
deploy is the wrong moment to discover a surprise edit.

```yaml gate
ops: [model_diff, list_verified_edits, export_verified_edits]
inputs:
  - name: diffReviewed
    question: "Have you reviewed the full before/after diff (model_diff + verified edits) and confirmed every change is intended?"
    type: text
    required: answer-or-decline
```

## Step 2: Validate: BPA clean, readiness holds, RLS roles tested

This is the hard gate and the full pre-deploy testing stage. Confirm no new best-practice
violation and no readiness regression versus the start of this run: `bpa_clean` diffs violations against
the run snapshot, `readiness_rescan` confirms the AI-readiness grade did not fall and no new finding
appeared. Then validate security before promoting: list the roles with `list_roles` and confirm each has
been attack-tested. Be honest about the offline limit: `run_dax` cannot impersonate a role (it has no
role parameter), so prove each role either by simulating its filter with `CALCULATE` over the role's
filter expression, or by real per-role impersonation against the PUBLISHED model post-deploy. The
secure-with-rls workflow is where a role is proven to block out-of-scope rows; a promote should not be
the first time security is checked. RLS never propagates through inactive relationships, and measures
touching OLS-secured columns error for restricted users, so confirm each role returns the rows it should
and no more. Fix any regression, or waive with a recorded reason, before moving on.

```yaml gate
strictness: hard
ops: [list_roles, run_dax]
inputs:
  - name: rlsTested
    question: "Have you tested every RLS role (and any OLS-secured measures) for correct row/column visibility, so each role sees the rows it should and no more?"
    type: text
    required: answer-or-decline
verify:
  - kind: readiness_rescan
    scope: model
  - kind: bpa_clean
    scope: model
```

## Step 3: Preview against the target: dry-run ALWAYS

Never promote blind. Confirm you are pointed at the right endpoint with `connection_status`,
then run `preview_deploy` and `deploy_gate` to see what WOULD change against the target: a dry run,
ALWAYS, before any writeback. The preview shows the parameter rebinds the deployment rules will apply and
any structural drops; read it the way you read Step 1's diff. Do not proceed if the preview shows anything
you did not intend.

```yaml gate
ops: [preview_deploy, deploy_gate, connection_status]
inputs:
  - name: previewReviewed
    question: "Have you dry-run the deploy (preview_deploy / deploy_gate) against the target and confirmed the previewed changes and parameter rebinds are correct?"
    type: text
    required: answer-or-decline
```

## Step 4: Promote through the right stage

Separate dev / test / prod workspaces are ESSENTIAL: never publish local dev
straight to production. Promote one stage at a time with `deploy_stage`, or via CI/CD with `cicd_publish`;
deployment rules rebind parameters per stage (server/database/environment) so the same model binds to the
right source in each. `deploy_live` and the live legs are consent- and commit-gated. This workflow surfaces
them but NEVER bypasses that gate; the XMLA writeback runs dry-run-first (Step 3) and only commits on the
user's explicit go-ahead. Confirm the target stage below before promoting.

```yaml gate
ops: [deploy_stage, deploy_live, cicd_publish, deployment_history]
inputs:
  - name: targetStage
    question: "Which stage is this promoting to, and is it the correct NEXT stage (dev→test→prod, not straight to prod)?"
    type: text
    required: answer-or-decline
```

## Step 5: Record and monitor

Close the loop. Read `deployment_history` to confirm the promotion recorded, and `save_model`
so the deployed state is captured beside the model. Set up refresh success/failure alerting on the target.
Note the monitoring caveat: usage and refresh telemetry come via the admin monitoring workspace or Log
Analytics, and Fabric workspace monitoring (2025+) partially supersedes and is MUTUALLY EXCLUSIVE with Log
Analytics: pick one per workspace, don't wire both. Record what was promoted and to which stage so the
next deploy has a clean history to diff against.

```yaml gate
ops: [deployment_history, save_model]
```
