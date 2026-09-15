---
name: semanticus
description: Use Semanticus to inspect, edit, test and publish Power BI or Fabric semantic models through MCP. Use when working in the Semanticus workbench or acting on its open model.
---

# Work with Semanticus

Semanticus is one live model shared by the user in VS Code and their own AI assistant over MCP.
Use the connected Semanticus tools for model work. The user's request and existing authorization
take precedence over this guidance.

## Start with the current session

Call `get_model_summary` at session start, after compaction or on a handoff. It gives the current
model, connection, tier, Primer excerpt, unfinished work and useful next calls. Read
`get_model_primer` when business definitions or known model issues matter. Fetch relevant details
instead of loading the whole model or every tool description. Reuse the current connection and
account when they fit; reconnecting is not a routine first step.

Distinguish the editable model, the live query connection, and the publishing destination.
They can differ. Check `connection_status` before measuring or testing. A local metadata edit
does not update the query server.

The VS Code workbench is organised as five areas: Model, Calculations, Checks, Changes and
Workflows. Use the area that matches the user's first question, then open its named tool. Model
holds diagrams, lineage, search, previews, Power Query and notes. Calculations holds DAX Lab.
Checks holds Tests, Model quality, AI understanding and Results. Changes holds Proposed,
History and Published. Workflows holds the library, runs, governance and Author. The area labels
are navigation only; existing tool ids and MCP names remain stable.

## Choose the route that fits

| Task | Starting point |
|---|---|
| Explain or inspect | Summary and Primer, then the relevant object, DAX, lineage or data tool |
| Edit one object | Its typed edit tool and the object reference returned by Semanticus |
| Review several changes | `propose_plan` or `add_plan_item`, then `get_plan` and `apply_plan` |
| Improve AI readiness | `ai_readiness_summary`, scoped findings and a Change Plan |
| Optimise DAX | `get_dax`, `benchmark_dax`, `profile_dax`, `verify_dax_equivalence` |
| Test business answers | Existing model tests or interview questions with independent expected answers |
| Repeat a process | `list_workflows`, then `get_workflow` for the current definition |
| Save or publish | `save_model` for local files; preview `deploy_live` before a requested live write |

Companion skills cover DAX optimisation, AI readiness, interviews, Change Plans, workflow creation
and model knowledge. Read only the relevant skill. Get workflow definitions through MCP rather
than reconstructing them from memory. Inspect an existing run or plan before starting another.

For workflow authoring, read `get_workflow_document` first. It returns the exact source, path,
byte hash and the editable projection used by Steps, Canvas and Source. Use `preview_workflow_edit`
with that path and hash plus typed edits, or with the current unsaved draft, to inspect the exact
proposed text, full-context diff and warnings without writing. For an existing user file, apply the
reviewed exact text with `edit_workflow_document`; for a new project workflow use `save_workflow`
with `createOnly: true`. `upgrade_workflow` is a dry-run by default and needs the reviewed path and
byte hash to write. Stock workflows are read-only, so copy one into the project before editing.
Use `get_workflow_layout` and `save_workflow_layout` only for Canvas positions. After a successful
edit, reread the library on the next call. The VS Code view updates at once; your assistant sees
the change on its next call.

## Work on the shared model

Model edits share one undo history. Assistant edits appear in the UI immediately; user edits reach
the assistant in its next tool result. Read returned changes and refresh affected state before
continuing a stale plan. Remote actions have their own preview and commit semantics; local undo
does not promise to reverse every remote action.

Choose test contexts from the tables relevant to the calculation, including totals and meaningful
edge cases. Matching tested contexts is evidence for those contexts, not a universal proof.
Live DAX needs a suitable query endpoint; SQL reconciliation also needs a SQL connection.
Without those connections, complete useful metadata work and identify what remains untested.

Use these action meanings consistently. `apply_plan` changes the working model and creates one
undoable edit. `save_model` writes local model files. `save_workflow` or
`edit_workflow_document` writes a reviewed workflow file. `deploy_live` is a separate remote write
after its preview and confirmation. Restore-point tools change a live destination and local Undo
does not reverse them. A no-model, no-live-connection, stale-query or permission result should name
one useful next action instead of pretending the requested check ran.

Report what changed, where it was saved or published, and checks actually run. Errors, skips and
unavailable traces are not successful verification. Respect the user's chosen workflow profile
and permission settings; this guide does not add approval gates.
