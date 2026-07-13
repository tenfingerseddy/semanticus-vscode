---
kind: template
name: month-end-close
title: Month-end close for {{period_label}}
description: Run your month-end close as a checklist the app enforces: confirm the close steps are current, check every total against the figure finance signed off, prove the headline number matches, then record who signed off and capture the certified figures so any later refresh or edit that moves them is caught. The close becomes evidence, not a spreadsheet someone remembers to run. This detects drift against what was certified; it cannot stop a refresh or an out-of-tool edit.
whenToUse: "The regular end-of-period close on a reporting model, using your own checklist and totals. To certify a model that is no longer changing, use metric-certification instead."
version: 1
strictness: hard
slots:
  - name: period_label
    question: "Which period is this close for (e.g. 'FY26 · July')?"
    type: text
    required: required
    example: "FY26 · July"
  - name: close_checklist
    question: "Your close checklist, one task per line, in the order you run them."
    type: text
    required: required
    example: "lock the source refresh · reconcile GL · post accruals · refresh model · check the totals"
  - name: control_totals
    question: "Every figure finance signed off, one per line, in this STRICT form so the close can be machine-checked:  measure:Table/Name ~ <dax filter context, or the literal (grand total)> ~ <exact number>. Include the headline as one of these lines. Use the EXACT signed-off figure — digits and an optional decimal point only, no thousands separators, currency, or '87.4M'. Express the context as the DAX filter you will capture at (columns only, not another measure), and write '(grand total)' for the model default."
    type: text
    required: required
    example: "measure:Finance/Total Revenue ~ 'Date'[MonthNo]=7 ~ 87437221"
  - name: freeze_window
    question: "While the close runs, what have you agreed must not change (and for how long)? This is recorded with the sign-off."
    type: text
    required: optional
    default: "the last two business days of the period, with no model changes except the close itself"
    example: "the last two business days of the period, with no model changes except the close itself"
  - name: signoff_owner
    question: "Who signs off the close (name or role), and what are they confirming?"
    type: text
    required: required
    example: "the Financial Controller, confirming every total reconciled to the GL"
---

## Step 1: Confirm the close checklist is current

{{close_checklist}} is this period's close scope. Walk it with the person who owns the close and confirm
each step still reflects how {{period_label}} is actually closed — a checklist that drifted from the real
process gives false assurance.

```yaml gate
inputs:
  - name: checklistConfirmed
    question: "Is the close checklist current and complete for this period — every step still valid, nothing missing?"
    type: text
    required: required
```

## Step 2: Check every total against its signed-off figure

Your control totals for this period, each declared as `measure:Table/Name ~ <dax context> ~ <exact value>`:

{{control_totals}}

For EACH line, evaluate the measure at its declared DAX context with probe_measure, and confirm it equals the
exact signed-off figure on that line. A mismatch is a stop-the-close signal — do not carry it forward silently.
Every line must be in the strict form (an exact number, and the context as the DAX filter you will capture at):
Step 4's gate re-parses these lines and will refuse a line it cannot read as ref, context, and value.

```yaml gate
ops: [probe_measure, pivot_measure, run_dax]
inputs:
  - name: controlTotalLog
    question: "Per control total: the source figure, the probed value, match/mismatch, and what you did about any mismatch."
    type: text
    required: required
```

## Step 3: Prove the headline number matches

The period's headline figure gets the engine's proof, not an attestation. The check evaluates the headline
measure at the model's DEFAULT (grand total) context — no slicer applied — and compares it to the signed-off
figure you give for that context. It proves that one number, at that one context, matches to within
floating-point rounding noise. A figure at a narrower reporting slice is a control total (Step 2), probed at
its stated context, and is captured at Step 4.

```yaml gate
strictness: hard
inputs:
  - name: headlineTarget
    question: "The headline measure for {{period_label}} (e.g. measure:Finance/Total Revenue)."
    type: objectRef
    required: required
  - name: headlineValue
    question: "Its signed-off figure at the model's DEFAULT (grand total) context — the number the model returns with no slicer applied — from the close pack. (A sliced/period figure belongs in the control totals, Step 2.)"
    type: verification
    required: required
verify:
  - kind: dax_probe
    when: inputs.headlineValue.answered
    probe: headlineValue
  - kind: bpa_clean
    scope: object
```

## Step 4: Capture the certified figures and sign off

Now freeze what was certified. For EACH declared line in {{control_totals}} (the headline is one of them),
capture it with capture_baseline into ONE certified baseline labeled "{{period_label}}": pass the measure as
objRef, the line's DAX context as filters (exactly as declared — grand total means no filters),
includeDependents:false, and groupBy empty, so each certified figure is a single number at its declared
context. A figure that cannot be evaluated, or whose context uses a LITERAL volatile function (TODAY/NOW), is
deliberately NOT certified — fix its context and re-capture. A context that references another measure cannot
be re-checked identically, so the gate REFUSES it — restate that context with columns only. A certification is
IMMUTABLE: to correct one, use a new label (e.g. "{{period_label}} r2").

Record the agreed freeze: {{freeze_window}}. This records the agreement; nothing here mechanically blocks
edits or refreshes. Record sign-off from {{signoff_owner}} against the check log and the Step 3 proof, then
save_model.

This step cannot pass until EVERY declared line is certified at its declared context with its exact declared
value — the gate re-parses {{control_totals}}, and a line it cannot read, or a figure whose certified context
or value does not match, refuses the close. Later — after any refresh or edit — run
compare_baseline(label: "{{period_label}}") to catch and NAME any signed-off number that moved (old to new,
beyond floating-point rounding noise). This is detection, not prevention: it cannot stop a service refresh or
an out-of-tool edit, but it reports what drifted from what was certified, on the model it was certified on.
Editing the model in unrelated ways (adding measures, say) is EXPECTED and does not block the check — each
figure is matched by its own identity, so a shape change is only noted, never a refusal. A moved figure may
also reflect a different security role — effective-user context is not captured.

```yaml gate
ops: [capture_baseline, compare_baseline, save_model]
inputs:
  - name: certifiedLabel
    question: "The label you captured the certified figures under (use the period, e.g. '{{period_label}}')."
    type: text
    required: required
  - name: signoffConfirmed
    question: "Confirm sign-off from {{signoff_owner}} against the check log and the Step 3 proof (name and what they are confirming)."
    type: text
    required: required
verify:
  - kind: baseline_captured
    probe: certifiedLabel
```
