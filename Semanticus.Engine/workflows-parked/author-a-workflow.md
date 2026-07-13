---
name: author-a-workflow
title: Author a new workflow for the user
description: The meta-workflow: capture what the user wants enforced, draft it in the workflow grammar against the REAL op surface, save it (parse-validated), then PROVE it admissible with the same check_workflow dry-run the engine uses (hard gate) before handing it over. A workflow that authors workflows, gated on the workflow being real.
whenToUse: "The user wants a NEW custom workflow authored: a repeatable task sequence with gates and verification unique to their team or project. Produces a saved, admissible workflow. To edit an existing one, get_workflow then save_workflow directly; to just curate which workflows are on the menu, use set_workflow_enabled."
version: 1
strictness: hard
triggers: [save_workflow]
---

## Step 1: Capture what to enforce

Before writing any markdown, understand the JOB the user wants a workflow to standardise. Get from them: the task the workflow governs; the ordered STEPS a good operator takes; at each step, what
must be TRUE before moving on (the gate) and how the engine could PROVE it: a known-good number (`dax_probe`),
an unchanged result after a rewrite (`dax_equivalence`), no new violations (`bpa_clean`), a held readiness grade
(`readiness_rescan`), a perf budget (`benchmark_delta`); and which are hard (block) vs advisory. A workflow with
no verifiable gate is just a checklist; find the one or two places where being wrong is expensive, and gate
those. Ground the design in the real op surface: `get_op_catalog` lists every op a `triggers:`/`ops:` line may
name, so you draft against what exists, not what you assume.

```yaml gate
ops: [get_op_catalog, list_workflows, get_workflow]
inputs:
  - name: job
    question: "What task should this workflow standardise, and what makes it worth enforcing (where does being wrong cost the most)?"
    type: text
    required: required
  - name: stepsAndGates
    question: "The ordered steps, and for each the gate: what must be true to proceed, how the engine could PROVE it (dax_probe / dax_equivalence / bpa_clean / readiness_rescan / benchmark_delta), and whether it is hard or advisory."
    type: text
    required: required
```

## Step 2: Draft and save the workflow

Write the workflow in the grammar: YAML frontmatter (`name` kebab-case matching the
filename, `title`, `description`, and a `whenToUse` so it routes among siblings), then one `## Step N:` heading
per step with a plain-language instruction, and a fenced ` ```yaml gate ` block where a step needs one. A gate
carries `inputs` (each with a `question` the agent asks the user, a `type`, and a `required` level) and/or
`verify` checks (each `kind` with its `probe`/`scope`/`when`). Remember the target convention: any `dax_probe`,
`dax_equivalence`, `benchmark_delta`, or object-scoped `bpa_clean` needs an `objectRef` input somewhere naming
the object it acts on. Save it with `save_workflow`, which PARSE-VALIDATES and refuses to write a file the parser
rejects, so a saved workflow always parses. Record the exact name you saved under; Step 3 checks that name.

```yaml gate
ops: [save_workflow, get_workflow]
inputs:
  - name: authoredName
    question: "The kebab-case name you saved the workflow under with save_workflow (e.g. 'monthly-close-check')."
    type: text
    required: required
```

## Step 3: Prove it admissible: HARD GATE

Parsing is not enough: a saved workflow can still name a phantom op, probe an input no gate collects, or put a
DAX verify where nothing supplies a target. This gate runs the SAME admission dry-run the engine's `check_workflow`
runs against the workflow you named in Step 2, and passes ONLY if it comes back Ok with no warnings. If it fails,
the detail names exactly what is wrong: fix the markdown, `save_workflow` again under the same name, and re-submit.
An authored workflow that ships with a broken op reference or an unrunnable gate would fail the FIRST time a
teammate ran it; this catches it before it ever reaches them. Do not hand over a workflow that is not admissible.

```yaml gate
strictness: hard
ops: [check_workflow, save_workflow, get_workflow]
verify:
  - kind: workflow_admissible
    probe: authoredName
```

## Step 4: Hand it over

The workflow is saved and proven admissible. Tell the user it now appears in `list_workflows` and the Studio
Workflows tab, and how to put it to work: it is available to run immediately, they can turn it off for the project
with `set_workflow_enabled` if it is not ready for the team yet, and (Pro) they can require it for a task with a
binding. Confirm the `whenToUse` reads clearly against its siblings so the next person or AI assistant
picks it for the right job.

```yaml gate
inputs:
  - name: handoffConfirmed
    question: "Confirm the workflow is saved, admissible, and its whenToUse distinguishes it from existing workflows, and tell the user how to enable/require it."
    type: text
    required: answer-or-decline
```
