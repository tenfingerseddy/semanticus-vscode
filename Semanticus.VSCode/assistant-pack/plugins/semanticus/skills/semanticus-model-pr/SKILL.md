---
name: semanticus-model-pr
description: Collect and review Semanticus model edits as a change list, then apply requested changes in one undoable step. Use for model diffs, reviewed bulk edits or a pull request for a model.
---

# Review model changes

Inspect an existing plan before replacing it. `propose_plan` builds an analysis-led change list and
`add_plan_item` adds a specific change. `get_plan` shows current and new values, status and risk.
Scope the list to the user's request. (The Plan page's own rows come from `get_model_plan`, and
`apply_plan_items` runs them; see the semanticus skill.)

For a rewrite that must keep its results, choose `verifyGroupBy` fields from dimensions that matter
to that measure. A match covers those contexts, not every filter. A deliberate change of business
meaning needs expected-answer checks instead. Unverified DAX and rename items stay pending until
approved with `set_plan_item`. A change to an item's content can reset its approval; read the status.

Use `set_plan_item` to fill, approve or reject items within the user's authorization. If the user
asked to review first, show the diff. Otherwise do the authorised work.

`apply_plan` applies the approved items; `approvedIds` narrows the set. Read its applied, skipped and
failed results. If the model changed since the list was built, read the new state and refresh the
affected items. One `undo_change` takes the whole application back.

`save_model` writes local files, after the changes are applied. A remote model stays as it is until
`deploy_live`: preview it, read the destination and changes, then commit when the task includes that
write. Restoring a remote version is a separate remote action that `undo_change` does not reverse.
Studio shows the same items under Pending changes on the Plan page and updates at once; you see a
person's edit on your next call.
