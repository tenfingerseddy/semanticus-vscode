---
name: make-ai-ready
title: Make the model AI-ready
description: Descriptions, synonyms, Q&A, and Prep-for-AI config in the order Microsoft's AI-readiness workflow prescribes, gated on a readiness rescan.
version: 1
strictness: hard
triggers: [make_model_ai_ready, ai_readiness_scan, set_description]
---

## Step 1: Optimize and describe first

Microsoft's AI-readiness step 1 is the classic hygiene and perf work (data types,
high-cardinality columns, inefficient DAX) plus descriptions. Run
`ai_readiness_scan` to see the gaps, then add business-meaning descriptions to every visible
table, column, and measure with `set_description`. Descriptions double as documentation and
the LLM's grounding; everything downstream depends on them.

## Step 2: Synonyms and perspectives

Add the business vocabulary (what people actually call things) per entity with
`set_synonyms`. For large models, curate role-scoped views with
`create_perspective` / `set_perspective_member`, which also scope the AI schema.
Watch for the Copilot Tooling Format `copilot/` folder: detect its presence, do not
hand-author it.

## Step 3: Enable Q&A

Q&A is still a Prep-for-AI prerequisite as of mid-2026. Enable it with
`enable_qna`. Note the legacy Q&A experience is deprecated (announced Dec 2025, removal by
Dec 2026); re-verify this dependency near that date before relying on it long-term.

```yaml gate
inputs:
  - name: qnaConfirmed
    question: "Is Q&A enablement confirmed for this model (or explicitly not applicable)?"
    type: text
    required: answer-or-decline
```

## Step 4: Prep for AI, schema and instructions

Curate which fields the AI sees with `set_ai_data_schema`: exclude plumbing and
ambiguous fields; a smaller, well-named schema beats an exhaustive one. Then author
model-level AI instructions (10k cap) with `set_ai_instructions`: the business
rules and definitional gotchas the LLM cannot infer. Consider verified answers for the
highest-value recurring questions.

```yaml gate
inputs:
  - name: schemaCurated
    question: "Have you excluded plumbing/ambiguous fields from the AI data schema and written AI instructions?"
    type: text
    required: answer-or-decline
```

## Step 5: Score and gate

Re-scan with `ai_readiness_summary` and confirm the grade improved with no new findings.
Semanticus's deterministic readiness scoring with hard gates is the
differentiated lane. Apply any remaining `apply_safe_fixes` and re-check before you call it ready.

```yaml gate
strictness: hard
verify:
  - kind: readiness_rescan
    scope: model
```
