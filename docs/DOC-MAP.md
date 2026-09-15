# Doc map

Where to read before non-trivial work. *(Consolidated 2026-06-30 — see
[`archive/README.md`](archive/README.md) for what was archived and why.)*

## Start here

- [`redesign/CURRENT.md`](redesign/CURRENT.md): current redesign build handoff, design and ownership.

- [`assistant-skills.md`](assistant-skills.md) — the customer skill pack, supported assistants and installation/update instructions.
- [`PLAN.md`](PLAN.md) — the forward roadmap + lane status. Sequencing: **Lineage → hardening →
  shipping**. The chronological ship-log lives in [`../CHANGELOG.md`](../CHANGELOG.md), not here.
- [`strategy/00-INDEX.md`](strategy/00-INDEX.md) — the consolidated **strategy** doc: the 4-pillar moat
  thesis (incl. the one-stop-shop convenience moat), the decisions log (**license = source-available
  Elastic License 2.0** — ratified 2026-07-06, ELv2 text shipped 2026-07-10 in PR #149, superseding the
  earlier MIT open-core call; multi-model kept in 1.0), monetization, the keep-core/rewrite-shell
  foundation verdict, Microsoft's reuse-vs-build surface, and the now-resolved open decisions /
  now-closed code holes. Distils the former `01`–`04` (archived).
  [`strategy/05-pro-mode-enforced-workflows.md`](strategy/05-pro-mode-enforced-workflows.md) (2026-07-02)
  extends it: Pro = enforced, evidence-verified, customisable workflows. **Superseded 2026-09-15:** Free and Pro are set by feature now, not by this split. See the `Free and Pro` section of `README.md` and `CHANGELOG.md` 1.2.0.
- [`NEXT.md`](NEXT.md) — a what-to-do-next entrypoint from the 2026-07-02 cloud handoff audit.
  ⚠ Written against pre-consolidation `main` (it predates the Verified Edits v1 / audit-layer /
  DAX-ruleset merge and the doc consolidation) — trust PLAN.md + CHANGELOG.md where they disagree.

## Flagships and lane specs

- [`verified-edits-plan.md`](verified-edits-plan.md) + [`dax-best-practice-rules.md`](dax-best-practice-rules.md)
  the Verified Edits ("Model CI") flagship spec (v1 spine + audit trail + accountable checkpoint
  SHIPPED; **Superseded 2026-09-15:** Verified Mode and the audit-trail export are FREE now) and the deterministic DAX best-practice ruleset (walker + lint + scored BestPractice category
  SHIPPED 2026-07-03).
- [`pro-mode-spec.md`](pro-mode-spec.md) — the buildable spec for the Pro-mode workflow engine (file
  format, state machine, gate semantics, [F]/[O] task tags), with
  [`semantic-model-journey.md`](semantic-model-journey.md) (9 phases → ~60 activities → per-activity best
  practices, mapped to MCP ops vs gaps — the seed catalog for Pro-mode workflows) and
  [`mcp-skills-research.md`](mcp-skills-research.md) (MCP as a skill/instruction-delivery surface).
- [`learning-loop-plan.md`](learning-loop-plan.md) — the **Learning Loop capstone** (2026-07-03):
  self-improvement via engine-captured experience (`.semanticus/experience.jsonl`), ExpeL-style insight
  store, fingerprint recall, `/distill-workflow` + `/post-mortem` skills, admission gates against
  skill-debt/poisoning. Research-verified (ACE/GEPA/ExpeL/AgentDebug/MemoryGraft). **Ratified by Kane
  2026-07-03:** L0 capture = immediate ride-along; L1+ after Studio v2 with the workflow engine;
  Free/Pro = capture/recall free, compounding machinery Pro. **Superseded 2026-09-15:** Free and Pro are set by feature now, not by this split. See the `Free and Pro` section of `README.md` and `CHANGELOG.md` 1.2.0.
  [`learning-loop-testing.md`](learning-loop-testing.md) (2026-07-05) — how to prove it production-ready
  **without dogfooding**: the end-to-end wiring smoke, the A/B efficacy simulation (`LearnBench`,
  deterministic-oracle lift), and the two mandatory safety benchmarks (poisoning, skill debt).
- [`ai-readiness-plan.md`](ai-readiness-plan.md) + `ai-readiness-catalog.json` — the AI-readiness lane
  spec + the machine-readable ~181-rule backlog (full catalog = a 1.0 goal).
  [`prep-for-ai-storage.md`](prep-for-ai-storage.md) — where the Prep-for-AI settings live on disk.
- [`change-plan.md`](change-plan.md) — the Change-Plan "PR for your model" flagship (shipped; the
  atomic apply primitive). **Superseded 2026-09-15:** applying a plan in bulk is FREE now.
- [`lineage-impact-plan.md`](lineage-impact-plan.md) — the Lineage & Impact "Measure Killer" tab.
- [`te3-rethink.md`](te3-rethink.md) — UI / authoring-IDE direction (stay-in-VS-Code, 445-story map, the
  5-phase authoring roadmap, the React Studio stack folded in from the old `visual-plan.md`).

## Engineering invariants and oracles

- [`harness-engineering.md`](harness-engineering.md) — **ADOPTED principles + backlog (2026-07-03):**
  Semanticus as an agent harness — the 6 ranked lessons (tool results = the real system prompt / result
  contract; tool-surface + token economy; ground-truth feedback loops; universal `dryRun`;
  telemetry-driven ergonomics from L0; the ≤2k-token orientation op) + the 4 harness KPIs and the
  tool-result contract (Appendix). New/touched ops are measured against the contract.
- [`op-routing-map.md`](op-routing-map.md) — the 3 write routes / undo-invariant / last-writer-wins
  conflict policy.
- [`tom-bump-gate.md`](tom-bump-gate.md) — the TOM/AMO-bump gate.
- [`feature-coverage-matrix.md`](feature-coverage-matrix.md) — the QA coverage oracle (226 features;
  tested / partial / untested + the deep-test backlog).
- [`../CHANGELOG.md`](../CHANGELOG.md) — the running ship-log ·
  [`../RELEASE-CHECKLIST.md`](../RELEASE-CHECKLIST.md) — the human launch gates ·
  `semanticus-synopsis.html` — the stakeholder pitch.

## Provenance archive

[`archive/`](archive/README.md) holds superseded research/handoffs for history, **not current**:
`feature-plan.md`, `visual-plan.md`, `multimodel-plan.md`, `ai-readiness-research.md`,
`ms-mcp-research.md`, `ms-fabric-skills-research.md`, `powerquery-tab-plan.md`, `MERGE-READINESS.md`,
and `strategy/01`–`04`. [`rlat-integration-plan.md`](rlat-integration-plan.md) is provenance too:
**discarded by Kane 2026-07-10**, do not propose again.

[`provenance/`](provenance/) holds **review and audit records that would otherwise exist nowhere** — a
reviewer's full severity ledger and its adjudication, a round-by-round adversarial review, an author's own
findings record, or a repository audit. **The work reviewed does not have to be merged:** records against a
still-open pull request belong here too, and so does an audit, which finds no defects in code at all. Like
`archive/`, every file here is **historical record, not current status**: read `../TASKS.md` for what is
actually open, and trust it wherever it disagrees. A record landed here is **not** a triage — a finding is
open until `../TASKS.md` or the file's own body says otherwise.

- [`provenance/2026-07-28-pr276-a2-parser-sol-review-ledger.md`](provenance/2026-07-28-pr276-a2-parser-sol-review-ledger.md)
  — SOL's 17 findings on PR #276 (the A2 PR-1 DAX parser, **merged**), ten of which had reached no committed
  file, plus why they nearly vanished and the rule that prevents a repeat.
- [`provenance/2026-07-29-ledger-rescue-verification-notes.md`](provenance/2026-07-29-ledger-rescue-verification-notes.md)
  — the verification notes behind that ledger's rescue: a claim-by-claim table of where the brief it was
  given was wrong, each with the measurement that corrected it.
- **PR #285 (`chore/t166-attribution-licence`, borrowed-code attribution + the licence gate) — OPEN.**
  [`2026-07-29-t166-attribution-licence-findings.md`](provenance/2026-07-29-t166-attribution-licence-findings.md)
  is the author's own findings record; then four adversarial rounds, every one of them **do not merge**:
  [round 2](provenance/2026-07-29-pr285-round2-attribution-licence-review.md) ·
  [round 3](provenance/2026-07-30-pr285-round3-attribution-licence-review.md) ·
  [round 4](provenance/2026-07-30-pr285-round4-attribution-licence-review.md).
- **PR #289 (`fix/hygiene-scanner-and-corpus-pin`, the test-hygiene + corpus-pin guards) — OPEN.** Three
  adversarial rounds, every one of them **do not merge**:
  [round 1](provenance/2026-07-29-pr289-round1-guards-review.md) ·
  [round 2](provenance/2026-07-29-pr289-round2-guards-review.md) ·
  [round 3](provenance/2026-07-30-pr289-round3-guards-review.md).
- [`provenance/2026-07-29-worktree-and-branch-audit.md`](provenance/2026-07-29-worktree-and-branch-audit.md)
  — an **audit, not a review**: every worktree and branch in Semanticus and Rome, with a per-branch verdict on
  whether its content is already on `origin/main`, proved by content residual rather than by ancestry. Several
  rows say DECIDE; nothing is authorised for deletion by the file existing.

## DAX kernel (Phase A)

The normative lane specs live in [`kernel-a0/`](kernel-a0/) (`A0-workplan.md`, `A0-language-scope.md`,
`A0-syntax-api-design.md`, `A0-greentree-results.md`, `A0-binding-corpus.md`) and
[`kernel-a/`](kernel-a/) (`A1-lexical-spec.md`, `A2-parser-spec.md`). Read the spec for the lane you are
touching. Alongside them, three dated **assessments** — record, not spec:

- [`kernel-a/A-lane-restart-assessment-2026-07-29.md`](kernel-a/A-lane-restart-assessment-2026-07-29.md)
  — where the lane actually stood before restarting it: what merged (#264, #266, #268, #276), whether any
  kernel work exists in only one place on disk, and the gaps found.
- [`kernel-a/A2-query-corpus-feasibility.md`](kernel-a/A2-query-corpus-feasibility.md) — `[T168]`: the 16
  corpus surfaces A2 PR-2 must fill with observed evidence, grouped by whether a live model can supply them
  at all. PR-2 is blocked on this.
- [`kernel-a0/A0-workplan-calibration-a1-a2.md`](kernel-a0/A0-workplan-calibration-a1-a2.md) — the A0
  workplan's own estimate bands against what A1 and A2 PR-1 actually cost. No band was re-baselined by it.

One **plan** and its downstream **build design**, neither of which is a spec or assessment:

- [`kernel-a/A3-public-api-baseline-plan.md`](kernel-a/A3-public-api-baseline-plan.md) — `[K-A3]`: how the
  already-shipped `Semanticus.Dax` public surface gets captured before it is changed. It plans **one** build:
  a dependency-free canonical full-signature snapshot in `Semanticus.Dax.Tests`, carrying nullability,
  generic constraints, parameter modifiers, defaults and contract attributes, regenerated and compared at
  test time. Two controls are pinned — a missing baseline fails, and a changed signature fails — and both
  must be watched red first. It deliberately carries **no counts**: no tally, no catalogue, no thresholds.
  It adds no product source and designs no future API. Landing it ratifies nothing; Kane ratifies later,
  once an exact additive diff names the option, the depth exception, the traversal type and the rewriting
  type. It lands after T182 step 1, which is already on `main`, and before step 2.
- [`kernel-a/A3-B1-red-first-build-design.md`](kernel-a/A3-B1-red-first-build-design.md) — `[K-A3]`: the
  exact red-first build of the A3 public-API baseline (B1) specified by the plan at `e88c0a0` — the row
  grammar, the measured `NullableAttribute` slot rule, why `NullabilityInfoContext` is unusable for nested
  generic positions, and the order in which C1, C2 and the encoder's own controls are watched failing.
  Its section 0 records that the plan lands first in the same change, so the implementation contract is
  reviewable before any product API moves.

## Protocol and release evidence

- [`mcp-2026-07-28-revision-impact.md`](mcp-2026-07-28-revision-impact.md) — what the MCP `2026-07-28`
  specification revision means for Semanticus and Nauticus: seven spec answers, a gap table against the
  current engine, and an unresolved contradiction over whether that revision is Current or Draft. Untriaged.
- [`release-evidence/`](release-evidence/) holds the dated gate evidence behind a release.
  [`release-evidence/mcp-stdio-wire-capture-2026-07-29.md`](release-evidence/mcp-stdio-wire-capture-2026-07-29.md)
  is observed bytes from one real MCP stdio session, read-only ops only. **The raw capture logs are
  deliberately not in the repository** (real tenant name, local paths), so its byte-level claims cannot be
  re-derived from anything here.
