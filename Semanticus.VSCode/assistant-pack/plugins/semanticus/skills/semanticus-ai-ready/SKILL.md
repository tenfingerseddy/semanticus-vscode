---
name: semanticus-ai-ready
description: Improve a Semanticus model's AI readiness findings, descriptions and metadata. Use when preparing a model for Copilot or data agents, or raising its readiness grade.
---

# Improve AI readiness

Start with `get_model_summary` and `ai_readiness_scan` with `detail='summary'`. Read `get_model_primer`
for business definitions. Pull the findings you need with `ai_readiness_scan` filters. When live
cardinality matters and a query connection is open, `live=true` adds live measurements.

AI readiness is part of the Plan from the start. Semanticus does the rows it can (formats, hidden
keys, folders). The words are yours: descriptions, business names, synonyms and AI instructions.
When the person presses Give the rest to your assistant, or a words row is on the Plan, they arrive
as tasks (see `list_assistant_requests`). Either way, `get_words_queue` lists every open words
finding, best lift first, with the tool to use and the grounding (DAX, table and sibling names).

Author each word from the real business meaning.
On Pro, send a page's descriptions and synonyms in one `write_words` call, then names and formats in one `edit_objects` call.
Each item's `tool` names the bulk tool on Pro and the single tool on Free.
Use `set_ai_instructions` once for model instructions; use `create_linguistic_schema` when the queue asks for it.
Send writes one after another. Parallel calls wait for the same writer and do not make writes faster.
Read all pages before writing, or read again from offset 0 after a write; writes change the queue.
On Free, use the single tools the queue names, one call after another.
Do not invent definitions to raise a grade. For a scoped set of changes, `propose_plan` with
`set_plan_item` and `apply_plan` works too; read the applied, skipped and failed items.

Recheck with `ai_readiness_scan` and `detail='summary'`, and report the grade. Studio shows it as one
coloured letter and a bar for each category (green from 80, amber from 60, red below 60). A higher
grade is better metadata, not proof that every business answer is right. For that proof use
`save_test` with an expected answer, or `ask_data` and compare with a known figure.

Model edits share one undo history. Studio updates at once; you see a person's edit on your next
call. `save_model` writes local files. A live model changes only through `deploy_live`: preview it,
read the destination and changes, then commit within the user's authorization.
