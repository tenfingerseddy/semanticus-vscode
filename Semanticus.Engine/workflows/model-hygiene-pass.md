---
name: model-hygiene-pass
title: Sweep the model for hygiene and AI-readiness
description: A single enforced pass over the whole model: scan for gaps, fix types/hiding/summarization/formatting/descriptions, remove genuinely unused objects only after checking report usage and dependents, then prove the pass regressed nothing.
whenToUse: "A periodic hygiene sweep over the whole model, or the tidy-up before a review or deploy: types, hiding, summarization, formatting, descriptions, AI-readiness, and the careful removal of genuinely unused objects (proven no report usage and no dependents first). For securing the model use secure-with-rls; for a single measure use new-measure."
version: 1
strictness: hard
triggers: [apply_safe_fixes, make_model_ai_ready]
---

## Step 1: Scan, the scan output is the work queue

Start by seeing the whole board. Run `ai_readiness_scan` and `bpa_scan` for the findings,
`list_columns` to walk the columns, and `unused_objects` for removal candidates. Do not fix from memory
or intuition: the scan output IS the work queue for this pass. Read it, and let it order the sweep.

```yaml gate
ops: [ai_readiness_scan, bpa_scan, list_columns, unused_objects]
```

## Step 2: The sweep

Work the queue. Types: money → Fixed Decimal, no numerics-as-text
(`set_column_data_type`). Hide the plumbing: keys, RLS/sort helpers, measure-only fact columns
(`set_column_hidden`). Set `SummarizeBy = None` on non-summing numerics such as keys, years, IDs
(`set_summarize_by`), and pair labels with sort keys (`set_sort_by_column`). Give every measure an
explicit format string (`set_measure_format`) and tag geography and URLs with `set_data_category`. At
scale, organise with display folders via `set_property`. Give everything visible a `set_description` and
an unabbreviated business name (`rename_object`); descriptions are both the docs AND the LLM's
grounding. Clear the deterministic backlog with `apply_safe_fixes`, and use `make_model_ai_ready` for the
AI-readiness content in one undoable batch.

```yaml gate
ops: [set_column_data_type, set_column_hidden, set_summarize_by, set_sort_by_column, set_measure_format, set_data_category, set_description, rename_object, set_property, apply_safe_fixes, make_model_ai_ready]
```

## Step 3: Remove genuinely unused objects, carefully

"Unused" is a claim to verify, never a licence to delete. For each candidate from
`unused_objects`, cross-reference report usage with `analyze_reports` and check model dependents with
`impact_of` BEFORE touching it: a column absent from the model graph may still be referenced by a report
visual or a downstream measure. Delete with `delete_object` ONLY when there is no report usage and no
dependents. When in doubt, hide rather than delete: a hidden column costs nothing and breaks nothing.

```yaml gate
ops: [unused_objects, analyze_reports, impact_of, delete_object]
inputs:
  - name: deletionsReviewed
    question: "For every object you deleted, did you confirm (via analyze_reports AND impact_of) that it has no report usage and no model dependents?"
    type: text
    required: answer-or-decline
```

## Step 4: Prove the pass regressed nothing

This hard gate diffs both scans against the start-of-run snapshot. The readiness score must
not fall and no new readiness finding may appear; and the sweep must introduce no new BPA violation
anywhere. A hygiene pass that lowers the grade or adds a violation is a net loss: the gate holds on this
step until the delta is non-regressive. Fix the regression, or waive with a recorded reason, then
re-submit.

```yaml gate
strictness: hard
verify:
  - kind: readiness_rescan
    scope: model
  - kind: bpa_clean
    scope: model
```

## Step 5: Save the model

Persist the cleaned model with `save_model` so the types, hiding, formatting, descriptions, and removals
land in the TMDL beside the model. Record what you deleted and why, so the next author does not
resurrect plumbing you deliberately retired.

```yaml gate
ops: [save_model]
```
