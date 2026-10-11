---
name: calendar-setup
title: Set up a calendar
description: Choose a calendar convention, create its mappings, and optionally check one time calculation before saving.
whenToUse: "Use when the model needs a new or updated date calendar. For comparisons on an existing calendar, use Add time comparisons; for importing a date table, use Import a table."
version: 1
strictness: hard
triggers: [define_calendar_from_template, define_calendar, list_calendars]
---

## Step 1: Choose the calendar shape

Choose the convention that matches the business: `gregorian`, `fiscal`, `iso`, `445` or `13period`.
Name the target table and date column when they are not unambiguous. A table may carry more than one
calendar, so this is an additive mapping decision. The fiscal start month applies only to `fiscal`.

```yaml gate
inputs:
  - name: template
    question: "Which calendar shape: gregorian, fiscal, iso, 445, or 13period?"
    type: text
    required: required
  - name: targetTable
    question: "Which existing date table or new table name should receive the calendar? Leave blank only when Date is unambiguous."
    type: text
    required: optional
  - name: dateColumn
    question: "Which date column should be mapped? Leave blank only when one DateTime column is unambiguous."
    type: text
    required: optional
  - name: fiscalStart
    question: "For a fiscal calendar, the first month numbered 1-12; decline or leave blank for other shapes."
    type: number
    required: optional
```

## Step 2: Check compatibility and create the mapping

Call `list_calendars` first. Calendar metadata needs compatibility level 1701 or higher; raising it is a
one-way model change, so ask before `set_compatibility_level`. Then call
`define_calendar_from_template` (or the explicit `define_calendar`) and show the returned mappings,
skipped columns and compatibility note. Existing columns are retained and mapped as returned.

```yaml gate
ops: [list_calendars, set_compatibility_level, define_calendar_from_template, define_calendar]
```

## Step 3: Review the mapping and optionally check time logic

Review the resulting calendar with `list_calendars`. When this task also creates a time measure, use
`create_measure` with the calendar-name overload and give `target` its ref plus an independent expected
value in a meaningful context. The scalar probe is optional for a metadata-only calendar; when no trusted
number is available, decline it and say that time logic remains unchecked. Do not imply that the mapping
alone proves every YTD or fiscal boundary.

```yaml gate
strictness: hard
ops: [list_calendars, create_measure, probe_measure]
inputs:
  - name: target
    question: "Optional ref of a calendar-aware time measure to check, for example measure:Sales/Sales YTD. Leave unanswered for metadata-only setup."
    type: objectRef
    required: optional
  - name: verificationValue
    question: "An independently known result for that time measure and context; decline when no calculation was created or no trusted value exists."
    type: verification
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.verificationValue.answered
    probe: verificationValue
```

## Step 4: Save the calendar

Call `save_model` and record the calendar convention, compatibility change and any representative time
check. The calendar becomes the input for later Add time comparisons work.

```yaml gate
ops: [save_model]
```
