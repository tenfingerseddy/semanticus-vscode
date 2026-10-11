---
name: make-ai-ready
title: Explain the model to assistants
description: Improve assistant-facing meaning for selected objects, review preparation settings, and show what remains unresolved.
whenToUse: "Use when assistants need clearer definitions, vocabulary or instructions for selected model objects. For types, visibility or unused-object cleanup, use Tidy a model; for one description, use the direct description action."
version: 1
strictness: hard
triggers: [make_model_ai_ready, ai_readiness_scan, set_description]
---

## Step 1: Scan and choose the content scope

Run `ai_readiness_scan`, `list_objects` and the relevant grounding reads. Choose the objects and missing
business context this pass will address. Deterministic visibility, type and summarization fixes belong to
Tidy a model; this workflow supplies authored meaning, vocabulary and instructions.

```yaml gate
ops: [ai_readiness_scan, list_objects]
inputs:
  - name: contentScope
    question: "Which selected tables, columns or measures need business descriptions, synonyms or assistant instructions in this pass?"
    type: text
    required: required
```

## Step 2: Add selected explanations and settings

Read existing descriptions and AI instructions before replacing them. Use `set_description` for concise
business definitions, `set_synonyms` for terms users actually use, and `set_ai_instructions` for model-wide
rules. Use `set_ai_data_schema` or a perspective only for a deliberate field set. Enable Q&A only when this
model's supported capability and the user’s plan need it; its legacy status is a follow-up to verify, not an
automatic requirement. These writes are live metadata/content changes, not a readiness proof by themselves.

```yaml gate
ops: [get_ai_instructions, set_description, set_synonyms, set_ai_instructions, set_ai_data_schema, create_perspective, set_perspective_member, enable_qna]
inputs:
  - name: qnaPath
    question: "Is Q&A or the linguistic schema relevant and supported for this model, or should this pass leave it unchanged?"
    type: text
    required: answer-or-decline
```

## Step 3: Rescan and report the remaining work

Run `ai_readiness_scan` again. The hard rescan compares the score and findings with the start of the run:
when earlier findings are still open it passes only if the grade improved. It does not prove that model
numbers changed or that a legacy Q&A capability will remain available. Record
remaining authored-content gaps and any deployed-model refresh needed before service-side instructions are
visible. Save the model after the review.

```yaml gate
strictness: hard
ops: [ai_readiness_scan, save_model]
verify:
  - kind: readiness_rescan
    scope: model
```
