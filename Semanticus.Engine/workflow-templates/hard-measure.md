---
kind: template
name: hard-measure
title: Author "{{measure_goal}}" and reconcile it against the requirement at every grain
description: Set up the same requirement-anchored verified-authoring playbook as the built-in Author a hard DAX measure workflow. It pins conventions only from the requirement's own words, locks expected values from raw rows before authoring, reconciles one canonical candidate against a locked raw-row witness at the shapes where wrong-but-runs bugs live, adjudicates disagreements cell-by-cell, and never lets a speed pick or an invented convention overwrite a correct answer.
whenToUse: "One measure whose correctness depends on filter context (ratios and shares, time intelligence, semi-additive logic, distinct counts), where a wrong form runs clean and looks right at the leaves. For a straightforward measure checked against one trusted number, use new-measure."
version: 5
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

## Step 1: Fix the specification. The requirement text is the only source

The requested metric is {{measure_goal}}. The likely pattern is {{measure_pattern}}. Restate the
metric and its grain. Classify additivity PER DIMENSION, not globally (e.g. "snapshot over Date;
recomputed, not summed, across currency pairs; non-additive over customer"): the per-dimension
rule decides which subtotal can silently lie.

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
from RAW ROWS ONLY: `run_dax` a plain row-level FILTER extract and aggregate it with arithmetic
that uses no CALCULATE, no time-intel, no measure references. Record each context, the extract
query, and the expected value.

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
correct (almost never a fully-crossed leaf: wrong-denominator / collapsed-context /
over-broad-ALL forms match at every leaf and diverge only where a dimension is absent, at the
bare subtotal or the grand total). THE DIVERGING SHAPE MUST BE A PINNED LEDGER ENTRY and one of
your locked contexts: if the requirement (or a clarify answer) does not pin behaviour at the one
shape that separates right from wrong, this workflow cannot certify the measure. Say so, raise it
with the user if present, or complete with the PARTIAL certificate Step 6 defines.

These locked values are the adjudication bench for every later disagreement. If later raw-row
arithmetic proves a locked expectation itself wrong, do not silently rewrite it: record an
EXPECTATION REVISION (original extract and value, corrected extract and value, the arithmetic
that convicted it) in the step evidence of whatever step you are in, then re-run everything that
consumed the old value: Step 3's candidate check and the entire Step-5 battery.

```yaml gate
inputs:
  - name: expectedValues
    question: "Two or three locked contexts (including the diverging shape, which must be PINNED, and one ordinary leaf), the raw-row extract query for each, and the expected value each yields."
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

Draft a SINGLE production candidate for {{measure_goal}} using {{measure_pattern}}: the clearest
correct expression for the Step-1 specification, composed from verified building-block measures
where they exist; VAR/RETURN structure; prefer filtering columns over tables (a table filter only
where the pattern genuinely requires one); no needless context transitions. Prefer the canonical
idiom (DATESYTD, DATEADD, PARALLELPERIOD, LASTNONBLANKVALUE, KEEPFILTERS, REMOVEFILTERS+VALUES,
TREATAS); its semantics are documented and proven. A hand-rolled date-arithmetic or
FILTER-over-ALL reconstruction of a standard idiom is a red flag in the CANDIDATE (that style
belongs to the witness, where independence is the point). Check the candidate returns the locked
expected value at each Step-2 context before submitting; on a mismatch, first re-verify the
expectation by raw arithmetic (an Expectation Revision if it was wrong), otherwise revise the
candidate. Create the measure, or update it in place when the workflow was triggered by an
update. Set `target` to the created or updated measure.

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
```

## Step 4: Lock the witness

Build and submit the raw-row WITNESS. Nothing else happens in this step, so the witness is on
the record BEFORE any comparison runs. It is a deliberately dumb scalar computation over raw rows
(FILTER over ALL of the fact table, set-and-date arithmetic keyed off the context's own values),
avoiding every idiom the candidate uses, implementing ONLY the Step-1 ledger (PINNED contexts as
pinned, OPEN contexts as whatever the dumb computation naturally yields). From this submission the
witness is LOCKED: any later revision must appear in Step 5's `adjudications` with before/after
expressions and the raw-row arithmetic that convicted it.

```yaml gate
inputs:
  - name: witnessDax
    question: "The raw-row witness, verbatim: FILTER-over-ALL + set/date arithmetic, no candidate idioms, implementing ONLY the ledger (PINNED pinned, OPEN natural)."
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
    question: "The CURRENT witness expression, verbatim: restate the Step-4 witness when unchanged, or the revised expression when an adjudication convicted it (the engine records a revision receipt on any change)."
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
  - name: certificate
    question: "The certificate level: FULL (every evaluated shape pinned and proven, no OPEN ledger contexts, no INCONCLUSIVE) or PARTIAL (name every unproven cell/observation)."
    type: text
    required: required
verify:
  - kind: dax_equivalence
    probe: witnessDax
    openShapesFrom: openShapes
```

## Step 7: Performance when attested, then finalize for production

Report the SLOWEST battery/equivalence query observed (context + elapsed). If it exceeded ~5
seconds, or the measure targets a large-fact hot path, run the performance pass; otherwise
DECLINE it quoting that slowest timing. A decline quoting anything but the maximum is invalid.

When you run it, the v1 record shows the exact trap: among candidates a blind grid calls
"equivalent", the CHEAPEST form is disproportionately the WRONG one (a collapsed scope is less
work; that is why it is wrong), and a perf tie-breaker without the diverging shape actively
selects it. So: benchmark in the measure-faithful shape only, `DEFINE MEASURE 'Sales'[__cand] =
( <expr> )` timed via `EVALUATE SUMMARIZECOLUMNS ( <natural axes>, "v", [__cand] )` with
`benchmark_dax_coldwarm`, never as an inline extension column (inline executes with cheaper
semantics and inverts rankings); serially, never concurrently; treat any 0-storage-engine cold
reading as INVALID (the formula-engine result cache serves identical query text, a fake
fastest), cross-checking sub-ms readings against a full-drain `run_dax` wall clock. Apply a
winner with `update_measure` ONLY after it matches the locked Step-2 expected values; the gate
below re-proves it against the witness under the SAME open-shape partition as Step 6, inherited
from the Step-6 `openShapes` answer (a completed step cannot be re-answered, so the partition
cannot be restated here), and you re-run the battery's pinned contexts.

Then FINALIZE for production regardless of the perf path: the measure carries its real production
name (not a working alias), a format string fit for the metric, a description stating what it
returns and the conventions PINNED in Step 1, and a display folder if the model uses them. An
OPEN-context observation worth the next author's attention belongs in the description.

```yaml gate
strictness: hard
ops: [update_measure, rename_object, set_measure_format, set_description, set_display_folder]
inputs:
  - name: perfPass
    question: "Either: the slowest observed query (context + elapsed) that triggered the pass, the measure-faithful cold/warm timings per candidate (noting any 0-SE readings discarded as cache artifacts), the winner, its match against the locked expected values, and the re-run pinned battery. Or DECLINE quoting the SLOWEST observed timing."
    type: text
    required: answer-or-decline
  - name: finalized
    question: "The production name, format string, description (including PINNED conventions), and display folder applied to the measure."
    type: text
    required: required
verify:
  - kind: dax_equivalence
    when: inputs.perfPass.answered
    probe: witnessDax
    openShapesFrom: openShapes
```
