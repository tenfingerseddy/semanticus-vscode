---
name: secure-with-rls
title: Secure the model with row-level security and attack-test it
description: Design least-privilege RLS by filtering dimensions and letting propagation reach facts, review the propagation limits that silently break security, PROVE the filter bites against a figure the role should see, then ADVERSARIALLY attack-test it (prove out-of-scope rows do not leak) before the role is allowed to stand. With an honest note on offline impersonation.
whenToUse: "Add or change row-level security (a role plus a table filter). Use it whenever a mistake would leak data across a security boundary such as regions, departments, or customers. It makes the adversarial 'prove it blocks' step mandatory, which manual authoring almost always skips."
version: 1
strictness: hard
triggers: [create_role, set_table_permission, set_role_permission]
---

## Step 1: State the security requirement

Security starts from the business rule, not the DAX. Capture who may see what (by region,
by manager, by cost centre) in the user's own words. Ask for a test identity (a UPN/email) you can reason
with concretely: an abstract rule is easy to get subtly wrong, a named user is not. Least privilege is the
default posture: a role sees the minimum it needs, nothing more.

```yaml gate
inputs:
  - name: securityRequirement
    question: "Who may see what, stated as a business rule (e.g. 'sales reps see only their own region's rows')?"
    type: text
    required: required
  - name: testIdentity
    question: "A UPN/email to reason with concretely (e.g. rep@contoso.com): the identity you'll simulate the filter for."
    type: text
    required: answer-or-decline
```

## Step 2: Design and create the roles

Create the role with `create_role` and apply the row filter with `set_table_permission`, adding members
with `set_role_member`. Filter the DIMENSION tables and let single-direction propagation
carry the restriction to the facts; filtering the fact directly is slower and easy to get wrong. Keep the
filter DAX the simplest expression that is sufficient. At scale, prefer DYNAMIC RLS
(`USERPRINCIPALNAME()` matched against a hidden user-mapping dimension) over N nearly-identical static
roles; hide that mapping table's key columns with `set_column_hidden` so it never surfaces to a report
author.

```yaml gate
ops: [create_role, set_table_permission, set_role_member, set_column_hidden]
```

## Step 3: Review the propagation limits

RLS security depends on how filters propagate, and two limits silently break it. First,
**RLS never propagates through inactive relationships**, which is exactly why role-playing dimensions must
be duplicated tables, each with one active relationship, not one table reached by USERELATIONSHIP. Second,
bidirectional filters combined with RLS open cross-filtering paths that can leak rows the rule meant to
hide. Walk the graph with `get_model_graph` and flag every bidirectional relationship that touches a
secured table; give each an explicit security review, tightening cross-filter with
`set_relationship_crossfilter` where the bidirectional path is not defensible.

```yaml gate
ops: [get_model_graph, set_relationship_crossfilter]
inputs:
  - name: propagationReviewed
    question: "Have you confirmed no secured filter depends on an inactive relationship, and reviewed every bidirectional relationship touching a secured table?"
    type: text
    required: answer-or-decline
```

## Step 4: Prove the filter bites

A security rule you cannot demonstrate is a guess. Author a control measure with
`create_measure` that computes what the role SHOULD see, simulating the role's filter with `CALCULATE` over
the role's filter expression, and give `target` its ref. Ask the business for the figure that role should
see, computed independently (the filtered row count, or a filtered total). The hard gate probes the measure
against that value; a mismatch means the filter is too loose or too tight: fix the filter DAX and re-probe.
Note honestly: the engine cannot IMPERSONATE a role offline (`run_dax` has no role parameter yet), so this
probe simulates the filter via `CALCULATE`; real impersonation testing happens post-deploy against the
published model.

```yaml gate
strictness: hard
ops: [create_measure]
inputs:
  - name: verificationValue
    question: "A figure the role SHOULD see, computed independently by the business (e.g. the filtered row count or a filtered total for the test identity)."
    type: verification
    required: answer-or-decline
  - name: target
    question: "The ref of the control measure that simulates the role's filter via CALCULATE (e.g. measure:Sales/Region-Filtered Row Count)."
    type: objectRef
    required: required
verify:
  - kind: dax_probe
    when: inputs.verificationValue.answered
    probe: verificationValue
```

## Step 5: Attack-test the role, prove out-of-scope rows do not leak (HARD GATE)

The adversarial step, the whole point of a security review. A role you cannot prove BLOCKS is on the
honour system. Try to reach data the identity must NOT see (another region's rows, the grand total
across all segments, a drill to a forbidden member), evaluating as the role (post-deploy against the
published model, or offline by simulating the role's filter with `CALCULATE` and attempting to reach
past it). Confirm the result is empty or blocked: ZERO leak. If anything out-of-scope comes back, the
filter is too loose or a bidirectional path routes around it, so fix it and re-attack. A model-scope
`bpa_clean` runs alongside to catch the structural smells that silently defeat RLS: a role with no
members, or a filter that leans on a disabled relationship. Do not skip: an unverified role is a data
breach waiting for an auditor.

```yaml gate
strictness: hard
ops: [run_dax]
inputs:
  - name: attackResult
    question: "As the role, attempting to reach OUT-of-scope data (other regions, cross-segment totals): confirm ZERO rows leak, and paste the evidence. Anything non-empty means the filter is broken, so fix it and re-attack."
    type: text
    required: required
verify:
  - kind: bpa_clean
    scope: model
```

## Step 6: Object-level security and save

If the rule hides the EXISTENCE of columns or tables (not just rows) add object-level security with
`set_table_ols` / `set_column_ols`. Design around its sharp edge: every measure that touches
an OLS-secured column ERRORS for restricted users, so a shared measure referencing a secured column breaks
the whole report for them; keep secured columns out of broadly-used measures. Then persist with
`save_model` so the roles, filters, and OLS land in the TMDL, and hand the model to the deploy stage for
real per-role impersonation testing.

```yaml gate
ops: [set_table_ols, set_column_ols, save_model]
```
