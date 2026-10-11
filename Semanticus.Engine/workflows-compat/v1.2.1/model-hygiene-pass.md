---
name: model-hygiene-pass
title: Tidy a model
description: Scan the model, apply only selected low-risk fixes, rescan the changed area and save with remaining findings visible.
whenToUse: "Use for a bounded cleanup of model properties before review or publish. For authored descriptions and assistant preparation, use Explain the model to assistants; for deletion or a risky change, use Review a risky change."
version: 1
strictness: hard
triggers: [apply_safe_fixes, make_model_ai_ready]
---

## Step 1: Scan and choose the work queue

Run `ai_readiness_scan`, `bpa_scan`, `list_columns` and `unused_objects`. Select a bounded set of fixes
that belongs to this pass, such as hidden keys, non-summing identifiers, formats or data categories.
Keep authored descriptions, synonyms and assistant instructions in Explain the model to assistants. Do not
turn an unused-object finding into a deletion: report usage and dependants need their own impact review.

```yaml gate
ops: [ai_readiness_scan, bpa_scan, list_columns, unused_objects]
inputs:
  - name: selectedScope
    question: "Which selected objects or finding categories are in scope for this cleanup? Leave unrelated findings for a later pass."
    type: text
    required: required
```

## Step 2: Review and apply selected fixes

Review each proposed change, then use `apply_safe_fixes` for deterministic low-risk fixes or the specific
property operation for a selected object. If a deletion is needed, stop and hand that object to Review a
risky change with `analyze_reports` and `impact_of`; this pass does not silently delete anything.

```yaml gate
ops: [apply_safe_fixes, set_column_hidden, set_summarize_by, set_measure_format, set_data_category, set_sort_by_column, set_property]
```

## Step 3: Rescan the changed area

Run the readiness and BPA scans again. `readiness_rescan` compares the run-start snapshot and `bpa_clean`
checks that this pass introduced no new violations. These checks show property quality; they do not prove
that every model number stayed unchanged. Any remaining unrelated finding stays visible in the result.

```yaml gate
strictness: hard
ops: [ai_readiness_scan, bpa_scan]
verify:
  - kind: readiness_rescan
    scope: model
  - kind: bpa_clean
    scope: model
```

## Step 4: Save the selected cleanup

Call `save_model`. Record the selected scope, applied operations and remaining findings. If assistant-facing
content is still missing, link the next run to Explain the model to assistants rather than claiming this
cleanup supplied business meaning.

```yaml gate
ops: [save_model]
```
