---
name: semanticus
description: Use Semanticus to inspect, edit, check and publish Power BI or Fabric semantic models through MCP. Use when working in the Semanticus workbench or acting on its open model.
---

# Work with Semanticus

Semanticus is one live model shared by the user in VS Code and their assistant over MCP.
Use the connected tools. The user's request and existing authorization take precedence over this guidance.

## Start with the current session

Call `get_model_summary` at session start, after compaction or on a handoff.
It gives the model, connection, tier, Model notes, unfinished work and useful next calls.
Read `get_model_primer` for business definitions or known issues. Fetch only relevant details.
Reuse the current connection and account when they fit.

Distinguish the editable model, the live query connection, and the publishing destination.
They can differ. Check `connection_status` before measuring or testing. A local metadata edit
does not update the query server.

The workbench has six areas: Plan, Explore, Edit, Review, Publish and Workflows.
The Plan lists work, who does it and what waits for what. History records edits and their undo points.
Explore holds Data, Ask your data (Pro), Diagram, Lineage and Size. Edit holds Find and replace,
Queries, AI instructions (including Model notes), Advanced modelling and Power Query.
Review holds Model quality, AI readiness, Validation and Docs. Publish holds published versions.
Workflows lists the user's workflows; Author edits one. Use the area that matches the person's first question.

## Drive the Studio

Before working on a page, call `show_in_studio(place, say[, focus, press])` once per step, not per tool call.
Use a place id such as `explore/lineage`, `plan/start` or `publish/changes`.
Make `say` one plain sentence, at most 200 characters: "I am checking lineage to see which reports use Net Sales."
Use `focus` for an object ref and `press` for a supported control id.
The press shows the control; do the work through MCP.

Read `screen` in the result. `moved` means the person sees the step.
`noted` means the person has the wheel or chose I drive. Carry on without trying to move their screen.
`noStudio` means no Studio is connected; explain the step in chat and continue.
When the person clicks in the Studio, they have the wheel until their next message.
Keep working, but do not move their screen. I drive also leaves your work running.

When the person's own words ask for a step (publish, delete, run the plan, build), that is their yes. Do it now.
Do not ask again. Pass on what the preview said, and stop only if it differs from what they asked for.
When you chose a start build or a publish yourself, describe it in chat and wait for their yes. A publish and a save-back are writes Undo cannot take back. Edits to the working copy are not.
Their yes in chat is enough; act on it without requiring a Studio click.
Edits change Semanticus's working copy on this computer only, also for a model opened from a Power BI workspace.
Nothing reaches the workspace until the model is saved back to it or published. One `undo_change` takes an edit back.
So an edit is not a change to a shared resource; a save back to a workspace or a publish is.
For a start build, read `get_start_plan`, then call `approve_start_plan(digest)` and `build_start_plan(digest)` after the yes.
Pass the `digest` of the plan they approved. Never touch the Permissions page or approve or deny held permission actions.
`grant_start_disclosure(digest)` lets Jev see the source and its tables. Say in chat what it covers. On the person's yes in chat,
call it with the same `digest`; nothing needs answering in Studio.

Three words cover repeatable work. A workflow is the user's own, in their list. A starter is a
workflow that comes with Semanticus, copied to make a workflow of their own. Team rules say which
workflows a team requires for a task, and how firmly their checks apply. Use these words with the
user even where a tool's name says profile or template.

## Words for the person

Say what you did in plain words. Never say a tool name to the person (`deploy_live`, `rollback_push`, `undo_change`, `apply_fixes` and the rest). Say "publish", "go back to the last copy", "undo" or "fix" instead. Say "Best practice" and "AI readiness", not rule ids or the names in square brackets.

## Choose the route that fits

| Task | Starting point |
|---|---|
| Explain or inspect | Summary and Model notes, then the relevant object, DAX, lineage or data tool |
| Edit one object | Its typed edit tool and the object reference returned by Semanticus |
| Edit many objects | Pro: `write_words`, `edit_objects`, `waive_findings`, `create_measures`. Any tier: `set_properties`, `set_display_folder`, `delete_objects`, `apply_dax_script` |
| See what is worth doing | `get_model_plan`, then `apply_plan_items` (Pro) with its `readyDigest` to run the plan |
| Review several changes | `propose_plan` or `add_plan_item`, then `get_plan` and `apply_plan` |
| See who changed what, or go back | `get_plan_history`, then `undo_change` with `toEntry` to undo to a point |
| Improve AI readiness | `ai_readiness_scan` with `detail='summary'`, scoped findings and a change list |
| Optimise DAX | `get_dax`, `benchmark_dax` or `benchmark_cold_warm`, `profile_dax`, `verify_dax_equivalence` |
| Stop a running query | `cancel_query` |
| Find a workspace's SQL endpoint | `list_workspace_sql_endpoints` gives each lakehouse and warehouse with its server and database, for `start_model_from_sql`. If it says a sign-in is needed, pass its sentence to the person |
| Set a connection environment or default account | `list_connections`, then `label_connection`; `list_account_profiles`, then `set_default_account_profile` |
| Check business answers | `run_tests` and `save_test` with independent expected answers |
| Repeat a process | `list_workflows`, then `get_workflow` for the current definition |
| Answer a question about the numbers | `ask_data` (Pro). It always answers and its `assumption` line says what it assumed, so pass that line on. If it names another reading ("You may have meant Y"), say so. `list_data_questions` lists past ones; `save_test` keeps an answer as a check |
| Explain a number | `explain_value` for one cell of one measure: its filters, what feeds it, where it comes from, the top contributors, and why it is blank |
| Turn words into workflow steps | `describe_workflow_steps`, then `save_workflow` |
| Save or publish | `save_model` for local files; `deploy_live` previews first, then commits with the token the preview returned. When the person asked for the publish, commit in the same turn. A copy raised to a higher compatibility level raises the live one as part of the same publish (never lower, one way): the preview lists it, so there is no separate tool for it |
| Compare the working copy with the live model | In Studio, Publish > Published > Changes takes the publish destination as the other side. Through MCP, `model_diff` with `targetEndpoint` and `targetDatabase` does the same compare and changes nothing, there or here (add `authMode` or `targetTenantId` if the sign-in needs them); without a target it compares with a git ref or a file. `apply_model_diff` with `commit=false` is the dry run of a merge |
| Publish a model that has no published copy yet | `publish_new_model` previews first (workspace, name, Direct Lake or Import), then `commit=true` creates it, loads its data and makes it the destination. When the person asked for it, commit in the same turn. Pass on its line; an import model needs its source sign-in set in the Power BI service. Never pass `authMode` `interactive`, `devicecode` or `azcli` and never run or suggest `az login`: the saved sign-in is used with no question, and with none saved the tool says in one line to press Connect in Studio, so tell the person that and stop |

Companion skills cover DAX optimisation, AI readiness, change lists, workflow creation, model
knowledge and model interviews (your assistant's own check, which Studio does not show). Read only
the relevant skill. Get workflow definitions through MCP rather
than reconstructing them from memory. Inspect an existing run or plan before starting another.

When the person asks you to run or work the plan, or says "get this model as good as it can be", do every row a tool can do, in one go, without asking. The person is the last resort.
1. Run the Semanticus rows: `apply_plan_items` with no items and the `readyDigest` from `get_model_plan`. It runs every ready Semanticus row,
   every wave, as one undoable step, then puts your rows on your queue. Use its `entryId` with `undo_change` and `toEntry` to undo the run.
2. Work your queue: `list_assistant_requests`, then `claim_request`, do the work, `complete_request`. Repeat until it is empty. Each row's `brief` has
   `Done when:` and `Default, no need to ask:`. Use that default.
3. Take the person's rows that a tool can do: `set_plan_doer(id, 'assistant')`, then claim the task it makes. The person can move a row back.
4. A row with every change unticked: keep the changes that make sense with `set_plan_item`, approve with `model_plan_action` (`approve`, with the row's `digest`), and say what you kept.
5. Then `refresh_plan` and work any new row. Keep going until the Plan has no Ready row a tool can do.
6. Then read BOTH grades again: `ai_readiness_scan` with `detail='summary'` (AI readiness) and the Best practice scan (`bpa_scan` with `detail='summary'`). Your own changes cause findings: hiding an unused column makes a "Remove unnecessary columns" finding. Work every new finding the same way, then read both grades once more.
7. Finish with both grades in the last message, before and after: "AI readiness F 42 to A 96. Best practice F 0 to B 85." Never report only one of them. If one is below B, say what is left and why each row is left.
"Fix N best-practice issues" and "Remove N unused columns" are tool rows. Do them. Do not leave them Ready, and do not call them low priority.
Unused generic columns (Key, ID, Code, Name copies, hidden helpers): the default is REMOVE. First find the model's reports (local folders and published ones), then set_report_scope and check_reports; if the model has none, call set_report_scope with an empty list, which counts as checked. Then `unused_objects`, then `remove_safe_objects` (it re-checks each column and the chosen reports, and never removes one a hierarchy, sort-by, role, calculation item, measure, relationship or report uses). Hide a column when something uses it, and write on the row why. A `visibleAttribute` column (the sweep never takes it) is removed with `delete_object` and reported only when `unused_objects` marks it, which it does only after at least one report is chosen and every chosen report is checked (or the model has no reports). With no report checked, hide the column and write on the row why. A table nothing uses (no relationship, no measure, no role, no checked report) is removed the same way once the reports are checked, then reported: do not ask whether to keep it, because Undo takes it back. Only a table something uses is kept, and you leave it out of what Copilot sees and write why on the row. A hidden column that nothing uses is still a Best practice finding.
Anything Undo can take back is done first and reported after: renames, removing unused objects with `remove_safe_objects`, removing duplicates with `remove_safe_objects` once nothing in the model or in a chosen report (`check_reports` first) uses them, hiding a duplicate a report uses and saying why on the row, hiding a field something uses,
synonyms, descriptions, raising the compatibility level, adding a calculated date table (`generate_date_table`, which marks it). Do not ask first.
Ask the person only for a sign-in or permission, for a fact no tool can find (a business definition, a check's expected value that the source cannot give), or for a step Undo cannot take back.
A model with no date column and no date table is such a fact: say so on the row and ask once.
Do the removals first: remove unused columns before you write words or formats, so no words go on a column that is about to go. A row that is Needs you only because you removed what it names: `decline_request` it with that reason, or `refresh_plan`.
Do not wait on a stuck row. The tools work without the Plan: when a row you can do is Waiting behind a row that is stuck, do the work with the plain tools (`unused_objects` then `remove_safe_objects`, `apply_fixes`), then leave a reason on the row that is still stuck.
Every row you leave or cannot do carries its reason ON THE ROW, not only in chat. A row you found on the Plan that is not yours yet: `set_plan_doer(id, 'assistant')`, `claim_request`, then `decline_request` with the reason (or `complete_request` with a reply when it is done in a way the check cannot see). A Waiting row you cannot claim gets its reason with `set_plan_task_note`; it leads the row's state note. The person reads that reason in the row's state note. A row whose only blocker is a fact no tool can find says so plainly: "Needs a date column: none exists in this model." Never leave a row Ready or Waiting with no reason.
Save with care. Never write over the person's only copy without a way back. Save to the same file when it is in git or you made a backup first; otherwise save a copy next to it and say so.
How common rows are done: a check (`Add a check for X`) needs an expected value. Get it from the source (`reconcile_measure` against the SQL
endpoint); ask one question only when the source cannot give it. Then `save_test`. `Save the changes you have made` is `save_model`, saved as above.
`Look at N reports again` is `check_reports` (a published report needs `consent` after the person's yes).
`Fix N best-practice issues` and `Fix N AI readiness issues` hold every finding that costs a grade and has no row of its own. Each line names the rule, its points
and the tool. A float column is your decision: if 4 decimal places hold the values, `apply_fix` (system `bpa`) changes the type, one Undo takes it back, and you say so.
`Raise compatibility level to N` and a Copilot limit are yours: make the change and say what you did.
`Remove N unused columns`: `unused_objects`, then `remove_safe_objects`, and `delete_object` for a `visibleAttribute` column only when `unused_objects` marks it (reports found, chosen and checked first); otherwise hide it and write why on the row. You do this row; never leave it Waiting. It removes only what nothing uses, and says what went and what it kept. Unused tables go the same way.
An assistant row comes first only when another row needs it. The person's rows never hold up the run.
A row waits ("Waiting for you", "Waiting for your assistant") only for the row it names. Say in chat what is left and why.

Read `get_workflow_document` before authoring; it returns the source, path and byte hash.
Check with `preview_workflow_edit`, then write with `edit_workflow_document`.
Use `save_workflow` with `createOnly: true` for a new project workflow.
Starters are read-only; `instantiate_workflow_template` copies one as the user's own.
`get_workflow_layout` and `save_workflow_layout` hold Canvas positions only. The semanticus-distill-workflow skill has the detail.

On Pro, Jev (the Semanticus helper) makes small modelling choices that become Plan rows.
It also reads questions for Ask and drafts workflow steps. `get_decision_points` lists its choices and whether each is live.
The person allows Jev once per model; `get_consent` lists what goes. On their yes in chat, call `set_consent`; nothing needs answering in Studio.
Semanticus remembers that permission on this machine. When Jev cannot be reached, Ask says so and does not guess.

## Work on the shared model

```text
Read -> author many items -> one bulk call -> review the result
```

Semanticus has one writer. Parallel calls and parallel helpers do not make writes faster.
On Pro, send one bulk call per page of work, one after another.
You may give pages to helpers to author the words, then send each page in one `write_words` call.
When many objects get the same value, use `set_properties`.
Put independent reads in one message to save turns; the engine still reads them one at a time.
For a new model, use `write_start_words` before the build. It takes many words in one call.

On Pro, never edit objects one call at a time when there is more than one.
Use `write_words`, `waive_findings`, `edit_objects`, `apply_plan_items` or a script in one call.
Use `waive_findings(items: [{system, ruleId, objRef?, reason}])` for many findings.
Use `write_words(items: [{objRef, description?, synonyms?, displayFolder?}])` for many objects.
Use `edit_objects(items: [{objRef, set: {name?, description?, formatString?, formatStringExpression?, aiSchema?, displayFolder?, hidden?, dataCategory?, summarizeBy?, sortByColumn?, dataType?}}])` for different property values across objects.
Use `unwaive_findings` or `apply_fixes` with items that name `system`, `ruleId` and `objRef` for selected findings.
Use `duplicate_objects` with `objRef`, optional `newName` and optional `targetRef` for each copy.
Use `set_doc_sections` with `objRef`, `sectionKey` and `markdown` for each section.
Use `set_perspective_members` with `perspectiveRef`, `objRef` and `include` for each cell.
Use `set_object_permissions` with `roleName`, `objRef` and `permission` for table or column OLS cells.
Use `create_measures` to create many measures in one call. Use `delete_objects` to delete many objects in one undo step.
Use `apply_dax_script` for many DAX expression edits. `apply_tmdl` updates existing objects; neither script tool creates new measures.
Each call writes all valid items in one undo step and names any rejected items. Do not make many single calls.
Synonyms replace the object's list; pass the complete list you want to keep. Omit a field to leave it unchanged.
Read `get_words_queue` a page at a time. Copy each item's `objRef` and author its words.
Read every page before writing, or read again from offset 0 after each write.
Writes change the queue, so an old `Next` offset can skip work after a write.
Send descriptions, synonyms and folders in one `write_words` call. Send names and static formats in one `edit_objects` call.
Use `edit_start_plans(items: [{op, target, value}])` to answer many start-plan questions at once.
Use `set_plan_items` or `add_plan_items` for many pending changes. Use `apply_plan` to apply their model edits.
Use `plan_rows` to pause, resume, note, assign, skip, unskip, remove, restore or mark done on many rows.
Name `id` and `action` for each row. A note or skip uses `text`; a doer switch uses `doer`.
These plan controls return one final plan per call. They do not create a model undo step.
Use `plan_rows` with `action: restore` to restore removed rows. Use `apply_plan_items` to run many reviewed rows.
Use `set_ai_instructions` once for the model's instructions.
On Free, use `set_properties` for one value on many objects, `set_display_folder` for many objects in one folder,
and `apply_dax_script` for many formulas. Send other edits one at a time, not in a parallel burst.

Model edits share one undo history. Assistant edits appear in the UI immediately. User edits reach
the assistant in its next tool result as one line, `changed since your last call (in Studio): <refs>; rev N`
(an edit by another assistant says `by another assistant`). When that line appears, re-read the named
objects and the plan before continuing work that may be stale. Remote actions have their own preview and
commit semantics; local undo does not promise to reverse every remote action.

The person can give you tasks on the Plan. A `Plan:` result line lists ready tasks and their dependencies.
It appears only when the list changes. Call `list_assistant_requests`, then `claim_request` and keep its `claimToken`.
Do the work, then call `complete_request` with a short reply, or `decline_request` with the reason.
Edits count toward your most recent claim; claim again before switching tasks.
If the tasks are paused, carry on with what the person asks directly.

Use `add_plan_task` and `add_plan_target` for tasks and aims; `model_plan_action` with `done` marks either done.
Use `pause_plan_item`, `set_plan_doer`, `move_plan_task_up`, `set_assistant_queue` and `set_plan_task_note` for Plan controls.
Use `remove_plan_row` / `restore_plan_row` for rows and `remove_plan_target` for aims.
Use `refresh_plan` when the person asks to refresh the plan or whether new findings are on it. It scans AI readiness and Model quality again,
adds rows for anything new and clears finished rows off the list (they stay in the plan's history). Pass on its one sentence.
Readiness and Model quality findings join the plan by themselves; do not add them as tasks.
Use `reading_card` to read the model's first grade (the Plan page shows it in its header) with the grade after the safe fixes and the top three issues, each saying who fixes it.
Use `fix_them` to apply every safe fix on the card as one undo step and adds the writing that is left to the plan as tasks for you. Take each task with `claim_request`. `undo` takes the fixes back.
Use `add_plan_repeat`, `set_plan_repeat` and `delete_plan_repeat` for repeats.
After the person's yes, use `approve_draft` or `continue_draft` when needed.
Use `edit_draft` with `apply_draft` only after they agree to replace their newer change.

A connection or target with no label counts as dev, the lightest label. Do not ask again before a removal the person already asked for.
When a task is open-ended ("make it better"), the request is the yes for every undoable step. Report what you did; the person undoes what they do not want.

When you find a problem inside the work the person asked for (a poor format, a missing description, an unclear name),
fix it in the same pass and say what you fixed. Do not wait to be asked again. Offer work outside the request with `suggest_next`.

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

After a task, summarise in two or three lines: what changed, where it was saved or published, and checks actually run.
Errors, skips and unavailable traces are not successful verification. Respect the user's team rules and permission settings.
Call `suggest_next` with two or three `{ label, prompt }` steps. Start each label with a verb.
List the same steps numbered in chat, so the person can reply with a number.
