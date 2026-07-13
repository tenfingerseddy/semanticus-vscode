---
name: dimensional-design
title: Design your star schema up front: Kimball's four steps, guided step by step
description: Select the process, DECLARE THE GRAIN as a binding contract, identify conformed dimensions and true-to-grain facts, and capture the expected numbers: the star-schema design decisions made deliberately and up front, before a single table is built.
version: 1
strictness: hard
triggers: [create_table, create_measure, build_model_from_spec]
---

## Step 1: Select the business process

Name the ONE measurement event this model is about: orders, shipments, payments.
"Sales" is too vague: a process is a single event that produces one fact table at one grain, not a
subject area. Get this wrong and every downstream decision inherits the ambiguity. This is a
requirements conversation, not a lookup: ask the user what business event they are measuring and
why, and record their words. Kimball is explicit that these decisions weigh business need against
source reality in a COLLABORATIVE session; a modeller working alone is a recipe for failure.
Design is a loop, not a line: later steps may send you back here.

```yaml gate
inputs:
  - name: businessProcess
    question: "Which single business process (measurement event) does this model capture? Name one event that produces one fact table (e.g. 'sales orders', not 'sales')?"
    type: text
    required: required
```

## Step 2: Declare the grain

The grain is a BINDING CONTRACT: state exactly what one fact row represents *before* choosing any
dimension or fact. Grain is set by dimension key VALUES, not merely which keys exist:
first-of-month dates + ProductKey means month-by-product grain. Never mix grains in one fact table.
Because the grain is otherwise undocumented model metadata, this workflow closes that gap by
convention: write the grain statement into the fact table's own `description` with `set_description`,
prefixed literally with `Grain: ` (e.g. `Grain: one row per order line`). That makes the contract
readable to the next human, to BPA, and to the LLM grounding.

```yaml gate
ops: [set_description]
inputs:
  - name: grainStatement
    question: "In one sentence, what does exactly one row of the fact table represent (e.g. 'one row per order line item per day')?"
    type: text
    required: required
```

## Step 3: Identify dimensions and facts

Now, and only now, choose the dimensions and facts. Every dimension must be
consistent with the declared grain; plan CONFORMED dimensions: a single Date, Customer, Product
shared across every fact table so numbers reconcile across processes. Each fact must be true to the
grain; prefer additive numerics, and store ratios as numerator/denominator to compute in measures,
never as pre-divided values. Read the current shape with `get_model_graph` (or `model_graph_summary`
for a large model) and classify STRICTLY: a table is a fact or a dimension, never both: no
mixed-role tables. The star schema is mandated twice over: it is how the query engine filters
efficiently, and it is the shape the LLM's generated DAX assumes. Confirm the classification below.

```yaml gate
ops: [get_model_graph, model_graph_summary]
inputs:
  - name: classificationConfirmed
    question: "Have you classified every table strictly as a fact OR a dimension (no mixed-role tables), with dimensions planned as conformed across the facts?"
    type: text
    required: answer-or-decline
```

## Step 4: Capture the expected numbers

Close the design session by capturing the verification values the rest of the design depends on.
Ask the business for a few known-good figures and the filter context each holds at:
last year's total revenue, a specific customer's order count, a month's shipped units. These are not
trivia: they become the reference values every later measure, calendar, and workflow probes against,
so a wrong number is caught the moment it is authored rather than in production. Record them now while
the business expert is in the room. If none are available yet, decline explicitly and note that later
verification gates will run thinner until they are supplied.

```yaml gate
inputs:
  - name: expectedNumbers
    question: "A few known-good figures from the business plus the filter context each holds at (e.g. 'FY2024 revenue = 4.2M', 'orders for ACME in Jan = 318'); these become the verification values for every later probe."
    type: verification
    required: answer-or-decline
```
