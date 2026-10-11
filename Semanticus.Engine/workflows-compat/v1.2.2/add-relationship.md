---
name: add-relationship
title: Connect tables
description: Inspect two endpoints, create the relationship with a deliberate shape, and check one meaningful result.
whenToUse: "Use when loaded tables need a relationship, such as a fact and dimension. For a new source table, use Import a table; for broad cleanup, use Tidy a model."
version: 1
strictness: hard
triggers: [create_relationship, set_relationship_cardinality, set_relationship_crossfilter]
---

## Step 1: Choose the endpoints

Use `get_model_graph` and the object browser to inspect the two key columns. Name the foreign-key and
lookup-key endpoints, state which side is ONE and which is MANY, and use single direction by default.
Call out duplicate lookup keys, a many-to-many shape, an inactive role-playing path or a bidirectional
reason before creating anything. These are relationship risks; unrelated BPA or description findings do
not belong in this job.

```yaml gate
ops: [get_model_graph]
inputs:
  - name: joinSpec
    question: "Which two key columns are joined, which side is ONE versus MANY, and why is the chosen filter direction safe?"
    type: text
    required: required
```

## Step 2: Create the relationship

Call `create_relationship` with the inspected endpoints, cardinality and single direction. Use
`set_relationship_cardinality`, `set_relationship_crossfilter` or `set_relationship_active` only when the
returned shape needs correction. Re-read the graph to ensure the new path did not create an unintended
duplicate or inactive route.

```yaml gate
ops: [create_relationship, set_relationship_cardinality, set_relationship_crossfilter, set_relationship_active, get_model_graph]
```

## Step 3: Check a representative aggregate

Reuse an existing measure when possible. Give `target` its ref and supply an independently known control
total or decline it with the reason. Probe the measure in a context that uses the new relationship, such
as Sales by Customer region. A mismatch or an unexpected many-to-many result requires fixing the endpoint,
cardinality or direction before saving. A declined value is recorded as unverified; it is not a pass.

```yaml gate
strictness: hard
ops: [probe_measure]
inputs:
  - name: target
    question: "The existing measure to check through this relationship, for example measure:Sales/Net Sales."
    type: objectRef
    required: required
  - name: controlTotal
    question: "An independently known result in a relationship-using context; decline when none is available."
    type: verification
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.controlTotal.answered
    probe: controlTotal
```

## Step 4: Save the relationship

Call `save_model` after the graph and representative result are reviewed. Record the chosen direction,
cardinality and any unresolved duplicate-key or many-to-many risk so a later model change knows what was
checked.

```yaml gate
ops: [save_model]
```
