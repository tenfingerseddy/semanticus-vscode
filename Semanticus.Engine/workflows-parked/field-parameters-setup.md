---
name: field-parameters-setup
title: Add field parameters for reporting flexibility
description: Let report users switch which measures or dimensions a visual shows, without model bloat. Build the field parameter, name and describe it, handle the Copilot/Data-Agent complexity honestly (the disconnected SELECTEDVALUE table trips them up), confirm the intentional disconnection, then gate on the report-side visual test that only a human can do.
version: 1
strictness: hard
triggers: [create_field_parameter]
---

## Step 1: Define the switch scenario

Field parameters exist to let REPORT users switch what a visual shows (which measures, or which dimension to
slice by) without you shipping a separate visual per option. Pin down the scenario before
building: which measures or dimensions should users switch between, and on which visuals. A field parameter
with no clear report intent is just an extra disconnected table; the report experience is the whole point, so
capture it first.

```yaml gate
inputs:
  - name: switchScenario
    question: "Which measures or dimensions should report users be able to switch between, and on which visual(s) will the field parameter drive the switch?"
    type: text
    required: required
```

## Step 2: Build the field parameter

Create it with `create_field_parameter` over the fields from Step 1: this lets users switch without model
bloat, no measure explosion. Name the parameter table clearly for what it switches (e.g. "Metric Selector",
"Analyze By") and give it a `set_description`, and hide any helper columns with `set_column_hidden` so only
the selectable label shows. State the AI caveat HONESTLY: a field parameter is a DISCONNECTED table read via
`SELECTEDVALUE`, and Copilot / Fabric Data Agents can mis-handle that pattern; the `DAC-FIELD-PARAM-COMPLEXITY`
advisory will surface for exactly this reason. If this model is consumed by an AI experience, consider
excluding the parameter table from the AI data schema with `set_ai_data_schema` so the agent is not confused by
a table it cannot reason about.

```yaml gate
ops: [create_field_parameter, set_column_hidden, set_description, set_ai_data_schema]
```

## Step 3: Confirm the structure and the advisory

Read the shape back with `model_graph_summary`: the parameter table shows as INTENTIONALLY disconnected (no
relationship to any fact), and that is correct, not a modelling error. Run `ai_readiness_scan` and expect the
`DAC-FIELD-PARAM-COMPLEXITY` advisory (Info) to appear; the point is that you UNDERSTAND it, not that you make
it vanish. If the team accepts the trade-off (they want the report flexibility despite the AI caveat), record
that decision with `waive_finding` and a reason so the readiness score stays honest rather than silently
carrying an unexplained finding.

```yaml gate
ops: [model_graph_summary, ai_readiness_scan, waive_finding]
inputs:
  - name: disconnectionUnderstood
    question: "Do you confirm the parameter table is intentionally disconnected, and have you either accepted (and waived, with a reason) or acted on the DAC-FIELD-PARAM-COMPLEXITY advisory?"
    type: text
    required: answer-or-decline
```

## Step 4: Gate on the report-side visual test

Be honest about the limit of engine verification here: field-parameter behavior is a REPORT-side experience:
whether the switcher actually toggles the visual correctly is something you confirm in Power BI Desktop or the
service, visually and manually. There is no DAX probe or equivalence check that proves it, so no engine
executor applies to this gate. Confirm below that the switch was tested where it lives (on the visual) before
you call it done.

```yaml gate
inputs:
  - name: visualsTested
    question: "Have you tested the field parameter on its intended visual(s) in Desktop/the service and confirmed switching changes the displayed measure/dimension as intended? (This is a manual, report-side check; nothing verifies it automatically.)"
    type: text
    required: answer-or-decline
```

## Step 5: Save the model

Persist with `save_model` so the field-parameter table, its description, hidden helper columns, any AI-schema
exclusion, and the recorded waiver land in the TMDL beside the model. Note for the next author which visuals
depend on this switcher, so a later rename or field change does not silently break the report experience.

```yaml gate
ops: [save_model]
```
