---
name: secure-with-rls
title: Set up data access rules
description: Define row access, inspect propagation, test allowed and denied cases, and record the remaining published-model check.
whenToUse: "Use when people must see different rows by region, department or customer. For a publish review of existing roles, use Publish changes; for general cleanup, use Tidy a model."
version: 1
strictness: hard
triggers: [create_role, set_table_permission, set_role_permission]
---

## Step 1: State who may see what

Capture the business rule in the user's terms. A test identity is useful for a dynamic rule but may be
declined when the model uses a static role. Keep least privilege and the intended denied population explicit.

```yaml gate
inputs:
  - name: securityRequirement
    question: "Who may see which rows, stated as a business rule?"
    type: text
    required: required
  - name: testIdentity
    question: "The UPN or other test identity to simulate, or decline when a static role has no identity-specific case."
    type: text
    required: answer-or-decline
```

## Step 2: Create the role and inspect propagation

Create the role, apply the simplest sufficient dimension filter, add members when the model permits it,
and inspect the graph. RLS does not cross inactive relationships; bidirectional paths touching secured
tables need an explicit review. Correct an unsafe direction with `set_relationship_crossfilter` before
testing. Dynamic members or OLS are conditional follow-ons.

```yaml gate
ops: [create_role, set_table_permission, set_role_member, get_model_graph, set_relationship_crossfilter]
inputs:
  - name: propagationReviewed
    question: "What graph paths were reviewed, including inactive or bidirectional relationships touching the secured tables?"
    type: text
    required: answer-or-decline
```

## Step 3: Check an allowed case

Create a control measure that simulates the role filter and give `target` its ref. Supply an independently
known allowed result when available. The scalar probe can verify the simulated calculation, but `run_dax`
has no role parameter, so this is not published impersonation evidence. A missing independent result stays
unverified.

```yaml gate
strictness: hard
ops: [create_measure, probe_measure]
inputs:
  - name: target
    question: "The control measure ref that applies the role's filter in CALCULATE."
    type: objectRef
    required: required
  - name: verificationValue
    question: "An independently known allowed result for the selected identity and context; decline when unavailable."
    type: verification
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.verificationValue.answered
    probe: verificationValue
```

## Step 4: Check a denied case

Attempt an out-of-scope member or total and record the result. A non-empty denied result is a security
failure that must be fixed and retested. `bpa_clean` can catch structural regressions, but it cannot prove
role impersonation; perform that check against the published model after deployment.

```yaml gate
strictness: hard
ops: [run_dax]
inputs:
  - name: deniedResult
    question: "What did the denied or out-of-scope query return? Record zero rows or the service refusal; any leaked rows require a fix."
    type: text
    required: required
verify:
  - kind: bpa_clean
    scope: model
```

## Step 5: Apply conditional OLS and save

If the requirement hides a table or column's existence, apply `set_table_ols` or `set_column_ols` and check
that shared measures do not touch secured fields. Then call `save_model`. Record the exact post-deploy
role-impersonation check still required; a simulated CALCULATE result is not that check.

```yaml gate
ops: [set_table_ols, set_column_ols, save_model]
```
