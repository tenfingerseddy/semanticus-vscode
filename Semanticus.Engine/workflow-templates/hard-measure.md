---
kind: template
name: hard-measure
title: Author "{{measure_goal}}" and prove it against an independent oracle
description: Set up the same ProBench v2 oracle-anchored workflow as the built-in Author a hard measure playbook. It pins the metric and edge policy, authors one candidate, builds an independent raw-row oracle, proves the comparison grid exposes a tempting wrong form, hard-gates candidate equality, then optionally optimizes and re-proves before finalizing.
whenToUse: "One complex or high-stakes measure whose totals, subtotals, boundary periods, denominator behavior, or relationship behavior must be proven. For a straightforward measure checked against one trusted number, use new-measure."
version: 4
strictness: hard
slots:
  - name: measure_goal
    question: "What should the measure compute, in business terms?"
    type: text
    required: required
    example: "year-on-year revenue growth percentage"
  - name: measure_pattern
    question: "If you know the likely DAX pattern, name it. Leave blank to let the AI Assistant choose."
    type: text
    required: optional
    default: "let the AI Assistant choose the clearest correct pattern"
    example: "year-over-year over the marked date table"
---

## Step 1: Pin the intent and edge policy

The requested metric is {{measure_goal}}. The likely pattern is {{measure_pattern}}. Restate the
metric, its exact grain, and the precise behavior required. Review the model's tables, columns,
relationships, and building-block measures. Enumerate the ordinary and degenerate contexts this
measure must survive: grand total, a single member, several members, a bare single-column subtotal,
first and last period with data, a year boundary, a period after the last data, an empty selection,
and a zero or empty denominator. Name how a tempting naive form fails in those contexts, such as
wrong grain, over-counting, blank versus zero, an over-broad filter, or a missing context transition.

Pin the required return value at every degenerate context as a policy, not as whatever a DAX idiom
happens to emit. This policy becomes the specification hard-coded into the independent oracle in
Step 3. Resolve genuine ambiguity before authoring and record the reading adopted.

```yaml gate
inputs:
  - name: requirement
    question: "Restate the metric, its grain, and the exact edge behavior required."
    type: text
    required: required
  - name: contexts
    question: "The ordinary, degenerate, and error contexts this measure must survive, and how a naive form goes wrong in them."
    type: text
    required: required
  - name: edgePolicy
    question: "The pinned return value required at each degenerate context: grand total, bare subtotal, boundary periods, past-last-data period, empty selection, and zero or empty denominator."
    type: text
    required: required
  - name: clarification
    question: "Any genuine ambiguity you resolved and the reading you adopted, or decline if none."
    type: text
    required: answer-or-decline
```

## Step 2: Author one candidate

Author one production candidate for {{measure_goal}} using {{measure_pattern}}: the clearest
expression you can write for the pinned intent and edge policy. Review it for best practice,
including useful variables, column filters instead of whole-table filters, minimal context
transitions, and correctly shaped table expressions. Do not author multiple candidates on the
correctness path. A second candidate built from the same mental model can share the first
candidate's blind spot and does not provide independent proof. Create the measure, set `target`
to its object reference, and record the expression verbatim. Alternative performance forms belong
in the optional fifth step, after correctness.

```yaml gate
ops: [create_measure]
inputs:
  - name: candidate
    question: "The single candidate expression you authored, verbatim."
    type: text
    required: required
  - name: target
    question: "The object reference of the measure you created, for example measure:Sales/Revenue YoY %."
    type: objectRef
    required: required
```

## Step 3: Build the independent raw-row oracle and prove the grid can bite

Build an independent raw-row oracle: an intentionally simple scalar computation over raw rows that returns exactly the Step 1 policy
in each context. Use explicit filtering over all rows plus set and date arithmetic keyed from the
current context. Hard-code the edge convention. Avoid the time-intelligence idioms, denominator
scope tricks, and relationship shortcuts used by the candidate, so the oracle cannot share the
same blind spot. Agreement with this independently shaped computation is evidence of correctness;
agreement between two similar candidates is only self-consistency.

Probe candidate and oracle across the full battery: grand total; single and multiple members;
bare single-column subtotal; first and last period with data; year boundary; after-last-data period;
non-contiguous or partial selection; empty selection; and zero or empty denominator. For every
applicable context, record value, blank, or error for both. Debug every mismatch, update the
candidate, and repeat until the candidate equals the oracle throughout the battery.

Then prove the equality grid will actually catch the guarded error. Write the tempting naive form
for this metric and probe it against the oracle. Find the context shape where they diverge. This is
often the bare subtotal or grand total, not a fully crossed leaf where every relevant dimension is
already pinned. Record that discriminating shape; Step 4 must use it. A grid on which a known wrong
form equals the oracle is not allowed to certify the candidate.

```yaml gate
ops: [probe_measure]
inputs:
  - name: oracleDax
    question: "The independent raw-row oracle expression, verbatim, with the pinned edge policy hard-coded and without the candidate's time-intelligence or scope shortcuts."
    type: text
    required: required
  - name: probeObservations
    question: "Candidate and oracle value, blank, or error at every applicable battery context; every mismatch and its context reason; and confirmation the survivor matches the oracle throughout."
    type: text
    required: required
  - name: divergenceProof
    question: "The tempting naive wrong form, the context shape where a probe proved it diverges from the oracle, and the group-by columns Step 4 must use to include that shape."
    type: text
    required: required
  - name: trapCheck
    question: "The applicable trap family, the context probed for it, and the evidence the candidate is correct, or decline only if no listed trap applies."
    type: text
    required: answer-or-decline
```

## Step 4: Hard gate, candidate equals oracle

The engine evaluates oracle and candidate across the supplied group-by grid and compares every
row. A fully crossed leaf grid does not emit grand totals or bare single-column subtotals. It can
therefore pin away a wrong-denominator, collapsed-context, or over-broad-filter bug and make a
known-wrong measure appear equal to the oracle.

Set `equivalenceGrid` to the discriminating context shape proven in `divergenceProof`: the bare
grouping column or columns at the metric's defined grain, not parent-crossed leaves. The Step 3
probe battery covers the other context shapes that one grid cannot emit. Leave `target` on the
candidate. If any compared context differs, fix the candidate and re-submit this step. Never
replace independent proof with visual inspection or candidate-to-candidate equivalence.

```yaml gate
strictness: hard
inputs:
  - name: equivalenceGrid
    question: "Comma-separated group-by columns for the proven diverging shape from Step 3, using the bare grouping at the metric's grain rather than parent-crossed leaves."
    type: text
    required: required
verify:
  - kind: dax_equivalence
    when: inputs.oracleDax.answered
    probe: oracleDax
```

## Step 5: Finalize, with an optional performance pass

Correctness is locked by Step 4. If speed matters, benchmark the deployed measure shape over its
natural grouping and representative full matrix. Benchmark it as a defined measure, not as an
inline extension expression with cheaper semantics. Draft alternatives only here, keep a faster
form only after updating `target`, and let the gate below re-prove it against the independent
oracle. If this is correctness-only work, decline the performance rewrite.

Whether or not performance work is needed, set the final format string and a description that
states the business meaning, grain, and pinned edge policy. Confirm the final DAX and metadata.

```yaml gate
strictness: hard
ops: [benchmark_dax_coldwarm, update_measure, set_measure_format, set_description]
inputs:
  - name: perfRewrite
    question: "The faster expression you applied and its deployed-shape cold and warm timing versus the original, or decline if no performance pass was needed."
    type: text
    required: answer-or-decline
  - name: finalDax
    question: "The final measure expression, verbatim."
    type: text
    required: required
  - name: finalMetadata
    question: "The format string and description you set, including the metric's grain and pinned edge policy."
    type: text
    required: required
verify:
  - kind: dax_equivalence
    when: inputs.perfRewrite.answered
    probe: oracleDax
```
