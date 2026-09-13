---
name: semanticus-model-pr
description: Stage and review Semanticus model edits as a Change Plan, then apply requested changes in one undoable batch. Use for model diffs, reviewed bulk edits or a pull request for a model.
---

# Review model changes

Inspect an existing plan before replacing it. Use `propose_plan` for analysis-led fixes or
`add_plan_item` for specific changes. `get_plan` shows current and proposed values, status and
risk. Scope the plan to the user's request.

For an optimisation that should preserve results, choose `verifyGroupBy` fields from dimensions
relevant to that measure. Matching checks cover those contexts, not every possible filter.
An intentional change in business meaning needs expected-answer tests instead.
Unverified DAX and rename items remain proposed until approved through `set_plan_item`.
Content revisions can reset approval; read the resulting status.

Use `set_plan_item` to fill, approve or reject items within existing authorization. If the user
asked to review before application, present the concrete diff. Otherwise do the already authorised
work without inventing a second approval step.

`apply_plan` applies approved items; `approvedIds` can narrow that set. Read its actual applied,
skipped and failed results. If the model changed since the plan was assembled, inspect the new
state and refresh affected items. A single `undo` reverts the applied model batch.

Use `save_model` for local persistence. Remote models remain unchanged until publication:
preview `deploy_live`, review its destination and changes, then commit when the task includes
that write. The user sees the same plan and edits in Studio.
