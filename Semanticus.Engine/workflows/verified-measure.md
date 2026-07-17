---
name: verified-measure
title: Author a hard DAX measure, reconciled against the requirement at every grain
description: "Requirement-anchored verified authoring: pin conventions only from the requirement's own words, lock expected values from raw rows before authoring, reconcile one canonical candidate against a locked raw-row witness at the shapes where wrong-but-runs bugs live, adjudicate disagreements cell-by-cell, and never let a speed pick or an invented convention overwrite a correct answer."
whenToUse: "A measure whose correctness depends on filter context (ratios and shares, time intelligence, semi-additive logic, distinct counts), where a wrong form runs clean and looks right at the leaves."
version: 6
strictness: hard
triggers: [create_measure, update_measure]
---

## Step 1: Fix the specification. The requirement text is the only source

Restate the metric and its grain. Classify additivity PER DIMENSION, not globally (e.g. "snapshot
over Date; recomputed, not summed, across currency pairs; non-additive over customer"): the
per-dimension rule decides which subtotal can silently lie.

Record the model facts that constrain authoring (facts, never conventions): fact-table date span
vs calendar span; fact grain; direction, cardinality and active-flag of every relationship the
measure will traverse; marked-and-contiguous date table when time-intel is involved.

Then build the CONTEXT LEDGER. Enumerate the degenerate contexts (grand total; each bare
single-column subtotal at the metric's grain; single member; first and last period with data; a
year boundary; past-last-data period; empty selection; zero or empty denominator). EVERY
enumerated context appears EXACTLY ONCE in the ledger, as either:

- PINNED: carries the verbatim requirement sentence (or the recorded `clarify` answer, quoted)
  that pins it, plus the pinned return value. Your own interpretation NEVER pins a context: no
  quote or recorded user answer, no pin.
- OPEN: the requirement is silent. OPEN contexts are observations to report in the evidence
  trail (and `clarify` questions when a user is present). You may not add guards, coercions,
  re-anchorings or conventions to change what a correct construction naturally returns at an OPEN
  context, and no gate may fail on one.

A published pattern (a future-date guard, a blank-suppression) is correct ONLY when a PINNED entry
asks for it. The ledger also decides what Step 6 may enforce, by one rule: a gate shape is OPEN if
ANY ledger context within it is OPEN (the engine excludes whole shapes; at an OPEN cell two
legitimate formulations can naturally differ, so a mixed shape must not stay enforced). Know the
honesty consequence up front: the certificate is FULL only when every evaluated shape is pinned,
zero ledger contexts are OPEN, and zero verdicts are INCONCLUSIVE; any of those makes it PARTIAL,
naming the observations (Step 6). Nothing is silently absorbed into "verified".

```yaml gate
inputs:
  - name: requirement
    question: "The metric and grain, restated; additivity PER DIMENSION."
    type: text
    required: required
  - name: modelFacts
    question: "Fact date span vs calendar span; fact grain; relationships traversed (direction, active); date table marked and contiguous (if time-intel)."
    type: text
    required: required
  - name: contextLedger
    question: "EVERY degenerate context, exactly once, as PINNED (verbatim requirement quote or quoted clarify answer, plus the pinned value) or OPEN (requirement silent: recorded, never acted on)."
    type: text
    required: required
  - name: clarification
    question: "clarify questions asked and answers recorded, if a user was present (decline if none)."
    type: text
    required: answer-or-decline
```

## Step 2: Lock the expected values before any candidate exists

Test-first: choose two or three DISCRIMINATING contexts and compute the expected value at each
OUTSIDE DAX. Do NOT ask DAX to produce the answer you are trying to prove. Instead `run_dax` a
small GROUPED row extract at the anchor context (a plain SUMMARIZECOLUMNS or GROUPBY over
row-level columns, filtered to that context, returning a handful of rows), then compute the
expected value from those rows with your OWN arithmetic (mental, a calculator, or Python), using
no CALCULATE, no time-intel, and no measure references. Record each context, the exact extract
query, and the expected value it yields. Keep the extract small: a grouped subtotal at the
metric's grain, never a scan that materializes the whole fact table.

Strengthen the anchor when the world offers one; both are OPTIONAL and their absence never
blocks. (a) If a source SQL endpoint is connected (and query permission allows), recompute one or
two of the locked values from the SOURCE tables with `run_sql`: an anchor independent of the
model's own load path, so a mismatch here indicts the MODEL (relationship, refresh, partition
filter), not the measure. Halt certification and surface that as a model finding before
proceeding. (b) If the user knows a reference number (a finance pack total, an existing trusted
report), capture it verbatim with its source. The certificate records which tier backed each
locked value (model raw rows, source SQL, or a user-stated reference) and is honest either way:
reconciling against the source of truth when one exists, and reconciling three independent ways
when none does.

Name the tempting naive wrong form for this metric and the context SHAPE where it diverges from
correct (almost never a fully-crossed leaf: wrong-denominator, collapsed-context and
over-broad-ALL forms match at every leaf and diverge only where a dimension is absent, at the
bare subtotal or the grand total). THE DIVERGING SHAPE MUST BE A PINNED LEDGER ENTRY and one of
your locked contexts: if the requirement (or a clarify answer) does not pin behaviour at the one
shape that separates right from wrong, this workflow cannot certify the measure. Say so, raise it
with the user if present, or complete with the PARTIAL certificate Step 6 defines.

These locked values are the adjudication bench for every later disagreement. If later raw-row
arithmetic proves a locked expectation itself wrong, do not silently rewrite it. Submit the
corrected anchor set only through a later declared `expectedValues` revision input, with an
EXPECTATION REVISION RECEIPT inside every changed anchor object: `originalExpect` equal to the
currently locked value, `correctedExpect` equal to its new `expect`, and `extractQuery` containing
the small row-returning grouped or raw-row DAX extract that convicted the old value. Keep the
context set unchanged. Outside the JSON fence, show the arithmetic applied to that extract. The
engine executes every changed anchor's `extractQuery`, requires at least one live row, and records
the result hash with the revision delta. A failed, empty, scalar-constant, or missing extract is
refused. Use Step 3 when the candidate check finds the defect, or Step 7 when it is found later.
Then re-run everything that consumed the old value: the candidate check and the entire Step-5
battery. A revision without that receipt is a free re-pin and is forbidden.

```yaml gate
inputs:
  - name: expectedValues
    question: "Two or three locked anchors as one fenced JSON array of {context, expect} objects (including the diverging shape, which must be PINNED, and one ordinary leaf). Outside the JSON fence, include each small GROUPED raw-row extract and the OUTSIDE-DAX arithmetic that produced its expected value (no CALCULATE or measure refs)."
    type: text
    required: required
  - name: externalAnchors
    question: "OPTIONAL stronger anchors: source-SQL recomputation of a locked value (query + result; a mismatch indicts the model, halt and surface it) and/or a user-stated reference number with its source. Decline when neither is available; absence never blocks."
    type: text
    required: answer-or-decline
  - name: naiveForm
    question: "The tempting naive wrong form; the diverging shape; the PINNED ledger entry that pins behaviour there."
    type: text
    required: required
```

## Step 3: Author ONE canonical candidate

Draft a SINGLE production candidate: the clearest correct expression for the Step-1
specification, composed from verified building-block measures where they exist; VAR/RETURN
structure; prefer filtering columns over tables (a table filter only where the pattern genuinely
requires one); no needless context transitions. Prefer the canonical idiom (DATESYTD, DATEADD,
PARALLELPERIOD, LASTNONBLANKVALUE, KEEPFILTERS, REMOVEFILTERS+VALUES, TREATAS); its semantics
are documented and proven. A hand-rolled date-arithmetic or FILTER-over-ALL reconstruction of a
standard idiom is a red flag in the CANDIDATE (that style belongs to the witness, where
independence is the point). Check the candidate returns the locked expected value at each Step-2
context before submitting; on a mismatch, first re-verify the expectation by raw arithmetic (an
Expectation Revision with the required receipt if it was wrong), otherwise revise the candidate.
Create the measure, or update it in place when the workflow was triggered by an update. Set
`target` to the created or updated measure. Leave this step's `expectedValues` unanswered to
inherit the Step-2 anchors unchanged. Answer it only with a receipted revision.

```yaml gate
ops: [create_measure, update_measure]
inputs:
  - name: candidate
    question: "The single candidate expression, verbatim, and its value at each locked Step-2 context (must match, or carry an Expectation Revision)."
    type: text
    required: required
  - name: target
    question: "The ref of the created or updated measure (e.g. measure:Sales/Revenue Share)."
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

## Step 4: Lock the witness. Independent AND efficient

Build and submit the raw-row WITNESS. Nothing else happens in this step, so the witness is on
the record BEFORE any comparison runs. It implements ONLY the Step-1 ledger (PINNED contexts as
pinned, OPEN contexts as whatever the simple computation naturally yields), avoiding every idiom
the candidate uses so it cannot share the candidate's blind spot.

Independence comes from AVOIDING THE CANDIDATE'S IDIOMS, never from being slow. A witness that
scans the whole fact table is not more independent, it is only slower, and on a large fact it
will not return before the gate's ceiling, so it proves nothing. Build the witness SARGable:
CALCULATE with plain equality or range filters on the raw columns; date-scoped ranges expressed
as column predicates (a lower bound and an upper bound on the date column), not a row-by-row date
reconstruction; grouped SUMMARIZECOLUMNS extracts where a subtotal is what you need. Do NOT wrap a
bare FILTER over ALL of a large fact table in any expression the gate will evaluate. A modest
lookup or dimension table may be scanned freely; the large fact table may not.

Prove the witness is fast before locking it: `run_dax` or `probe_measure` ONE witness query at a
representative context and record its elapsed time. Treat anything over roughly 2 seconds as a
WITNESS DEFECT, not a fact of life: rebuild it SARGable and re-time it before you submit this
step. From this submission the witness is LOCKED: any later revision must appear in Step 5's
`adjudications` with before/after expressions and the raw-row arithmetic (or the timing defect)
that convicted it.

```yaml gate
inputs:
  - name: witnessDax
    question: "The raw-row witness, verbatim: SARGable (plain column filters, date ranges as predicates, grouped extracts), no bare FILTER over ALL of the fact table, no candidate idioms, implementing ONLY the ledger (PINNED pinned, OPEN natural)."
    type: text
    required: required
  - name: witnessTiming
    question: "The one witness query you timed (context + elapsed). It must be under roughly 2 seconds; if it was slower, rebuild the witness SARGable and re-time before submitting."
    type: text
    required: required
```

## Step 5: The battery. Adjudicate, don't auto-fix

Probe candidate AND witness across the battery: every ledger context, plus two named layouts
where the second REMOVES a grouping column so a leaf becomes a bare subtotal (a reshuffle that
keeps the same columns proves nothing). Then:

- Agreement at every PINNED context, and each PINNED value matches its pin: record what the OPEN
  contexts returned and proceed.
- DISAGREEMENT anywhere: adjudicate THAT cell, arithmetic only, against a raw-row extract
  computed exactly as in Step 2. Raw rows convict a side only on ARITHMETIC. If the disagreement
  is SEMANTIC (a leap-day mapping, calendar-vs-fact anchoring, blank-vs-zero, which aggregation
  a subtotal should use) and no PINNED entry decides it, the verdict is INCONCLUSIVE: record it,
  change NEITHER side, raise `clarify` if a user is present, and know it caps the certificate at
  PARTIAL. A correct candidate must never be rewritten to match a witness; a witness is revised
  only when raw arithmetic convicts it, with before/after on the record.
- A witness may also be convicted by SPEED: if a battery probe shows the witness itself is too
  slow to evaluate (the Step-4 efficiency bar), rebuild it SARGable and re-time it. Independence
  still comes from avoiding the candidate's idioms, never from a bare FILTER over ALL of the fact
  table.
- After ANY change to either side, candidate or witness, re-run the ENTIRE battery. Partial
  re-probes are how regressions ship.

This step re-submits the CURRENT witness under the same `witnessDax` name so every later equality
proof runs against the live witness: restate the Step-4 expression when unchanged, or submit the
revised expression when an adjudication convicted it, and the engine records a revision receipt
whenever the expression actually changed. It may not be declined.

```yaml gate
ops: [update_measure]
inputs:
  - name: battery
    question: "Candidate AND witness value/blank/error at every ledger context and both named layouts; each PINNED context's match against its pin; the OPEN contexts' returned values (recorded, not acted on)."
    type: text
    required: required
  - name: witnessDax
    question: "The CURRENT witness expression, verbatim: restate the Step-4 witness when unchanged, or the revised SARGable expression when an adjudication convicted it (the engine records a revision receipt on any change)."
    type: text
    required: required
  - name: adjudications
    question: "For each disagreement: the raw-row extract, the arithmetic verdict (which side convicted, or INCONCLUSIVE: changed neither, certificate capped at PARTIAL), any witness revision with before/after expressions, and confirmation the FULL battery was re-run after every change (decline if no disagreements)."
    type: text
    required: answer-or-decline
```

## Step 6: HARD gate. Equality where the bugs live, on PINNED shapes only

The engine proves candidate-vs-witness equality over the grid axes you name, evaluating the full
cross, each bare single-axis subtotal, and the grand total. Set `equivalenceGrid` to the metric's
natural grain axes so the Step-2 diverging shape (PINNED, by Step 2's rule) is among the
evaluated shapes: the wrong-denominator class agrees on every fully-crossed leaf and diverges
exactly at the bare subtotal, so leaving the diverging axis out un-arms the gate.

Partition the evaluated shapes with `openShapes`, a machine-read field carrying canonical shape
ids and nothing else, by one rule: a gate shape is OPEN if ANY ledger context within it is OPEN
(the engine excludes whole shapes; at an OPEN cell two legitimate formulations can naturally
differ, so a mixed shape must not stay enforced). An OPEN shape is excluded from enforcement: a
mismatch there is REPORTED into the evidence trail, never a gate failure. The gate passes ONLY on
a positive equivalence result with rows actually compared on the PINNED shapes; a skipped,
offline, or zero-coverage verify is NOT a pass, and proceeding without the proof is an explicit
`skip_workflow_step` with a reason, on the record. THE CERTIFICATE IS HONEST: it is FULL only
when every evaluated shape is pinned, zero ledger contexts are OPEN, and zero verdicts are
INCONCLUSIVE; any of those makes it PARTIAL, naming the observations. On a reported mismatch:
adjudicate per Step 5 (never auto-rewrite), fix only a convicted side, re-run the battery,
re-submit.

Witness-repair window: if THIS gate proves the witness itself broken or too slow (a timeout, or
an equality failure that adjudication convicts the witness on, not the candidate), submit a
revised `witnessDax` with this step. Answer it ONLY to repair a witness the gate itself proved
broken or too slow, never to move a correct candidate: the revision goes on the record and the
engine fires a witness-revision receipt automatically. Last-answered-wins, so the repaired
witness is the one this gate re-proves against. A correct candidate is still never rewritten to
match a witness. When the witness stands, restate the Step-5 expression verbatim. Never decline
this input: a decline is a later run answer and would hide the witness that the gate must prove.

```yaml gate
strictness: hard
ops: [update_measure]
inputs:
  - name: equivalenceGrid
    question: "The comma-separated natural-grain axes for the equality proof, chosen so the Step-2 diverging shape is among the evaluated shapes (e.g. 'Product'[Category],'Product'[Subcategory])."
    type: text
    required: required
  - name: openShapes
    question: "Machine field: the evaluated shapes that are OPEN, as a comma-separated list of canonical shape ids (grand_total | cross | axis:<column>) and NOTHING else. A shape is OPEN if ANY ledger context within it is OPEN. Decline only when every evaluated shape is pinned."
    type: text
    required: answer-or-decline
  - name: witnessDax
    question: "The CURRENT witness expression. Restate the Step-5 witness verbatim when unchanged. Revise it ONLY to fix a witness THIS gate proved broken or too slow, never to move a correct candidate. A revision must be SARGable with no bare FILTER over ALL of the fact table; the engine records a revision receipt. This input may not be declined."
    type: text
    required: required
  - name: certificate
    question: "The certificate level: FULL (every evaluated shape pinned and proven, no OPEN ledger contexts, no INCONCLUSIVE) or PARTIAL (name every unproven cell/observation)."
    type: text
    required: required
verify:
  - kind: dax_equivalence
    probe: witnessDax
    openShapesFrom: openShapes
```

## Step 7: Performance against the model floor, then finalize for production

Report the SLOWEST battery/equivalence query observed (context + elapsed). Then grade the
deployed candidate against the MODEL FLOOR, not against a bare time threshold: benchmark the
candidate AND the base aggregation the metric sits on (the plain building-block measure or SUM
the metric is composed from, e.g. [Sales Amount]) AT THE SAME grid, so "slow" means slow RELATIVE
to the cheapest correct answer at that grid, not slow in absolute seconds on a large fact table.
Grade the candidate:

- FINE: within a small multiple of the model floor at its natural grid. Decline the rewrite
  quoting the floor comparison.
- SLOW: materially slower than the floor. Enter an optimize-on-demand pass: author an alternative
  form, re-prove it on the SAME anchors and the pinned battery (never a fresh unproven grid), and
  only THEN let speed pick the winner among forms already proven equal.
- FAST-BUT-FRAGILE: at or below the floor but leaning on a formula-engine result-cache or a
  collapsed scope. Record it as an FE-bound note and re-time with a full drain before trusting it.

In an optimization pass, a blind grid can call a collapsed-scope form equivalent even though it
is wrong, and the cheaper work can make that wrong form look fastest. Benchmark in the
measure-faithful shape only, `DEFINE MEASURE 'Sales'[__cand] =
( <expr> )` timed via `EVALUATE SUMMARIZECOLUMNS ( <natural axes>, "v", [__cand] )` with
`benchmark_dax_coldwarm`, never as an inline extension column (inline executes with cheaper
semantics and inverts rankings); serially, never concurrently; treat any 0-storage-engine cold
reading as INVALID (the formula-engine result cache serves identical query text, a fake
fastest), cross-checking sub-ms readings against a full-drain `run_dax` wall clock. Apply a
winner with `update_measure` ONLY after it matches the current locked expected values; the gate
below re-proves it against the witness under the SAME open-shape partition as Step 6, inherited
from the Step-6 `openShapes` answer (a completed step cannot be re-answered, so the partition
cannot be restated here), and you re-run the battery's pinned contexts.

If raw arithmetic after Step 3 convicted an anchor, submit the corrected set through this step's
`expectedValues` input with the same per-changed-anchor `originalExpect`, `correctedExpect`, and
row-returning `extractQuery` receipt fields, plus the outside-DAX arithmetic, then re-run the
candidate check and the entire pinned battery. Leave the input unanswered to inherit the latest
anchors (the Step-3 revision when one exists, otherwise Step 2). The final expected-values gate
always checks that current set, even when the performance rewrite is declined.

Then FINALIZE for production regardless of the perf path: the measure carries its real production
name (not a working alias), a format string fit for the metric, a description stating what it
returns and the conventions PINNED in Step 1, and a display folder if the model uses them. An
OPEN-context observation worth the next author's attention belongs in the description.

```yaml gate
strictness: hard
ops: [update_measure, rename_object, set_measure_format, set_description, set_display_folder]
inputs:
  - name: expectedValues
    question: "OPTIONAL EXPECTATION REVISION WITH REQUIRED RECEIPT: leave unanswered to inherit the latest anchor set (the Step-3 revision when present, otherwise Step 2). Answer only when grouped raw-row arithmetic proves an anchor wrong. Submit the full corrected fenced JSON array without changing its contexts. Every changed anchor object must contain originalExpect equal to the locked value, correctedExpect equal to its new expect, and a row-returning extractQuery that produces the grouped or raw rows used by the outside-DAX arithmetic. Failed, empty, missing, or scalar-constant extracts are refused. Never re-pin an anchor merely to match a candidate or performance result."
    type: text
    required: optional
  - name: perfPass
    question: "Either: the candidate-vs-model-floor benchmark at the natural grid (measure-faithful cold/warm timings, noting any 0-SE readings discarded as cache artifacts), the grade (FINE / SLOW / FAST-BUT-FRAGILE), and for a SLOW grade the winning rewrite, its match against the locked expected values, and the re-run pinned battery. Or DECLINE quoting the floor comparison that graded it FINE."
    type: text
    required: answer-or-decline
  - name: finalized
    question: "The production name, format string, description (including PINNED conventions), and display folder applied to the measure."
    type: text
    required: required
verify:
  - kind: expected_values
    anchors: expectedValues
  - kind: dax_equivalence
    when: inputs.perfPass.answered
    probe: witnessDax
    openShapesFrom: openShapes
```
