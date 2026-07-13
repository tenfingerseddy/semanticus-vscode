---
kind: template
name: deploy-freeze-guard
title: Release checks for {{freeze_window}}
description: While changes are locked, send every deploy through a set of checks: confirm the change is allowed under your rules, prove the model did not get worse (no new readiness or best-practice problems), and record who approved anything that ships. Most of the year it stays out of your way; while changes are locked it makes the rule enforce itself.
whenToUse: "A recurring period when changes are locked (month-end, quarter-end, or a production-only window). Fill it in with when changes are locked, what your rules allow, and who can approve an exception."
version: 1
strictness: hard
triggers: [deploy_live]
slots:
  - name: freeze_window
    question: "When are changes locked, and where does this apply (for example: the last two business days of each month, and all deploys to the production workspace)?"
    type: text
    required: required
    example: "the last two business days of each month, and all deploys to the production workspace"
  - name: change_policy
    question: "While changes are locked, what is allowed to ship, what is blocked, and what needs approval?"
    type: text
    required: required
    example: "a quick fix to a broken measure can ship if it is approved; changes to tables or relationships are blocked; anything else needs approval"
  - name: override_authority
    question: "Who can approve a deploy while changes are locked, and what must they write down?"
    type: text
    required: required
    example: "the release manager, who writes down the change, the business reason, and the rollback plan"
---

## Step 1: Check the change against your rules

Changes are locked: {{freeze_window}}. Check this change against your rules: {{change_policy}}.
Say plainly whether it can ship, is blocked, or needs approval, and why.

```yaml gate
inputs:
  - name: changeClassification
    question: "What is this change, and under your rules is it allowed, blocked, or does it need approval (and why)?"
    type: text
    required: required
```

## Step 2: Prove the model is still clean

A deploy while changes are locked must not make the model worse. The app re-scans: readiness must not drop
and no new best-practice problem may appear across the model. A hard gate: if it got worse, fix it or do not ship.

```yaml gate
strictness: hard
verify:
  - kind: readiness_rescan
  - kind: bpa_clean
    scope: model
```

## Step 3: Record who approved it

Nothing ships while changes are locked without approval: {{override_authority}}. Record who approved it,
the business reason, and the rollback plan. Every exception is written down, never a silent bypass.

```yaml gate
inputs:
  - name: authorisation
    question: "Who approved this deploy, the business reason, and the rollback plan (written down, not a formality)."
    type: text
    required: required
```
