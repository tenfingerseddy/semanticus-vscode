---
name: verified-measure
title: Test a complex measure
description: Define the hard cases for a measure, compare a candidate with independent expected values, and report checked and unresolved cases.
whenToUse: "Use for ratios, shares, time logic, distinct counts or other measures that can look right in a simple total. For a straightforward new measure, use Add a measure; for a speed or readability rewrite, use Fix a slow measure."
version: 7
strictness: hard
triggers: [create_measure, update_measure]
---

## Step 1: Fix the specification. The requirement text is the only source

Restate the metric and its grain, and classify additivity PER DIMENSION rather than globally (for example
"snapshot over Date; recomputed, not summed, across currency pairs; non-additive over customer"). The
per-dimension rule decides which subtotal can silently lie.

READ the model facts, do not ask for them. Use `get_grounding` and `get_model_graph` for the fact-table date
span against the calendar span, the fact grain, and the direction, cardinality and active flag of every
relationship this measure will traverse, plus whether the date table is marked and contiguous when time
logic is involved. Confirm that reading with the user once, in this step. Never ask a person to retype what
the model already holds.

Calendars may extend past the data. Keep the last fact date separate from the last calendar date. A
future-period guard blanks a period only when the WHOLE period is beyond the data edge, meaning FirstDate >
LastData. Endpoint touch is still a period with data and must never be blanked merely because LastDate
reaches or passes the edge.

Then build the CONTEXT LEDGER and keep it in these instructions, not in a question. Enumerate the degenerate
contexts: grand total; each bare single-column subtotal at the metric's grain; a single member; the first
and last period with data; a year boundary; a past-last-data period; an empty selection; a zero or empty
denominator. EVERY enumerated context appears EXACTLY ONCE, as either:

- PINNED: it carries the verbatim requirement sentence, or the user's quoted answer, that pins it, plus the
  pinned return value. Your own interpretation never pins a context. No quote, no pin.
- OPEN: the requirement is silent. An OPEN context is an observation to report. You may not add guards,
  coercions, re-anchorings or conventions to change what a correct construction naturally returns there, and
  no gate may fail on one.

A published pattern such as a future-date guard or a blank suppression is correct ONLY when a PINNED entry
asks for it. Carry the ledger forward into every later step and into the terminal record. Ask about an
ambiguous business meaning once, here, and not again at each later step. Know the honesty consequence up
front: open grains or countersigns make the certificate PARTIAL, and skipping a hard gate makes it
OVERRIDDEN. Nothing is silently absorbed into "verified".

```yaml gate
ops: [get_grounding, get_model_graph]
inputs:
  - name: requirement
    question: "The metric and grain, restated, with additivity PER DIMENSION, and the model facts you read confirmed by the user."
    type: text
    required: required
  - name: clarification
    question: "The ambiguous business meaning you had to settle and the user's quoted answer; decline when nothing was ambiguous."
    type: text
    required: answer-or-decline
```

## Step 2: Lock the expected values before any candidate exists

Test first. Declare `equivalenceGrid` and the requirement-silent `openGrains` NOW, before a candidate
exists. The grid is a comma-separated list of qualified display columns. Open grains use only the canonical
ids `grand_total`, `axis:<column>` and `cross`. The engine refuses a later submission that shrinks or
renames what an earlier accepted submission declared; additions are recorded as receipted superset
revisions. Once this step passes, both are fixed for the run.

For EVERY grain type of the grid, supply exactly one discriminating anchor unless that grain is listed in
`openGrains`: the grand total, each bare single-axis grain, and the full cross. Compute each expected value
OUTSIDE DAX. Do not ask DAX to produce the answer you are trying to prove. Instead `run_dax` a small GROUPED
row extract at the anchor context, then apply your OWN arithmetic with no CALCULATE, no time logic and no
measure references. Keep each extract small: a grouped subtotal at the metric's grain, never a scan that
materializes the whole fact table.

A grand-total anchor has an empty context. An axis anchor has one real member of that axis and no other
context keys. A cross anchor has one real member for every grid column and no other keys. A two-column grid
needs evidence shaped like:

```json
[
  {"context": {}, "expect": 1000},
  {"context": {"'Product'[Subcategory]": "Laptops"}, "expect": 218},
  {"context": {"'Date'[Year]": 2025}, "expect": 460},
  {"context": {"'Product'[Subcategory]": "Laptops", "'Date'[Year]": 2025}, "expect": 90}
]
```

Add `axis` when the value depends on the visible sibling set. Ranks, shares of visible, and anything using
ALLSELECTED need visual semantics. In a share-of-parent example a raw grouped extract returned Laptops = 218
against a visible parent total of 1000, and outside-DAX arithmetic gave 218 / 1000 = 0.218, so the shaped
anchor is:

```json
{"context":{"'Product'[Subcategory]":"Laptops"},"axis":["'Product'[Subcategory]"],"expect":0.218}
```

The axis entries are the visible grouping columns; other context entries are slicers. Use a shaped anchor
whenever siblings are load-bearing. If a flat anchor collapses that context, the engine's failure hint
compares flat and visual semantics and prints a copy-paste shaped skeleton.

When the grid contains a date axis, one anchor must sit on a bare-period grain whose window crosses the
last-data boundary. That pins the guard direction against a calendar that runs past the facts.

The candidate may later compose existing model measures, but a reused measure is not evidence. If you plan
to reuse one, add an anchor at a grain where its semantics matter and treat the composed value as UNVERIFIED
until that anchor pins it.

Strengthen an anchor when the world offers a stronger one. Both of these are optional and their absence
never blocks. When a source SQL endpoint is connected and query permission allows, recompute one or two
locked values from the SOURCE tables with `run_sql`: an anchor independent of the model's own load path, so
a mismatch there indicts the MODEL, meaning its relationships, refresh or partition filters, and not the
measure. Halt and surface that as a model finding. When the user knows a reference number such as a finance
pack total, capture it verbatim with its source. Record which tier backed each locked value inside
`expectedValues`: model raw rows, source SQL, or a user-stated reference.

Name the tempting naive wrong form for this metric and the context SHAPE where it diverges from correct.
It is almost never a fully-crossed leaf. Wrong-denominator, collapsed-context and over-broad-ALL forms match
at leaves and diverge where a dimension is absent, at a bare subtotal or at the grand total. The diverging
grain must already be anchored or declared open. A requirement-silent grain is honest PARTIAL evidence; it
may not be opened later merely because a candidate disagreed there.

These locked values are the adjudication bench for every later disagreement. If later raw-row arithmetic
proves a locked expectation itself wrong, do not silently rewrite it. Submit the corrected anchor set only
through a later `expectedValues` revision input with an EXPECTATION REVISION RECEIPT inside every changed
anchor object, and re-run everything that consumed the old value.

```yaml gate
inputs:
  - name: expectedValues
    question: "The locked anchors as one fenced JSON array of {context, optional axis, expect} objects. Cover each non-open grain type of equivalenceGrid: grand total, every bare axis, and cross. Include the diverging grain and one ordinary leaf. Outside the JSON fence, include each small GROUPED raw-row extract, the OUTSIDE-DAX arithmetic that produced its expected value, and which tier backed it: model raw rows, source SQL, or a user-stated reference."
    type: text
    required: required
  - name: equivalenceGrid
    question: "Machine field: the comma-separated qualified display columns that define the proof lattice, for example 'Product'[Category],'Product'[Subcategory]. The grid must list every display column the anchors exercise."
    type: text
    required: required
  - name: openGrains
    question: "Machine field: the requirement-silent grains, as a comma-separated list of canonical shape ids (grand_total | cross | axis:<column>) and nothing else. Decline only when every grain is anchored. This partition is declared before authoring and cannot later shrink."
    type: text
    required: answer-or-decline
  - name: naiveForm
    question: "The tempting naive wrong form; the diverging grain; and whether that grain is anchored from a PINNED ledger entry or declared requirement-silent in openGrains."
    type: text
    required: required
verify:
  - kind: anchor_coverage
    anchors: expectedValues
```

## Step 3: Author ONE canonical candidate

Draft a SINGLE production candidate: the clearest correct expression for the Step-1 specification. It may
compose model measures, but reuse is not proof; treat every reused measure's semantics as UNVERIFIED until
a Step-2 anchor pins the composed value at a grain where those semantics matter.

Use VAR and RETURN structure, prefer filtering columns over tables, and avoid needless context transitions.
Prefer the canonical idiom (DATESYTD, DATEADD, PARALLELPERIOD, LASTNONBLANKVALUE, KEEPFILTERS,
REMOVEFILTERS with VALUES, TREATAS) because its semantics are documented and proven. A hand-rolled date
reconstruction of a standard idiom is a red flag in the CANDIDATE; that style belongs to the witness, where
independence is the point.

Check the candidate returns the locked expected value at each Step-2 context before submitting. On a
mismatch, first re-verify the expectation by raw arithmetic, then either submit a receipted Expectation
Revision or revise the candidate. Create the measure, or update it in place when the workflow was triggered
by an update, and set `target` to the created or updated measure.

```yaml gate
ops: [create_measure, update_measure]
inputs:
  - name: candidate
    question: "The single candidate expression, verbatim, and its value at each locked Step-2 context (must match, or carry an Expectation Revision)."
    type: text
    required: required
  - name: target
    question: "The ref of the created or updated measure, for example measure:Sales/Revenue Share."
    type: objectRef
    required: required
  - name: expectedValues
    question: "OPTIONAL EXPECTATION REVISION WITH REQUIRED RECEIPT: leave unanswered to inherit the Step-2 anchor set. Answer only when grouped raw-row arithmetic proves an anchor wrong. Submit the full corrected fenced JSON array without changing its contexts. Every changed anchor object must contain originalExpect equal to the locked value, correctedExpect equal to its new expect, and a row-returning extractQuery that produces the grouped or raw rows used by the outside-DAX arithmetic. Failed, empty, missing, or scalar-constant extracts are refused. Never re-pin an anchor merely to match the candidate."
    type: text
    required: optional
verify:
  - kind: expected_values
    anchors: expectedValues
```

## Step 4: Reconcile against an independent witness, then finalize honestly

Build the raw-row witness, run the battery, let the hard gate prove the grid, and finish the measure for
production. This is one step so the person answers once, not four times; the discipline below is yours to
hold, not theirs to retype.

THE WITNESS. It implements ONLY the Step-1 ledger, with PINNED contexts as pinned and OPEN contexts as
whatever the simple computation naturally yields, and it avoids every idiom the candidate uses so it cannot
share the candidate's blind spot. It contains ZERO bare measure references: the engine token-scans every
`witnessDax` submission because the input declares `daxPurity: no-bare-measures`, and a reference such as
`[Sales PY]` is refused before the step is accepted. Rebuild every dependency from qualified base columns.
Independence comes from avoiding the candidate's idioms, never from being slow. Build it SARGable: CALCULATE
with plain equality or range filters on raw columns, date scopes as column predicates with a lower and an
upper bound, and grouped SUMMARIZECOLUMNS extracts where a subtotal is what you need. Never wrap a
bare FILTER over ALL of a large fact table in anything the gate will evaluate; a modest lookup or
dimension table may be scanned freely. Time one witness query at a representative context with `run_dax` or `probe_measure`
before you trust it, and treat anything over roughly 2 seconds as a witness defect to rebuild, not a fact of
life.

THE BATTERY. Probe candidate AND witness across every ledger context, plus two named layouts where the
second REMOVES a grouping column so a leaf becomes a bare subtotal; a reshuffle that keeps the same columns
proves nothing. On agreement at every PINNED context, record what the OPEN contexts returned and proceed. On
any disagreement, adjudicate THAT cell with arithmetic only, against a GROUPED row extract computed exactly
as in Step 2. Raw rows convict a side only on arithmetic. When the disagreement is semantic, such as a
leap-day mapping, calendar-against-fact anchoring, blank against zero, or which aggregation a subtotal
should use, and no PINNED entry decides it, the verdict is INCONCLUSIVE: record it, change NEITHER side,
ask the user if one is present, and know it caps the certificate at PARTIAL. A correct candidate is never
rewritten to match a witness; a witness is revised only when raw arithmetic or a timing defect convicts it,
with before and after on the record. After ANY change to either side, re-run the ENTIRE battery. Partial
re-probes are how regressions ship.

THE HARD GATE. The engine proves candidate-against-witness equality over the locked `equivalenceGrid`,
evaluating the full cross, every bare single-axis subtotal and the grand total, under the `openGrains`
partition that was locked before authoring. Every anchored grain must return positive comparison evidence
and match. A skipped, offline or zero-coverage verify is not a pass; proceeding without proof needs
`skip_workflow_step`, and that makes this run's certificate OVERRIDDEN. A mismatch on a requirement-silent
open grain blocks as `unavailable` with the exact cell coordinate and both values. Exit A is to fix the
candidate or witness after adjudication, re-run the full battery and re-prove the grid clean. Exit B is to
answer `countersign` with the exact disputed cell set copied from the blocking message plus one verbatim
stated interpretation sentence; a missing or extra cell is refused, and an accepted countersign prints as
UNVERIFIED with both values. The `certificate` answer is a CLAIM, not the verdict: the engine computes its
own level from coverage, countersigns, disputes, anchor receipts and hard-step skips, and the weaker of the
two is final, so a claim may downgrade a run but never inflate it.

PERFORMANCE AND FINALIZE. Grade the candidate against the MODEL FLOOR rather than a bare time threshold:
benchmark the candidate and the base aggregation the metric sits on at the SAME grid, so "slow" means slow
relative to the cheapest correct answer there. Benchmark in the measure-faithful shape only, as
`DEFINE MEASURE 'Sales'[__cand] = ( <expr> )` timed through
`EVALUATE SUMMARIZECOLUMNS ( <natural axes>, "v", [__cand] )`, serially, never as an inline extension column
and never concurrently, and treat any zero-storage-engine cold reading as INVALID cache noise. A rewrite is
applied with `update_measure` only after it matches the current locked expected values, and the gate above
re-proves it against the witness under the same open-shape partition. Decline `perfPass` when no comparable
live environment exists; unmeasured is unverified, never a pass, and only a measured comparable result
supports the word faster. Then finalize regardless of the performance path: the real production name, a
format string fit for the metric, a description stating what the measure returns and the conventions PINNED
in Step 1, and a display folder if the model uses them. The terminal record prints the grid, the
anchored-against-open coverage map, the open grains, countersigned cells with both values, anchor and
witness revision counts, and any skipped step with its reason.

```yaml gate
strictness: hard
ops: [probe_measure, run_dax, benchmark_dax, update_measure, rename_object, set_measure_format, set_description, set_display_folder, save_model]
inputs:
  - name: witnessDax
    question: "The raw-row witness, verbatim: ZERO bare measure references; every dependency rebuilt from qualified base columns; SARGable plain column filters, date ranges as predicates, grouped extracts; no bare FILTER over ALL of the fact table; no candidate idioms; it implements only the ledger. The engine refuses bare refs and this input may not be declined."
    type: text
    required: required
    daxPurity: no-bare-measures
  - name: battery
    question: "Candidate AND witness value, blank or error at every ledger context and both named layouts; each PINNED context's match against its pin; and the OPEN contexts' returned values, recorded and not acted on."
    type: text
    required: required
  - name: adjudications
    question: "For each disagreement: the GROUPED row extract, the arithmetic verdict naming which side was convicted or INCONCLUSIVE, any witness revision with before and after expressions, and confirmation the FULL battery was re-run after every change. Decline when nothing disagreed."
    type: text
    required: answer-or-decline
  - name: perfPass
    question: "The candidate against the MODEL FLOOR at its natural grid, with measure-faithful cold and warm timings and any zero-storage-engine reading discarded, plus the winning rewrite and its match against the locked anchors when one was applied. Decline when no comparable live environment was available; unmeasured is unverified."
    type: text
    required: answer-or-decline
  - name: finalized
    question: "The production name, format string, description including the PINNED conventions, and display folder applied to the measure, with the checked grains and every unresolved OPEN observation."
    type: text
    required: required
  - name: countersign
    question: "Machine exit: only after an open-mismatch block, copy the exact disputed cells array and provide one verbatim stated interpretation sentence. Leave unanswered when there is no current dispute. A decline is not a countersign."
    type: text
    required: optional
  - name: certificate
    question: "Your certificate CLAIM: FULL, PARTIAL, or OVERRIDDEN, with any honest downgrade named. The engine computes the evidence-backed ceiling and the weaker level wins."
    type: text
    required: required
verify:
  - kind: dax_equivalence
    probe: witnessDax
    openShapesFrom: openGrains
    openMismatch: countersign
  - kind: expected_values
    anchors: expectedValues
```
