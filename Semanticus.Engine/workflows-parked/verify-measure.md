---
name: verify-measure
title: Verify a measure against a known-good number
description: Prove an existing measure returns a number the business already trusts. Take the measure and a known-good figure, confirm it returns that figure at the right filter context, sweep the other contexts it must respect, then record the verification on the measure so the proof travels with it.
whenToUse: "You already HAVE a measure and want to prove it returns a known-good number and survives its contexts, with no authoring. To author a new verified measure use new-measure; to rewrite an existing one use refactor-measure or optimize-dax."
version: 1
strictness: hard
triggers: [probe_measure, verify_dax_equivalence, pivot_measure]
---

## Step 1: Name the measure and its known-good number

Verification is only as good as the number you check against. Point at the measure to
verify, and get a KNOWN-GOOD figure plus the exact filter context it holds at, and it must come FROM THE
BUSINESS (a source-system total, a signed-off report figure), never a number this model derived, which would
just check the model against itself. These are the same `expectedNumbers` the dimensional-design session
captured; reuse them here rather than inventing new ones. Record both the value and the context so the probe
below has something unambiguous to compare against.

```yaml gate
inputs:
  - name: measureRef
    question: "Which measure are you verifying (e.g. measure:Sales/Total Sales)?"
    type: objectRef
    required: required
  - name: verificationValue
    question: "A known-good figure FROM THE BUSINESS plus the exact filter context it holds at (e.g. 'FY2024 revenue for the West region = 4.2M'): a source-of-truth number, not one derived by this model."
    type: verification
    required: required
```

## Step 2: Probe the measure at that context

This is the hard gate. Evaluate the measure at the verification context and compare: `probe_measure` for the
scalar check, `pivot_measure` or `run_dax` to see it broken out by the group-by the figure applies to. The
gate probes the measure named in Step 1 against the known-good value; if it differs, the measure is wrong at
that context (a filter leaks, the grain is off, a relationship doesn't propagate); fix the expression and
re-probe. The gate holds on this step until the number matches or you decline the check.

```yaml gate
strictness: hard
ops: [probe_measure, pivot_measure, run_dax]
inputs:
  - name: contextConfirmed
    question: "Does the probe use the SAME filter context the known-good figure was quoted at (same dates, entity, and slicers)?"
    type: text
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.verificationValue.answered
    probe: verificationValue
```

## Step 3: Sweep the contexts it must respect

One matching number is a spot check, not a proof of correctness. Evaluate the measure under the
slicers and RLS roles it must respect with `pivot_measure` and `run_dax`, and check the totals row
deliberately: an iterator and an aggregate can agree on a grand total yet diverge per row, or vice versa.
A grand total that is NOT the sum of the visible rows is often a DESIGN QUESTION (distinct-count, ratio-of-
sums, semi-additive), not automatically a bug; decide which it is here rather than assuming. Note any context
where the value surprises you.

```yaml gate
ops: [pivot_measure, run_dax]
inputs:
  - name: sweepReviewed
    question: "Have you evaluated the measure under its intended slicers/RLS roles and checked totals-row behavior (iterator vs aggregate), and is any total-≠-sum-of-rows explained by design, not a bug?"
    type: text
    required: answer-or-decline
```

## Step 4: Record the verification on the measure

Close the loop by writing the proof onto the measure itself with `set_description`: append
`Verified: <figure> @ <context> on <date>` (e.g. `Verified: 4.2M @ FY2024 West on 2026-07-04`). This is the
same convention family as dimensional-design's `Grain: ` prefix; it makes the verification readable to the
next human, to BPA, and to the LLM grounding, and it tells a future author exactly what number to re-check
after a refresh or a rewrite. A verification nobody can find is a verification that decays; put it where the
measure is.

```yaml gate
ops: [set_description]
```
