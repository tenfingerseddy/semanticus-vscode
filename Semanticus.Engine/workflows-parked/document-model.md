---
name: document-model
title: Document the model and prove the grade improves
description: Inventory what's undescribed from the readiness findings, describe everything visible in business terms, keep the narrative docs current with the model, then prove the description and naming categories are non-regressive: documentation that doubles as the LLM's grounding.
version: 1
strictness: hard
triggers: [set_doc_section, get_doc_model]
---

## Step 1: Inventory what's undescribed

Documentation starts from what's missing, not from a blank page. Run
`ai_readiness_scan`: the DESC-* findings ARE the queue: every table, column, and measure without a
description surfaces here. Read the current narrative shape with `get_doc_outline` so you know what
sections already exist and which objects the model considers documented. Do not describe from memory;
let the scan order the work, top-down by what's visible and undescribed.

```yaml gate
ops: [ai_readiness_scan, get_doc_outline]
```

## Step 2: Describe everything visible

Work the queue. Give every visible table, column, and measure a `set_description` that
carries business meaning, units, and caveats, not a restatement of the name. A description doubles as
documentation AND as the LLM's grounding: "Revenue net of returns, in AUD, excludes tax" tells both a
human and Copilot what the field means. Where a name is cryptic or abbreviated, rename it to an
unabbreviated, business-friendly form with `rename_object` before describing it; a good name is the first
line of documentation. Skip hidden plumbing; document what a report author or the LLM actually touches.

```yaml gate
ops: [set_description, rename_object]
```

## Step 3: Keep the narrative docs current

Beyond per-object descriptions, maintain the model's narrative documentation. Read the
current generated doc with `get_doc_model` and its structure with `get_doc_outline`; inspect a specific
section with `get_doc_section`; and write or refresh per-object and per-area narrative sections with
`set_doc_section`: the design intent, the grain, the role matrix, the lineage notes that per-field
descriptions can't hold. Generated documentation is only useful if it's kept current WITH the model, so
update the sections your changes touched rather than letting them drift stale.

```yaml gate
ops: [get_doc_model, get_doc_outline, get_doc_section, set_doc_section]
```

## Step 4: Prove the grade improved

This is the hard gate. Re-scan with `readiness_rescan`: the description and naming
categories must be NON-REGRESSIVE against the start of this run, and should improve; documenting a model
that lowers its readiness grade is a contradiction. If the delta regressed (a rename broke a reference, a
description got dropped), the gate holds on this step; fix it and re-submit. A documentation pass that
raises the DESC/naming categories is the visible proof the work landed.

```yaml gate
strictness: hard
verify:
  - kind: readiness_rescan
    scope: model
```

## Step 5: Save the documented model

Persist with `save_model` so the descriptions, renames, and narrative sections land in the TMDL beside the
model. Note for the next author that these descriptions are AI-readiness RAW MATERIAL: the model's
10-step AI workflow starts with hygiene and descriptions, so this pass is what later Copilot / Q&A / data
-agent grounding is built on. Keep it current with every subsequent model change rather than treating it
as a one-time chore.

```yaml gate
ops: [save_model]
```
