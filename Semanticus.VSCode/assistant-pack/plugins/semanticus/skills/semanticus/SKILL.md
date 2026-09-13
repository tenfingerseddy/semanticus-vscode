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

## Work on the shared model

Model edits share one undo history. Assistant edits appear in the UI immediately; user edits reach
the assistant in its next tool result. Read returned changes and refresh affected state before
continuing a stale plan. Remote actions have their own preview and commit semantics; local undo
does not promise to reverse every remote action.

Choose test contexts from the tables relevant to the calculation, including totals and meaningful
edge cases. Matching tested contexts is evidence for those contexts, not a universal proof.
Live DAX needs a suitable query endpoint; SQL reconciliation also needs a SQL connection.
Without those connections, complete useful metadata work and identify what remains untested.

Report what changed, where it was saved or published, and checks actually run. Errors, skips and
unavailable traces are not successful verification. Respect the user's chosen workflow profile
and permission settings; this guide does not add approval gates.
