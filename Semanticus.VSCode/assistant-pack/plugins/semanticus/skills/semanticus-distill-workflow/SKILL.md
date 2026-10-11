---
name: semanticus-distill-workflow
description: Turn a completed Semanticus task into a reusable workflow. Use when asked to save a process, make a successful change repeatable or capture a workflow from a completed run.
---

# Save a reusable workflow

Read the actual completed run with `get_workflow_run`, or the change list's apply report, and the
model's notes. Identify useful steps and checks actually run. A `distillable` hint invites
assessment; it does not independently prove success.

Read a relevant current definition with `get_workflow`; use `list_workflows` if needed. Follow
its returned format and declared version rather than an old schema from memory. Replace
model-specific names and values with explicit inputs. Keep each step short and name its operation.

When editing an existing workflow, read `get_workflow_document` so the exact source, path and
byte hash are current. The Studio Steps, Canvas and Source views share one draft. Use
`preview_workflow_edit` to inspect typed edits or a draft, including the full-context diff and
warnings, before writing. Apply reviewed text with `edit_workflow_document`; use `save_workflow`
with `createOnly: true` for a new project copy. Starters, the workflows that come with Semanticus,
are read-only. Use
`upgrade_workflow` as a dry run first when a version-1 document needs stable step ids. Layout is
presentation only and belongs through `get_workflow_layout` and `save_workflow_layout`. A successful
write updates the Workflows list in VS Code at once, while your assistant sees it on its next call.
In Studio the person does the same with Make it reusable on a finished run in the Plan's History.

Preserve checks the source run supports. Label additional checks as suggestions; do not claim
offline or skipped checks passed. Respect the user's team rules and how firmly their checks apply.
An instruction-only workflow is useful when no check has to pass.

Use `save_workflow` with the new name and full Markdown. It validates before writing and returns
parse errors to repair. Saving under a starter's name makes a project copy that replaces the
starter, and a team rule that requires that starter stops it. Do it only when replacing the
starter is what the user wants. To begin from a starter instead, `instantiate_workflow_template` with its name
copies it as the user's workflow. For an existing saved file, use the workflow document
tools and their returned revision information.

Retain the source run or plan identifier in provenance where the current format supports it.
If the user wants the lesson captured in model knowledge, use `add_insight` with relevant operation
keys. Model notes suggestions stay pending until accepted with `accept_primer_suggestion`. Report the
saved workflow, reusable inputs and verification actually preserved.
