---
name: semanticus-distill-workflow
description: Turn a completed Semanticus task into a reusable workflow. Use when asked to save a process, make a successful change repeatable or capture a workflow from a completed run.
---

# Save a reusable workflow

Read the actual completed run with `get_workflow_run`, or the Change Plan apply report, and the
model's Primer. Identify useful steps and checks actually run. A `distillable` hint invites
assessment; it does not independently prove success.

Read a relevant current definition with `get_workflow`; use `list_workflows` if needed. Follow
its returned format and declared version rather than an old schema from memory. Replace
model-specific names and values with explicit inputs. Keep each step short and name its operation.

Preserve checks supported by source evidence. Label additional checks as proposed; do not claim
offline or skipped tests passed. Respect the user's chosen profile and strictness. An instruction-only
workflow is useful when enforced checks are unnecessary.

Use `save_workflow` with the new name and full Markdown. It validates before writing and returns
parse errors to repair. Saving under a stock name creates a project override; do that only when
customising the stock process is intended. For an existing saved file, use the workflow document
tools and their returned revision information.

Retain the source run or plan identifier in provenance where the current format supports it.
If the user wants the lesson captured in model knowledge, use `add_insight` with relevant operation
keys. Primer suggestions remain proposals until accepted through the Primer tools. Report the
saved workflow, reusable inputs and verification actually preserved.
