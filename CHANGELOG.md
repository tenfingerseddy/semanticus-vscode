# Changelog

All notable changes to Semanticus are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

> This file is the project's running **ship-log** — the chronological record of what landed, when, and
> with what verification.

## [Unreleased]

## [1.0.0] - 2026-07-14

The first public Windows 11 x64 release of Semanticus Studio. The platform-specific VSIX bundles its own
engine and exposes the same model session through the VS Code workbench and MCP. This cut includes the frozen
1.0 authoring, analysis, proof, workflow, lineage, calendar, compare and deployment-preparation surface described
below. Live-tenant capabilities still depend on the user's tenant, capacity, permissions and credentials; the
documented human acceptance checks remain mandatory for the artifact that is published.

### Changed: CI now produces and executes the runnable production VSIX (2026-07-13)

The unsigned artifact and dormant publication rail now use the Windows x64 platform packager, including the
self-contained engine, instead of raw `vsce`. Packaging opens the finished archive, rejects anything outside
the production allow-list, validates the target and required runtime files, extracts the engine, and launches
that exact binary without global .NET. PDBs are excluded. The clean-machine VS Code install remains a human gate.

### Fixed: RPC dual-drive smoke no longer depends on notification scheduling (2026-07-13)

The change-plan broadcast assertion now waits for the exact `addPlanItem` payload instead of whichever delayed plan
notification happens to arrive next. This preserves the real cross-client assertion while removing a Windows CI race.

### Added: Public contracts for offline MCP reads (2026-07-13)

Direct MCP regression anchors now cover named-expression reads, reference-tree enumeration, deterministic DAX
lint, pending approvals, saved test definitions and test-run history. They prove exact results, honest missing-input
failures, the Free-tier history boundary, isolated approval and sidecar storage, and zero model revision or broadcast
for every read. The generated public-door gap count falls from 17 to 11.

### Changed: Public release claims now match the frozen 1.0 boundary (2026-07-13)

The public and Marketplace READMEs now state the honest Windows 11 support boundary, keep Ubuntu installation
unclaimed until clean-machine acceptance, and exclude macOS from 1.0. Stale provider-specific commands, unratified
pricing, cloud-report implications and deferred Fabric write claims were removed. A public support and limitations
contract plus an auto-discovered extension test now guard platform language, assistant terminology, package/lock
version consistency and publisher-checklist drift. Final version and release-note stamping remains an RC action.

### Added: Fail-closed human RC acceptance pack (2026-07-13)

The release procedure now preregisters and records the human-only gates that CI cannot honestly satisfy: F5
interaction and activation timing, clean-machine install, real extension upgrade, uninstall, large-model execution,
supervised XMLA dry-run/write/revert, and publication. A PowerShell runner verifies the exact RC SHA, clean checkout,
green exact-SHA CI and allow-listed runnable VSIX, captures local evidence, and deliberately performs no live model
or publication write. The reusable sign-off record treats skips and ambiguous evidence as failures.

### Changed: User-facing copy rules are now enforced (2026-07-13)

The extension test suite now audits user-visible source literals, marketplace and configuration labels, and
built-in workflow catalog copy for the release terminology and punctuation rules. Current violations were
replaced with plain labels, and dynamic operation names, statuses, kinds and relationship settings are humanized
before rendering. The audit deliberately excludes source comments, protocol identifiers and historical evidence.

### Added: Reproducible performance and lifecycle gate (2026-07-13)

`Semanticus.PerfSmoke` captures machine- and fixture-bound p95 baselines for model open, orientation, Tests and
DAX-verification payloads. It also exercises repeated local lifecycle and named-pipe reconnect cycles while
tracking memory, working set, threads and handles. RC comparison rejects unlike environments and fails a p95
regression above 20 percent; the initial Windows x64 standard-corpus baseline is recorded with a passing replay.

### Fixed: Public Git operations now fail closed and report real reloads (2026-07-13)

Direct MCP contracts now cover branch, checkout, pull and clone against isolated local repositories and remotes.
Routine failure returns a structured result instead of throwing or falsely succeeding. Checkout and pull refuse
while the open model has unsaved in-memory edits, dirty-worktree conflicts preserve the model byte for byte, and
the reload flag is set only when the on-disk model actually changed, including a no-op checkout while the model file
is temporarily unreadable. Relative clone targets are workspace-based and reject escape, a linked workspace root,
linked workspace directories and overwrite attempts. A clone becomes visible only after workspace-root staging,
model and branch inspection, concrete model-storage validation, and a second linked-parent check succeed.
A linked PBIP `definition` child is refused before publication and its external target remains untouched. Checkout
inputs must resolve to a commit or branch, preventing tracked filenames from becoming destructive pathspecs. A
clean-session gate spans checkout and pull, so concurrent edits are refused until Git finishes instead of racing the
backing-file change. Saves share that gate: an in-progress save completes before Git, while a save attempted after
Git owns the files is refused and cannot overwrite the switched model. Atomic admission preserves a caller that
already owns model state, rejects queued non-owners when Git announces intent, and uses a generation check to refuse
an edit or save that observed the old state but did not seek admission until after Git completed. The same
operation holds the session lifecycle and resolves its repository from the captured session, so a concurrent open/create waits and cannot
redirect checkout or pull into the replacement model's repository. Surfaced Git output and error fields scrub password and username-only URI credentials, including
malformed slash-containing userinfo, plus query tokens. Successful
and failed state-changing agent attempts broadcast one activity event without claiming a model edit; read-only
branch listing remains silent. Combined with the restore-point purge contracts already on main, the generated
public-door gap count falls from 11 to six.

### Security: Restore-point purge is previewed, path-contained and truthful (2026-07-13)

`purge_restore_points` now requires exactly one safe selector and defaults to a token-bound dry-run showing the
exact snapshots that would be permanently deleted. Both doors refuse negative ages, combined selectors, stale
confirmations, tampered manifest paths and linked target stores. Malformed, oversized and linked manifests or
snapshots remain visible for validated-sibling purge but are refused for rollback. Filesystem failures identify the
affected restore points and are never counted as successful removals; preview and commit publish honest activity
without claiming a model edit.

### Security: Dependency advisories are remediated and CI-enforced (2026-07-13)

StreamJsonRpc is upgraded to 2.25.29 across the engine and direct smoke clients, replacing vulnerable
MessagePack 2.5.108 with 2.5.302. ModelContextProtocol is upgraded to 1.4.1. ANTLR's legacy .NET Standard
dependency now resolves patched System.Net.Http 4.3.4 and X509Certificates 4.3.2 packages, while a self-contained
Windows payload uses the .NET 8 runtime assemblies. Repository-wide restore policy promotes every NuGet advisory
or unavailable source to an error, and the required extension test gate discovers every npm lockfile on Ubuntu
and Windows. Independent review expanded the gate from two explicit roots to all four current package trees,
exposing a Moderate ECharts XSS advisory in the Studio webview. ECharts 6.1.0 clears it while the supported v5
compatibility theme preserves the established visual defaults. The parser-self-tested combined audit remains
available for release evidence.
Independent-review revisions also require a valid single-line or multiline NuGet JSON document and complete,
integer, internally consistent npm severity metadata, preventing empty or malformed reports from appearing green.

### Fixed: High-risk metadata writers now fail loudly and have public-door contracts (2026-07-13)

Direct MCP regression anchors now cover partition M, relationship cross-filter, sort-by and data-category
writes. They prove successful mutation, one attributed broadcast, public read-back, shared human undo and MCP
redo, honest `Changed=false` idempotence, and zero revision or broadcast for invalid calls. Unknown sort
columns and invalid relationship directions previously returned a misleading `Changed=false`; they now refuse
with exact recovery guidance and preserve the model unchanged. The generated public-door gap count falls from
21 to 17, and the oracle now excludes manifests and fixtures from textual coverage evidence.

### Changed: Test execution no longer depends on checkout or machine luck (2026-07-13)

The xUnit project now compiles only its explicit tracked-source manifest and copies every runtime fixture
beside the test assembly. Checkout source greps were replaced with compiled call-graph contracts. The harness
pins invariant culture, rejects machine-local clock/timezone reads, and deterministically varies serial test
order across operating systems. Adversarial order testing also found and fixed a mock live-snapshot fixture
that could delete the shared temp root. Two 1,637-case runs of the same external assembly pass under different
orders with foreign working directories, isolated homes and temps, empty global Git configuration, and no
failed or skipped tests.

### Changed: Extension tests are now a required cross-platform CI gate (2026-07-13)

The existing Ubuntu and Windows build-and-smoke jobs now install the VS Code extension dependencies from
the committed lockfile and run every extension behavioral test before building .NET. `npm test` discovers
all `test/*.test.mjs` files automatically and fails on an empty suite or the first failing child, preventing
new tests from being present but unexecuted. The dormant publication job runs the same suite before publish.

### Changed: Aligned Author a hard measure with ProBench v2 (2026-07-13)

The built-in `verified-measure` playbook and Featured `hard-measure` template now share the same
version-4, five-step contract proven by ProBench v2: one candidate, an independent raw-row oracle,
a deliberately discriminating context grid, hard candidate-to-oracle equivalence, and only then an
optional performance pass. A compiled structural test prevents the two product surfaces drifting again.

### Changed — Trimmed the stock workflow library to a lean 14-workflow launch set + new `add-relationship` (2026-07-08)

The shipped playbook library went from 32 to a curated **14** so the launch menu reads as a clear set of
jobs, not a wall of near-duplicates. No enforcement changed — every shipped workflow is still an enforced
Pro funnel with evidence-verified gates.

- **New — `add-relationship`**: the Power-BI-native "relate two tables" job. Set cardinality and filter
  direction, review the graph for an ambiguous path or a stray inactive relationship, then a HARD gate probes
  a known measure against its control total *after* relating — catching the wrong cardinality / fan-out /
  dropped rows the way `import-table` catches a bad load.
- **Reframed** — `import-table` now folds in the control-total proof (a hard `dax_probe` that a source figure
  survived the load, for any table, fact or dimension); `optimize-dax` broadened to "rewrite for speed OR
  clarity" (absorbing the readability-rewrite job) while keeping its hard `dax_equivalence` gate against the
  recorded original.
- **Merged** — `author-complex-measure` → `verified-measure` (its benchmark step is now skippable, so one
  playbook serves both the correct-and-fast and correctness-only grades); `secure-and-attack-test-rls`'s
  adversarial attack-test gate → `secure-with-rls`; `pre-deploy-validation`'s RLS role-test → `deploy-to-production`;
  `cleanup-unused` → `model-hygiene-pass`; `time-intelligence-suite` → `refactor-to-calculation-group`;
  `refactor-measure` / `performance-tune` → `optimize-dax`.
- **Cut** — `add-fact-table` (its control-total gate moved to `import-table`), `add-dimension`,
  `author-simple-measures`, `measure-from-pattern`, `cohort-retention-family`.
- **Deferred (kept for IP, off the launch menu)** — `verify-measure`, `dimensional-design`, `composite-model-setup`,
  `document-model`, `adopt-source-control`, `field-parameters-setup`, `author-a-workflow` are parked in
  `Semanticus.Engine/workflows-parked/` (excluded from the csproj copy glob, so they ship neither to the binary
  nor to the parse-proof tests). Seed availability has no ship-hidden frontmatter flag — the `enabled` toggle is
  a per-project `workflow-settings.json` concept — so parking is the clean way to preserve them without shipping.

## [0.2.0] - 2026-07-08

The AI-native authoring wave: the four innovation features (health delta, Model Interview, number
time-machine, Explain This Number) and their fast-follows; Find & Replace P2/P3; Model-tree copy/paste
and measure folders; Properties-grid prefills and the measure Format-expression editor; user-authored
BPA + AI-readiness rules; the Ink/Signal rebrand across the extension. Benchmarked in the open (see
[semanticus.com.au/benchmarks](https://semanticus.com.au/benchmarks)).

### Added — Custom rule authoring: user-authored AI-readiness rules + the authoring UX for both rule kinds (2026-07-07)

Users (and their AI assistant) can now write their own rules for BOTH analyzers. FREE end-to-end
(authoring is content, the save_workflow/daxlib_install call); the existing enforcement gates are untouched.

- **Custom READINESS rules (the new engine capability)** — the BPA expression vocabulary (the SAME vendored
  Dynamic-LINQ engine + scope→collection map; never a second language) wearing readiness metadata: a target
  `ReadinessCategory` (one of the EXISTING categories, validated with the list), Severity, message, optional
  Description + advisory FixKind (`SafeFix` refused — that affordance stays deterministic/built-in). Storage
  mirrors BPA exactly: `load_readiness_rules` / `reset_readiness_rules` (5-file dual-drive; file / URL /
  inline JSON; persisted on the model via the `Semanticus_ReadinessRules` annotation; merge-by-id or replace;
  undoable; dry_run-denied like the load_bpa_rules family). Scoring honors the house conventions: optional
  `AppliesTo` = the population filter (Applicable = objects matching it; violations = Expression hits within
  it), an empty population is DORMANT (Applicable=0, never inflates its category — pinned by test), a
  built-in rule id can never be overridden (load refuses with teaching; a hand-edited annotation is skipped
  LOUDLY via the new `Scorecard.RuleErrors`), and custom rules can never register hard gates. Findings carry
  `custom:true` provenance (BPA violations from model-embedded rules likewise); waivers work on them unchanged.
- **`validate_rule` (agent door + the form's live check, one op for both kinds)** — compile through the REAL
  parser (errors point at the failing position) + an honest test-run against the open model: Applicable,
  violation count, first flagged objects, a dormant flag, and the BPA standard-id override warning. A preview
  only, nothing saved. `get_custom_rules` lists both kinds plus the category/scope vocabularies (the single
  source of truth behind the form's dropdowns).
- **Studio authoring UX (both tabs)** — a "Custom rules" panel on AI Readiness and BPA: manage list
  (edit/remove), a "New rule" form with 4 starter templates per kind (missing-property, naming pattern,
  format-strings-in-a-folder, and hide-key-columns / auto-fix), category dropdowns fed by the engine, LIVE
  validation as you type, and the labeled test-run preview before saving. Saving routes through the load ops
  (merge). "(custom rule)" provenance renders in both findings lists; readiness RuleErrors surface as a
  banner. "Ask the AI Assistant for help" copies a ready-to-paste authoring prompt (copy-prompt pattern).
- **Verification** — 10 new xUnit cases (round-trip + undo, AppliesTo scoring, the dormant/no-inflation pins,
  collision refusal, hand-edit fail-loud, custom-finding waiver, validate_rule teaching for both kinds, BPA
  provenance, dry_run denial); AirSmoke +9 checks (population-honest category math, waiver, dormancy, reset);
  McpSmoke +10 checks (agent-door round-trip incl. the didChange broadcast + collision refusal); uishot
  screenshots of both tabs incl. the open form + live preview.

### Added — Model tree Copy/Paste + duplicate_object grows a paste target (2026-07-07)

- **Copy/Paste in the Model tree (native VS Code view).** `Copy` (context menu, or `Ctrl+C`/`⌘C` with the
  tree focused) stashes an object REFERENCE (kind + ref) in extension state — never content; `Paste`
  (context menu, `Ctrl+V`/`⌘V`) duplicates via the engine's `duplicate_object`, so every paste rides the
  normal tracked-change chokepoint: one undoable step, `model/didChange` broadcast, visible in Edit
  History. Same-container paste lands a copy beside the original with the engine's collision-safe name; a
  **measure pasted onto another table** (or a calculation item onto another calculation group) is cloned
  there. Copyable kinds: measure, column, calculated column, hierarchy, table (incl. calculated tables /
  field parameters), calculation group, calculation item. The `semanticus.treeClipboard` context key gates
  the Paste menu per clipboard kind, so an incompatible target never offers Paste — and the Ctrl+V
  KEYBOARD path re-checks the same target matrix (a keybinding bypasses menu gating) and teaches on an
  unsupported selection instead of silently duplicating in place; the residual can't-express-in-when
  cases (a column/hierarchy pasted onto a foreign table) teach in plain English too, and the engine's
  teaching refusals surface as information, never behind a "failed" error. The Reference-Model-tree
  Ctrl+C → Ctrl+V flow is preserved — a reference copy lights the same Paste menu (dedicated 'reference'
  clipboard value) and with both clipboards loaded, the most recent copy wins. Single object v1 (no
  multi-select).
- **`duplicate_object` extended (5-file dual-drive, both doors).** New optional `targetRef` (the paste
  container): a `table:` ref clones a MEASURE onto another table, a calc group's `table:` ref clones a
  CALCULATION ITEM onto another group — cross-container copies are named against the TARGET (model-wide
  measure uniqueness + landing-table column collisions honoured; `Clone()` names against the source, so
  the copy is created under a throwaway name and renamed in the same undo batch). New kinds: calculation
  item, table / calculated table (field parameters keep their `ParameterMetadata` marker), calculation
  group (items included). Columns and hierarchies stay on their own table; tables duplicate at model
  scope — foreign targets refuse with teaching errors. Passing the source's own container collapses to
  the classic in-place duplicate. On the RPC door `targetRef` is APPENDED after `origin`, so the legacy
  3-arg positional shape (objRef, newName, origin) from an older extension bundle keeps binding origin
  correctly — additive wire evolution, `searchModelEx`-style; RpcSmoke pins BOTH shapes. Still excluded:
  roles, role members, perspectives, partitions, relationships, levels, functions, data sources.
- **Docs:** `docs/keyboard-shortcuts.md` + the Studio `?` cheat sheet updated (the Model-tree Ctrl+C/Ctrl+V
  rows), per the keep-in-sync rule.
- **Verified:** suite 739 green (15 new `DuplicateObjectTests`: cross-table naming incl. explicit names +
  landing-table column collisions, same-target collapse, table/field-parameter/calc-group clones, calc-item
  cross-group paste, every refusal path, ONE-undo revert with no temp-name residue); McpSmoke +4 checks
  (measure→other-table, explicit name, table at model scope, cross-table column refusal) PASS; RpcSmoke +6
  checks (targetRef over RPC, dual-drive broadcast, other-door read, undo, and the LEGACY 3-arg positional
  shape staying in-place with origin intact) PASS; `tsc --noEmit` clean.

### Added — Properties grid prefills + measure Format expression editor (2026-07-07)

- **Prefills — the grid offers real choices instead of blank text boxes.** `get_properties` now enriches
  its reflected descriptors (same payload over RPC and MCP, no new op): a `suggestions` side-channel on
  **Display Folder** (distinct folders in use — the object's table first, then model-wide, capped at 60)
  rendered as native autocomplete; the **Data Type** dropdown restricted to `set_column_data_type`'s
  allow-list (`SettableColumnDataTypes` — one source, so the pick list can't drift from what the engine
  parses; a current out-of-list value like Binary still shows), locked with a plain reason on calculated
  columns; and a **Format String picker** on measure/column rows — the curated template catalog's common
  entries (grouped by category, example output per entry), lazy-fetched once per session via
  `list_format_templates`. Free text stays available everywhere; the grid routes Data Type writes through
  `set_column_data_type` so refusals surface analyst-plain inline.
- **Format expression (dynamic format) editor for measures.** A dedicated `formatExpression` row shows
  whether a dynamic format exists (truncated monospace preview) with Edit expanding into a lightweight
  DAX editor — monospace textarea, insert-from-template picker (the catalog's dynamic entries), balanced
  quotes/parentheses check, Apply/Cancel. Apply writes through `set_measure_format_expression` (tracked:
  undo, `model/didChange`, Edit History); clearing restores the static format string honestly. Below
  CL 1601 the row states the compatibility requirement instead of a dead control.
- **Verified:** `Semanticus.Tests` 728/728 (4 new in `PropertyGridPrefillTests`); McpSmoke 197 PASS /
  RpcSmoke 69 PASS; `tsc` clean; uishot propgrid scenarios extended (`column`, `formatexpr`, `lowcl`)
  and all 7 screenshots reviewed.

### Added — Model Interview fast-follows: seeds, oracle hardening, the advisory deploy leg (2026-07-07)

The four items PR #84 deferred, each deliberately minimal and honest:

- **Probe-hardened value oracle (the ProBench lesson made kernel)** — interview value-tier grading now
  uses the HOUSE scoring convention (tools/probench/compare.py, pre-registered): BLANK, ERROR and VALUE
  are three distinct worlds (BLANK ≠ 0 in *both* directions — a produced blank against a numeric oracle
  is confidently wrong, and the literal `BLANK` sentinel records "the right answer is no value"), numbers
  match iff |got−want| ≤ max(1e-6 abs, 1e-9·|want| rel) — STRICTER at scale than the rewrite-equivalence
  band (a ±50 drift at 1e9 is now caught), and an erroring query stays Unverified (computes-cleanly is a
  precondition for "confidently wrong"). The no-oracle detail now SHOWS the cleanly-computed answer so the
  confirm-and-record flow has its number — shown, never auto-trusted (self-verification isn't verification;
  the oracle is). Matrix (per-group grid) cells grade under the same convention, blank sentinel included.
- **`list_interview_seeds`** (5-file pattern, both doors; FREE, read-only) — ready-made question candidates
  from two deterministic sources. (1) **Verified answers** (Prep-for-AI gap A closed read-only): the
  `VerifiedAnswers/definitions/<guid>/definition.json` files beside the model are PARSED, not just counted —
  defensively, since the schema is observed-not-documented (structural walk: trigger/question/prompt/phrase
  keyed strings → the question + alt phrasings; PBIR-shape `Measure`/`Column` field refs + bare `queryRef`s
  → target refs, both casings). A definition that yields no question is skipped WITH the reason and counted
  honestly. (2) The **built-in hard-question pack**: 12 model-agnostic templates (`docs/interview-hard-pack.json`,
  embedded like the fix map) distilled from the benchmark's trap FAMILIES — rank-with-ties, YTD under a text
  date attribute, rolling-12-month distinct count, prior full period, share of grand total, semi-additive
  closing, same-period-last-year, retention cohort, weighted average, inactive-relationship totals,
  blank-vs-zero, grand-total additivity — carrying NO bench text and NO gold values. Binding is deterministic
  bind-or-skip: a template instantiates only when every shape it needs exists (marked date table, a measure
  to target, a labelled dimension, an entity key, two plain numerics, an inactive relationship — each choice
  disclosed in `targets`), and every miss is a skip naming the exact missing shape. No candidate carries an
  oracle; the result teaches the confirm-then-`add_interview_question` flow (`seedSource` gains `hard-pack`).
  Already-saved questions come back as skips, never silent duplicates.
- **deploy_gate advisory interview leg** — `DeployGate` gains an `interview` block (null when no pack, so
  the existing shape is untouched) that replays the saved project pack with the run_interview machinery
  (offline = Unverified ceiling, never a fabricated pass) and reports PER-QUESTION OUTCOME DELTAS vs the
  last recorded outcomes. Advisory by contract: it never lands in `Pass`/`Blockers`, a broken store never
  breaks the gate, refusal-tier questions are counted not-replayable (they grade the assistant, not the
  model — the card's "Ask all" precedent), and offline outcomes are NOT recorded (a connectivity gap must
  not stomp the last real evidence). The Deploy tab renders it as a plain informational line under the gate chip.
- **Studio card** — seeded questions carry their provenance in plain words ("From a verified answer" /
  "Built-in hard question") and a hint line shows how many ready-made questions could be added; the copy
  keeps the every-number-is-confirmed-with-you promise explicit.
- **Verification** — `dotnet test` 729/729 green (17 new: BLANK≠0 both directions incl. matrix cells, the
  stricter-at-scale tolerance pinned against the old band, error≠wrong, the confirm-flow detail, VA parse
  fail-soft over observed + nested + corrupt shapes, pack binds-all/skips-all/named-measure/no-leftover-
  placeholder/no-bench-text, seeds op dedup + no-model teaching, advisory never-blocks with byte-identical
  Pass/Blockers + delta + offline-not-recorded); McpSmoke PASS (new seeds leg over the cross-process door,
  Prep-for-AI anchors created + removed so the vendored tree stays clean); RpcSmoke PASS; webview rebuilt +
  `tsc --noEmit` clean; uishot AI Readiness + Deploy (gate clicked) screenshots reviewed.

### Added — Find & Replace P2/P3: preview-before-apply + bulk replace as a Change Plan (2026-07-07)

- **P2 finish — preview-before-apply on the one-by-one replace (free).** `replace_in_object` gains
  `preview=true`: the engine rehearses the exact change (before → after, warnings, and — for a rename —
  the `references` blast radius counted off the FormulaFixup dependency graph) with NO mutation; a safety
  refusal lands in `.blocked` (with the "rename the object instead" hint) instead of a throw. The Search
  tab's per-row **Replace** now opens an inline preview panel (diff windowed to the changed part, the
  "Also updates N places…" line, amber M-breakage warnings) with Apply/Cancel — nothing changes until
  Apply, and each apply stays one undoable step. Search query/toggles now persist across tab switches.
- **P3 — bulk find & replace as a Change Plan (`propose_replace`, 5-file pattern, both doors).** Runs the
  detailed search and emits the RIGHT KIND of item per MatchClass: `rename` (opt-in "proposed"; fixup
  rewrites every DAX/RLS reference at apply, renames apply last), `set_description` / `set_display_folder`
  / `set_measure_format` / `set_format_string` / `set_synonyms` (pre-approved plain text), `set_dax`
  (literal/comment spans spliced SPAN-WISE so reference spans are never touched; parse-validated, opt-in
  because it changes results, never equivalence-gated), `set_m` (opt-in literal M edit with the loud
  not-reference-fixed warning). **DAX-reference / DAX-code / RLS matches yield NO items** — the plan-level
  `note` reports them honestly (`ChangePlanView.Note`, rendered as a banner on the Change Plan tab).
  Collisions / emptying replacements surface as `skipped` items with reasons. The Pro line is the
  EXISTING `apply_plan` >1-item gate — no new gate; free builds/reviews the plan and applies one item at
  a time; the badged atomic apply buttons and the entitlement-refusal upsell were already on the tab.
  New plan-item kinds wired into `ApplyOneItem` (+ `set_dax` now covers calculation items, and
  `set_description` falls back to the reflection seam for any describable object). `propose_replace`
  joined the dry_run deny list beside `propose_plan` (plan-state writer — a rehearsal would persist it).
- **UI:** Search tab **Replace all…** → builds the plan → hands off to the Change Plan tab for
  review/apply ("Plans every match of this search… nothing changes until you apply"). New `m` risk badge
  ("M edit") on plan rows.
- **Verified:** suite 688 green (9 new `ReplacePlanTests`: item-kind routing, reference exclusion at plan
  scale, collision/empty skips, free-vs-Pro gate at the apply chokepoint, Pro bulk apply + ONE-undo
  revert incl. fixup rewrite, preview no-mutate + blocked honesty, set_m apply/undo on a CL1500 fixture);
  McpSmoke +5 replace/plan legs; RpcSmoke PASS; webview rebuilt; uishot Search PNGs (results, preview
  panel, plan hand-off with note banner) reviewed.

### Added — Explain This Number: the deterministic cell dossier + "why is this blank?" (2026-07-07)

- **`explain_value`** (5-file pattern, both doors; **FREE**, read-only) — a deterministic evidence dossier
  for ONE cell of ONE measure: the value re-derived in the cell's EXACT filter context (the probe_measure
  `ProbeFilter`/TREATAS vocabulary + the DAX Lab CALCULATETABLE predicate wrap, so the explained number IS
  the visual's number), the dependency chain (BFS over DependsOn, capped), source lineage per leaf column,
  and an RLS advisory (roles that WOULD filter the dep tables — the engine cannot impersonate, so never
  "computed under role X"). Offline degrades to the metadata-only dossier (`Evidence.Available=false`,
  the summary says the value wasn't computed) — never a throw, never a gate.
- **The "why is this blank?" checklist (the ship-first slice)** — when the cell IS blank, a deterministic
  checklist runs: *filters remove every row* (empirical: drop-one-filter probes name the culprit filter),
  *no relationship path* / *filter flows the wrong way (single direction)* / *inactive relationship*
  (propagation-aware BFS over the model graph honoring One→Many flow + BothDirections), *blank by design*
  (grand-total probe + DIVIDE/ISBLANK/BLANK body scan), and *row security could hide rows* (advisory-only,
  says so). The strongest PROVEN signal is named the likely cause; empirical proof outranks structural
  suspicion.
- **Contributor decomposition behind THE NON-ADDITIVE GUARD** — top-K parts split by a related dimension
  (caller's `decomposeBy`, else a deterministic pick one active hop from the data tables), shipped ONLY
  when Σ(parts) provably equals the cell's value (the DaxBench tolerance band). Distinct counts / ratios /
  MIN-MAX / semi-additive measures get an explicit plain-language refusal with EMPTY rows — never a
  misleading sum-of-parts list; a truncated member scan (>5000) also refuses honestly. Includes a
  synthetic "(everything else, N more)" remainder row so the shown parts always reconcile to the total.
- **Studio UI: right-click a value → the Explain slide-over** — DAX Lab matrix cells, the table viz, and
  the workbench Result grid (new opt-in `onCellMenu` on `ResultGrid`, mirroring `onColumnMenu`; passes the
  view row's VALUES, not an index, so client-side sort/filter can't misattribute). The panel renders the
  dossier in analyst-plain sections (Your selection / Why is this blank? / What makes it up / What feeds
  it / Where the data comes from / Row security / How this was checked) and a **"Ask the AI Assistant to
  explain"** button that copies a ready-to-paste `explain_value` prompt — the engine assembles evidence,
  the UI never narrates. Third transport: an `explain_value` audit record + rich activity event with an
  Edit History evidence renderer (weld pattern), recorded only when a value was actually observed.
- **Verified:** suite 679 → 702 green (`ExplainValueTests`: reachability verdicts incl. direction-blocked
  and inactive-only, non-additive guard on distinct-count/ratio/truncated shapes, tolerance band, RLS
  intersection, blank-by-design scan, offline degradation, bare-name resolution, error recovery text);
  McpSmoke + RpcSmoke explain legs prove both doors; webview build + tsc clean; uishot DAX Lab PNGs
  (rich dossier + blank checklist) captured via the new `UISHOT_CTXSEL`/`UISHOT_STATE`/`UISHOT_EXPLAIN`
  hooks and reviewed.
- **Review fixes (PR #92 bots):** (A) the cell is now re-derived in the VISUAL'S evaluation context — the
  caller's `GroupBy` axis columns become SUMMARIZECOLUMNS group-by args (scope-sensitive DAX like
  ISINSCOPE resolves as the visual saw it) with the clicked coordinates pinned by single-member filters;
  row selection is explicit (`PickCellValue`: one row = the cell, zero rows with an axis = honest blank,
  >1 rows = refuse with pin-the-cell guidance) and the blank-checklist probes + contributor decomposition
  run in the SAME axis-preserving context (a scope-branching measure can't get a wrong diagnosis or a
  fake breakdown); the UI payloads now pass the axis through. Failing-first regression test proves the
  old scalar shape is rejected. (B) right-clicking a row/column group cell no longer misattributes to the
  first Values measure (`explainPayloadFromRow` returns null off value columns). (C) `ResultGrid` only
  suppresses the default context menu when the cell actually produced an explanation (boolean-return
  `onCellMenu` contract); label cells keep the browser menu and lose the misleading hover hint.

### Added — the number time-machine: ambient vital signs + "What moved this number?" (2026-07-07, PR #86)

- **Ambient vital-signs capture (Pro, silent skip on free, host-attached like the ExperienceTee)** — at
  checkpoint moments (the Verified-Edits audit chokepoint: `apply_plan` / `optimize_measure` / committed
  `deploy_live`, plus an explicit `save_model` hook — save is not a tracked mutation) the engine appends one
  record to `.semanticus/baselines/vitals.jsonl`: the top-25 measures (report-usage rank when sibling PBIP
  reports exist, dependency-centrality fallback) with their whole DAX dependency-cone EXPRESSION snapshots
  (metadata — captured even offline) and, when live, observed VALUES (grand total + up to 3
  dominant-dimension cells) only for measures the edit's impact cone reaches. Retention: last 200
  checkpoints / 20MB, oldest pruned first with the prune stamped on the record that caused it; suppressed
  under `DryRunScope`; never throws.
- **`blame_value` / `list_value_history`** (5-file pattern, both doors; SOFT Pro gate — free gets
  `Status="pro"` + a plain invitation naming the free manual capture/compare path, never a throw). Verdict
  honesty is non-negotiable: single-edit window = `attributed`; multi-edit = `interval` with candidates
  RANKED by dependency overlap (formula-changing edits first) and explicitly never a causal claim;
  identical formulas + a moved value = `data-suspected` (proof needs the deferred shadow pre-edit instance
  — the note says so); thin history = `inconclusive`. Formula causes are proven by a deterministic
  expression diff of the cone between the two endpoints; untracked edits inside the window are counted and
  disclosed. Each real analysis emits the shared evidence pair (audit record + `Kind="blame_value"`
  activity), the health-delta precedent.
- **Edit History UI: the "What moved this number?" panel + sparkline** — a new rich-evidence renderer
  (plain sentences; the UI never says "blame"/"checkpoint"/"interval"/"vital-signs"): before → after,
  ranked window edits with one-click **Show this edit** (scrolls/flashes the timeline row where "Undo to
  here" lives), the changed formula's before/after, and "This number over time" fed by
  `list_value_history` (null points are honest gaps; the free tier sees the invitation).
- **Verified:** suite 613 → 635 green (`ValueBlameTests`: verdicts, cone-skip, ranking order, store
  append/prune/corrupt-line honesty, free no-throw + no ambient writes, dry-run, offline expr-only);
  McpSmoke + RpcSmoke legs prove both doors; webview build + tsc clean; uishot Edit History PNG reviewed.

### Added — the Model Interview (behavioral readiness, 2026-07-07)

- **The Model Interview** (docs/product-innovation-brainstorm.md §1) — "interview your model before your
  users do": a deterministic question bank the ENGINE executes, compares, and scores (golden rule 1 —
  the user's Claude authors NL questions + DAX attempts via the new **`/interview-model`** skill; the
  engine never infers). Three tiers: **value** (a full EVALUATE vs a trusted number/row-set), **paraphrase**
  (the same question asked two ways must agree — silent-wrong detection with no ground truth), **refusal**
  (declining IS the pass). Outcomes `Correct | Refused | SilentlyWrong | Unverified` with a HIGH-PRECISION
  honesty ladder: offline / erroring query / truncated / zero-rows / missing-oracle are all Unverified —
  never a fabricated pass, never a fabricated "confidently wrong".
- **Store** — `.semanticus/interview/questions.jsonl` (+ `~/.semanticus/interview/` global), the
  KnowledgeStore delta kernel cloned: append-only, replay-materialized, corrupt-line-skip, 64KB cap; run
  outcomes persist as `record-run` deltas so the Studio card is useful with no agent attached. A `seedSource`
  field ('user' | 'claude' | 'verified-answer') pre-seeds the later verified-answers extraction + hard-pack lanes.
- **Ops (dual-drive)** — `list_interview_questions` (free) · `run_interview` (free, one-off inline or saved;
  refusal tier grades fully offline) · `delete_interview_question` (free) · **`add_interview_question` (Pro** —
  persistence is the gate; the refusal names the free alternative). New `interview_replay` workflow verify kind:
  empty pack FAILS instructively, offline SKIPS honestly, any SilentlyWrong fails the gate, the tally is disclosed.
- **Failure→fix as DATA** — `docs/interview-fix-map.json` ({tier, outcome} → readiness rule id + a plain hint),
  embedded like format-templates.json; the author's `fixRuleId` wins, and a test pins every mapped id to the real
  ruleset. **Studio** — an Interview card on the AI Readiness tab (plain labels only: Right · Safely said it
  couldn't answer · **Confidently wrong** · Couldn't check), free per-question replay. Verified: xUnit 623
  (21 new `InterviewTests`), McpSmoke (4-op leg, offline-honesty asserted) + RpcSmoke green, uishot reviewed.

### Added — "Remove all safe-to-remove": the Lineage sweep's act half (2026-07-07)

- **`remove_safe_objects(refs?, reportPaths?)`** — the Measure-Killer tab finally gets its delete. The engine
  RECOMPUTES the safe set server-side and RE-VERIFIES every item at apply time, inside the single-writer
  mutation delegate — an item whose tri-state verdict went stale since the caller's scan (something now
  references it, a report uses it) is SKIPPED with a plain-English reason, never deleted stale. The still-safe
  set is deleted as ONE tracked, undoable transaction (normal MutateAsync path — undo timeline + `model/didChange`,
  no raw TOM bypass); `reportPaths` makes the at-apply verification report-aware via the same PBIR parser as
  `analyze_reports`. Returns `removed[] / skipped[{ref,reason}] / count` + the verification basis; writes ONE
  append-only `VerifiedEditRecord` (verdict `batch`) carrying the removed/skipped evidence. Dual-drive (MCP tool +
  RPC method) and dry-runnable: `dry_run(remove_safe_objects)` rehearses the whole sweep — including the would-be
  removed/skipped report — and leaves no mutation, no audit record, no activity event.
- **Gate (practical-merit shape):** >1 item = Pro, thrown before any mutation with the free path named
  ("Each item can be deleted one at a time free; Pro removes all N verified-safe items in one undoable step");
  a single-item call stays free — the same per-item primitive `delete_object` offers.
- **Lineage tab UI** — a free per-row **delete** (verdict-aware confirm, undoable) on every removal candidate,
  closing the detect-with-no-act gap on the tab itself; a **"Remove all N safe to remove"** header button badged
  Pro via the shared `pro.tsx` kit (free click = teaching UpsellNotice, never a raw exception); the confirm states
  plainly "This removes N items that nothing depends on. You can undo it."; the result surfaces removed/skipped
  counts with per-item reasons. The reports pane threads its local PBIR paths into the sweep so the re-check is
  report-aware there too. New xUnit `RemoveSafeObjectsTests` (7 cases: atomic sweep + one-undo restore,
  stale-verdict skip, free single/bulk gate with model-intact refusal, single audit record with evidence,
  dry-run leaves nothing, no-refs full-sweep accounting); suite 602 → 609; McpSmoke + RpcSmoke green.

### Added — §9c op→workflow binding (mandatory routing, 2026-07-06)

- **`set_workflow_binding(op, require, mode)`** — the "Required for…" control, the third orthogonal workflow
  axis (availability · REQUIRED · strictness). `hard` REFUSES the bare authoring op with a plain-language
  teaching error naming the op, the required workflow set, `start_workflow` and `get_workflow_policy` (never
  "binding violation", §9.17/§10.12); `warn` allows the edit but publishes a `landed_outside_required_workflow`
  advisory (in warn mode the audit record IS the enforcement, §9.6); `off`/empty clears and prunes. Persisted in
  the git-tracked `.semanticus/workflow-settings.json` and re-broadcast to both doors. **Pro** for hard/warn
  (mandatory routing is the enforcement the moat sells); reading and clearing are free.
- **Enforced at six dual-drive authoring chokepoints** — the deliberate v1 bindable-op set (an op that isn't
  wired here cannot be bound, honest by design per §9.6): `create_measure`, `update_measure`,
  `create_calculated_column`, `create_calculation_item`, `create_table`, `create_relationship`.
- **The §9.10 A/B/C decisions, built to:** (A) the exemption is STEP-scoped — a bound op is allowed only while
  an active run of a required workflow is AT a step whose declared `ops:` perform it (closes the
  start-and-freestyle hole); (B) enforcement keys on `bindings.<op>.mode` alone, so the global strictness
  kill-switch never silently voids a mandate; (C) a `userDisablable:false` binding is locked against the agent
  door (a reviewed file edit still governs it — we do not police the file itself).
- **DryRunScope-exempt** — a `dry_run` (and `replay_check_workflow`'s admission rehearsals) of a bound op is a
  rehearsal, not a landing, so the mandate stands down and admission replays never break; `set_workflow_binding`
  itself joins the dry-run deny-list (it writes sidecar policy, not a model-definition edit).
- **`get_workflow_policy()`** — a free, token-lean read of the whole project policy (global enforcement mode +
  per-workflow availability/gated/whenToUse/required-for-ops + the raw bindings) so Claude self-routes from the
  orientation primer instead of discovering mandates by rejection (§9.12). New xUnit `WorkflowBindingTests`
  (9 cases); suite 523 → 532; McpSmoke (32 workflows) and LearnSmoke unchanged (default settings carry no bindings).

### Added — workflow customisation wave + the learning-loop proof (2026-07-05/06, PRs #61–#64)

- **Learning-loop test suite (PR #61)** — the three §10 bars of `docs/learning-loop-testing.md`, all green:
  `Semanticus.LearnSmoke` (L0→L2 wiring over the MCP door, 14/14), the two safety benchmarks as xUnit
  ship-blockers (MemoryGraft poisoning capture **0** at every injection fraction + one-op purge to 0;
  skill-debt admission 2/2 genuine kept, 0/4 degenerate admitted, net **+2**), and `Semanticus.LearnBench`
  (A/B efficacy: memory-ON 100% vs OFF 0% first-submission gate-pass, +100pp; returns nonzero without
  positive lift). T4 (real-LLM judgment harness) remains. Internal synopsis grew Docs/Benchmarks tabs.
- **§9a/9b workflow customisation (PR #62)** — `set_workflow_enabled` (off-the-menu-but-listed, teaching
  refusal, merge-and-prune settings writes) + `whenToUse` routing on the library; the **graded
  measure-authoring family** (verified-measure · author-complex-measure · author-simple-measures ·
  refactor-measure · measure-from-pattern · time-intelligence-variants); **`workflow_admissible`** (the
  sixth verify kind: a hard gate that runs real `check_workflow` admission on a just-authored workflow) +
  **`author-a-workflow`** (the meta-workflow); stock library **25 → 32**, all admission-clean.
- **Review-fix hardening (PR #63)** — the YTD leg of time-intelligence-variants now hard-proven in its own
  step (it was collected but never verified); probe-requiring verify kinds with no `probe:` warn at
  admission instead of failing at run time; resource-disposal + null-guard sweep across the new tests and
  smokes. Suite 523/523.
- **§10 design APPROVED (PR #64)** — workflow **templates** (slots, structure-preserving instantiation,
  admission-gated) + **dynamic activation** (rules on the §9.11 predicate grammar, profiles-as-scenarios,
  mandate-safety) + the §10.12 analyst-to-pro-dev UX bar. Design only; build order 9c → 10-T1…T4.

### Changed — 0.1.1 launch build: the production signing key + US$79 founder pricing (2026-07-04)
- **Production keypair minted** (Kane, on his machine; private half in his password manager + the Worker
  secret). `LicenseEntitlement.EmbeddedPublicKey` rotated to its public half — free, since the pre-launch
  key never issued a license. Tokens from the deployed fulfillment Worker validate against THIS build;
  the marketplace 0.1.0 must be superseded by 0.1.1 before the first sale.
- **Founder price US$49 → US$79/yr** (Kane's call pre-launch; monthly stays US$10). Same Paddle price id —
  Kane edited the amount in place — so the site JS and the Worker's PRICE_ANNUAL var needed no id change.
  Store README + storyboard captions updated; site swept in the semanticus-site repo.
- Extension version → **0.1.1**; fulfillment `wrangler.toml` now carries the real KV namespace id (an
  identifier, not a secret — the deploy runbook stays reproducible).

### Added — automated Pro fulfillment: the Cloudflare Worker + a `verify` command on the minter (2026-07-04)
- **Context:** semanticus.com.au went live with a WORKING Paddle checkout today — every sale now needs a
  license token delivered. This lands the automation (Kane ratified: full auto, signing key as a Cloudflare
  Worker secret; the engine still ships only the public key and verifies offline — golden rule #1 untouched).
- **`tools/fulfillment/worker/`** — a zero-dependency Cloudflare Worker: verifies the Paddle Billing webhook
  HMAC (raw-body, replay-windowed, constant-time), mints the Pro token in WebCrypto (ECDSA P-256, byte-identical
  wire format to `LicenseVerifier`), emails it to the buyer via Resend (owner BCC), and records every sale in
  KV. Never-lose-a-sale posture: mint + record always happen; without Resend configured tokens queue as
  `pending-delivery` on an ADMIN_KEY-protected `GET /pending` (the launch-week manual mode). Idempotent on
  Paddle's retried/duplicated `transaction.completed`. Token expiry derives from the transaction's billing
  period, else the known price id (annual/monthly), else a conservative +1 month flagged for review.
- **`Semanticus.License verify --pub … --token …`** — the CLI grows the verifier side (engine code path,
  distinguishes INVALID from EXPIRED), so out-of-process minters can prove format compatibility.
- **Verification:** `worker/selftest.mjs` — cross-language proof: node-minted token PASSES the real .NET
  verifier, .NET-minted token passes the node verifier, a tampered token FAILS, and the webhook HMAC matches
  a node-crypto oracle (7/7). `dotnet test --filter Entitlement` 28/28. Deploy runbook in the worker README.

### Added — the workflow-ENFORCEMENT toggle: turn gated runs off for quick tasks (2026-07-04)
- **Kane's T5 ("a button for enabling and disabling pro mode"):** a model-wide enforcement kill-switch over
  workflow gates. New top-level `"strictness"` in `.semanticus/workflow-settings.json`; it tops the WHOLE
  resolution — `global ?? per-gate ?? per-workflow settings ?? frontmatter ?? hard` — deliberately above the
  stock seeds' per-gate `hard` (per-gate hardness stops an agent sidestepping a step; the toggle is the
  accountable owner opting out wholesale; anything weaker would be a dead switch).
- **Semantics when OFF:** every gate is skipped with the honest per-step note ("gate skipped (strictness
  off)"), runs record no verified evidence, `Gated` flags flip off in the library, and gated workflows start
  FREE (the entitlement chokepoint already keys on "does anything enforce"). Runs freeze the mode at start —
  a mid-run toggle can't tear a run.
- **Dual-drive:** new ops `get_workflow_enforcement` / `set_workflow_enforcement` (MCP, teaching descriptions
  incl. "ask the user before turning enforcement off") + `getWorkflowEnforcement`/`setWorkflowEnforcement`
  (RPC). The set merges the settings file non-destructively (per-workflow overrides survive), re-broadcasts
  the library so both doors see cards flip, and emits an activity event.
- **UI:** an "Enforcement on/off" switch in the Workflows library rail + a loud amber banner (with a
  "Turn enforcement on" button) across the main pane while off. help.tsx gains the kill-switch section +
  a "Where do I…?" entry (the guide-accuracy contract).
- **Verification:** 4 new tests (`WorkflowEnforcementTests`): resolution precedence incl. global-beats-per-gate-hard;
  off ⇒ un-gated list + free start + honest skip note + restore; settings merge + junk-mode refusal; mid-run
  freeze → **497/497**; McpSmoke PASS; both toggle states uishot-verified (new `UISHOT_WF=enfoff` harness state).

### Fixed — Docs preview navigation + Print/PDF, Data Agent tenant identity (2026-07-04, Kane's live reports)
- **Docs: clicking any TOC link blanked the preview.** The generated doc renders in a sandboxed `srcDoc`
  iframe; a fragment link navigated the frame away from its srcdoc → blank. The PARENT now intercepts clicks
  on the iframe document (allow-same-origin): fragments scroll in place, http(s) links open in the system
  browser via the host, anything else is refused — the frame can never navigate away.
- **Docs: "Print / PDF" silently did nothing** — `window.print()` is suppressed inside VS Code's webview
  host. The button now sends the rendered HTML to the host, which writes a temp file and opens it in the
  SYSTEM browser: Ctrl+P / "Save as PDF" work properly there. New `printDoc`/`openExternal` webview→host
  messages (openExternal is http/https-only).
- **Data Agent: workspaces from the wrong tenant.** The tab hard-coded `azcli` auth — the az CLI can be
  signed into a DIFFERENT tenant than the model's XMLA session (hit live: client-tenant workspaces against a
  nexwave-bound model). The header gains a persisted auth-mode picker (az cli / Entra interactive / device
  code / service principal) + a tenant field (non-azcli), threaded through EVERY Fabric call on the tab
  (list/get/create/update/publish/delete). Help guide updated. (The Deploy tab's Fabric panels still assume
  azcli — follow-up.)

### Changed — Help is now the USER GUIDE: detailed per-tab guides + a "Where do I…?" task index (2026-07-04)
- **The '?' slide-over grew from ~4 bullets to a real per-tab guide** (`help.tsx` rewritten): every tab now
  carries titled sections (concepts, step-by-step tasks naming the ACTUAL buttons, gotchas), an explicit
  **Pro note** where something is gated, a tip, and clickable **"Looking for…"** cross-links that jump tabs.
  The three previously-undocumented surfaces — **Workflows, Knowledge, Edit History** — get full guides
  (gates/strictness/designer; insights/learned-workflows/recall/purge; timeline + hash-chained audit trail).
- **NEW "Where do I…?" view** (toggle in the panel header): a searchable task → location index answering the
  discoverability problem head-on — measure authoring, descriptions, format strings, renames, relationships,
  bulk scripts etc. live in the **Model tree / Properties view / command palette**, not Studio, and users look
  in Studio first. ~35 tasks in five groups (Author · Build & analyze · Improve & ship · AI assistant & safety ·
  Setup), each saying exactly where and how; Studio-answerable entries are clickable jumps.
- **Accuracy by extraction, not memory:** content was authored against fact sheets extracted from every tab
  component + `package.json`/`extension.ts` (exact menu titles, empty states, Pro gate texts, caveats) in the
  same PR window — the header comment makes the contract explicit: change a tab's controls, change its entry.
- Verified with the uishot harness (help panel open on Workflows/Diagram; the Where-do-I view with both
  click hooks) — self-reviewed PNGs; webview rebuilt and committed.

### Fixed — MCP-door harness fixes from the first full live-model session (2026-07-04)
Four real failures hit while operating a LIVE Fabric model end-to-end over the MCP door, each fixed with a test:
- **The deploy gate no longer counts WAIVED BPA violations as blockers** (`LocalEngine.DeployGateAsync`): it
  reported `bpaBlocking=19` with 0 active violations, forcing an audited override on a fully-waived model —
  defeating the waiver lane. Waived error-severity findings now drop out of `bpaBlocking` and surface on the new
  `DeployGate.BpaWaivedBlocking` + a Note remark (honesty doctrine: excluded, never hidden). Readiness hard-gates
  stay RAW by design — physical floors can't be accepted past.
- **The MCP tool boundary surfaces the engine's real (scrubbed) error** (`McpErrorBoundary`, new): the MCP SDK
  swallows non-`McpException` tool failures into a bare "An error occurred invoking 'X'." — which is exactly how
  the deploy gate's teaching refusal became invisible and cost a diagnosis session. A `CallToolFilter` (registered
  via `WithRequestFilters` in `Program.Mcp`) now catches inside the SDK's swallow point, unwraps to the root cause,
  scrubs secrets with the same `FabricRest.Scrub` the Fabric lane uses, and returns
  `"<tool> failed: <teaching message>"`. Cancellation/protocol exceptions still propagate to the SDK untouched.
- **The attached MCP proxy survives an owner-engine restart** (`RemoteEngine.ResilientRpc`): the UI has an
  engine-restart button, and a restart used to permanently kill every subsequent MCP call until a manual
  `/mcp` reconnect. All ~230 proxy methods now funnel through one chokepoint that, on a LOST connection (never on
  a server-side error), re-reads `.semanticus/engine.json`, re-attaches to the new owner, and retries once; if the
  retried call then fails on the fresh session, the error says the engine restarted and how to recover.
- **`get_object` on a table answers authored-metadata questions** — it returned only measure/column counts, so
  "does this table have a description?" was unanswerable through the op (hit live; worked around with huge
  `get_properties` payloads). Tables now carry description + isHidden; columns add description, formatString,
  summarizeBy, dataCategory, isKey, sortByColumn; other kinds fall back to `IDescriptionObject`.
- **NEW `get_ai_instructions`** (dual-drive: MCP tool + `getAiInstructions` RPC): the Prep-for-AI writers had NO
  reader — recovering the current text live took an `INFO.LINGUISTICMETADATA()` DMV query and hand-parsing 549KB
  of LSDL. Returns text/length/limit/culture with teaching notes for the absent cases.
- **Verification:** 8 new tests in `Semanticus.Tests/HarnessFixTests.cs` (gate-delta w/ rule-level waive, boundary
  teaching/unwrap/scrub/rethrow, live owner-restart reattach over real pipes, get_object shape, reader round-trip)
  → **493/493**; Smoke + RpcSmoke + McpSmoke + AirSmoke all PASS.

### Changed — "Power Query" is no longer OUR feature name — the language is M (2026-07-03)
- **Naming rule (Kane):** "Power Query" is Microsoft's product, so Semanticus never names its own tab / lane /
  feature that. The Build tab is **M Code** (nav label unchanged), the second lane is **M query**, and the
  language is **M** ("M expression", "M code"). Swept every UI string, MCP tool description, engine message,
  command title, help text, and current doc (`grep -ri "power query\|powerquery"`, excluding
  `node_modules`/`bin`/`obj`/`external`/`out`/`docs/archive`).
- **Internal identifiers renamed** so the word can't leak back: `webview/src/powerquery.tsx` → `mcode.tsx`
  (`PowerQueryView` → `MCodeView`), the Studio tab id `'powerquery'` → `'mcode'` (App.tsx / extension.ts /
  help.tsx), and the lazy code-split chunk `studio-powerquery.js` → `studio-mcode.js` (derives from the module
  name; the old chunk is removed from `media/studio`).
- **Kept nominative** (Microsoft's own product / literal identifiers, per the rule): `@microsoft/powerquery-*`
  npm package names, the `vscode-powerquery` upstream, the `UsePowerQueryPartitionsByDefault` TOM property, the
  "Power Query for Excel" public-client app name, and doc sentences describing Microsoft's Power Query editor.

### Changed — TOM/AMO bump 19.112.0 → 19.114.0 (calendars lane unblocked) (2026-07-03)
- **Bumped the pinned TOM/AMO from `19.112.0` to `19.114.0`** at every pin site (the [tom-bump-gate.md](docs/tom-bump-gate.md)
  inventory): `Semanticus.Core` `<TOMNugetVersion>`, `Semanticus.Engine`'s `Microsoft.AnalysisServices` +
  `Microsoft.AnalysisServices.AdomdClient` pair, and the `TomVersionPinTests` runtime guard (now asserts 19.114.x).
  The vendored TE2 config under `external/TabularEditor/**` is **left at 19.112 on purpose** — it is a pinned git
  submodule kept pristine and is never compiled by our build (Core resolves TOM from its own `PackageReference`,
  not the submodule's HintPaths), so the literal there is inert; re-pinning the donor is a separate Kane-level
  submodule change. **Why:** the calendars redesign
  ([docs/calendars-redesign-plan.md](docs/calendars-redesign-plan.md) step 0) needs the TOM `Calendar` /
  `CalendarColumnGroup` / `TimeUnitColumnAssociation` / `TimeRelatedColumnGroup` classes + `Table.Calendars` +
  the `TimeUnit` enum, which ship in **19.114+** (19.112 lacked them).
- **New test** `Semanticus.Tests/TomCalendarAvailabilityTests.cs` (2 cases): (a) the types + `Table.Calendars`
  resolve at runtime and the `TimeUnit` enum carries Date/Year; (b) a `Calendar` with a
  `TimeUnitColumnAssociation(TimeUnit.Date)` on a date column **survives a TMDL folder round-trip** through TOM's
  own `TmdlSerializer` (serialize→deserialize; the primary-column reference re-resolves).
- **Verified (Windows, `-c Verify`):** Core/Analysis/Engine build 0-error — the compiled-in TOMWrapper sources
  need **no** source change against 19.114; `dotnet test Semanticus.Tests` **386/386** green; all four smokes
  (Smoke / RpcSmoke / McpSmoke / AirSmoke) **PASS**. Not re-run on Linux/CI and the FormulaFixup old-vs-new
  differential (gate items 2–3) was not executed in this pass — owed on the PR before merge.

### Added — AI-Readiness catalog batch 2: 7 deterministic rules (2026-07-03)
- **7 new deterministic AI-readiness rules** in `Semanticus.Analysis/ReadinessRules.cs`, all over metadata TOM
  already exposes (no new live/DMV/report-layer/Prep-for-AI-store dependency), closing rows of
  [`docs/ai-readiness-catalog.json`](docs/ai-readiness-catalog.json) (`covered` 21→28): `LIMIT-QNA-INDEX` (over the
  Q&A 1,000-entity index ceiling — tables+columns+measures; scored, fires only when breached), `DATE-AMBIGUOUS`
  (a non-date-marked table exposing ≥2 visible *event* date columns — Order/Ship/Due — with Start/End validity
  *range* pairs stripped so they don't false-fire), `REL-HIERARCHY-SINGLE-LEVEL` (a hierarchy with only one level —
  no drill path), `NAME-HIERARCHY` (cryptic hierarchy name — extends the naming rules to hierarchies), and three
  **Applicable=0 advisories** that surface a finding but never score: `LIMIT-DATAAGENT-TABLES` (>25 visible tables —
  scope the data-agent source), `DAC-PERSPECTIVE-NOT-SCOPE` (perspectives aren't the Copilot scoping mechanism — the
  AI data schema is), `DAC-FIELD-PARAM-COMPLEXITY` (field-parameter disconnected tables add Copilot complexity). No
  new `ReadinessCategory` and no gate/weight change. Scoring rules use **presence design** (dormant-or-dock, never an
  always-pass 100). Every finding message names the fixing op (`set_ai_data_schema` / `mark_date_table` /
  `create_hierarchy` / `rename_object` / `unused_objects`) per the harness tool-result contract.
- **Precision-tuned against AdventureWorks:** DATE-AMBIGUOUS initially fired on Product/Promotion (Start/End validity
  ranges) — tuned by stripping range-boundary date columns (start/begin/end/finish/expiry/effective/thru), so it now
  fires only on the 4 genuine role-playing-date tables (Internet/Reseller Sales, Customer, Employee). The rest are
  dormant on AW except `DAC-PERSPECTIVE-NOT-SCOPE` (AW has 3 perspectives — a correct advisory, Applicable=0).
- **Tests:** `Semanticus.Tests/ReadinessCatalogBatch2Tests.cs` (9 cases — violating + conforming per rule, incl. the
  Start/End precision guard and a clean-model dormancy/anti-inflation guard). Verified: `dotnet build
  Semanticus.Analysis -c Verify` 0 errors; `dotnet test Semanticus.Tests -c Verify` green; `dotnet run --project
  Semanticus.AirSmoke -c Verify` PASS (with batch-2 true-positive + precision assertions added to the smoke).
- **Rejected as not offline-deterministic** (added to the catalog `rejected[]`): *"Mark the model 'Approved for
  Copilot'"* (a service-side item flag, not in the model file) and *"Measures must be in a valid error-free state"*
  (measure calc-error state needs a live engine recalc — not in offline metadata). Deferred pending reader/LSDL infra
  (not rejected — deterministic in principle): verified-answer hidden-field / trigger-count checks (the reader exposes
  only the answer count), AI-data-schema dependency-closure, and Row-label presence.

### Added — AI-Readiness catalog batch 1: 10 deterministic rules (2026-07-03)
- **10 new deterministic AI-readiness rules** in `Semanticus.Analysis/ReadinessRules.cs`, all over metadata TOM
  already exposes (no new live/DMV/report-layer/Prep-for-AI-store dependency), closing `missing`/`partial` rows of
  [`docs/ai-readiness-catalog.json`](docs/ai-readiness-catalog.json) (backlog `missing` 121→111, `covered` 11→21):
  `NAME-GENERIC` (auto-generated placeholder names — Table1/Column2/Measure 3), `FMT-DATATYPE` (date/money column
  stored as Text), `CAT-URL` (link/image column with no WebUrl/ImageUrl data category), `REL-INACTIVE` (inactive
  relationships Q&A/Copilot can't traverse), `REL-HIERARCHY-MISSING` (date/geo dimension with drill columns but no
  hierarchy), `VIS-KEY` (visible key column, disjoint from VIS-FK/VIS-TECH), `VIS-TABLE-ALL-HIDDEN` (visible table
  exposing no visible field), `MODE-DIRECTLAKE-QNA` + `CFG-PREP-MODE` (Direct Lake advisories — Applicable=0, never
  score), `DESC-ECHO-OBJECT` (table/column description echoes the name). No new `ReadinessCategory` and no gate
  wiring — no `ReadinessAnalyzer` weight change. Scoring rules use **presence design** (Applicable = the violation
  population → dormant-or-dock, never inflate a category to an always-pass 100); the two Direct Lake rules are
  Applicable=0 advisories that surface a finding but never move the score. Precision-tuned against AdventureWorks
  (FMT-DATATYPE money nouns end-anchored so text dimensions like "Sales Territory Region" don't false-fire; VIS-KEY
  excludes DateTime keys so a date table's visible date column isn't flagged) — batch fires only genuine findings on
  AdventureWorks (REL-INACTIVE 6, REL-HIERARCHY-MISSING 2, the rest dormant).
- **Tests:** `Semanticus.Tests/ReadinessCatalogBatch1Tests.cs` (15 cases — violating + conforming per rule, plus a
  clean-model dormancy/anti-inflation guard). Verified: `dotnet build Semanticus.Analysis -c Verify` 0 errors;
  `dotnet test Semanticus.Tests -c Verify` 384 green; `dotnet run --project Semanticus.AirSmoke -c Verify` PASS.
- **Rejected as not-actually-deterministic:** the hard-gate *"Verified answers require non-hidden referenced fields"*
  (catalog `deterministic:true`) — `PrepForAiReader` exposes only the verified-answer COUNT, not each answer's
  referenced-field list, so the hidden-field predicate is unbuildable today (deferred to the verified-answer reader).
  `SYN-COLLIDE` similarly stays deferred (the naive shared-synonym detector over-flags auto-generated terms — see the
  in-code rationale; needs LSDL State/Weight-aware parsing).

### Added — Learning Loop L4 (partial): Knowledge pane + `check_workflow` admission dry-run (2026-07-03)
- **Engine `check_workflow(name)`** ([`docs/learning-loop-plan.md`](docs/learning-loop-plan.md) §3.3, dual-drive,
  free/read-only): the parse + dry-run half of the admission pipeline. Loads the def (parse errors surface as
  today), then statically resolves it against the LIVE op catalog and its own gate inputs — every `triggers:`/
  `ops:` entry must be a real op; every verify `when`/`probe` must name an input some gate collects;
  probe/equivalence (and object-scoped `bpa_clean`) verifies need a target `objectRef` input. Returns a typed
  `{Name, ParseError?, Findings[]{severity info|warn}, Ok}` (Ok = parses AND no warn; info never docks it); a
  DISTILLED workflow's `derived_from` provenance surfaces as an info finding. A small parser change adds
  `WorkflowDef.Provenance` (unknown frontmatter keys preserved verbatim). Mirrored across
  IEngine/RemoteEngine/EngineRpcTarget/McpTools. **The expensive REPLAY check (re-executing deterministic steps
  against the originating snapshot) is explicitly OUT of scope — a later [F] layer.**
- **Studio Knowledge pane** (`webview/src/knowledge.tsx`) — a standalone far-right surface beside Workflows/Edit
  History (a cross-cutting concern, not a Build→Inspect→Improve→Ship stage): (1) **Insights** — both scopes with
  kind/scope pills, keys chips, score/retrievals/uses counters, expandable provenance, and upvote/downvote/edit
  (inline)/delete, plus a **Pending-approval** subsection (approve/delete) and a corrupt-line banner when nonzero;
  (2) **Learned workflows** — library defs carrying `derived_from` provenance, each with a **Check** button that
  runs `check_workflow` and renders its findings verbatim; (3) **Recall preview** — a query box driving
  `recall_experience` against the open model, the ranked candidates (matchedKeys + why + rank) and the model
  **fingerprint** card; plus a scoped **Purge…** (Review→Apply: dry-run count then confirm). Dual-drive (refreshes
  on knowledge Activity events); "AI Assistant", never "Claude".
- **Verification:** `WorkflowCheckTests` (4 cases — provenance captured; unknown-op + unresolved-when flagged;
  clean stock workflow passes; missing workflow throws) under `-c Verify`; full xUnit suite green; webview
  `tsc --noEmit` clean + `build:webview` clean; Knowledge tab screenshotted headlessly (populated insights incl.
  one pending + fingerprint + recall results + a learned-workflow Check report) and reviewed.

### Added — Learning Loop L1 + L2 + L3 (self-improvement as a product feature, 2026-07-03)
- **L1 knowledge store + L2 recall** ([`docs/learning-loop-plan.md`](docs/learning-loop-plan.md) §3.1–3.4,
  PR #23): the ExpeL insight store as the user's own append-only JSONL (`.semanticus/knowledge/insights.jsonl`
  project + `~/.semanticus/knowledge/` global, never rewritten) with **delta-only MCP ops** — `add_insight`
  (write-gated pending→approve, SSGM), `edit_insight`/`upvote_insight`/`downvote_insight` (agent judges,
  engine counts the importance counter; score-0 materializes out), `delete_insight`, `list_insights`,
  `purge_knowledge` (dry-run-by-default MemoryGraft valve) — and `recall_experience`: the engine computes the
  open model's **fingerprint**, reads both scopes' approved insights, and returns a deterministically-ranked
  candidate set (key overlap + same-shape bonus + importance + temporal decay, each with `matchedKeys`) — the
  agent does the semantic ranking. Provenance on every record.
- **L3 distill skills + engine `distillable` hints** (this change): the three agent skills —
  **`/distill-workflow`** (verified success → parameterized md+YAML workflow saved via `save_workflow` with
  `derived_from:`/`distilled:` provenance frontmatter, then an `add_insight`), **`/post-mortem`** (root-cause
  isolation → EXACTLY ONE guardrail: an insight, a new gate on a workflow step, or a waiver-with-reason), and
  **`/curate-knowledge`** (periodic ExpeL pass: merge near-duplicates, downvote stale, upvote proven — delta
  ops only). The engine *offers the moment*: `ApplyPlanReport.Distillable` = a clean multi-item apply
  (`FailedCount==0 && AppliedCount>=2 && OverallAfter>=OverallBefore`); `WorkflowRunView.Distillable` = a
  completed run with every step passed and at least one PASSED (real, not skipped/offline) verify — each with
  a one-sentence `DistillableWhy`. Admission gates (parse/dry-run/replay + approvals) are L4.
- **Verification:** `DistillableHintTests` (5 cases — 3 kernel-built WorkflowRunView + 2 real-engine
  ApplyPlanReport, offline); full xUnit suite green (365). Skills are markdown; workflow snippets validated
  by eye against `WorkflowParser.cs`.

### Added — M Code tab transforms ("steal the best of Microsoft's Power Query editor", 2026-07-03)
- **The UI writes M for you** ([`docs/pq-transforms-plan.md`](docs/pq-transforms-plan.md)): a transform bar +
  per-column menus on the sample grid (Remove · Rename · Change type · Filter rows · Replace values · Sort ·
  Trim & Clean · Remove duplicates · Keep top N) — each appends a correctly-quoted step to the partition's M
  via the new `mtransform.ts` kernel (span-aware let-chain scanner, unique step naming, in-result re-pointing);
  **every generated step is offline-parsed first** (there is no cross-platform M engine — invalid M is never
  written), Save stays the explicit act, and the toast discloses "applies at next refresh (the sample shows
  loaded data)".
- **Interactive Applied Steps**: rename a step (binding + every reference, bare or `#"…"`), delete with
  Power Query's re-point-to-predecessor semantics (first/only-step refusals instructive), click → the editor
  selects the binding span.
- **Column profiling** (PQ's quality bars): distinct/null counts + valid-bars per column via one read-only
  DAX pass, badged "profiled from loaded data"; failures degrade, never break the sample.
- **Duplicate / Reference** on shared expressions. uishot `?pq=menu|profile` states self-reviewed.

### Added — Fabric Data Agent engine + live probe (2026-07-03, Kane: "APIs and SDK are now available — full new tab")
- **Engine slice** ([`docs/data-agent-tab-plan.md`](docs/data-agent-tab-plan.md)): `DataAgentRest` over the
  FabricRest internals (pagination/LRO/scrub reuse), the §1 part codec, pure publish assembly
  (draft→published + publish_info). **7 dual-drive ops**: reads free; `generate_data_agent_config` = Pro
  (element tree from the open session with descriptions, Prep-for-AI exclusions → `is_selected:false`,
  LSDL-seeded instructions, placeholder-GUID disclosure — never guessed ids); create/update/publish/delete
  **dry-run by default returning before token acquisition**; read-modify-write never drops unknown parts;
  15k instruction cap; ActivityEvents on executed writes. `PrepForAiConfig.AiSchemaExcludedNames` added.
- **Live-probed** on the real tenant (create→getDefinition→delete round-trip, probe removed): item type IS
  `DataAgent`; `$schema` values are FULL URLs (docs' tables show bare versions — we emit what the service
  round-trips); fresh definitions carry `.platform` (preserved); getDefinition = 202 LRO. Remaining live
  leg recorded in the spec (datasource element ids, publish mechanics, fewshots-on-semantic-model).

### Added — Workflows tab + DESIGNER (2026-07-03, Kane: "a workflow designer… chaining mcp primitive actions… like mcp skills")
- **Run mode** (`workflows.tsx`): library rail (stock/user/error badges, gated/free pills, triggers), the
  Edit-History-style step rail, and a HUMAN-drivable gate panel — answer or decline-with-reason, submit,
  watch engine verify evidence; rejections verbatim; dual-drive live via `workflow/didChange`.
- **Design mode** (`workflowdesign.tsx`): step cards as a chain — `ops:` chips from `get_op_catalog`
  (reflected from the McpTools attributes, can't drift), insert/reorder/remove steps, gate tables, + New
  creation; **deterministic markdown emission** (the file stays the artifact; View-file toggle); stock
  opens read-only with *Customise…* (user shadow), shadow-delete reverts to stock; client guards for the
  two shape-shifting body mistakes. Engine: `save_workflow` (parse-validate-before-write),
  `delete_workflow`, `workflow/libraryDidChange`; the `ops:` step grammar (ops-only fence gates nothing).
  Workflows are explicitly **MCP skills with teeth** — get_workflow = skill delivery (free), gates = the
  paid enforcement. All states uishot-reviewed (`?wf=run|design|new`).

### Added — Pro-mode WORKFLOW ENGINE v1 (2026-07-03, Lane 3 — "the core of Pro mode")
- **The enforcement kernel** ([`docs/pro-mode-spec.md`](docs/pro-mode-spec.md) §2/§4): user-editable
  **markdown + YAML-gate workflow files** (`Workflow.cs` DTOs · `WorkflowParser.cs`, a hand parser for the
  tiny fixed gate grammar — malformed files are *surfaced with `error:`, never skipped*; unknown keys
  forward-compatible, unknown enum values fail loud) and a **session-held run state machine**
  (`WorkflowRunner.cs`): submit/skip/abort transitions, a **run-wide answer namespace** (a step-3 probe reads
  the step-1 `verificationValue`), the **answer-or-decline input gate** whose rejection names every unanswered
  question verbatim (the error text IS the steering mechanism), hard/warn/off strictness (per-gate > settings
  > frontmatter), skip-requires-reason accountability, and cloned torn-proof views.
- **Dual-drive ops** (`LocalEngine.Workflows.cs` + both-door mirrors + 7 MCP tools):
  `list_workflows`/`get_workflow` **free** (the funnel), `start_workflow` = **the one Pro chokepoint** when any
  gate enforces (a workflow whose gates all resolve `off` runs free), `get_workflow_run`/`submit_workflow_step`/
  `skip_workflow_step`/`abort_workflow`. Definitions hot-read from `.semanticus/workflows` (user shadows stock;
  workspace fallback for live/unsaved sessions) + `workflow-settings.json` strictness overrides; every transition
  broadcasts `workflow/didChange`; terminal runs ride the Activity bus into the **experience log** (the run's
  answers/declines/evidence outlive the session — learning-loop §3.1).
- **Engine-evaluated verify executors** (never self-graded): `dax_probe` (the user's known-good number vs the
  live grand total), `dax_equivalence` (the recorded pre-rewrite original vs the measure's current expression
  over an answered grid), `bpa_clean`/`readiness_rescan` (diff ACTIVE findings vs a start-of-run snapshot —
  a missing snapshot fails loud, never blames pre-existing violations on the step), `benchmark_delta` honestly
  skipped-not-wired in v1. Offline = `skipped`, never silently passed. Target convention: the run's latest
  answered `objectRef` input.
- **Stock seed library ×5** (shipped beside the engine binary, copy-to-customise): `new-measure` ·
  `import-table` · `make-ai-ready` · `optimize-dax` (hard `dax_equivalence` gate on the recorded original) ·
  `pre-deploy-validation` — authored from the [journey map](docs/semantic-model-journey.md) with journey-row
  citations.
- **Verification:** WorkflowKernel/Ops/SeedLibrary xUnit suites + a McpSmoke dual-drive workflow proof
  (agent starts `new-measure` over MCP, is rejected with the verbatim questions, declines with a reason,
  completes; the UI door observes `workflow/didChange`). One CRLF-checkout CI failure caught and pinned
  line-ending-agnostic. v1 non-goals per spec §8: cross-session run resumption, composition, marketplace
  import, elicitation (`[verify-before-wiring]`), the Studio Workflows tab (next phase).

### Added — "Semanticus: Connect Claude Code" command (2026-07-03, ship-gater b of the MCP install logistics)
- One command wires the user's own Claude Code to the engine: writes/merges `mcpServers.semanticus` into the
  workspace `.mcp.json` (Claude Code discovers servers ONLY from its own config; the VS Code MCP API wires
  Copilot, not Claude). Merge-not-clobber: other servers deep-preserved, a differing existing entry prompts
  modal Replace/Keep (identical = idempotent), invalid JSON fails loud and writes nothing. The entry is
  `dotnet <abs engine dll> mcp --workspace <abs root>` — full absolute paths (Claude launches with a minimal
  environment) and deliberately NO env block / `--license`: entitlement follows the OWNER engine, and a
  headless Claude-owned engine resolves its tier itself (env → `~/.semanticus/license`). Remaining ship-gater:
  (a) self-contained engine bundling in the .vsix (the command still points at the configured `engineDll`).

### Added — rich evidence UI in Edit History (2026-07-03, Kane's ask: "see the results, not just text")
- The audit trail's evidence expander now renders **typed evidence, not a JSON blob** (`webview/src/evidence.tsx`):
  **optimize_measure** = the race as cards — baseline vs candidate **DAX side by side** (winner highlighted),
  proven/failed/unverified pills, the **mismatch table as a real grid** (filter context · current body · candidate),
  benchmark **bars**, and the comparison query behind a toggle; **compare_baseline** = impact chips + per-measure
  moved/missing rows with the before→after grid; **apply_plan** = counts + grade/BPA deltas + the item digest as a
  table with verify pills; **deploy_live** = gate state + blockers. Unknown ops/verdicts fall back to the raw-JSON
  expander (never crash on a new shape), and the raw JSON stays one toggle away on every typed view.
- **Two evidence tiers, honestly labelled:** the persisted chain stores capped digests, while the FULL payloads
  (candidate expressions, per-context mismatch rows, queries, benchmark runs) ride the live `ActivityEvent.result`
  broadcasts — a session-lifetime cache welds a rich result to its audit record (same op + object, closest-in-time
  within 5 min; marked "captured live this session"); older records degrade to a typed digest view.
- New `safe`/`impact` verdict badges (compare_baseline writes them); uishot grew `UISHOT_EVIDENCE` (auto-expand the
  audit rows) + `UISHOT_H` (tall viewport for inner-scrolled content), and the harness fixture now carries the REAL
  digest shapes + a synthetic rich activity weld — the full tier is screenshot-reviewed headlessly.
- **Digests now carry the grids**: the persisted optimize/compare evidence includes the top-8 mismatch contexts (`mismatchSamples`) per candidate/measure, so the before→after grids render across sessions — not only while the live rich result is cached.

### Added — value-capture-at-edit-start: `capture_baseline` / `compare_baseline` (2026-07-03)
- The RESTRUCTURE pipeline's load-bearing primitive (`docs/verified-edits-plan.md` "Honest gaps" #2 —
  the single biggest unbuilt dependency, now shipped v1). **`capture_baseline(objRef, groupBy, filters)`**
  freezes the MEASURED values of the object's blast radius — its lineage-downstream measures
  (`LineageGraph.Impact`), evaluated **by reference** (`[Name]`, not frozen bodies — after a structural
  edit the question is whether the *measure* still produces the same numbers) over the probe-query grid
  (comment-proof `InlineScalar` shape, `__present` sentinel) — into a bounded session-held store.
  **`compare_baseline(captureId)`** re-evaluates the same grid on the live model and reports per measure:
  unchanged / **moved** (exact contexts + before→after values, equivalence-tolerance compare) /
  **missing** (the measure no longer resolves — an impact, never a skip), and records the safe/impact
  verdict + evidence digest to the Verified Edits audit chain (`Revision=0` — no backing mutation).
- Honesty rails: `Safe=true` only when nothing moved, nothing missing/errored, AND coverage wasn't
  truncated; grand-total-only grids are called out as thin evidence; over-cap measures land in `Skipped`;
  and the **false-safe window is disclosed** — compare reads the LIVE model, so session edits made since
  capture are covered only once deployed (the result says so, per-edit-count, explicitly).
- Dual-drive (MCP `capture_baseline`/`compare_baseline` + RPC `captureBaseline`/`compareBaseline`), Pro +
  live-required. 15 offline tests (gates, structured refusals, deterministic target selection with
  reported overflow, the pure diff incl. BLANK≠0 and vanished-context-as-impact, store LRU with the drop
  returned). 300/300 green.

### Added — Learning Loop L0: the experience log (2026-07-03, ratified ride-along)
- **`.semanticus/experience.jsonl`** — the append-only capture layer of the Learning Loop capstone
  (`docs/learning-loop-plan.md` §3.1): a host-attached `ExperienceTee` (RpcServer's subscribe pattern;
  owner host only — an attached MCP proxy never double-writes, tests capture nothing unless they attach
  one) tees the whole dual-drive ChangeBus stream — change notifications (origin/label/deltas/revision,
  incl. waiver events, which already ride the bus) and activity events (run/verify/benchmark outcomes) —
  each wrapped in the provenance envelope (`schemaVersion/when/kind/sessionId/origin/modelFingerprint/
  inputSources`). Placement reuses LayoutStore's sidecar path authority; **live/unsaved sessions fall back
  to the workspace's `.semanticus/`** (their model anchor is an ephemeral `%TEMP%\semanticus-*` snapshot
  that dies with cleanup — the highest-value sessions must not be captured into a dir that evaporates).
  Fingerprints: anchor-dir hash for file-backed models, endpoint|database for live (the semantic
  fingerprint is Phase L1). Best-effort by construction: a failed append never breaks the op; oversized
  activity payloads drop to a capped stub.
- **`apply_plan` report captured instead of discarded** — the `ApplyPlanReport` (per-item
  kind/rule-id/verify-state + the measured before→after BPA/grade delta) now also publishes as an
  `apply_plan` ActivityEvent, so the tee persists the complete "what worked" record the plan calls gold.
- 6 offline tests (envelope required-fields, apply_plan capture, no-tee/disposed-tee = no capture,
  ephemeral-anchor fallback, quiet no-op without an anchor, fingerprint stability). 285/285 green.

### Fixed — `deploy_live` silently dropped partition M edits (2026-07-03, reported live by Kane)
- A partition's M expression edited in the session (`set_partition_m` / the M Code tab's Save)
  registered locally but deployed NOWHERE — `LiveDeploy` carried names/DAX/visibility-style metadata only
  and didn't even report the omission. The sync core now carries **partition source expressions** (M,
  calc-table DAX — previously also undeployed — and legacy queries) and **shared M expressions/parameters**
  (update matched-by-name, ADD new ones like new measures), all counted as real changes (`Partitions` /
  `NamedExpressions` on the report). Anything structural stays report-only and LOUD: new/removed
  partitions, a source-type change, and Direct Lake entity rebinding land in `Unmatched`/`LiveOnly`, never
  silently dropped. Data is NOT refreshed — the deployed M goes stale-side until the partition reprocesses.
- The diff/apply core was extracted to a server-free `LiveDeploy.SyncModels(src, live, apply)` (the caller
  owns the `SaveChanges` boundary), giving the whole deploy diff its first offline coverage: 9 new tests
  (dry-run purity, M + calc-table sync, structural refusals, named-expression add/update/live-only, and the
  audit-annotation ride-along regression). 278/278 green.

### Fixed — trailing line comments in stored measure bodies poisoned the whole verify surface (2026-07-03, caught LIVE)
- A stored measure body legitimately ending in `-- note` was spliced verbatim into the generated
  `SUMMARIZECOLUMNS` comparison/probe/pivot queries, where the comment swallowed the joining comma and
  broke the entire query — so `optimize_measure`, `verify_dax_equivalence`-over-stored-bodies,
  `probe_measure`, `pivot_measure`, and `apply_plan`'s equivalence checks all failed (honestly: nothing
  was ever mis-applied — `none-proven`, fail-closed) on any such measure. Found during the first live
  `optimize_measure` run against a real Fabric endpoint. Fix: one shared `DaxBench.InlineScalar` wraps
  every inlined scalar in parens with a comment-terminating newline (value-preserving); pinned by
  builder-shape tests.

### Added — DAX best-practice ruleset: the structural walker + the scored BestPractice category (2026-07-03, `feat/verified-edits-v1`)
- **`DaxScan` structural walker** — the self-contained token scanner grew the primitives the enforceable rules
  needed and the vendored ANTLR lexer couldn't provide to Analysis: delimited-name capture (`Inner`/`Delim`,
  bracket-vs-quote), every-call-site enumeration, matching-paren/exact-span root-call detection, top-level
  operator walks, strict normalized subtree equality, and lexical `VAR` declaration scoping with a fail-safe
  table-vs-scalar classification.
- **10 more `lint_dax` rules** (now ~15, all token-path, FP guards pinned in tests): the DIVIDE- and
  SEARCH/FIND-specific IFERROR forms (a 4-arg SEARCH stands down — its IFERROR guards a different error),
  hand-rolled zero/blank guards around division (strict `E ≡ E′` subtree equality), `VAR`-as-live-alias inside
  CALCULATE (a VAR is a constant — the classic time-intel bug), unused VARs, the SELECTEDVALUE and
  DISTINCTCOUNT idioms, REMOVEFILTERS-over-ALL in modifier slots, bare-table CALCULATE filters (identity-gated:
  a new `DaxLintContext` carries real table names — the engine passes them automatically when a session is
  open), and a measure-in-boolean-predicate detector (info/advisory — it can't split `[M] > 100` from the
  contested `col > [M]`). The SUMMARIZE rule now honors the aggregation/CALCULATE/measure carve-out and checks
  every call site, not just the first.
- **Scored `BestPractice` AI-readiness category** (weight 0.08) — 9 scored rules + 6 advisory
  (surfaced-never-scored) + model-level `BP-AUTO-DATETIME` (GUID-suffixed local/template date-table footprint).
  **Presence design:** `Applicable` = the *violation* population, so the category is dormant on clean models and
  can only DOCK a grade, never lift one — the 32-agent adversarial review proved both alternatives perverse
  (trigger-population Applicable activated the category at score 100 on a benign `IF`; any
  proportional-when-present score above the model's average would *raise* the overall). `DATE-MARK` is now
  **TI-gated** (a model with no time-intelligence DAX isn't dinged for an unmarked date table). AiContent BP
  findings ship equivalence-gated fix prompts (rewrite → `verify_dax_equivalence` → only then apply), and
  calc-item findings carry resolvable `calcitem:` refs (grounding for `make_model_ai_ready` holds).
- **BPA token path** — `BpaRule.TokenCheck` (an optional token predicate that replaces Dynamic-LINQ evaluation;
  Expression is kept for TE-compat and is no longer required on token-only rules): the FP-prone text forms of
  `DAX_AVOID_IFERROR` / `DAX_AVOID_FILTER_TABLE_REFERENCE` are re-implemented on it — comments/strings/
  `[bracketed names]` can no longer false-flag, and the unquoted `FILTER(Sales, …)` bare-table case the old
  `"FILTER('"` text rule structurally missed is now caught. Unknown TokenCheck values fail loud into
  `RuleErrors`.
- **Verification** — 267/267 xUnit green under `-c Verify` (+~95 new tests incl. the FP guards, scanner-helper
  pins, category dormancy/anti-inflation, BPA token cases) + all 5 smokes; adversarially reviewed (6 lenses,
  2-refuter verification, 32 agents) — all 5 confirmed findings fixed same-session and pinned. Also fixed: a
  raw NUL byte in `ReadinessRules.cs` that made grep/git treat the file as binary and silently stop searching it.

### Added — Verified Edits v1: the audit trail + the accountable checkpoint (2026-07-02, `feat/verified-edits-v1`)
- **Append-only, hash-chained audit trail** persisted ON the model (`Semanticus_VerifiedEdits` annotation —
  travels with reload/git/deploy). Written through a new Core seam (`AuditAnnotations`) to the TOMWrapper's
  internal `SetAnnotation(…, undoable:false)`, so an `undo_change` from either door **cannot erase a record**
  — the one sanctioned exception to the undoable-write invariant (see
  [`docs/op-routing-map.md`](docs/op-routing-map.md)); a session-level audit-dirty bit keeps the trail visible
  to save/git/close even when undo returns the model to its checkpoint. Each record links the previous by
  SHA-256 over a **length-prefixed canonical basis** (no field-boundary smearing), and a **head anchor**
  (`…_Head`: count + last hash) makes tail truncation detectable too (`ChainIntact`/`FirstBrokenSeq`
  self-check — deleting *everything* wholesale is the one inherently invisible act, and the docs say so);
  a corrupt blob is preserved verbatim under `Semanticus_VerifiedEdits_Damaged` + an explicit chain-reset
  record (fail-loud — never the waiver store's silent degrade-to-empty); the active chain segments to a
  numbered archive annotation at 500 records (bounded append cost, the fresh chain's first record vouches
  for the frozen segment's count + hash).
- **Recording pipelines** — `optimize_measure` persists its verdict + compact evidence (grid, per-candidate
  equivalence rows/truncated/mismatches, warm-median benchmarks, noise band) on applied / no-improvement /
  none-proven outcomes of a real Pro attempt; Verified-Mode-intercepted single-edit DAX ops record an honest
  `validated` verdict (validity only — never "proven"; an empty, never-validated expression records nothing);
  batch applies record the Change-Plan certificate seed (verified/unverified/overridden denominator);
  deploys + overrides record themselves.
- **Accountable checkpoint (closes the "enforcement is theater" gap)** — `deploy_live` commits now run the
  deploy gate: RED **pauses** with the blockers; shipping anyway takes an explicit `overrideReason`, recorded
  **before** the session is serialized, and `LiveDeploy` carries the audit annotations to the live model —
  so the override record genuinely travels **inside the artifact it authorized**. `apply_plan` gains
  reason-required `overrideIds` (a failed/unprovable rewrite can ship with its honest verdict kept + per-item
  override records). `deploy_stage`'s `forceOverride` is demoted to reason-required and **fails closed** if
  the override can no longer be recorded mid-flight. Never a hard wall on ANY door — the Save-to-Live command
  and the Deploy tab both surface the blockers and take a reasoned override, same as the MCP door.
- **Dual-drive ops** — `list_verified_edits` (free; chain + self-check) and `export_verified_edits`
  (Pro; markdown report / CI JSON) on both doors; Edit History tab gains the Pro audit layer (verdict badges
  welded to timeline entries by revision + session, the audit-trail section, chain-integrity indicator,
  export).

## [1.0] - 2026-06-30

The everything-shipped-to-date baseline. The `ai-readiness-rules` branch **merged to `main`**
(`b8bfa0a`, `--no-ff`) and **pushed to `origin`** (`abf11e0`) — closing the dual-drive engine, the
AI-Readiness moat, the ALM lane, the M authoring lane, the Pro entitlement gate, the foundation
hardening, and live-tenant verification. Every capability ships on **both doors** (VS Code UI over RPC +
the user's own Claude Code over MCP) on one shared `IEngine`/`SessionManager`/`ChangeBus`/undo timeline;
the .NET engine runs **no inference** and holds **zero Anthropic credentials**. Each item below is
build-green and smoke-/xUnit-verified; live items are confirmed against a real tenant (Contoso / the
curated Finance PBIP / the Nexwave Fabric service principal).

### Added — engine & dual-drive foundation
- **The dual-drive engine** — **166 MCP tools + 178 RPC methods** over one shared `IEngine`, a single-writer
  `ModelDispatcher` (TOM), change-tracked + undoable, broadcasting `model/didChange` so both doors see every
  edit live. No door-only paths (golden rule #2).
- **Typed TOM authoring primitives** (the only create/delete route) — tables (import / Direct-Lake /
  calculated), columns / calculated-columns / measures / hierarchies / relationships / roles / data-sources /
  named-expressions / functions (DAX UDFs) / calculation-groups / calculation-items; generic
  `delete_object` / `duplicate_object` / `rename_object` (rename runs FormulaFixup). [`0471c06` Phase-1 ops;
  `1cb5ee4` UDF editor — functions are first-class `function:Name` refs, CL≥1702 one-way opt-in.]
- **Property-grid descriptor layer** — `get_properties` / `set_property` reflect each TOM wrapper's
  ComponentModel metadata + dynamic `IsBrowsable` gate into JSON descriptors (string/bool/number/enum),
  editing through the wrapper's own tracked/undoable setter (Name → AutoFixup rename); plus `get_dependents`
  (ReferencedBy) so "check dependents before delete" is actionable. [`b6e5d61`, `0471c06`.]
- **Tree-/script ops** — `script_objects(refs[], format)` (DAX annotated multi-object script / TMSL
  createOrReplace / TMDL per-object via `TmdlScripter`); `search_model` (case-insensitive substring over
  name/description/DAX across tables/measures/columns/hierarchies/calc-items/functions/**roles/perspectives**),
  `list_measures` / `list_columns` / `get_model_graph` / `get_dependencies` / `get_dependents`. [`41e5001`,
  `aa315f9`, `1dfefac`, `2cd8c5f`, `d111963`, `9b9e7dd`.]
- **Bulk / script write routes (Pro-gated at the chokepoints):** `apply_dax_script` (parses `// @object <ref>`
  blocks → one undoable batch); `apply_tmdl` (in-place TMDL apply via `MetadataSerializationContext` →
  `ReadFromDocument` → `UpdateModel` → `Reinit`, made fully undoable by a custom `IUndoAction`, atomic per
  doc; **rejects new top-level objects BY DESIGN** for undo invariance); `apply_model_diff` (selective
  cross-model merge). [`9d11701`, `7239555`, `2ec5cea`.]
- **The Change-Plan engine** (`Plan.cs`) — the "pull request for your model" flagship: `propose_plan` /
  `get_plan` / `add_plan_item` / `set_plan_item` / `apply_plan` (Pro-gated >1 item) / `clear_plan` on both
  doors, broadcasting `plan/didChange`. Propose analyses without mutating (SafeFix + BPA CanAutoFix fully
  specified before→after; AI-content items queued with grounding); apply executes the approved subset as ONE
  `MutateAsync` batch (single undo reverts all); `set_dax` items carrying a verify matrix are proven
  equivalent first and skipped if results change or can't be proven. Default scope = whole model. [`3445125`
  slice-1, `934573c` hardened.]

### Added — AI-Readiness engine (the moat)
- **AI-Readiness "BPA for AI"** (`Semanticus.Analysis`) — an A–F scorecard over **36 deterministic rules /
  8 weighted categories** (incl. a populated DataAgentConfig). Hard-gates wired by rule id: `LIMIT-SCALE`
  caps a model at D; >50% undescribed objects caps the score at 69. Tools: `ai_readiness_scan` /
  `ai_readiness_scan_live` / `ai_readiness_summary`, `apply_safe_fixes`, `make_model_ai_ready`,
  `apply_fix` / `get_fix_prompt` (grounding-rich Claude instructions). [foundation; `1d7152d` graph+fix.]
- **Live readiness rules** — `SCALE-QNA-INDEX` (visible-column cardinality sum vs the Q&A index's 5M
  text-value ceiling) and `SCALE-HICARD-COLUMN` (a single visible String column >1M); live per-column
  cardinality via `COLUMNSTATISTICS()` through `ai_readiness_scan_live`; the offline path stays
  byte-identical (LiveRules appended only when stats are supplied). [`597b82f`, `3884123`.]
- **MS-cited rule additions** — `DESC-LONG-OBJECT` (>200 chars; Copilot reads ~200), `NAME-INVALID-CHARS`
  (emoji/tab/edge-space in a visible name), `MEAS-DUP-EXPR` (same-table identical DAX),
  `SUMMARIZE-DIMENSION` (a visible numeric identifier still auto-aggregating → SafeFix SummarizeBy=None,
  partitioned against `DAC-IMPLICIT-MEASURE`), `NAME-TECH-PREFIX` (a visible Fact/Dim/Stg-prefixed table
  name `NAME-TABLE`'s `IsCrypticName` misses). All low-FP, blast-radius-verified on the curated Finance
  model + AdventureWorks. [`51e3ee2`, `7e33132`.]
- **Prep-for-AI model-side writers** — `enable_qna`, `set_ai_instructions` (LSDL `CustomInstructions`,
  10k-capped), `set_ai_data_schema`.
- **Collapsible rule groups** in the AI Readiness + BPA views (Category → Rule → Items) + right-click
  "Reveal in Model tree" / "Copy reference". [`54ca1ac`.]

### Added — BPA (Best Practice Analyzer)
- **General-purpose BPA + auto-fix** — the classic TE BPA reusing the donor's self-contained **Dynamic-LINQ**
  engine + scope map. **9 built-in rules** to prove the engine, plus the canonical Power BI **26-rule**
  loadable ruleset (TabularEditor/BestPracticeRules, MIT) as the embedded default. `bpa_scan` / `bpa_summary`,
  `bpa_fix` / `bpa_fix_all` (deterministic literal `Prop = value` fixes — bidi→single, key SummarizeBy=None;
  destructive method-call fixes refused), `bpa_get_fix_prompt` (routes every other rule to the user's Claude
  on the same live session), `load_bpa_rules` (file/URL/inline JSON) / `reset_bpa_rules` (persists onto the
  model annotation, undoable, travels with the model). [`fead8ec`, `60125bf`, `77633e1`.]

### Added — DAX suite (live)
- **Query + analysis** — `run_dax` / `run_dmv`, `validate_dax` (offline structural + reference validation,
  line:column, conservative so a Valid verdict is trustworthy), `preview_table` (EVALUATE TOPN),
  `pivot_measure` (SUMMARIZECOLUMNS → client-side matrix). [`7ad13e3`, `c714d61`, `a7361ae`.]
- **Optimize loop** — `benchmark_dax` / `benchmark_dax_coldwarm` (cold/warm wall-clock),
  `verify_dax_equivalence` (proves a rewrite returns identical values across a SUMMARIZECOLUMNS filter-context
  matrix before applying), `profile_dax` (AS-trace FE vs SE split, SE query count/CPU/parallelism + heaviest
  xmSQL scans), `capture_query_plan`, `evaluate_and_log` (EVALUATEANDLOG intermediates). [`feb611f`,
  `0c66be7`, `630ea44`.]

### Added — authoring & generators
- **Calendar / time-intelligence** — `generate_date_table` (calculated date table + mark-Time; integer
  Quarter Number / Year Quarter / Half Year / Year-Month sort key + relative today-anchored columns) and
  `generate_time_intelligence` (the YTD/QTD/MTD/PY/YoY/YoY% suite + 8 opt-in variants: ROLL12/R3M/R6M/SPLY/
  PYTD/PM/MoM/MoM%; idempotent, format-inheriting). [`0223f93`, `50ac029`.]
- **Calculation groups** — `set_calc_item_format_string` (dynamic FormatStringExpression; empty clears),
  `set_calc_group_precedence`. [`3d9efdf`.]

### Added — M authoring (new Studio tab)
- **M Code tab** with two lanes — **Incremental Refresh** (a form over the IR policy API + a live
  prerequisite checklist) and **M query**.
- **M editor** (CodeMirror) — syntax highlighting, offline format, a validity strip, an Applied-Steps
  outline, and Save.
- **Standard-library-aware autocomplete + hover types** — the **866-symbol** M standard library (`Table.*`,
  `List.*`, `Text.*`, `Sql.Database`, …) vendored from Microsoft's MIT `vscode-powerquery`, driving
  `@microsoft/powerquery-language-services` entirely in-webview.
- **Inline diagnostics** — squiggles + hover messages for syntax errors and duplicate identifiers
  (`@codemirror/lint`).
- **Live "Sample of loaded data"** — a read-only `EVALUATE TOPN` of the loaded table when connected (there
  is no cross-platform M-evaluation engine, so this samples loaded data, not per-step output).
- **Create / edit shared expressions + parameters** inline.
- **Engine M APIs** (dual-drive, undoable, broadcast) — `list_partitions`, `get_partition_m`,
  `set_partition_m`, `list/get/update_named_expression`, `create_named_expression`.

### Added — incremental refresh & partition processing
- **Incremental-refresh policy** — `get` / `set` / `remove_incremental_refresh_policy` (metadata-only;
  configures `BasicRefreshPolicy` via the change-tracked wrapper path, never `ApplyRefreshPolicy`), with the
  **PollingExpression** (Detect-Data-Changes) write knob and prerequisite validation (RangeStart/RangeEnd
  parameters exist and a partition M actually filters on them, refused not half-written). [`0bff172`,
  `6105990`.]
- **Per-partition refresh (process)** — `list_refresh_types` (the catalog with what/when/caveat
  explanations + a partition-level flag) and `refresh_partition` (**dry-run by default**; `commit=true`
  executes `RequestRefresh` + `SaveChanges` via `LiveRefresh`, deploy-to-source default, a file model with no
  live origin refused; a commit failure reported on `RefreshReport.Error`, never thrown) — both doors + a VS
  Code partition context menu. [`646f2cc`, `b466ed7`.]

### Added — security (RLS / OLS)
- **Row-level security** — `list_roles`, `create_role`, `delete_role`, `set_role_permission`,
  `set_table_permission` (the per-table row-filter DAX; a non-empty filter auto-promotes None→Read, echoed
  back so the elevation isn't silent), `set_role_member` (Azure-AD / external). Calc-group tables rejected
  for row-filters; a governed (V3Restricted) model fails with guidance. [`9dc8d65`.]
- **Object-level security** — `set_table_ols` / `set_column_ols` (Default/None/Read), surfaced on
  `list_roles`; gated to CL≥1400; calc-group tables/columns rejected; setting Default is a true net-zero that
  never nukes a TablePermission still carrying an RLS filter. [`fc5c623`.]

### Added — VertiPaq / VPAX
- **`export_vpax`** via the official SQLBI/MS **Dax.Vpax**; **`vpaq_scan`**; live VertiPaq storage stats
  when connected.

### Added — live connectivity & supervised writeback
- **Unified "Open Model"** — one model drives the tree + Studio + status bar: `open_model` / `open_local`
  (snapshots a running Power BI Desktop instance, localhost integrated auth) / `open_live` (loads a deployed
  Power BI/Fabric model's metadata + binds `_live` over one token). `connect_xmla` / `connect_local` /
  `connection_status` / `disconnect` / `list_local_instances`. VS Code `Open Model…` is a source picker
  (file / Desktop instance / XMLA endpoint). [`8dda398`, `748d791`.]
- **`deploy_live`** — writes the edited session's metadata back via `Model.SaveChanges()` (names /
  descriptions / visibility / data-categories / format-strings / display-folders / summarize-by / measure +
  calc DAX / linguistic schema), **LineageTag-matched (rename-safe)**, metadata-only, **dry-run by default**
  (`commit=true` required); binds a `LiveOrigin` (non-secret {Endpoint, Database, Tenant}) so an empty
  endpoint deploys "back to source"; **adds** new measures + calculated columns onto a matched live table.
  Confirm-gated "Save to Live Model" human-door button (dry-run diff → modal confirm → commit), local
  write-back via integrated Windows auth (no token), cloud via a service-principal write token. [`8c26514`,
  `1997ce5`, `09c99e8`, `e7389bf`.]
- **EntraToken** — azcli / serviceprincipal / interactive / devicecode / token modes. **Interactive XMLA
  open to Fabric works live** — minting tokens under the "Power Query for Excel" public client
  (`a672d62c-fc7b-4e81-a576-e60dc46e951d`), pre-consented everywhere, accepted by construction (the
  appid-allowlist root cause, not a tenant wall); override via `SEMANTICUS_ENTRA_CLIENT_ID`. [`42ba393`,
  `297c561`, `d6b5d2b`.]
- **Persistent encrypted interactive-auth token cache** — one Entra browser sign-in is reused silently across
  engine restarts (DPAPI-encrypted MSAL cache + `AuthenticationRecord`); Windows-gated, degrades safely.
- **Recent XMLA connections** picker — one-click reconnect (no secrets stored), with per-item forget.
- **Live-tenant verification** (Nexwave Fabric service principal) — read-only Fabric REST + XMLA model lanes,
  the DAX-equivalence keystone, and a supervised `deploy_live` write round-trip, all confirmed against a live
  tenant (CI-gated; off when secrets absent). End-to-end on Contoso: connect → grade C → AI-readiness
  optimise → `deploy_live` → re-read live = grade A.

### Added — ALM lane (5 pillars; cloud writes gated / dry-run, shipped-but-dark)
- **Phase 1 — Model Compare + local source control + the Deploy tab** — `ModelCompare.cs` (object-level
  semantic compare of two RAW TOM models into Create/Update/Delete/Equal with before/after + a dependency-
  ordered selective apply that validates the merged model in memory before writing); `model_diff` /
  `apply_model_diff` (dry-run default) / `deploy_gate` (BPA + AI-readiness + pending-change count). Local git:
  9 dual-drive ops (`git_status`/`diff`/`log`/`commit`/`branch`/`checkout`/`pull`/`push`/`clone`) over the
  model's working dir, auth via the user's own credential helper; outward writes gated (`git_commit` dry-run
  default + saves first, `git_push` needs confirm). The **Deploy** Studio tab (source-control panel +
  scaffolded cloud placeholders). [`2ec5cea`, `77e6dc4`, `ac00475`, `a207286`.]
- **Phase 2 — Fabric REST plumbing (read-only)** — `FabricRest.cs` (pagination + LRO poller + 429 handling +
  stable-errorCode hints, all scrubbed before crossing a door); `list_workspaces`,
  `list_deployment_pipelines`, `get_pipeline_stages`, `get_stage_items`; Fabric scopes via
  `AcquireFabricAsync` (static/uncached so a Fabric call never reuses an XMLA token). [`d7873ef`, `73f8f8d`.]
- **Phase 3 — gated Fabric deploy lane** — `preview_deploy` / `deploy_stage` (dry-run default) /
  `deployment_history`; `DeployGuard.cs` binds a confirm token to the exact intent, surfaced only by the
  human door, and an agent can never commit a prod promotion. [`8d85d51`, `e97ed24`.]
- **Phase 4 — Fabric Git (workspace ⇄ git sync), gated** — `fabric_git_connection` / `fabric_git_status`
  (reads) + the gated writes `fabric_git_commit` / `fabric_git_update` / `fabric_git_connect` /
  `fabric_git_disconnect`; contracts grounded against the MS Fabric REST docs. [`b196d92`, `ad72c59`.]
- **Phase 5 — CI/CD publish (gated) + fabric-cicd scaffold emit** — `cicd_publish` (enumerates the model's
  on-disk Fabric `.SemanticModel` into base64 parts → Items API `updateDefinition`; dry-run default; an agent
  can never run the live publish) + `cicd_generate` (pure file authoring — `parameter.yml`, a GitHub/Azure
  DevOps workflow, a `deploy.py` running real `fabric-cicd`; no gate/token/network/Python dep, no-clobber
  writes). [`56e730a`, `3f6aee0`.]
  > **Policy:** the cloud-write lane is shipped-but-**dark** — not advertised/sold until a live-XMLA CI job
  > exercises the write lane. The remaining cloud ops are Kane's supervised live runs.

### Added — multi-model (compare / copy / reference-tree)
- **Multi-model Tier 1** (`ModelCompare.cs`) — `model_diff`, `apply_model_diff`, `cherry_pick`; the
  Reference Model tree (copy-into-open-model). **Kept in 1.0.** Tier 2 (multi-process) + the unverified
  Fabric ALM cloud lane are deferred.

### Added — spec-driven Fabric-first authoring
- **All three phases** — Direct-Lake read-correctness; create-from-scratch primitives
  (`create_directlake_table`, `create_import_table`, `create_calculated_table`, `create_data_source`,
  `create_model`); the **Spec** Studio tab + `build_model_from_spec` + `autogenerate_spec_from_model` /
  `autogenerate_spec_from_fabric` + `load_spec` / `save_spec` / `get_spec` / `set_spec` / `clear_spec`.

### Added — monetization (open-core)
- **Pro entitlement gate** (Phase 4) — `Semanticus.Engine/Entitlement/`: `IEntitlement` + `EntitlementGuard`,
  `LicenseVerifier` (offline **ECDSA P-256**; .NET 8 has no native Ed25519; mint with the private key, verify
  with the embedded public key; **fails CLOSED to free, never throws**), `LicenseEntitlement` (reads
  `SEMANTICUS_LICENSE` env / `~/.semanticus/license`, checks `exp`; `SEMANTICUS_DEV_PRO=1` dev escape; the
  engine holds the public key ONLY — no secret/network/inference, golden rule #1 intact). **Nine bulk
  chokepoints gated** (thrown before any mutation): `apply_plan`(>1), `bpa_fix_all`, `apply_safe_fixes`,
  `make_ai_ready`, `build_model_from_spec`, `apply_dax_script`(>1 block), `apply_tmdl`(>1 doc),
  `apply_model_diff`-into-session(>1), `cherry_pick`(>1). New read-only `get_entitlement` on both doors.
  Offline issuance via the `Semanticus.License` CLI (keygen + mint; private key gitignored in `.secrets/`).
  Ships under the root **MIT `LICENSE`** — **license DECIDED = MIT open-core, shipped**. The free tier stays
  fully usable one-edit-at-a-time; Pro unlocks the one-click bulk/atomic apply. [`40e6f4c`.]

### Added — React Studio webview (11 live tabs)
- **AI Readiness · Optimize · BPA · Diagram · M Code · Statistics · Data · DAX Lab · Compare · Docs ·
  Spec · Deploy** — built the modern way (React webview; no WinForms port, golden rule #3). The DAX
  analytical suite (query · benchmark · profile FE/SE · verify-equivalence · debug · pivot · preview) is
  complete. (Lineage is **not** yet a tab — it is the next build.)
- **Diagram tab** — React Flow + dagre, draggable nodes whose positions survive reloads, named saved
  diagrams (New/Rename/Delete), an "＋ Add table" picker + per-node "＋ related" / "✕ remove", Auto-arrange +
  Fit; rich relationship encoding (cardinality / bidirectional / inactive / isolated), live on
  `model/didChange`; crow's-foot markers tinted to line state; position-driven crossing-free routing;
  **bus-matrix default on open**; seeds from Power BI Desktop's native `diagramLayout.json` (read-only base
  layer, the sidecar overlays on top — never writes the native file). [`452e9b5`, `ed70a10`, `01b83bb`,
  `dc96423`.]
- **Audit grids** — Measures / Columns / Relationships sub-tabs (sortable/filterable, amber smell flags,
  "needs attention" filter, stats bars). [`e2fdc3a`, `1dfefac`, `48f9821`, `c452a1e`.]
- **DAX Lab** (benchmark bars + A/B equivalence grid + Profile FE/SE), **Data** (table list → virtualized
  100k-row TanStack grid), **Statistics**, **Pivot** (true row×column matrix), VertiPaq treemap (ECharts),
  readiness-trend sparkline, inline click-to-fix findings. [`9a01568`, `30728c1`, `c714d61`.]
- **Properties property-grid** (2nd webview) — typed editors (string/bool/number/enum), search/filter,
  collapsible categories, multiline auto-grow editors, inline validation, **tri-state multi-select bulk
  edit**. [`91259b8`, `05dd568`.]

### Added — VS Code extension (native, non-webview)
- **Model tree as an authoring surface** — a curated, value-prioritized right-click menu over every object
  type (create/script/hide/show/delete, multi-select), tree expansion to all menu-distinct kinds (calcgroup,
  per-table measures/columns/hierarchies/partitions, calc-items, relationship/role/perspective folders), Find
  in Model, Reveal-in-tree, mark-date-table, generate-time-intelligence. [`66d3e81`, `41e5001`, `cb4403f`.]
- **Reference Model tree** — copy-into-open-model.
- **DAX language service** — a TextMate grammar + completion (the full **~359-function** library + the live
  model's tables/measures/columns/UDFs, narrowing after `'Table'[`) + signature help + diagnostics
  (squiggles for unbalanced brackets / unknown refs, conservative) + hover (function signature / measure DAX
  / column type) + go-to-definition + an outline (VARs + RETURN) + VAR hover + drag-from-tree + offline DAX
  formatting (Shift+Alt+F / format-on-save) with an opt-in consent-gated daxformatter.com path. [`18cf053`,
  `094ead9`, `0052436`, `7d2e2e9`, `0ff58a1`, `<dax-format>`, `<dax-lint>`.]
- **One-click Restart Engine** (toolbar ⟳) — drops the connection, kills the engine, rebuilds Debug,
  reconnects in place. [`3c1966e`, `85b479c`.]

### Added — foundation (Phase 0 + Phase 1)
- **Phase 0 — hermetic build** — TE2 vendored as a git submodule; cross-platform CI; a NOTICE file; the
  release rail.
- **Phase 1 — the four verified code holes closed** — the diagram-layout sidecar (`.semanticus/layout.json`,
  LineageTag-keyed, relocated out of the publishable tree); the op-routing map + TMDL-create routing
  (top-level create rejected for undo invariance, last-writer-wins per object); the dual-drive/undo invariants
  pinned in xUnit; the TOM-bump pin gate; the **`IModelSession` seam**.
- **PBIP interop** — open a modern TMDL PBIP from any natural entry point (`ModelPathResolver` normalizes to
  the inner `definition` root); save TMDL back into `definition/` preserving every `.SemanticModel` sibling
  (empirical-oracle-verified against the real Finance PBIP: 0 missing/added/changed). [`ba41150`, `7f9942f`,
  `23bffb7`.]
- **xUnit suite exists** — `Semanticus.Tests`, **48 test cases across 9 files** (`576bcc7`), alongside 5
  smoke runners (Smoke / Rpc / Mcp / Air / Cicd). _(Corrects the stale "there is no xUnit project" note.)_

### Changed
- **DAX editor** — reliable Go-to-Definition / Peek for measure (and `VAR`) references; pretty-print
  single-line measures; open DAX for objects whose name contains `%`. [`94e1dfa`.]
- **BPA default ruleset** — promoted from the curated ~9-rule proof set to the canonical 26-rule Power BI
  ruleset as the embedded default (the 9 built-ins remain). [`77633e1`.]
- **Studio navigation** — folded the model-audit grids under one "Audit" tab with sub-tabs (nav 11→fewer
  top-level tabs without losing surface). [`c452a1e`.]
- **Deep code-review + simplification pass** — a two-track adversarially-verified review of all first-party
  code (8 lenses; 61 raised → 27 confirmed). All 15 confirmed defects fixed (verify-gate over-claiming
  "proven"; `_live` use-after-dispose NRE; connection/trace resource leaks; child-before-parent rename order;
  score denominators; spawn-error listener) and all 12 simplifications applied (redundant plan locks dropped,
  dead members deleted, webview deduped into shared `wire.ts`/`hooks.ts`/`ui.tsx`). [`8c90edb`, `11508d6`,
  `b9a4927`, `1acf533`, `460ab01`, `b6d7c43`.]

### Fixed / hardened
- **Security** — `FabricRest.Scrub` redacts a **bare JWT** (no `Bearer` prefix, e.g. echoed in a JSON error
  body) that could previously leak through a cloud-lane catch onto a door-crossing DTO (a 9-case scrub
  battery). [`717a675`.]
- **Open-DAX `%` crash** — `refFromUri` double-decoded `uri.path` (already percent-decoded by VS Code),
  throwing `URIError` on any name with a literal `%`; dropped the redundant decode. [`94e1dfa`.]
- **Correctness (merge-prep review)** — case-insensitive named-expression lookup (TOM names are
  case-insensitive); a stale-result guard + re-fetch on the live data sample; reconnect auth-mode default.
- **Dual-drive parity** — the MCP door gained `undo_change` / `redo_change` (revert a whole `apply_plan` /
  `bpa_fix_all` batch as one step) + `set_relationship_active`; RPC smoke now drives the change-plan flagship
  + the LSDL Prep-for-AI writers over the wire. [`df10d4f`, `4ddf5d0`.]
- **Feature-surface QA pass** — `docs/feature-coverage-matrix.md` inventories every MCP tool / RPC op /
  Studio tab / rule / cross-cutting invariant as one-line user stories with current automated coverage, plus
  a ranked deep-test backlog. [`bfab1d7`.]
- **Tests** — the xUnit suite grew to cover apply-plan atomicity + `bpa_fix_all` single-undo, the dual-drive
  broadcast invariant, the delete-dangling contract, the one-way compatibility-level guard, and every
  entitlement-gated chokepoint (free-refused / pro-allowed / single-allowed). [`14b9878`, `717a675`.]

### Known limitations
- The interactive-auth token cache does not self-heal a stale record (after the refresh token ages out ~90d
  or an account switch you may be re-prompted once). Live-verified happy path; edge left for a follow-up.
- The new engine M APIs + auth cache require the engine to be rebuilt/restarted to take effect in a running
  VS Code session (`bin/Debug`) or MCP server (`bin/Release`).
- The Fabric cloud-write lane (deploy / Fabric Git / CI-CD publish) is code-complete + smoke-verified offline
  but **dark** — a live write is Kane's supervised run; not advertised/sold until a live-XMLA CI job exercises
  it.

[Unreleased]: https://github.com/tenfingerseddy/semanticus/compare/abf11e0...HEAD
[0.2.0]: https://github.com/tenfingerseddy/semanticus/releases/tag/v0.2.0
[1.0]: https://github.com/tenfingerseddy/semanticus/releases/tag/v1.0
