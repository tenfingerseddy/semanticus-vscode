---
name: composite-model-setup
title: Set up a composite model with the right storage modes
description: Choose storage mode per table with intent: Import by default, DirectQuery only for a real freshness or volume constraint, and Dual (never plain Import) for dimensions that join DirectQuery facts from the same source, then PROVE a cross-mode figure before you trust the joins.
version: 1
strictness: hard
triggers: [create_directlake_table, set_property]
---

## Step 1: State the freshness requirement, and default to Import

Storage mode is a per-table decision, and the default answer is **Import**: it queries
fast from in-memory VertiPaq and exposes the full feature surface. Reach for **DirectQuery** ONLY when there
is a genuine constraint (data that must be fresher than a refresh cycle can deliver, or a fact volume too
large to import), not as a reflex. Be explicit about the folklore trap: do NOT justify DirectQuery with
refresh-count criteria (this framing is a myth). Capture the ACTUAL freshness/volume constraint from the user, in their
words, so the mode choice has a reason attached to it.

```yaml gate
inputs:
  - name: freshnessRequirement
    question: "What is the real freshness or volume constraint that would justify DirectQuery over the default Import? How fresh must the data be, and how large is the fact source? (If none, the answer is Import.)"
    type: text
    required: required
```

## Step 2: Assign storage mode per table: Dual for same-source dimensions

Now set the mode on each table deliberately. Read the current shape with `get_model_graph` and set the mode
with `set_property`. The nuance that makes or breaks a composite model: **dimension tables that
will be queried with DirectQuery facts FROM THE SAME SOURCE go to DUAL (not plain Import) because Dual
enables the single efficient native-SQL join**; a plain Import dimension against a DirectQuery fact creates a
*limited* cross-source-group relationship (weaker filtering, extra round-trips). So: DirectQuery facts stay
DirectQuery, their same-source shared dimensions become Dual, and any Import-only fact keeps Import dimensions
as Import. Encode the Dual-vs-Import distinction exactly, and
tell the user why each table got the mode it did.

```yaml gate
ops: [get_model_graph, set_property]
```

## Step 3: Wire and review every cross-source-group relationship

Relate the facts to their dimensions with `create_relationship`, and confirm exactly one active path per
pair with `set_relationship_active`. Then walk the graph with `model_graph_summary` and REVIEW every
cross-source-group relationship it shows; those are the seams where a mis-set mode turns an efficient native
join into a limited relationship. If a Dual dimension is still showing a limited relationship to its
same-source DirectQuery fact, the mode is wrong upstream: go back to Step 2 and fix it with `set_property`
before proceeding. The goal is that every DQ-fact-to-shared-dim edge resolves through a Dual dimension.

```yaml gate
ops: [set_property, create_relationship, set_relationship_active, model_graph_summary]
inputs:
  - name: relationshipsReviewed
    question: "Have you reviewed every cross-source-group relationship in the graph and confirmed each same-source dimension on a DirectQuery fact is Dual (not Import), so the join is a single native query, not a limited relationship?"
    type: text
    required: answer-or-decline
```

## Step 4: Prove a cross-mode figure

A composite model is unproven until a number that SPANS the modes comes back right. Author a
measure with `create_measure` that totals across the DirectQuery fact and filters through a Dual dimension
(the exact path Step 3 just wired) and give `target` its ref. Ask the business for the matching known-good
figure at that context (from the source system, not derived by this model). The hard gate probes the measure
against that value; if it differs, the cross-source-group join is wrong (a mode is mis-set or a
relationship is limited when it should be native); fix it and re-probe. The gate holds on this step until the
number matches or you decline the check.

```yaml gate
strictness: hard
ops: [create_measure]
inputs:
  - name: verificationValue
    question: "A known-good figure that spans the DirectQuery fact and a Dual dimension (e.g. 'total 2024 sales for the West region = 4.2M'), supplied by the user from a source of truth, not derived by this model."
    type: verification
    required: answer-or-decline
  - name: target
    question: "The ref of the cross-mode measure you created to check the join (e.g. measure:Sales/Total Sales by Region)."
    type: objectRef
    required: required
verify:
  - kind: dax_probe
    when: inputs.verificationValue.answered
    probe: verificationValue
```

## Step 5: Save the composite model

Persist with `save_model` so the per-table modes, relationships, and the cross-mode control measure land in
the TMDL beside the model. Record for the next author which tables are DirectQuery, which dimensions are Dual
and why (same-source join), and what the control measure verifies against, so a later mode change gets
re-checked rather than silently degrading a join back to a limited relationship.

```yaml gate
ops: [save_model]
```
