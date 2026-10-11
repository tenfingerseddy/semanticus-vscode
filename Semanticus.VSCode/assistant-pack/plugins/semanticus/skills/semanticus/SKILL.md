---
name: semanticus
description: Use Semanticus to inspect, edit, check and publish Power BI or Fabric semantic models through MCP. Use when working in the Semanticus workbench or acting on its open model.
---

# Work with Semanticus

Semanticus is one live model shared by the user in VS Code and their own AI assistant over MCP.
Use the connected Semanticus tools for model work. The user's request and existing authorization
take precedence over this guidance.

## Start with the current session

Call `get_model_summary` at session start, after compaction or on a handoff. It gives the current
model, connection, tier, Model notes excerpt, unfinished work and useful next calls. Read
`get_model_primer` when business definitions or known model issues matter. Fetch relevant details
instead of loading the whole model or every tool description. Reuse the current connection and
account when they fit; reconnecting is not a routine first step.

Distinguish the editable model, the live query connection, and the publishing destination.
They can differ. Check `connection_status` before measuring or testing. A local metadata edit
does not update the query server.

The VS Code workbench is organised as six areas: Plan, Explore, Edit, Review, Publish and
Workflows. When pointing the user somewhere, use the area that matches their first question, then
its named tool. The Plan is where the user starts: it is every piece of work on the model, who does
each one (Semanticus, the user, or you), what waits for what, and History, the record of every edit
with who made it, where any edit still on the undo stack can be undone back to. A workflow run is
a row on the Plan, and its report is in History. Pending changes and Model Spec open on the Plan too. Explore holds Data, Ask your data (Pro), Diagram,
Lineage and Size. Edit holds Find and replace, Queries, AI instructions (its Notes tab
holds Model notes) and Advanced modelling (Power Query is one of its pages). Review holds Model quality, AI readiness,
Validation (its Reports tab holds Reports) and Docs. Model quality and AI readiness each end with
Give the rest to your assistant, which puts one task per finding on the Plan for you. Publish holds
Published, where the person publishes with one press and can go back to an earlier version. Workflows is the list of the user's workflows, and Author when one is
open. The area labels are for people; MCP tool names do not depend on them.

Three words cover repeatable work. A workflow is the user's own, in their list. A starter is a
workflow that comes with Semanticus, copied to make a workflow of their own. Team rules say which
workflows a team requires for a task, and how firmly their checks apply. Use these words with the
user even where a tool's name says profile or template.

## Choose the route that fits

| Task | Starting point |
|---|---|
| Explain or inspect | Summary and Model notes, then the relevant object, DAX, lineage or data tool |
| Edit one object | Its typed edit tool and the object reference returned by Semanticus |
| See what is worth doing | `get_model_plan`, then `apply_plan_items` with its `readyDigest` to run the plan |
| Review several changes | `propose_plan` or `add_plan_item`, then `get_plan` and `apply_plan` |
| See who changed what, or go back | `get_plan_history`, then `undo_change` with `toEntry` to undo to a point |
| Improve AI readiness | `ai_readiness_scan` with `detail='summary'`, scoped findings and a change list |
| Optimise DAX | `get_dax`, `benchmark_dax`, `profile_dax`, `verify_dax_equivalence` |
| Check business answers | `run_tests` and `save_test` with independent expected answers |
| Repeat a process | `list_workflows`, then `get_workflow` for the current definition |
| Answer a question about the numbers | `ask_data` (Pro). It always answers and its `assumption` line says what it assumed, so pass that line on. If it names another reading ("You may have meant Y"), say so. `list_data_questions` lists past ones; `save_test` keeps an answer as a check |
| Explain a number | `explain_value` for one cell of one measure: its filters, what feeds it, where it comes from, the top contributors, and why it is blank |
| Turn words into workflow steps | `describe_workflow_steps`, then `save_workflow` |
| Save or publish | `save_model` for local files; `deploy_live` previews first, then commits with the token the preview returned |
| Publish a model that has no published copy yet | `publish_new_model` previews first (workspace, name, Direct Lake or Import), then `commit=true` creates it, loads its data and makes it the destination. Pass on its line; an import model needs its source sign-in set in the Power BI service |

Companion skills cover DAX optimisation, AI readiness, change lists, workflow creation, model
knowledge and model interviews (your assistant's own check, which Studio does not show). Read only
the relevant skill. Get workflow definitions through MCP rather
than reconstructing them from memory. Inspect an existing run or plan before starting another.

Run the plan is one call. `apply_plan_items` with no items and the `readyDigest` from `get_model_plan`
runs every row Semanticus can do, every wave, as one undoable step. `entryId` in the result is the
History entry that undoes the whole run with `undo_change` and `toEntry`. A run does not save; the
person presses Save on the result line, or you call `save_model`. A row for you (your assistant) comes
first only where another row waits for it. The person's own rows come last and never hold up the run.

For workflow authoring, read `get_workflow_document` first. It returns the exact source, path and
byte hash. Check a change with `preview_workflow_edit`, then write it with `edit_workflow_document`,
or `save_workflow` with `createOnly: true` for a new project workflow. A starter (a workflow that
comes with Semanticus) is read-only: `instantiate_workflow_template` copies it as the user's own.
`get_workflow_layout` and `save_workflow_layout` hold Canvas positions only. The semanticus-distill-workflow skill
has the detail.

On Pro, Jev (the Semanticus helper) makes small modelling choices that arrive as Plan rows: column
formats, base measures for number columns, what Copilot sees and the Plan's order. It also reads the
person's words for Ask, and drafts workflow steps (`describe_workflow_steps`, or a task typed on the
Plan). `get_decision_points` lists each choice, whether it is on, and whether this build asks Jev for
it ('live'). The person allows Jev once for each model, and Semanticus remembers that on this
machine, outside the model files. Studio shows one Allow button if it is ever needed, and
`get_consent` lists exactly what goes. When Jev cannot be reached, Ask says so and does not guess.

## Work on the shared model

Model edits share one undo history. Assistant edits appear in the UI immediately. User edits reach
the assistant in its next tool result as one line, `changed since your last call (in Studio): <refs>; rev N`
(an edit by another assistant says `by another assistant`). When that line appears, re-read the named
objects and the plan before continuing work that may be stale. Remote actions have their own preview and
commit semantics; local undo does not promise to reverse every remote action.

The person can give you tasks on the Plan. When a result carries a line starting `Plan:`, it lists the tasks
that are ready for you, whether they depend on each other, and any that wait for another row. The
line comes only when that list changes. Call `list_assistant_requests`, take one with `claim_request` (keep the
`claimToken` it returns), do the work, then `complete_request` with a short reply, or `decline_request` with the
reason if you cannot. While you hold a claim, your edits count toward the task you claimed most recently, so
claim again before switching between two tasks. If the list says the tasks are paused, carry on with what the
person asks you directly. Adding, ordering and removing tasks and aims is the person's to do in Semanticus.

Choose test contexts from the tables relevant to the calculation, including totals and meaningful
edge cases. A match covers the contexts you checked, not every context.
Live DAX needs a suitable query endpoint; a check against SQL also needs a SQL connection.
Each model has one optional SQL endpoint (`connection_context` shows it as `sqlEndpoint`; set or
clear it with `set_model_sql_endpoint`). A check that names no SQL source reads from it, so leave
the source off a new check unless that one check needs a different warehouse.
Without those connections, complete useful metadata work and identify what remains untested.

Use these action meanings consistently. `apply_plan` and `apply_plan_items` change the working model
and create one undoable edit. `save_model` writes local model files. `save_workflow` or
`edit_workflow_document` writes a reviewed workflow file. `deploy_live` is a separate remote write
after its preview and confirmation. Tools that restore a version change a live destination and local Undo
does not reverse them. A no-model, no-live-connection, stale-query or permission result should name
one useful next action instead of pretending the requested check ran.

Report what changed, where it was saved or published, and checks actually run. Errors, skips and
unavailable traces are not successful verification. Respect the user's team rules and
permission settings; this guide does not add approval steps.
