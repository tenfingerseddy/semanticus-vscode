---
kind: template
name: metric-certification
title: Certify {{surface_name}} against the business's numbers
description: Check every KPI on {{surface_name}} against its owner's signed-off figure, raise any gaps with the named owner (never silently "fix" a certified number), and record where each passing measure's number comes from, so trust in the surface is evidence, not guesswork.
whenToUse: "A recurring certification of a business-critical surface using YOUR KPI dictionary. For a one-off single measure verified against a known-good number, use new-measure."
version: 1
strictness: hard
slots:
  - name: surface_name
    question: "What are you certifying (e.g. 'the FY26 Exec Dashboard')?"
    type: text
    required: required
    example: "the FY26 Exec Dashboard"
  - name: kpi_dictionary
    question: "Your KPI dictionary — one line per measure: measure ref · business definition · OWNER · where the signed-off figure comes from."
    type: text
    required: required
    example: "measure:Sales/Net Sales — total net sales after returns — owner: CFO — signed off from the FY25 board pack"
  - name: escalation_rule
    question: "What happens on a discrepancy (e.g. 'stop and raise with the owner — finance figures are never adjusted to match the model')?"
    type: text
    required: required
    example: "stop and raise with the owner — finance figures are never adjusted to match the model"
  - name: certification_tag
    question: "The stamp for certified measures (e.g. 'Certified FY26-Q1 · Finance')."
    type: text
    required: optional
    default: "Certified"
    example: "Certified FY26-Q1 · Finance"
---

## Step 1: Confirm the dictionary still holds

{{kpi_dictionary}} is the certification scope. Confirm with the owners that every definition and
figure-source is current for this cycle — a certification against a stale dictionary certifies the
wrong thing.

```yaml gate
inputs:
  - name: dictionaryConfirmed
    question: "Is the dictionary current — every measure, definition, owner and figure-source confirmed for this cycle?"
    type: text
    required: required
```

## Step 2: Check every KPI at its stated context

For EACH dictionary line, collect this cycle's signed-off figure from the owner (per-run evidence —
never reuse last cycle's), evaluate the measure with probe_measure at the exact stated context, and
record match or mismatch. On any discrepancy: {{escalation_rule}}.

```yaml gate
ops: [probe_measure, pivot_measure, run_dax]
inputs:
  - name: probeLog
    question: "Per KPI: the owner's figure, the probed value, match/mismatch, and the escalation taken for each mismatch."
    type: text
    required: required
```

## Step 3: Prove the anchor number

The single most business-critical measure gets the engine's proof, not an attestation — an equivalence
between the owner's signed-off number and what the model actually returns at the stated context.

```yaml gate
strictness: hard
inputs:
  - name: anchorTarget
    question: "The single most critical measure of {{surface_name}} (e.g. measure:Sales/Net Sales)."
    type: objectRef
    required: required
  - name: anchorValue
    question: "Its exact context and signed-off figure for THIS cycle, from the owner (not the model), in the form `<dax filter context, or (grand total)> ~ <exact number>`. The app evaluates the measure under that context and proves it returns that figure."
    type: verification
    required: required
verify:
  - kind: dax_probe
    when: inputs.anchorValue.answered
    probe: anchorValue
  - kind: bpa_clean
    scope: object
```

## Step 4: Stamp and save

set_description on every passing measure with the stamp "{{certification_tag}}: <figure> @ <context> on
<date>, owner <name>" — provenance travels with the measure so trust is legible later. Then save_model.
