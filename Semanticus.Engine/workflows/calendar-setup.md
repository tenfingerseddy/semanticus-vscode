---
name: calendar-setup
title: Set up a calendar and prove its time intelligence
description: Stand up calendar-based time intelligence (CL 1701+) from a template, then PROVE a calendar-aware YTD against a known-good figure before you trust it: modern time intelligence, verified, not eyeballed.
version: 1
strictness: hard
triggers: [define_calendar_from_template, define_calendar, list_calendars]
---

## Step 1: Choose the calendar shape

Decide what the model needs. A calendar maps the date table's columns to
**TimeUnit** categories (Year / Quarter / Month / Week / Date, and the recurring `…OfYear` variants),
which is what lets DAX shift *hierarchically*: this is the modern replacement for a classic Gregorian
date table, not a cosmetic rename. Pick the template that matches the business calendar, and confirm the
target table + date column with the user rather than guessing:

- **gregorian**: Year/Quarter/Month/Day, the standard calendar.
- **fiscal**: a fiscal year starting in `fiscalStart` (FY labelled by its ENDING year).
- **iso**: ISO-8601 weeks (Thursday rule).
- **445**: 4-4-5 retail periods over ISO weeks.
- **13period**: 13 four-week periods.

Several calendars can coexist on one table (Gregorian + Fiscal + ISO), so this is additive, not a
migration. The week-based templates (iso / 445 / 13period) share the same ISO scaffolding columns.

```yaml gate
inputs:
  - name: template
    question: "Which calendar shape: gregorian, fiscal, iso, 445, or 13period?"
    type: text
    required: required
  - name: targetTable
    question: "Which table holds the calendar: an existing date table, or a NEW name to spin up a fresh CALENDAR() table? (default 'Date')"
    type: text
    required: answer-or-decline
  - name: dateColumn
    question: "The date column on that table (leave blank to auto-detect when the table has a single DateTime column)."
    type: text
    required: answer-or-decline
  - name: fiscalStart
    question: "Fiscal template only: the first month of the fiscal year, 1-12 (default 7 = July). Ignore for the other templates."
    type: number
    required: answer-or-decline
```

## Step 2: Confirm support, then generate the calendar

Calendars need **compatibility level 1701+**. Read the current state with `list_calendars`: it reports
`calendarsSupported` and the model's `compatibilityLevel` offline, no connection required. If the model is
below 1701, raise it with `set_compatibility_level` (a one-way upgrade; confirm with the user first). Then
run `define_calendar_from_template` with the answers from Step 1: it generates the template's calculated
columns (only where absent; existing columns are kept and mapped as-is) AND the TimeUnit mappings in one
undoable step. Surface the returned `mappings`, `skipped`, and `note` so the user sees exactly what was
created versus reused.

```yaml gate
ops: [list_calendars, set_compatibility_level, define_calendar_from_template]
```

## Step 3: Prove the calendar-aware time intelligence

A new calendar is worthless until a number confirms it. Author a calendar-aware YTD with `create_measure`
using the calendar-name overload (`TOTALYTD ( [<base measure>], '<calendar>' )`) pointed at the calendar
you just built. Then verify it against a figure the user can check
independently: the classic date-column YTD at the same context, or a source-of-truth total. The hard gate
below probes the measure against that known-good value; if it differs, the gate holds on this step: fix the
expression (or the calendar mappings) and re-probe. Give `target` the ref of the measure you just created.

```yaml gate
strictness: hard
ops: [create_measure]
inputs:
  - name: verificationValue
    question: "A known-good YTD figure at a context you can check independently (the classic date-column YTD, or a source-of-truth total), from the user, not derived by the model."
    type: verification
    required: answer-or-decline
  - name: target
    question: "The ref of the calendar-aware YTD measure you just created (e.g. measure:Sales/Sales YTD (Fiscal))."
    type: objectRef
    required: required
verify:
  - kind: dax_probe
    when: inputs.verificationValue.answered
    probe: verificationValue
```

## Step 4: Save and hand the calendar to the model

Persist the change with `save_model` so the calendar and its mappings land in the TMDL beside the model.
Tell the user what they now have: the agent should author time intelligence with the calendar-name overloads
(`TOTALYTD(expr, '<calendar>')`, `DATESYTD`, flexible hierarchical shifts) rather than classic date-column
patterns on this table; `get_grounding` surfaces the calendars so future edits stay calendar-aware. If a
classic date table still exists alongside, note that both work; the calendar is the modern path.

```yaml gate
ops: [save_model]
```
