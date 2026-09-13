---
name: semanticus-ai-ready
description: Improve a Semanticus model's AI-readiness findings, descriptions and metadata. Use when preparing a model for Copilot, Q&A or data agents, or raising its readiness grade.
---

# Improve AI readiness

Start with `get_model_summary` and `ai_readiness_summary`. Read `get_model_primer` for business
definitions. Pull relevant findings with `ai_readiness_scan` filters. When live cardinality matters
and a query connection is available, `ai_readiness_scan_live` adds live evidence.

Use `propose_plan` for the requested scope. It provides grounded content requests and deterministic
changes without applying them. Author descriptions, synonyms and instructions from actual business
meaning. Fill content with `set_plan_item`; inspect `get_plan` for before and after values and
approval state. Do not invent definitions to increase a grade.

Apply the requested approved changes with `apply_plan`, read applied, skipped and failed items,
then recheck `ai_readiness_summary`. A higher grade is a metadata improvement, not proof that
an assistant answers every business question correctly. Use interviews or expected-answer tests
when the user wants evidence about answers.

The user can inspect the same Change Plan in Studio. Model edits share one undo history.
Reuse the existing account for a deployed model; service-principal authentication is an option,
not a universal requirement. `save_model` saves local files. For a requested remote update,
preview `deploy_live`, review its actual changes and destination, then commit within the user's
authorization. Report the grade and any checks or changes that remain incomplete.
