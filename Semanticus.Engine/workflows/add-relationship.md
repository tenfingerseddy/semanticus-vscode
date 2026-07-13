---
name: add-relationship
title: Relate two tables and prove the join did not fan out
description: Relate two tables on their keys at the right cardinality and direction, review the graph for ambiguity or an unintended inactive path, then PROVE a known measure still equals its control total after relating. The relationship equivalent of import-table's control-total proof.
whenToUse: "Connect two tables so a measure can filter across them (a fact to a dimension, or two dimensions through a bridge). Use it whenever a wrong cardinality or filter direction could silently fan out rows and inflate a total. To bring a single table in and prove its own load, use import-table."
version: 1
strictness: hard
triggers: [create_relationship, set_relationship_cardinality, set_relationship_crossfilter]
---

## Step 1: State the join and its cardinality

A relationship is a contract about grain, not just a line between two boxes. Name the
two tables and the key column on each, and state which side is the ONE and which is the MANY. A
dimension's key is unique (one); a fact's foreign key repeats (many). Getting this backwards is how a
join fans out: a many-to-many where you meant one-to-many multiplies rows and inflates every total.
Confirm both key columns are the same data type and, ideally, integer keys. Never relate on a datetime
column; join through an integer date key instead.

```yaml gate
inputs:
  - name: joinSpec
    question: "Which two tables and key columns, and which side is the ONE (unique key) vs the MANY (repeating FK)?"
    type: text
    required: required
  - name: filterDirection
    question: "Single-direction (the default, where the dimension filters the fact) or bidirectional, and if bidirectional, the reason it is needed?"
    type: text
    required: answer-or-decline
```

## Step 2: Create the relationship

Create it with `create_relationship` at the cardinality from Step 1, single filter direction by
default. Reach for bidirectional only with the reason you gave, and set it explicitly
with `set_relationship_crossfilter`. An unjustified bidirectional path is a classic source of
ambiguity and RLS leaks. If the model already relates these tables another way, decide deliberately
whether the new relationship is the active one or an inactive role-playing path, and correct the
cardinality with `set_relationship_cardinality` if the inferred cardinality is wrong.

```yaml gate
ops: [create_relationship, set_relationship_cardinality, set_relationship_crossfilter]
```

## Step 3: Review the graph for ambiguity and stray inactive paths

A relationship never lands in isolation. Walk the model with `get_model_graph` and
confirm this join introduced neither an ambiguous filter path (two routes between the same tables,
which the engine resolves unpredictably) nor an unintended INACTIVE relationship left behind from a
prior attempt. Flag any bidirectional relationship that now touches the same tables. An ambiguous or
duplicated path is a correctness bug you want to catch here, before a measure quietly reads the wrong
route.

```yaml gate
ops: [get_model_graph, set_relationship_active, set_relationship_crossfilter]
inputs:
  - name: graphReviewed
    question: "Have you confirmed no ambiguous filter path and no unintended inactive/duplicate relationship between these tables?"
    type: text
    required: answer-or-decline
```

## Step 4: Prove the join did not fan out (HARD GATE)

A relationship is unproven until a known number survives it. Pick a measure whose
correct total you already know, such as a row count or an additive total the business can confirm
independently, and give `target` its ref. The wrong cardinality or an accidental bidirectional path
fans out rows and inflates that total; a dropped-key mismatch deflates it. The hard gate probes the
measure against the control total you supply: if it no longer matches, the join changed a number it
must not have, so fix the cardinality or direction and re-probe. A structural `bpa_clean` on the model
surfaces the ambiguity and inactive-relationship smells alongside the numeric proof. The gate holds on
this step until the number matches or you decline the check.

```yaml gate
strictness: hard
ops: [create_measure, probe_measure]
inputs:
  - name: target
    question: "The ref of a known measure to prove the join did not fan out (e.g. measure:Sales/Order Line Count)."
    type: objectRef
    required: required
  - name: controlTotal
    question: "That measure's correct total, confirmed independently by the user, not derived by this model (e.g. 'Order Line Count should be 1,204,882 for 2024')."
    type: verification
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.controlTotal.answered
    probe: controlTotal
  - kind: bpa_clean
    scope: model
```

## Step 5: Save the relationship

Persist with `save_model` so the relationship, its cardinality, and its filter direction land in the
TMDL beside the model. Note which measure and control total you proved the join against, so a future
refresh or key change has a known re-check point.

```yaml gate
ops: [save_model]
```
