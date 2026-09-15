---
name: reconcile-saved-tests-sql
title: Reconcile saved tests with SQL
description: Run saved SQL comparisons, inspect mismatches or missing access, and export the checked contexts and unresolved cases.
whenToUse: "Use for a repeatable month-end or handoff check when the model already has saved SQL reconciliation tests. Do not use it to author or retype a new SQL test."
version: 1
strictness: hard
triggers: [run_tests, export_test_report]
---

## Step 1: Choose the saved test pack and endpoints

Call `list_tests` and select the saved `measureReconcile` definitions for this run. Review each mapping with
`review_reconcile_mapping`; it reports the detected SQL endpoint, database and reachable source tables and
refuses an ambiguous endpoint instead of guessing. Do not retype the saved SQL, filters, group-by columns or
blank policy. If the saved test or endpoint is missing, keep that case visible as unresolved.

```yaml gate
ops: [list_tests, review_reconcile_mapping]
inputs:
  - name: testPack
    question: "Which saved reconciliation test ids are in scope, and which endpoint/mapping result was reviewed for each?"
    type: text
    required: required
```

## Step 2: Run the comparisons

Call `run_tests` for the selected saved tests, or the complete suite when the handoff requires all saved
checks. Read each verdict and coverage field. A Product-category mismatch beneath a matching grand total
stays a mismatch; a missing SQL connection, insufficient coverage, refused query or missing target stays
NotVerifiable or Missing. Never convert those results into a pass from a total alone.

```yaml gate
ops: [run_tests]
```

## Step 3: Export the result

Call `export_test_report` after the run. The existing report carries the health grade with coverage, root
failures, reconciliation rows and the unresolved reason. Record which saved tests ran, their checked
contexts, source/model execution notes and the next recovery action. `list_test_runs` may be used for
persisted history when available; history does not replace the current report.

```yaml gate
ops: [export_test_report, list_test_runs]
```
