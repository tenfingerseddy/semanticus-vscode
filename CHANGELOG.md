# Changelog

All notable changes to Semanticus are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

> This file is the project's running **ship-log** — the chronological record of what landed, when, and
> with what verification. [`PLAN.md`](docs/PLAN.md) is the forward roadmap (what's next, not what shipped);
> the per-commit narrative that used to live in PLAN's "Shipped & verified" section now lives here.

## [Unreleased]

## [1.1.2] - 2026-09-12

Post-fix UAT release, including the merged save, publish, workflow Submit, licence,
copy and expression-validation repairs. Final checks also corrected remaining faults:
M validation rejected valid quoted values when building a model from a spec, and a stale
Change Plan could overwrite a newer column aggregation setting (D-026). Optional workflow
inputs now let the MCP server generate its tool schemas without crashing at startup.
The final CI check also corrected argument counting for quoted DAX values and narrowed
an M comma check that rejected valid row filters. Existing test fixtures now create the
objects their expressions reference.

Linux and Windows automated checks passed. Installed Linux acceptance passed, including
shared UI/MCP edits, workflow Submit, Save, validation and Undo. An independently checked
live Publish-and-restore round trip returned the test model to its starting values.

The [1.1.2 GitHub release](https://github.com/tenfingerseddy/semanticus-vscode/releases/tag/v1.1.2)
provides Windows x64 and ARM64, Linux x64, macOS Intel and Apple Silicon installers
with SHA-256 checksums. Each package is built on a matching runner, then its bundled
engine is extracted and executed. Marketplace publication remains a separate step.
Further UAT findings remain tracked for later work.

### Added: explicit workflow format upgrade preview (T243)

Studio and the agent can preview a saved workflow's v1-to-v2 upgrade, then apply the
reviewed change. The upgrade adds the format marker and stable step ids through exact
source edits, preserving unrelated text and refusing changes that alter parsed meaning.
Applying requires the original path and byte hash. Normal saves never upgrade a file,
v2 files need no changes, and stock workflows must first be copied into the project.
Safe previews return a structured apply action with the reviewed file path, byte hash and
current session, so agents can act without extracting arguments from prose.

The stock library is not migrated by this change. Thirteen of its fifteen current files
produce valid previews; calendar-setup and model-hygiene-pass refuse because their
existing unquoted description colons are invalid under the strict v2 parser.

### Added: saved workflow source editing and shared Canvas layouts (T241, T242)

Studio can edit a saved workflow's exact Markdown, including a file that needs parse-error
repair. Saves check the original file path and byte hash. Refused saves keep the draft, and
stock workflows offer an explicit project copy. The agent has the same document read and
edit tools. Untouched text retains its comments, line endings and UTF-8 byte order mark.

Canvas positions now live in the project's workflow layout sidecar and update Studio when
the agent moves steps. The agent can read and save those positions too. Revisions include
the project path, workflow bytes and layout bytes, so an old view cannot overwrite another
project or a changed layout. Conflicts keep the local positions visible with a reload action.
Arrange and Reset save layout changes; Fit all changes only the view.
An explicit action in the saved-file comparison lets users keep a draft and adopt the
reviewed file as its next save base. Newer shared layout notifications received before a
local save reply are retained and applied once no local edits remain.

### Added: workflow calls with inputs and returned answers (T240)

Workflows can call other workflows through the UI and MCP using one run and certificate.
Calls receive only named inputs and return only declared answers. Calls within loops bind
each item's value, and nested calls can relay returned answers to their caller. Invalid call
graphs refuse before a run is registered. Existing gate checks and per-workflow edit permissions
apply to the workflow that owns each step.

The run view groups nested calls and loops, shows received and returned names, and prefills
supplied answers while keeping them editable. Evidence identifies each step's workflow and
frozen version, and records the supplied values used by its gate.
Frame completion walks the plan once per transition, and completed calls retain their return
position so later answer reads do not repeatedly scan every call's rows.

### Added: movable workflow Canvas and loop run navigation (T239)

The Author Canvas now supports dragging steps, zooming, fitting and resetting the layout,
and selecting a step to read its details. Positions are remembered in the Studio panel.
Arrows still follow file order, and moving a step does not edit the workflow file.
Loop runs show each item value and its progress, keep the current item open, and let users
expand finished items to read their records. Both views use the existing workflow definitions
and run state. Saved-file editing and public call starts remain unfinished.

Workflow verification now takes an answer's object-reference type from the row that
supplied it. Nested loop answers stay inside their workflow call, and a callee's final
answer cannot prepare a caller's following loop.

### Fixed: complete date marking and offline waiver handling (T214)

Direct date marking and change-plan items now make the same complete mark: exact `Time`
category, correcting lowercase `time`, and a valid `DateTime` column marked as the key.
An omitted column is chosen only when there is exactly one date column. Invalid or ambiguous
choices make no model changes; other plan items can still apply. Offline readiness scans
preserve valid live-rule waivers without calling them orphaned, evaluating live rules or
changing offline scores. Those waivers still apply when statistics are supplied later.

BPA corrections keep unsupported scopes and aborted evaluations unknown and block deployment
for severity 2/3 rules, even when a fix expression is syntactically valid. Unknown coverage has
no known repair target. Severity-1 unknowns stay visible without blocking; known empty scopes
stay clean. Identified violations collected before a failure retain their own fixability. Quoted fix values treat semicolons as data and decode Unicode
escapes once, so percentage-format previews match the applied value; multiple statements
remain refused.

Recorded offline evidence includes 22 date-marking tests, 112 waiver/readiness/custom-rule
tests and focused verification against the real date BPA rules. Live XMLA/Fabric checks remain
unavailable and unverified. The engine battery was not rerun for this documentation change.

### Added: internal bare-call workflow execution (T236)

The internal runner can resolve and splice a bare callee into the current execution plan,
preserving frozen ownership and rejecting stale or altered plans before mutation. Nested
call completion and disposal finish before certificate computation. Four independent byte
comparisons preserve no-call views and records under both serializers. T-3970 passed the
focused verification and all 33 call cases. Full integration results are recorded separately.
Public call starts, argument binding and declared returns remain unavailable.

### Fixed: nested blocks and inline literals in the task register (T229)

An unclosed fenced or HTML block inside a list item no longer hides later task allocations
after the item ends. Decoded Unicode whitespace cannot hide a heading allocation, and encoded
brackets in code spans remain literal citations. The offline scanner keeps its canonical count
of 76 rows and zero duplicates. Independent source review passed; the full core battery passed
3,287 cases and the supported extension battery passed 44 files. Live Air/Fabric remain
unavailable. The product workflow parser is unchanged.

### Added: frozen ownership for planned workflow rows (T234)

Each planned row retains the frozen definition that authored its step. Gate strictness,
condition roots and start-time inventories use the reachable ownership closure. The engine
validates that closure before the remaining public call refusal. Public loop starts keep
working. The saved foundation was recovered with one stale loop-control assertion updated
and one overbroad source comment corrected. Core 3,042 and 367 focused tests passed, as did
44 extension files on Node 20.20.2/npm11.6.2. Live Air/Fabric checks remain unavailable and
one existing DAX case remains skipped. Late review corrections now keep witness declarations
within the captured frame and honor required-callee activation. Coordinator core Release 3,046
passed on Yoga, the offline battery and final Codex verification passed. The internal call splice is tracked separately in T236.

### Fixed: loop setup grants no iteration permission (T235)

An unexpanded loop setup row no longer grants a body-operation binding exemption or marks
an edit as workflow-authored. Real iterations retain their existing behavior. The correction
uses the runner's existing expansion-only classification and has regression coverage through
the engine routes. It passed final verification with the ownership correction.

### Added: public starts for the existing loop runner (T231)

Workflows containing `forEach:` now start through the shared local, RPC and MCP engine.
Start returns setup instructions until the first applicable iteration is ready. Call-only
and combined call/loop workflows remain refused without allocating a run. No new runner,
public operation or workflow-file editor was added. The no-loop saved records keep their
existing golden bytes. The build passed 3022 core tests and all applicable offline smokes;
live Air and Fabric checks remain unavailable, and one existing DAX case remains skipped.


### Added: read-only Boxes view of a workflow file (T2395 repair / T2416)

Authoring now has a Boxes view of the parsed file. Outline still edits the lossless subset. Any present provenance key, including an empty value, blocks the lossy emitter. Lossy files stay on Boxes. Lossless files get an Outline/Boxes control. Boxes follows file order with arrow connectors, labels explicit versus positional ids, shows full forEach maxIterations and full call with/returns, and says it is not a run. An agent save refreshes Boxes while a dirty Outline draft remains. The banner does not claim omitted raw text is displayed.

### Fixed: the dependency advisory gate is green again on all four lockfile roots (T-3821)

`dependency-audit.test.mjs` had gone red on locks that were byte-identical to `origin/main`: the
advisory database moved, not the tree. Eight newly published advisories matched three transitive
dev dependencies. `fast-uri` 3.1.5 to 3.1.7 and `qs` 6.15.3 to 6.16.0 in the extension lock;
`browserslist` 4.28.4 to 4.28.9 in the webview lock, which pulls its own five declared dependencies
(`baseline-browser-mapping`, `caniuse-lite`, `electron-to-chromium`, `node-releases`,
`update-browserslist-db`) up to the floors 4.28.9 names. Every depender already declared a range
wide enough, so no `package.json` changed. No package was added or removed and no licence field
changed, so no attribution update was owed. The test stops at the first failing root, so `webview`
was found by auditing each root directly rather than by reading the test output. Audit totals are
now 0 at all four roots, `lockfile-drift.test.mjs` passes 10 of 10 (both rewritten locks are
offline-complete and byte-canonical under npm 11.6.2), the extension suite passes 43 of 43 files,
and `npm run build:webview` succeeds on the new closure. Dev-only and peer packages only, so no
shipped runtime dependency moved and this carries no release claim. Detail:
[`docs/notes/T-3821-dependency-repair.md`](docs/notes/T-3821-dependency-repair.md).

### Fixed: CommonMark heading allocations cannot reuse a task id (T205)

Task ids at the start of real top-level ATX or Setext headings now join duplicate-allocation detection while
canonical definition counts remain unordered-list rows. The scanner preserves fenced code, comments, type-1 and
custom type-7 HTML blocks, nested list headings and fences, and emphasis-wrapped headings. It follows CommonMark
ordered markers, tab stops, case-sensitive entities, encoded brackets, quoted HTML attributes, link definitions,
reference-link headings, and code-span escape rules. Ordered items and table cells remain citations. The T165
heading is prose-first and remains a citation. The focused task-register suite and live register pass at exactly
73 rows, 73 ids and zero duplicate allocations. F-018 stays scheduled until the repair has a squash commit on main.

### Fixed: CicdSmoke headlines UNAVAILABLE when live Fabric did not run (T228)

When every offline check passed and the live Fabric lane was unavailable, the smoke still printed
`CICD SMOKE: PASS` with a qualifier. The headline is now `CICD SMOKE: UNAVAILABLE`, exit 0, and the
line never contains PASS. Plain PASS remains only for a successful live lane. Prior failures and
disagreement still exit 1. Offline CicdSmoke passed 134 checks, exit 0. Live Fabric remains unverified.

### Fixed: CicdSmoke tells a dead Fabric credential apart from a broken door (T199)

A live Fabric authentication failure is no longer recorded as a failed check. `FabricRest` now keeps
non-success HTTP status on an inner `FabricHttpException` through wrapping. `Semanticus.CicdSmoke`
classifies only exact `Azure.Identity.AuthenticationFailedException` or `CredentialUnavailableException`
and a typed Fabric 401 as UNAVAILABLE. Typed 403 and every other exception are disagreement. UNAVAILABLE
prints fixed text, adds no failure, qualifies the final summary, and exits 0 when every other check
passed. Disagreement adds one failure and exits 1. An outer crash still exits 2. `XmlaAuthHint.IsAuthFailure`
is not used for this control flow, and an auth exception message is never printed. F-005 stays scheduled
until this lands on main. Offline CicdSmoke passed 134 checks, exit 0, with live Fabric UNAVAILABLE.
Live Fabric and tenant paths remain unverified.

### Fixed: A folded certificate can only ever weaken the exported verdict, never strengthen it

The evidence export used to compute its headline verdict from the step rows alone, so a run whose folded
certificate said PARTIAL or OVERRIDDEN could still export as Verified. The fold repair closed that by reading
the certificate instead of the steps, which opened the same false certainty pointing the other way. The
certificate ladder is deliberately blind to warn-strictness rows, and the spec says a warn-only skip does not
demote the certificate, so a FULL certificate was lifting a run with a skipped warn step, or with a warn-gate
verify that came back failed or unavailable, all the way to Verified.

The exported verdict is now the worst of both: the worst step verdict and the certificate's own verdict. A
certificate may weaken the headline and may never strengthen it. `OverrideReason` returns with it, because it
is populated only when the headline is Overridden. Three watched-red cases pin the three ways a FULL
certificate used to over-claim, and a fourth pins that clean steps under a FULL certificate still export
Verified rather than being weakened for no reason. This repair is `T-2211`; the fold it repairs is `T-1820`.

The full engine suite passed 2,947 of 2,947 with no failures or skips, measured on this tree with
`dotnet test Semanticus.Tests/Semanticus.Tests.csproj`. The coverage oracle stayed byte-current at 306 MCP
operations with zero untested; no test file was added, so its inventory is unchanged. The fold entry below
recorded 2,933, which this tree does not reproduce; it is corrected to 2,945, that same measured 2,947 less
the two facts this repair adds. Extension tests, the .NET smokes, DAX mode runs and every live XMLA, Power BI
Desktop, Fabric, tenant and authentication path were not re-run here and remain unverified for this repair.

### Fixed: T182 visitor 3,000 is not an overflow

The deep probe now has a `visitor` command. Two `LiteralCountingVisitor` subclasses (void and generic)
count `VisitLiteralExpression` callbacks exactly. Linux Debug and Release complete 2,000 and 3,000 with
matching counts, so 3,000 is not a visitor overflow. The durable floor stays 2,000. `DeepFormulaTests`
still skips `walker` at 3,000 and `rewriter` at 5,000. Step 4 must migrate those two visitor subclasses
plus `CountingWalker` and `IdentityRewriter`. The T182 note now points at section 2.3 for that command,
not 2.5, and no longer asks the reader to add a command that already exists.

### Fixed: DAX CI now runs Debug as well as Release on both OS legs

T182 step 1 lacked Windows Debug because CI ran `Semanticus.Dax.Tests` only in Release. The existing OS
matrix job now runs explicit Debug and Release commands, and its contract test pins both commands inside
the Linux-and-Windows matrix. Hosted run 33249582723 passed all four combinations at 477 passed and one
disclosed callback skip each. That closes T182 step 1. T182 remains open for the visitor, walker and
rewriter work in steps 2-4.

### Fixed: Coverage oracle records the evidence path set

The coverage inventory field `trackedEvidenceFiles` is now the ordinal-sorted set of evidence paths, not a
count. Two lines of work that each add a different test file can no longer both match the same recorded number
while the merged tree matches neither. The coverage-oracle extension test proves count drift: it refuses a
count-shaped value. Exact path-set drift is proved by the whole `pwsh -File tools/coverage-oracle.ps1 -Check`
byte-compare of the inventory, including the path set. That check reported 306 MCP operations and zero
without a tracked test reference, and the focused extension test passed.

### Fixed: The CI gate battery runs from PowerShell

The decide child PATH prepended the gh shim with a colon. Windows PATH uses a
semicolon, so from PowerShell bash lost cat, wc and the shim while Git Bash still
passed (F-003). The join now uses `path.delimiter`. On Unix that is still a colon,
so Bash behavior is unchanged. The shim is marked executable so Linux honors it
the way Git Bash already did.

A watched-red extension test rejects a colon join. The focused battery held all
64 cases. The coverage oracle moved tracked evidence files from 238 to 239.

### Security: Odd-offset UTF-16 scanning no longer aborts Node 26

The release-security scanner still reads a one-byte-shifted UTF-16LE view so a token that starts on an
odd byte stays visible. Node 26 aborted when that view was decoded in place, because the decoder saw an
odd `byteOffset`. The shifted bytes are copied first. A child-process probe on the sizes that aborted,
plus an odd-offset token assertion, passed on Node 20.19.5, 24.20.0 and 26.7.0. The coverage oracle
stayed current at 306 operations. No CI or lockfile change. (T221)

### Added: A deferred `forEach` loop expands at its own row (T220 runner unit 18, DECISION 1.6.1d)

A deferred loop that reaches the run still unexpanded because its list source was answered two or more
rows back, or because the adjacent step that answered it was then skipped, now takes exactly one
expansion-only submission addressed to its base id and carrying no answers. It resolves the list from the
loop's own loop-entry projection and splices, running no gate, no verification and no receipt, and not
completing the submitted top-level body frame. Because the splice happens at the loop's own row, the frozen
iteration seed is taken at actual loop entry, so an answer recorded between the declarer and the loop is
inside it; seed capture itself did not move. This closes the one bounded stop Unit 17 left open: the runner
now expands from every declarer position `check_workflow` accepts, so the parser and the runner agree on
position and only the inherited `scope: run` and call-boundary rules remain.

The advance walk now defers **every** condition on an unexpanded template, and the explicit base-id setup
call validates its source before setup parses `when:` once for classification. Expanded rows then use the
existing per-iteration parse and evaluation walk. Missing, explicit null, declined, disallowed blank,
over-bound and cross-frame scope cases all refuse before anything is recorded, each naming
`skip_workflow_step` and what the skip costs. Blank ownership follows the winning visible answer's own
declaration through one backwards resolution shared with the gap path's scope branch, so the two can never
disagree about the winner; an answer excluded only by the absence of `scope: run` gets the scope refusal,
while one behind a `call:` boundary reports as missing, because scope cannot reach across one and offering it
would be advice that cannot be followed. A loop-free false or unreadable condition then records
`not_applicable` with zero iterations through the same recorder the advance walk uses, and a predicate
referencing any loop fact expands first and is judged per iteration. The runner walks to the first applicable
iteration; if none exists it leaves the loop. The view teaches the actual current row or none. A skipped declarer's recorded answer is resolved fresh while the
decision that step abandoned stays `abandoned`, and the certificate stays `OVERRIDDEN`.

`docs/workflow-canvas-spec.md` carries this as **DECISION 1.6.1d** beside 1.6.1a, 1.6.1b and 1.6.1c, with
one condition rule shared by all three explicit current-row setup shapes. There is no new public state, no
new wire member, no parser change, and `start_workflow` still refuses `forEach:`.

### Fixed: Four corrections found while completing unit 18, recorded rather than quietly patched

**A `condition_false` decision leaves its target for its own setup call.** Unit 17's adjacent `expand` and
`empty` paths keep their timing and do not reach the template. `condition_false` splices nothing, so its target *is* reached current
and unexpanded, and its `not_applicable` is now recorded by that row's own setup call rather than by the
advance walk. The verdict and the row are unchanged and only the moment moved later, which is the
direction the deferral exists for. Two shipped Unit 17 cases asserted the old moment and were retargeted.

**The blast-radius measurement was scoped too narrowly.** "No shipped test puts a loop fact in the `when:`
of an unexpanded template" held. What moved instead were the **loop-free** conditions on a Unit 17 target,
which that measurement did not look for.

**The quoted and hyphen controls could no longer fail.** `inputs.team.value == 'loop.index'` and
`inputs.loop-mode.answered` prove the runner decides loop-dependence from the parsed predicate's referenced
roots and never by text-matching the authored string. They proved it by asserting an unexpanded plan, but
after the deferral a template held back *as* a loop fact is also unexpanded until its setup call, so that
assertion passes either way. Both controls now drive the setup call and pin the loop-free verdict by its
note carrying **no** resolved loop binding, with a real loop fact as the contrast beside them.

**Self-sourced preparation named no way past an unusable source.** The inline and deferred setup paths
append `skip_workflow_step` and the skip's certificate consequence to every unusable-source refusal.
Preparation's missing, explicit null and disallowed-blank refusals carried neither, and its decline sentence
carried the route without the consequence on the one shape that reaches the row holding the only answer
that could make the source usable. All four now carry both.

The `submit_workflow_step` description also regained the ratified "pre-pair checks this unit owns" wording
that `CHANGELOG.md`, `TASKS.md`, `docs/PLAN.md` and the Unit 17 note all carry, and now teaches the
`scope: run` route and the no-gate, no-verification, no-receipt rule in those words.

Red first: 8 of 211 focused cases failed against the exact base, then 211 of 211 passed across
`WorkflowRunPlanTests`, `WorkflowRunFrameTests` and `WorkflowStepConditionTests`. All 2,895
`Semanticus.Tests` cases passed with zero skips. The coverage oracle moved exactly one line, the
`McpTools.cs` `sourceHash`, with the operation count unchanged at 306 and no new test file. The 15
pre-existing condition cases all stayed green, which is the behaviour-preserving check on the shared
recorder extraction. At Unit 18 delivery, the full gate passed the Release solution build, all 2,895 unit
cases, all 43 extension test files, the current 306-operation coverage inventory and all seven .NET smokes.
After integrating #322, the same gate passed 2,902 unit cases, all 43 extension test files, the 306-operation
inventory and all seven .NET smokes. AirSmoke and CicdSmoke ran their offline lanes. Live XMLA, Power BI
Desktop, Fabric, tenant and authentication paths remain **unverified**.

### Fixed: RPC dual-drive paste observation no longer races a straggler

The paste check's `WaitNextAsync` took the next `model/didChange`, so an in-flight notification from the
previous mutation could be observed as the paste (F-001 / T195). The waiter now matches the paste, consumes
each event once so a returned match or ignored straggler cannot satisfy a later wait, throws on an overlapping
wait instead of orphaning the first, and still uses the five-second deadline. RpcSmoke mutation waits name
origin and label so an own echo cannot pass a later check. Watched red: a returned match and an ignored
straggler both completed a later wait, and an overlapping wait threw nothing. Focused tests 7 of 7;
RpcSmoke dual-drive PASS.

### Fixed: The millisecond verify-budget proof no longer bets on runner timing

The expected-values test for a 1.9-second cancellation budget slept for 1.4 seconds and assumed the hosted
runner would schedule it within the remaining half second. Windows run 33251621002 exposed that test flaw:
the result genuinely finished after the deadline even though the product still preserved the exact budget.
The existing production arithmetic is now one internal pure helper used by the live path and tested with a
fixed 1,900ms value. The cancellation duration stays exactly 1,900ms and only the integer server timeout
rounds up to 2 seconds. Restoring the historical integer-floor calculation made the new test fail; the
ExpectedValues suite then passed 103 of 103 with exact arithmetic restored.

### Added: The workflow certificate is folded from every frame, not from whichever one submitted last

The certificate was the one part of the runner that never learned about frames. It read run-level
compatibility accessors, so a future looped or called run could have been graded from whichever frame was
ambient when the certificate was requested. No user could reach that state because `start_workflow` still
refuses both `forEach:` and `call:`. This is required foundation before either form can execute.

`ComputeCertificate` now takes every frame explicitly. It computes each frame's level from that frame's
hard rows and surface state, then gives the run the weakest level. One shared existence rule governs the
certificate and its OVERRIDDEN consequence. Receipts and counters sum across all frames in run order, while
the evidence lattice remains the top frame's own so separate proofs can never combine into a stronger claim
than any frame made.

The agent claim is read only from the top frame. A callee or iteration cannot assert the run's level. Five
null-omitted fields add the frame projections and iteration totals, including a bounded sample of failed
iteration values. A no-loop run keeps its recorded JSON bytes unchanged, and the certificate frame order
matches `get_workflow_run` so clients can join them by index without exposing internal frame ids.

The full engine suite passed 2,945 of 2,945 with no failures or skips. Both DAX modes passed 477 tests with
the one documented callback skip. All 43 extension test files and all seven .NET smokes passed. Coverage
remained current at 306 MCP operations with zero untested. Live XMLA, Power BI Desktop, Fabric, tenant and
authentication paths remain unverified.

No call splice, planned-row ownership, public `forEach:` or `call:` start, parser change or UI ships here.

### Fixed: R13c C2 is an iteration-frame silent-top claim control, and F1-F8 reached the spec

T-1820 shipped the per-frame certificate fold with R13c C2 built as a call frame. That subrun cannot catch
the rejected `AccumulatedAnswers(run, Frames[0], cutoff)` reuse, which already hides callee rows. C2 is now
one iteration frame whose `scope: run` claim cannot supply the run's claim while the top frame is silent,
matching C3's replace direction. `docs/workflow-canvas-spec.md` takes the eight fold findings in place:
`failedIterationValues` names iteration frames that did not complete as `passed`, the min-fold takes the
frame as a parameter and sums across every frame, the existence gate is named separately from the
contributing set, `PartitionLocks` are kept apart by the per-frame move rather than unrolled ids, stale
line citations for the claim reader, `ComputeCertificate`, `MinimumCertificateLevel` and the conditional-
equivalence shortcut are current, check 17 joins the two frame lists by index, check 17b names the twelve-
store set as per-frame rather than the certificate's six inputs, and `SkipConsequence` is recorded as
run-wide.

### Fixed: Every shipped workflow now has admission coverage

The stock workflow admission theory now includes `check-blast-radius`, closing the only gap across the 15
shipped workflows. Its case passed on the first focused run, so the shipped YAML was already valid and stayed
unchanged. The focused theory passed 15 of 15, the coverage oracle remained current at 306 MCP operations with
zero untested, the Release solution build completed with zero warnings and zero errors, and the full affected
Release suite passed 2,850 of 2,850 with zero skips.

### Fixed: DAX reliability controls now fail closed

The local corpus bar now returns a failure for every partial snapshot instead of printing `PARTIAL` and
returning success. Its README states the same contract. Controlled partial, exact-baseline, BIM one-short,
TMDL one-short and empty fixtures returned 1, 0, 1, 1 and 1.

The existing CI contract test now pins the explicit `Semanticus.Dax.Tests` Release command and proves that
removing it makes the guard fail. The dependency-free DAX projects remain outside `Semanticus.sln` by
design.

Four exact parser goldens now pin `&` against `+` on both sides, `*`, and `&&`. The normative parser catalog
contains the same four rows. The focused four passed, and the full DAX Release suite passed 470 tests with
the one disclosed T182 deep-walker skip.

The T180 record now reflects the reproducible corpus-pin mechanism that already reached main in #289. Its
local fixture proves fresh and existing checkouts return to the saved commit, dirty and untracked files are
repaired, and the pin file stays byte-identical.

### Fixed: Deferred loop decisions now survive the permanent run record

Unit 17 kept each deferred `forEach` decision in the live run view, but the terminal experience-log
projection dropped it. `BuildRunRecord` now carries a deep copy of `pendingForEach`, including its target,
values, outcome and final state. Rows without a decision still omit the member, so the exact no-loop record
stays byte-identical. Red-first recovery coverage also pins retry replacement, skip abandonment, abort
preservation, view snapshots and both wire serializers. The focused plan, frame and experience-log suites
passed **141 of 141** with no skips.

### Added: A deferred forEach call decides the next loop; condition_false does not expand

Answering a list input that the **immediately following** `forEach:` step reads now runs that declaring
step's own gate, verification and receipt **and** decides the next loop, all in the same submission. That
loop expands only when the usable-list decision is `expand` or `empty`. A loop-free false or unreadable
`when:` records `applied` `condition_false` and creates no iterations. The declaring call is an ordinary
submission, not a setup call.

The submission is three ordered parts rather than one atomic transition. First a decision taken before any
mutation. Then **one paired write** of the source answer and that decision, with no `await` between them.
Then execution of the recorded decision at the top of the synchronous tail, before the row is marked
`passed` and before its advance. A blocked hard gate therefore leaves the answer and an **unexecuted**
decision together on a `failed`, retryable row, and every retry re-decides and replaces both. Nothing
re-executes a recorded decision on a later call.

The one public wire addition is a null-omitted `pendingForEach` member on `StepResult`, ignored when null by
both serializers, so a run with no deferred loop keeps the no-loop golden bytes exact. It carries one
outcome of `expand`, `empty` or `condition_false` and one state of `pending`, `applied` or `abandoned`.
Every executed decision reaches `applied`, including the `condition_false` one that splices nothing.
`skip_workflow_step` marks an outstanding decision `abandoned` beside the skip; `abort_workflow` leaves it
on the terminal record.

Ownership is structural, not searched: a step declares only for the loop on the row immediately after it, so
no plan index but the next one is read, and a step declaring the same input name while another row owns the
loop is an ordinary submission that is never refused on that loop's account. A deferred loop no adjacent row
owns is left alone, reached unexpanded, and refused there with the skip route.

One rule governs the source. The decision is made against the exact answer map the loop's own seed will
hold. An unusable list is refused by the pre-pair checks this unit owns, before anything is recorded, with
**no** exception for an omitted optional name, and **before** the target's `when:` is read. A
condition referencing `loop.index` or `loop.<as>`, including one that also reads `inputs`, is deferred
**whole** and judged per iteration on its own bindings, so `condition_false` is a loop-free outcome only.
The committed answer map became run-owned so the recorded decision and the frozen iteration seeds cannot
disagree; every submission now stores answer clones, so `result.Answers` is no longer reference-identical to
the caller's object.

No source is frozen, so every iteration carries the **full** authored gate. An empty list marks the loop
`not_applicable` with the existing note, records no answers on that row, and is walked past exactly once.

**The next current row is the first APPLICABLE expanded iteration, not always `#0`.** The `submit_workflow_step`
help and the Unit 17 contract both claimed `#0` unconditionally. The same advance walk that lands the index
judges each deferred loop-dependent `when:`, so it can mark `#0`, and any run of leading iterations,
`not_applicable` with their own loop facts and land on a later `#n`, or leave the loop entirely when the
condition excludes every iteration. Both the help and the contract were corrected.

**A fifth repair after T-1498.** Pre-pair refusal wording now names only the refusals this unit owns, so a
hard-gate failure is not claimed to land before the pair. A deferred call decides the next loop and expands
only when the usable-list decision is `expand` or `empty`; a loop-free false or unreadable `when:` records
`applied` `condition_false` and creates no iterations. `TotalStepsProvisional` remains true for that
unexpanded row even after `not_applicable`, as a conservative accepted cost. The `submit_workflow_step` help,
the contract, **DECISION 1.6.1c** (heading, intro, and the 1.6.1b prior-shape lead), check 13b, and two existing tests were corrected. `WorkflowRunner` was not.
Focused plan and frame suites passed **121 of 121** with no skips after those assertions. Coverage oracle
`-Check` passed after regenerating only the `McpTools.cs` sourceHash. `git diff --check` was clean.

`docs/workflow-canvas-spec.md` carries this as **DECISION 1.6.1c** beside 1.6.1a and 1.6.1b. Acceptance
checks 12, 13, 13b, 13c and 14 were tightened **in place**, so the section's enumerated count did not grow;
13b is the one this finally makes real for a deferred source. Check 5 now **records** that the parser and the
runner do not agree on who may declare a deferred source: the parser accepts any strictly preceding step,
the runner only the immediate predecessor. That gap is written down rather than described as agreement, and
must be reconciled before `forEach:` may start. `start_workflow` still refuses `forEach:` and still accepts
no answers. Public signatures, `CurrentStepView` members, Studio, RPC and the MCP operation count are
unchanged.

Red first: the red cases were committed separately at `663f28f` before the implementation. Four repairs then
landed on the saved candidate. `WorkflowRunPlanTests` expected `StepIndex` 1 where `review-region#1` is plan
index **2**. The caller-mutation-during-verify case blocked the executor on a `ManualResetEventSlim` that the
test thread itself had to release, so `SubmitStepAsync` could never yield; it now uses a
`TaskCompletionSource` with `RunContinuationsAsynchronously` and additionally asserts the submission is
genuinely incomplete at the mutation point. The null-gap case banned the word "empty" from the refusal, which
would have banned the required Unit 9 gap sentence itself, since that sentence exists to say a blank list is
an empty loop and a gap is not; it now proves the gap **state** (no decision, no expansion, no committed
answer, the row still an unexpanded template).

Verification on this machine: the focused plan and frame suites passed **121 of 121** with no skips (62 plan,
59 frame), and the `submit_workflow_step` description test passed after the help change. The exact v1
goldens passed **4 of 4** and both golden files stayed byte-identical. A Release solution build completed
with **zero errors and 471 existing warnings**. All **2,840** engine tests passed with zero skips. Smoke,
RpcSmoke, McpSmoke, LearnSmoke, AirSmoke and CicdSmoke all reported PASS, and LearnBench reported a
**+100 percentage-point** lift. Coverage stayed current at **306** operations with zero untested; the
inventory hash moved because `McpTools.cs` changed, and the only other movement was two test-reference
paths added under `submit_workflow_step`. `git diff --check` was clean and the task/findings register
passed with 0 problems.

Not run here, with reasons: the VS Code extension tests, because `Semanticus.VSCode/node_modules` does not
exist in this checkout and installs are shared-tree-forbidden. Skipped, not passed: live XMLA
(`SEMANTICUS_LIVE_XMLA`/`_DB` unset) and live Fabric (no service-principal env), both of which the smokes
named as offline-green degradations.

### Added: One expansion-only submission expands an inline InLiteral loop

A current unexpanded `forEach:` whose list is an authored inline `InLiteral` now takes exactly one
**expansion-only** submission addressed to the base id. Null and an empty object are equivalent and mean no
named fields. Any named field is refused before mutation and stays an ordinary iteration question after a clean
expansion. The call copies the authored strings verbatim into `<id>#0 .. <id>#N-1` without running verify,
completing a frame, or executing `#0`. An empty list keeps the template result, records `not_applicable`
with the existing empty-list note, records no answers, creates no receipt, and advances once.

One helper projects every `CurrentStepView` member for both expansion-only shapes. Title may stay authored.
Instructions are the synthetic setup phrase. Questions are source-only for Unit 15 and empty for inline.
Verify kinds and ops are empty, and effective strictness is null. After a nonempty expansion the runner walks
to the first applicable iteration; if none exists it leaves the loop. The view teaches the actual current row
or none. That current iteration, when there is one, restores authored instructions after loop render, remaining
questions, authored verify kinds, authored ops, and authored effective strictness. Ordinary rows, earlier-source templates, and real iterations keep today's
projection.

LocalEngine classifies both shapes with one union before `CaptureSubmissionFrame`, so neither setup call
stamps a witness lock. `submit_workflow_step` help now teaches both setup shapes. The generic accepted-
submission activity label is unchanged. `start_workflow` still refuses `forEach:`. Public signatures and
`CurrentStepView` members are unchanged.

`docs/workflow-canvas-spec.md` carries this as **DECISION 1.6.1b** beside 1.6.1a. Acceptance checks 12, 13
and 14(a) were tightened in place. 1.6.1a's pre-expansion view now matches the shared setup projection.

Red first: focused plan and frame suites failed **19 of 95** against origin/main, then passed **95 of 95**
with no skips (44 plan facts, 51 frame cases). The exact no-loop golden stayed byte-identical. A Release
solution build completed with **zero errors**. All **2,814** engine tests passed with zero skips. Smoke
passed 24 checks, RPC 77, MCP 251, LearnSmoke 14, LearnBench reported a +100 percentage-point lift,
AirSmoke passed, and CicdSmoke passed. Coverage stayed current at 306 operations; the inventory hash moved
because `McpTools.cs` changed, and no evidence list moved. `git diff --check` and the task/findings
register passed. Extension tests were not run here because this checkout has no `node_modules` and
installs are shared-tree-forbidden. Live XMLA and Fabric were unavailable and remain unverified.

### Changed: submit_workflow_step tells the truth after a Unit 15 preparation

`submit_workflow_step` no longer claims the engine always runs the current step's verify checks, and its
success activity no longer always says the step passed. An ordinary or iteration submission still runs its
gate and verification. An unexpanded self-sourced loop first accepts only its source answer, prepares the
iterations without body verification or a receipt, and walks to the first applicable iteration for the first real body submit; if none exists it leaves the loop. Success
activity uses one generic `submission accepted` label for both that preparation and an ordinary passing
submit. The label still names the resulting current id or `run COMPLETED`. It does not guess the transition
kind from string ids. No new public field. Runner behavior, LocalEngine behavior, public signatures, Studio,
RPC, and MCP operation count are unchanged.

Red first: the three new `WorkflowRunFrameTests` cases failed 3 of 3 against the old copy. The description
lacked `ordinary or iteration`, and both public-door activities still said `step passed`. After the copy
change those three passed, then the focused plan and frame suites passed **75 of 75** with no skips (33
plan, 42 frame). A clean Release solution build, after `dotnet clean` and removal of every project `obj/`
and `bin/`, completed with **zero errors and 466 existing warnings**. All **2,794** engine tests passed with
zero skips. Smoke passed 24 checks, RPC 77, MCP 251, LearnSmoke 14, LearnBench reported a +100
percentage-point lift, AirSmoke 283, and CicdSmoke 110. Coverage stayed current at 306 operations; the
inventory hash moved because `McpTools.cs` changed, and no evidence list moved. Source-byte hygiene passed
4 of 4. Attribution passed all 38 declared checks. Task, findings, fixed-commit, and `git diff --check`
passed. Under node 20.19.5 and npm 11.6.2, a clean install added 291 packages with zero known
vulnerabilities and all **42 of 42** extension test files passed, including `release-security.test.mjs`.
Live Codex coverage failed here for want of a repository credential and is not counted as a pass. Live XMLA
and Fabric were unavailable and remain unverified. Full verification is in `docs/notes/T220-unit15-findings.md`.

### Added: One preparation submission expands a self-sourced input-backed loop

A current `forEach:` template whose list source is declared on that same step now takes exactly one **preparation**
submission. It answers only that source, resolves the list with the existing comma and line-break semantics, and
splices the plan into aligned iteration rows and frames. It runs no verify executor, completes no iteration frame,
and does not execute the loop body: the runner walks to the first applicable iteration; if none exists it leaves the loop. The view teaches the actual current row or none.
Both visible result rows and both frame seeds get independent frozen clones of the answer.

The run view is a two-sided projection of that one gate. **Before expansion it offers only the list source
question**, because the template's other inputs are per-iteration questions this submission refuses, and advertising
them taught the agent to submit a payload the row cannot accept. After expansion every iteration hides the frozen source
and offers the rest in authored order. Inline literal loops, sources declared on an earlier step, an expanded
non-iteration row and ordinary steps match neither projection and keep the full authored gate.

The list source's own `required:` rule binds inside the preparation. It runs no input gate — it must not enforce the
questions it refuses to accept — so a `required` or `answer-or-decline` source answered blank, or with separators
only, now **refuses atomically**, naming that input's question and its rule and naming no other input. Only
`required: optional` may spell blank or separators-only as an empty list, which is the same rule an ordinary
submission has always had through `EnforceInputs`.

A payload naming anything besides the self source is refused rather than dropped or shared, because the preparation
does not run the gate that would enforce a second answer and the iteration it creates is where that answer belongs.
The whole replacement — payload shape, the source answer, the resolved list, the bound, and every synthetic frame
identity — is validated before any run object changes, and the submitted answer is never written to the template row
and rolled back. Missing, null, declined, malformed, over-max, stale-address, incoherent-frame, duplicate-frame and
repeated preparations refuse with plan, results, answer objects, proof stores, status, index, frames, history, origin,
locks and revisions unchanged. An answered blank optional source keeps its visible audit answer on the surviving
`not_applicable` row, records the expansion, and advances exactly once, including terminal completion.

The engine classifies the preparation after parsing and after the address and coherence refusals but **before** it
installs submission-frame and witness-receipt bookkeeping, so a preparation stamps no witness lock: the first real
`#0` submission remains the first verification and the first receipt event, and a hard failure there keeps `#0`
current for retry. Ordinary and already-expanded iteration submissions keep the existing receipt path. Inline loops,
sources declared on earlier steps, ordinary steps, the exact acceptance-check-34 no-loop terminal bytes, start
refusals, public signatures, Studio, RPC, MCP, certificates, calls and returns are unchanged.

`docs/workflow-canvas-spec.md` now carries this as a canonical decision rather than a runner behaviour nobody wrote
down. **DECISION 1.6.1a** defines the source-only preparation call: section 1.6.1 previously said a loop expands "at
the submission of the step that answers the list input", which is complete only while that step is an earlier one and
silent on the case where the answering step and the loop step are the same row. Acceptance checks 12, 13 and 14 were
tightened in place, so the section's enumerated count is unchanged. `start_workflow` is unchanged and still accepts
no answers.

A stale comment above `WorkflowRunner.ValidateSubmission` was also corrected. It claimed to validate every request
property that can refuse before the engine installs receipt bookkeeping. It does not, and now says the exact
boundary: the address, the current row's coherence and iteration-frame state, and a frozen-source rewrite.
Submission-limit, input-gate, input-policy and verification refusals stay inside normal receipt bookkeeping on
purpose, because each is decided against accumulated answers or executed evidence. No behaviour changed for this.

Red first, twice. The original plan focus failed 4 of 30 and the frame focus 3 of 38. The view, blank-source and
engine-door cases then failed 3 of 33 and 1 of 39 against that tree — the pre-expansion view returned
`["finding", "regions", "note"]` where only `["regions"]` is answerable, and a blank `required` source expanded to an
empty loop instead of refusing. Both suites now pass **72 of 72** with no skips. The exact no-loop golden passed 1 of
1. A clean Release solution build completed with zero errors and 466 existing warnings, and all **2,791** engine
tests passed with zero skips. Smoke passed 24 checks, RPC 77, MCP 251, LearnSmoke 14, LearnBench reported a +100
percentage-point lift, AirSmoke 283, and CicdSmoke 110. Coverage stayed current at 306 operations, and byte hygiene,
attribution, task, findings, fixed-commit and diff checks passed against the exact base. The architect then repeated
the gate on exact commit `4e3345fd`: the same clean build, all 2,791 engine tests, the 72 focused cases, the exact
no-loop golden, all seven smoke programs, coverage, byte hygiene, attribution, task, findings, fixed-commit,
exact-base, diff, and live Codex coverage all passed. A clean install under the CI versions, node 20.19.5 and npm
11.6.2, added 291 packages with zero known vulnerabilities and all 42 extension test files passed, including the
release-security boundary. Independent source check `T-1472` passed. The worker's node 26.7.0 scanner abort remains
useful environment evidence, not a product result. Live XMLA and Fabric were unavailable and remain unverified.
Full verification and limits are in `docs/notes/T220-unit15-findings.md`.

### Changed: Expanded iterations freeze a self-declared input list source

When an input-backed loop declares its own list-source input on the loop step, expansion now copies the answered
loop-entry control value into every synthetic iteration result. Each row owns an independent `AnswerValue` clone.
The current iteration view omits only that frozen source from its ordered questions, and a fresh submission or
hard-gate retry preserves a new clone while resetting ordinary iteration answers. Supplying or declining the frozen
name against an iteration id refuses before status, note, answers, verify results or history, effective strictness,
index, frame state, or proof stores can change. The refusal explains that expansion fixed the list and only the
remaining iteration questions may be submitted.

The engine wrapper now runs that same validation after parsing answers and before it installs submission-frame and
witness-receipt bookkeeping, so a rejected frozen-source rewrite cannot alter hidden locks or revisions. Expansion
also refuses atomically when a self-declared source was never answered in the loop-entry seed. Missing, null, and
declined sources cannot create iterations that hide an uncaptured control answer.

An empty expansion keeps the source answer on its surviving `not_applicable` result. A source declared only on an
earlier step remains only on that earlier result, even though it is present in the iteration frame seed. Inline loops,
ordinary rows, and no-loop behavior remain unchanged. This unit does not enable `forEach:` or `call:` at start, wire
automatic preparation or expansion to LocalEngine, expose frame seeds, add an answer ledger, fold certificates,
unroll calls, implement returns, or change public, Studio, RPC, or MCP source.

The red-first plan focus failed 1 of 24 cases because iteration results had no frozen answer. The red-first frame focus
failed 4 of 34 cases because the source was missing from results or remained in the current questions. The two
receipt-boundary tests were also red first. One intermediate repair protected the source only in hidden frame seeds
and lost the visible step-result audit answer; gatekeeper review rejected it despite its automatic check passing. The
exact candidate was then composed with the valid repair. Final independent source review passed and confirmed that
canvas, call, and live behavior remain outside this unit. Final focused runs passed 25 plan and 35 frame cases with
no skips. The exact no-loop golden passed. A clean Release solution build completed with zero errors and 466 existing
warnings, and all 2,779 engine tests passed with zero skips.
Smoke passed 24 checks, RPC 77, MCP 251, LearnSmoke 14, LearnBench reported a +100 percentage-point lift, AirSmoke 283,
and CicdSmoke 110. Coverage, byte hygiene, attribution, task, findings, fixed-commit, exact-base, and diff checks passed.
The clean extension install added 291 locked packages with zero known vulnerabilities, and all 42 test files passed.
Live XMLA and Fabric were skipped for absent credentials or endpoints and remain unverified. Full verification and limits are recorded in
`docs/notes/T220-unit14-findings.md`.

### Changed: Terminal iterations complete their exact run frame

An already-expanded iteration now completes the exact `RunFrame` captured by the shared current-step coherence
path beside its terminal result transition. Success completes that frame as passed before normal advancement. An
audited skip completes it as failed. A false or unreadable step condition still records `not_applicable`, then
completes the iteration frame as passed because the row is outside the applicable population. A hard-gate failure
records failed evidence but leaves both the row current and its frame in progress for an exact retry.

A current iteration frame already outside `in_progress` refuses submission, skip, and automatic condition advancement
before result status, note, answers, evidence, index, or any frame changes. Existing plan, result, and frame metadata
checks remain on the same shared path. Completion uses the captured frame directly, never an authored id or iteration
search. Frame object identity and all twelve proof-store identities remain intact, another iteration frame is untouched,
and ordinary top-level or synthetic non-iteration nested rows do not complete a projected frame. Certificates, public
DTOs, RPC, MCP, Studio, parser and authored workflow shapes, calls, abort policy, start refusals, list resolution, and
expansion wiring are unchanged.

After one invalid ordinary-row expectation in the test draft was corrected, the valid red-first focused frame run
failed 8 of 29 cases before the runner change. The combined frame and condition focus then passed all 65 cases with
no skips. The exact acceptance-check-34 no-loop golden passed, and the full engine suite
passed all 2,768 tests with no skips. A clean Release solution build completed with zero errors and 466 existing
warnings. Smoke passed 24 checks, RPC 77, MCP 251, LearnSmoke 14, LearnBench reported a +100 percentage-point lift,
AirSmoke 283, and CicdSmoke 110. Coverage, source-byte hygiene, attribution, task and findings registers,
fixed-commit, and diff checks passed. The worker lacked extension dependencies. The clean gate installed 291 packages
from the committed lockfile with zero known vulnerabilities. The normal extension aggregate repeated the documented
host cleanup failure in `release-security.test.mjs` and finished 41 of 42. That exact file passed alone and directly
after `release-docs.test.mjs`; all 42 files passed against the same candidate with a one-second cleanup gap. This is
host cleanup timing evidence, not an aggregate-run pass. AirSmoke skipped live XMLA because credentials and an endpoint
were absent, and CicdSmoke skipped live Fabric because service principal credentials were absent. Both live paths
remain unverified. Full evidence is in
`docs/notes/T220-unit13-findings.md`.

### Changed: Expanded iterations use their exact planned identity

One already-expanded loop iteration is now directly addressable without enabling loop starts. The current-step
view teaches the exact `PlannedStep.InstanceId`, such as `review#0`. Submit and skip require that exact id for an
iteration and enter the same fail-closed plan, result, and frame coherence path used by conditions and instruction
rendering before they mutate the run. The authored base id, another iteration id, a stale prior id, a blank id,
missing planned index, and every existing frame metadata mismatch refuse. A successful first iteration advances
and teaches the second exact id. The address rule is the planned id for every row, so a qualified nested row cannot
fall back to its authored id. Ordinary top-level rows still teach and accept their unchanged authored id, including
the legacy blank-id route, and the authored `WorkflowStep.Id` remains unchanged.

The focused suite failed 3 of 36 cases before the runner change: both iteration views taught the authored id, and
submission refused the exact planned id. It then passed all 36 with no skips. The first full run found three older
synthetic iteration fixtures that used base ids or incomplete authored loop metadata; after those fixtures modeled
coherent iterations, all 2,757 engine tests passed with no skips. Gate review then removed an iteration-only identity
fork and pinned the exact planned id on a synthetic qualified non-iteration row. The repeated independent check found
that an invalid iteration id still entered LocalEngine's receipt-finally path, where existing seeded DAX evidence could
rewrite witness locks or revisions without a published run update. LocalEngine now validates the exact address and
coherence before installing that receipt scope, while hard-gate failures after a valid submission keep the existing
receipt behavior. Two gate regressions bring the final engine total to 2,759 passing tests with no skips. The exact
no-loop terminal-record golden passed.

The clean Release solution build passed with zero errors and 466 existing warnings. Seven .NET smokes, coverage,
source-byte hygiene, attribution, task and findings registers, fixed-commit, and diff checks passed. The clean gate
installed 291 packages from the committed lockfile under JavaScript runtime 20.19.5 and npm 11.6.2 with zero known
vulnerabilities. The normal extension aggregate repeated the native host cleanup abort in
`release-security.test.mjs` after 29 earlier children and finished 41 of 42. That exact file passed alone and directly
after `release-docs.test.mjs`; all 42 files passed against the same source with a one-second cleanup gap. This is host
cleanup timing evidence, not an aggregate-run pass. Live XMLA and Fabric remain unverified. Resolution, expansion,
start refusals, frame completion, calls, public signatures, RPC, MCP, and TypeScript are unchanged. Full evidence is
in `docs/notes/T220-unit12-findings.md`.

### Changed: Current iteration instructions render from their exact run frame

The current run view now replaces exact `[[loop.index]]` and `[[loop.<as>]]` references in an already-expanded
iteration's instruction text. The zero-based index uses invariant text, and replacement is ordinal, literal, and
single-pass, so dollar signs, backslashes, line breaks, or another loop token inside the value are never interpreted.
The authored step and instructions remain unchanged. A well-shaped reference to any other loop binding refuses, while
a non-iteration step's instructions remain byte-for-byte exact.

Conditions and rendering share one fail-closed current plan, result, and frame coherence path. Result identity, frame
kind, iteration index, authored step id, loop variable, and non-null value must agree before either door can act.
`CurrentStep.StepId` remains the authored id because submission still addresses it. Titles, questions, ops, call values,
authored definitions, resolution, expansion, submission wiring, start refusals, frame completion, certificate folding,
calls, Studio, RPC, MCP, and public DTOs are unchanged.

The worker's focused suite was red first with 8 failures and 10 existing passes; its final focused set passed all 19
cases. Gate review then found that the view entered the shared coherence path only when the plan still carried an
iteration index, so a corrupt iteration frame whose planned index was missing could masquerade as an ordinary row.
The view now always uses the shared path. The square-bracket token grammar is also shared with call bindings, keeping
their optional inner whitespace and fail-closed name set aligned. A seventh coherence regression brings the focused
set to 20, and an older provisional-total fixture now models its call frame and result identity honestly. All 2,741
engine tests passed with no skips. The exact no-loop golden, clean Release solution build with zero errors and 466
existing warnings, seven .NET smokes, coverage, source-byte, attribution, task, findings, fixed-commit, and diff checks
passed.

The worker could not run the extension aggregate because its dependency tree was absent and installs were forbidden.
The clean gate installed 291 packages from the committed lockfile under JavaScript runtime 20.19.5 and npm 11.6.2
with zero known vulnerabilities. The normal aggregate driver repeated the documented host cleanup fault: a native
allocator abort in `release-security.test.mjs` after 29 earlier child processes left the aggregate at 41 of 42. The
exact file passed alone and directly after `release-docs.test.mjs`, and all 42 files passed against the same candidate
with a one-second cleanup gap. This is host process-cleanup timing evidence, not an aggregate-run pass. Live XMLA and
Fabric remain unverified. Full evidence is in `docs/notes/T220-unit11-findings.md`.

### Changed: Expanded iteration conditions read their exact run frame

A step-level condition on an already-expanded loop iteration now reads the zero-based `loop.index` and exactly
one `loop.<as>` value from the `RunFrame` named by that planned row. Before evaluation can change the run, the
runner verifies the planned iteration index, authored step, loop variable, frame kind, and result-row identity all
agree. Inconsistent internal state refuses atomically. Outside an iteration, both loop facts remain unavailable even
if stale or injected values exist in the start snapshot. Existing model, connection, git, session, date, and
frame-aware accumulated input facts are preserved.

This remains internal runner foundation only. It does not wire list resolution or expansion to submission, remove
the existing `forEach:` or `call:` start refusals, substitute instructions, complete frames, fold certificates,
unroll calls, change public DTOs, Studio, MCP signatures, or public behaviour. The focused suite was red first:
9 new cases failed while 5 existing cases passed because iteration facts were absent, injected top-level loop facts
acted, and five forms of incoherent plan/frame metadata did not refuse. The repaired focused suite passed all 14
cases with no skips. The first full run then exposed one older hand-built frame fixture whose plan, result rows,
and frame identities were incoherent; after making it a real two-iteration plan, all 2,735 engine tests passed with
no skips. The exact no-loop golden passed. The Release build completed with zero errors and 466 existing warnings.
Smoke passed 24 checks, RPC 77, MCP 251, LearnSmoke 14, LearnBench reported +100 percentage-point lift, AirSmoke
283, and CicdSmoke 110. Coverage, source-byte hygiene, attribution, task, findings, fixed-commit, and diff checks
passed.

The worker could not run the extension aggregate because its dependency tree was absent and installs were forbidden.
The clean gate installed 291 packages from the committed lockfile under JavaScript runtime 20.19.5 and npm 11.6.2,
with zero known vulnerabilities. The normal aggregate driver then repeated the documented host cleanup fault: a
native allocator abort in `release-security.test.mjs` after 29 earlier child processes left the aggregate at 41 of
42. The exact file passed alone and directly after `release-docs.test.mjs`, and all 42 files passed against the same
candidate when each child received a one-second cleanup gap. This is host process-cleanup timing evidence, not an
aggregate-run pass. Live XMLA and Fabric remain unverified.

### Fixed: Empty loop-value resolution keeps its fresh-allocation promise

Repeated resolution of either an inline empty `forEach:` list or an answered-blank input now returns a distinct
mutable array on every call. The resolver explicitly allocates and copies zero-length as well as non-empty lists;
inline strings, input splitting rules, and run state are unchanged. Red-first identity checks failed on both empty
routes against the merged unit 9 implementation before the repair. The repaired focused suite passed 20 cases, the
exact no-loop golden passed, all 2,726 engine tests passed with no skips, and all seven .NET smokes passed. Coverage,
source-byte, attribution, task/finding-register, fixed-commit, and diff checks also passed. The extension gate was
unavailable because this worker had neither the linked dependency tree nor the pinned npm version and is forbidden
to install packages. A clean gate then installed all 291 packages with the pinned JavaScript and npm versions and
found zero known vulnerabilities. The aggregate test driver hit a native allocator abort in
`release-security.test.mjs` twice after 29 preceding child processes. That exact file passed on its own, and all 42
files passed against the same candidate when isolated with a one-second cleanup gap. This is recorded as a diagnosed
host timing fault, not counted as an aggregate-run pass. Live XMLA and Fabric skipped and remain unverified.

### Changed: A current loop's value list resolves internally, without touching the run

The workflow runner can now read the item list for the current unexpanded `forEach:` plan entry. Resolution is
pure: refusal or success, it leaves plan, results, frames, statuses, index and the immutable definition exactly
as they were, and always returns a freshly allocated list. It stays a separate step from the splice added in the
previous unit, so a resolution that fails can never half-expand a loop.

An inline literal list is handed back verbatim as a defensive copy, so an authored item keeps its exact string
including padding and any comma inside it. An input-backed list reads the current planned step's own frame through
a cutoff that includes that step's own row. This keeps an answer collected by an earlier step visible and also
admits one collected on the loop step's own submission, without reading any later row. That answer is split on
commas and line breaks, each item trimmed, and blanks dropped, so a
trailing comma or a CRLF pair adds no phantom iteration and an optional input answered blank is an empty loop
rather than one pass over the empty string. A missing, null or declined input is refused with text naming the
input and what to do; a decline is never quietly downgraded to an empty list, because those are different claims.
Terminal runs, missing current entries, misaligned plan and result rows, non-loop rows, iteration rows and
already-expanded templates are all refused.

This remains foundation only: submission wiring, loop-variable substitution, frame completion, certificate
folding, call unrolling, Studio and MCP signatures are unchanged, and the existing `forEach:` and `call:` start
refusals remain.

Seven red-first cases bring the focused plan suite to 20 passing tests. All seven first failed to compile. Five
mutations were then run against the finished resolver; the one that made an answered-blank list refuse survived,
which exposed a real gap in the blank fixture, and that case now covers five blank spellings and bites. The exact
no-loop terminal golden passed. The clean Release build completed with zero errors and 466 existing warnings; all
2,726 engine tests passed with zero skips. Smoke passed 24 checks, RPC 77, MCP 251, and LearnSmoke 14. Coverage
remained current at 306 operations; source-byte hygiene passed 4 checks, and the findings and task registers and
the diff check passed.

The worker could not run the extension suite because its dependencies were absent and its JavaScript runtime did
not match the lockfile contract; the identical failure reproduced on unchanged `main`. The clean gate then used
JavaScript runtime 20.19.5 and npm 11.6.2, installed 291 packages from the committed lockfile with zero known
vulnerabilities, and passed all 42 extension test files. LearnBench, AirSmoke, and CicdSmoke also passed in the
gate. Live XMLA and Fabric remain unverified.

### Changed: Resolved loop values can atomically splice the internal run plan

The workflow runner now has one internal transition for an unexpanded `forEach:` plan entry after its values
have already been resolved. A non-empty list replaces the current source plan/result row in place with aligned
iteration rows whose instance and frame ids extend the planned source identity and frame, fresh statuses, ordered
iteration frames, and deep-isolated copies of the answers visible at loop entry. An empty list keeps one authored
source row, records
`not_applicable` with the reason, marks only the planned template expanded, advances normally, and makes the total
exact. The immutable workflow definition and its authored `ForEach` remain untouched.

Every refusal is atomic. An over-bound count names the actual count and authored bound without truncating or
changing status. A null value list, terminal runs, non-loop rows, iteration rows, and second expansion attempts likewise
leave plan, results, frames, status, and index unchanged. This is foundation only: list resolution, loop-variable
substitution, submission wiring, frame completion, certificate folding, calls, Studio, and MCP signatures remain
out of scope, and the existing `forEach:` and `call:` start refusals remain.

Seven red-first cases bring the focused plan suite to 13 passing tests, including two planned occurrences of the
same authored loop under distinct parent frames. The exact no-loop terminal golden passed. The clean Release build
completed with zero errors and 466 existing warnings; all 2,719 engine tests passed with zero skips. A clean install
from the committed lockfile under JavaScript runtime 20.19.5 and npm 11.6.2 found zero known vulnerabilities, and
all 42 extension test files passed. Smoke passed 24 checks, RPC 77, MCP 251, and LearnSmoke 14. Coverage remained
current at 306 operations; source-byte hygiene, findings, task-register, and diff checks passed. Live XMLA and
Fabric remain unverified.

### Changed: Loop-bearing workflow totals are marked provisional

Workflow run views now expose nullable `totalStepsProvisional`. A run whose definition and current plan contain no
loop omits it through both public serializers, preserving the v1 wire shape. A loop-bearing definition or current
plan reports true exactly while the plan contains an authored loop template with no iteration index, and false after
only synthetic iteration instances remain. `TotalSteps` continues to report the current plan count, which is a lower
bound while provisional. Studio routes all five visible total phrases through one formatter that says `at least N
steps`, and the MCP start activity uses the same honest wording. Exact totals keep their normal `N steps` wording.

A red-first engine regression covers a loop injected through the current plan when the top definition has no loop.
Existing coverage preserves the expanded false case, both serializers, no-loop omission, and the byte-identical
terminal-record golden. The Release build completed with zero errors and 466 existing warnings; all 2,712 engine
tests passed with zero skips, including the six focused plan cases and the exact no-loop golden. A clean install from
the committed lockfile under JavaScript runtime 20.19.5 and npm 11.6.2 found zero known vulnerabilities, and all 42
extension test files passed. Smoke, RPC, MCP, and LearnSmoke passed, as did the coverage, source-byte, findings,
task-register, and diff checks. This unit does not splice plans, execute loops or calls, parse answer lists,
substitute loop values, or alter certificates. Live XMLA and Fabric paths remain unverified. Full details are
recorded with T220 unit 7 in `TASKS.md` and `docs/notes/T220-unit7-findings.md`.

### Changed: Workflow answers resolve inside their captured frame

Each run frame now starts from one frozen, deep-cloned answer seed that never appears in the frame view or
terminal record. Answer reads overlay only same-frame step rows through the selected plan cutoff, with later
answers winning. The narrow `scope: run` escape hatch crosses iteration frames, while ordinary iteration answers
stay isolated. A call frame can neither import nor export result answers implicitly, even when an input declares
run scope; explicit returns remain later work. During a submission, `AllAnswers` retains the frame and plan index
captured before execution so post-submit witness receipts see the submitted context. In contrast, automatic
advancement evaluates each newly current step with that planned step's own frame and cutoff. Outside a submission,
the current planned frame is used; terminal reads return to the top frame. Existing linear runs keep their
latest-wins behavior and exact terminal record bytes. This foundation does not expand a plan, execute loops or
calls, or implement returned values.

Seven red-first cases bring the focused frame suite to 18 passing cases. The exact no-loop golden passed. The
Release solution build completed with zero errors and 466 existing warnings; all 2,709 engine tests passed with
zero skips; Smoke, RPC, MCP, and LearnSmoke passed; and coverage, source-byte, findings-register, and diff checks
passed. A clean install from the committed lockfile under JavaScript runtime 20.19.5 and npm 11.6.2 found zero
known vulnerabilities, and all 42 extension test files passed. Live XMLA and Fabric paths remain unverified.

### Changed: Workflow submissions keep proof state on their captured frame

Each workflow-step submission now captures the frame named by its current planned step before execution. All
existing proof accessors stay routed to that frame through verification and witness-lock receipt bookkeeping,
even when the runner advances or rejects the submission. The scope clears on every exit. Reads and records outside
a submission retain top-level behavior, preserving the no-loop terminal record bytes. Unknown or duplicate frame
ids and nested scopes fail loudly. This unit does not expand plans or execute loops or calls.

Eleven focused frame cases pass, including executable local-engine cases for both advancement and a throwing hard
gate. The Release build completed with zero errors and 466 existing warnings; all 2,702 engine tests passed with
none skipped; Smoke, RPC, MCP, and LearnSmoke passed; and the coverage, source-byte, findings-register, and diff
checks passed. A clean install from the committed lockfile under Node 20.19.5 and npm 11.6.2 completed with zero
known vulnerabilities, and all 42 extension test files passed. Live XMLA and Fabric-tenant paths remain unverified.

### Changed: Workflow run views can project iteration and call frames

The runner now has the frame view contract needed by later loop and nested-workflow units. Ordered iteration
and call frames carry only their ratified kind-specific metadata, grouped step rows, and cloned proof receipts.
The grouped rows are the same point-in-time clones held by the flat top-level step list. A linear run still emits
no `frames` member, so its current wire JSON is unchanged. The public carrier is one concrete typed frame array,
which survives the Newtonsoft RPC round trip into `RemoteEngine` before System.Text.Json writes the MCP result.
Frame completion mutates the same internal frame from `in_progress` to `passed` or `failed`, preserving every
proof-store object. This unit does not expand plans, execute loops or calls, change certificates or evidence
records, or add visible Studio UI.

Seven focused frame cases pass. The serializer regression was red first: both public `object[]` elements returned
from the RPC round trip as `JObject` rather than frame DTOs. The Release build completed with zero errors and 466
existing warnings; all 2,698 engine tests passed with none skipped; Smoke, RPC, MCP, and LearnSmoke passed; and the
coverage, source-byte, and findings-register checks passed. A clean install from the committed lockfile under
Node 20.19.5 and npm 11.6.2 completed with zero known vulnerabilities, and all 42 extension test files passed.
Live XMLA and Fabric-tenant paths remain unverified.

### Changed: Workflow proof state now lives behind one run frame

Every workflow run now creates one internal `RunFrame`, keyed by the run id. The full twelve-store proof set
lives on that frame. Compatibility accessors keep all current executors on the same state, and the existing
no-loop terminal record stays byte-identical. This foundation does not expand plans, add wire fields, or execute
loops or calls.

The three focused frame tests were red first and now pass. Independent review found that the first structural
test also barred future frame metadata required by the spec. The repaired test still checks all twelve stores
one by one without barring that metadata. The Release build completed with zero errors and 466 existing warnings;
all 2,694 tests passed with none skipped; all 42 extension test files and the four required smoke programs passed;
and the coverage inventory remained current at 306 operations. Live XMLA and Fabric-tenant paths were not
available and remain unverified.

### Changed: Workflow step conditions now execute honestly

Workflow runs now evaluate each step-level `when:` through the shared predicate evaluator. A false condition
records a terminal `not_applicable` result and advances automatically. That result is not passed or skipped, is
outside certificate and evidence verdict counts, and remains visible with its reason in the engine, MCP results,
evidence document, and Studio run rail. Missing optional `answered` and `declined` facts are false while an
unavailable value remains unknown. An unreadable condition fails closed and never runs its guarded step.

Five focused red-first condition cases pass. Release and Debug builds completed with zero errors and 466 existing
warnings; all 2,691 tests passed with none skipped; all 42 extension test files passed; Smoke, RPC, MCP, Learn, and
LearnBench passed; the headless Air and Cicd checks passed; and the coverage inventory remained current at 306
operations. The Studio screenshot harness rendered the packaged run rail. Live XMLA and Fabric-tenant paths were
not available and remain unverified.

### Fixed: Studio rebuilds remain byte-clean

The Studio build could minify low-control separators from first-party source and Graphlib into invisible raw
bytes in the committed JavaScript bundles. First-party cache keys now use JSON tuples, the bundle emitter turns
raw controls back into printable JavaScript escapes without changing their runtime values, and the byte-hygiene
gate refuses low-control escapes in webview source. Every webview build now runs the byte-wise sweep against its
own output.

### Changed: Workflow runs now own a mutable execution plan

The first runner unit for workflow stage 1 slice 2 is in place. Each run now owns a mutable plan of
`PlannedStep` entries and a matching mutable result list. Current-step lookup, advancement, certificates,
view totals, skipped-step checks, evidence rendering, and result-to-step lookup all read through that plan.
This is the seam needed by later control-flow work. The next unit now executes `when:` as described above;
`forEach:` and `call:` still refuse at start.

The no-loop terminal record stays byte-identical. The Release build passed with zero warnings and errors;
all 2,687 tests passed with none skipped; Smoke, RPC, MCP and Learn smokes passed; and the coverage inventory
remained current at 306 operations. Live XMLA and tenant paths were not exercised and remain unverified.

### Changed: Every directly referenced NuGet package is checked, not just one

The attribution gate used to prove notices only for declared source copies and the webview bundle.
ANTLR and the Dax libraries already rode the same packaging path into the engine, with stub
sections and no check that the licence text was actually there. The gate now reads `kind: nuget`
from the manifest and scans the PackageReferences that ship in Core, Analysis and Engine. Each
shipping package must be declared or classified, and each declared package must have its licence
text in `THIRD-PARTY-NOTICES.md`. The ANTLR BSD notice, the Dax MIT notice and the Newtonsoft.Json
MIT notice were filled in from the pinned upstream versions.

**What this covers, stated exactly.** The population is the package and project references written
literally in the project files of the three packaged projects, and in the extension's tracked webview
lockfile. It is a reading of project text, not of a build: no MSBuild import is read as a source of
references, no property is expanded beyond a single unconditioned definition in the same file, no
condition is evaluated, and nothing is proved about which references a build copies into the payload.
An asset setting removes nothing on its own; it fails the gate unless a named exemption carries measured
evidence for that exact package, version, project and setting. A repo-controlled file the projects import
fails the gate if it declares a reference, because the gate cannot see one there. It is NOT the resolved
publish closure: transitive packages those references pull in, and the whole .NET 8 runtime pack the
self-contained publish copies into the payload, are outside it. Deriving the resolved publish closure is
the named open item on the board and is not claimed here. The earlier wording said "every shipped
third-party library", which the gate did not and does not prove.

### Fixed: Three defects in the new DAX reader, caught by a line-by-line review

An adversarial review of the merged lexer against its own specification found three real faults. The size
limit was only checked after a whole candidate had been read, so a limit of ten thousand units still let two
hundred thousand be scanned; the cancellation check only fired when the scan landed exactly on a 4,096
boundary, so scanners that move two characters at a time skipped every check; and a line break inside a block
comment was invisible, so the space after it was attached to the wrong token. Each fault was reproduced first
(27 new cases fail before the fix and none after), the random-input run was raised from 6,000 to the specified
100,000 inputs, and the suite grew from 102 to 215 tests. Nothing in the product behaves differently yet: this
code still sits outside the shipping solution. (PR #268)

### Added: A first-party DAX reader and the specification it implements

The first build step of the Kernel program. `Semanticus.Dax` turns DAX text into tokens with no outside
dependencies at all (no TOM, no ANTLR, no Tabular Editor, no Semanticus core), written against a normative spec
that names every token, the rules that make the tokens re-tile the exact source text, about 82 named
regressions, and a 12-point exit gate. It fixes what the vendored reader could not do: a bad character is
reported instead of silently vanishing, dotted names such as `INFO.VIEW.MEASURES` keep every character, and a
scientific number stays one token. 102 tests green, and the 3,555-expression corpus still rebuilds byte for
byte. Three rows of the exit gate (the 100,000-input CI fuzz, byte-identical snapshots across operating
systems, and scaling benchmarks) are CI work that is not done, and they are called out rather than claimed. No
production path changes: the projects are deliberately outside the solution and the shipping engine still runs
on the vendored core. (PR #266)

### Security: The postcss path-traversal advisory is patched

A high-severity advisory against postcss 8.5.17 and earlier turned the build red for days with no code change,
because the dependency check requires a clean audit in every package root. postcss arrives indirectly through
the webview styling stack, so auditing the extension root on its own reported nothing and made the failure easy
to misread. The lockfile now carries postcss 8.5.23, and every package root audits clean. (PR #267)

### Changed: The Connections hub, fixed from a live pass, plus a check before you connect

A live run through the new hub found real dead ends, and all of them are fixed. "Query this model" did nothing
when nobody was signed in; it now routes to the account picker, attaches for queries only rather than opening
for editing, shows visible progress, reports the engine's real error, and says "Already used for queries"
instead of going quietly grey. Accounts is no longer a dead end: it offers a sign-in button, an actionable empty
state, and a targeted "Sign in and use" for a saved account that is signed out. "Open a new model" is now a
full-width strip at the top, and the remembered list and the detail pane each own their scrolling, so nothing is
cut off. A new read-only operation on both doors, `probe_auth_prerequisites`, reports what the engine can
actually see before you connect: which service-principal environment variables are present by name (never a
value) and which az login account is detected. The Add view states plainly that the secret is never stored and
that a Key Vault reference is not resolved in the app, so you put the value in the environment yourself.
(PR #265)

### Changed: The Kernel decision and its first spike are recorded

Mostly paperwork, and it gives you nothing new to use. It records the decision to build a first-party DAX layer,
the measured result of the exploratory spike (3,555 real expressions rebuilt byte for byte, and the vendored
reader ruled out on measurement rather than opinion), and a schedule that retires the earlier three-month
estimate. The one real code addition is a set of characterization tests proving the existing calendar API works
as documented, which removed one of the stated reasons for replacing it. (PR #264)

### Added: Many saved Microsoft accounts, and a real account choice each time you open

Saved account profiles replace the single sign-in per tenant. Only identity details are written to disk (a test
proves no token material is stored), tokens stay in Microsoft's own encrypted store, and the account you already
had becomes your first profile with no new sign-in. Choosing an account when you open a model applies to that
open alone; making it the tenant default is a separate, explicit act that states how many things the default
affects; and every switch lands in History. A failed authorization returns you to the account choice, never to
an endpoint form. The AI Assistant can list and select accounts that are already signed in, while interactive
sign-in, setting a default, and forcing a fresh sign-in stay human-only and are refused before anything happens.
Opening another tenant's model as a guest is not supported in this release and is tracked as follow-up work.
(PR #263)

### Changed: Connections is a full hub instead of a drawer

The side drawer is replaced by a full-screen manager in four parts: Open a model (your remembered models, a
detail pane, and quick-open cards), Current setup, Accounts, and a filterable History timeline. It is one
component serving both doors, so "Manage connections" opens a standalone panel when Studio is closed instead of
dragging all of Studio up. Creating a model while the open session has unsaved work now asks for a name and
explicit consent first, on both doors. No engine or AI Assistant operations changed: the hub drives the
connection registry that already existed, so live updates and undo behave exactly as before. (PR #262)

### Changed: Compare picks a remembered model, never a typed endpoint

The standalone "diff any two models" picker no longer exposes a free-text endpoint box. It selects from the
shared connection list, shows each record's environment chip (an unlabelled record reads "Production
safeguards"), threads the record's tenant into the snapshot, and sends "Add a published model" to the one
sanctioned add form in the Connections manager. Adding or relabelling a connection now reaches views that are
already open, from either door, so a new record shows up without a reload. (PR #261)

### Changed: The Connections design of record and the plan to finish it are in the repo

Paperwork only, with nothing for you to use. The ratified design, both competing proposals from the original
bake-off, and a brief on what shipped versus what was left undone are now stored in the repo instead of living
only in a chat artifact, and the remaining Connections work is written down as a numbered plan with the hub as
the agreed end state. (PRs #259 and #260)

### Added: Verified-measure v7: shaped anchors, grain coverage, an engine-computed certificate

The verified-measure workflow's proof is hardened again. Anchors can carry an optional axis (shaped anchors:
parsed refs only, the axis a subset of the declared context, width-capped, canonically distinct so a locked-axis
change is refused by the shipped revision-equality check), Step 2 must cover the declared grains, open shapes are
countersigned, and the certificate level is computed by the engine from the evidence rather than asserted by the
author. A live-pilot follow-up fixes the certificate claim normalizer to accept any non-alphanumeric boundary
after the level keyword, so an honest claim beginning "FULL." is parsed instead of being dragged to OVERRIDDEN
by its punctuation.

### Fixed: Report lineage is honest about parts it could not read

A report definition that parsed only partially still counted as readable, so a field used only in an unreadable
part could be graded safe to remove. Skipped parts (malformed JSON or a failed file read) are now counted, any
report with skipped parts makes coverage INCOMPLETE, and every would-be "safe" verdict degrades to caution: the
same honesty rule already applied to unreadable and wrong-model reports.

### Fixed: Cloud report analysis is fast and fail-loud

Analyzing a published report could sit silent for eight minutes before timing out, because the interactive
definition read inherited the write lane's polling ceiling. The report-read chain now has its own scoped poll
budget and fails loudly in seconds when a definition is not coming, and the Removal Candidates view gains an
object-type filter.

### Fixed: Safe-to-remove is report-aware and deletes re-verify at apply

Whenever a loaded analysis actually read a report, the Safe to remove view shows the report-aware list
(report-used fields excluded) with a scope badge instead of silently reverting to the model-only list, and every
"safe" delete routes through the single-item remove path that recomputes the unused verdict at apply time.

### Added: Report-reference completeness: bookmarks, hierarchies, filter aliases, named residue

An audit of unresolved report references (184/184 resolved on two real PBIR reports) showed the remaining gaps
were parser completeness, not inherent opacity. Local report reads now walk bookmarks (a stale bookmark can be a
field's only remaining user; the cloud door already walked every part, so this closes a local/cloud asymmetry),
hierarchy bindings resolve to the model hierarchy, filter aliases resolve to their fields, and any residue is
named in the result instead of silently dropped.

### Changed: Em dashes are swept from every user-facing product string

The product copy rulebook bans em dashes in user-facing copy. After the lineage surface was swept, a dedicated
repo-wide pass rewrote 856 string literals across 61 files in the product surfaces (Engine, Core, License,
Deploy, VSCode webview) with meaning preserved exactly; comments and internal strings are untouched.

## [1.1.1] - 2026-09-11

The 1.1.0 UAT repair wave. The Inspiron survey found 170 defects. Every one of them has a fix on
main, and every fix has a test. This list says, in plain words, what changed for the person using
Semanticus, and the commit that carries it. One line per defect, no id twice.

### Groundwork and design

- The live publish path moved out of the big engine file into its own file, so the live cards and
  the editor cards stop fighting over one file. (`88839388`)
- A recorded stand-in for the live target lets later cards test a publish with no real endpoint.
  (`2bbefd9b`)
- A retest map and a lane relaunch check name the survey cases each card has to re-run.
  (`5c1b56a7`)
- The one-click publish design note and three mockups, one per place the control could live.
  (`48a0994d`, `e36f5ac0`)
- The Tests authoring design note and mockup, so a person can write tests without the agent.
  (`3a67e3da`, `461f941e`)

### One click to live

- **D-001** Deletions on the live model are listed and left unticked. Nothing is removed on the
  server unless you tick it. (`877d881e`)
- **D-002** The "the live model already matches" line appears only when there is truly nothing to
  send. (`877d881e`)
- **D-003** Every write to live saves a restore point first, and Roll back can list it.
  (`74acb1b6`)
- **D-005** The deploy gate checks the change you are pushing, not the whole model. Older warnings
  become a count you can open. (`377a12ae`)
- **D-006** A live write is previewed first. The write is refused without the token the preview
  handed back. (`091dba43`)
- **D-007** Publish is one word for the act, wherever it appears. (`a9fffba9`)
- **D-008** Switching models forgets the previous publish target. (`a9fffba9`)
- **D-009** A calculation-group column no longer blocks a live push. A real data column with no
  source still fails. (`974f28f6`)
- **D-010** The Deploy page header loads instead of hanging on loading. (`a9fffba9`)
- **D-011** Fabric Git Status names a missing workspace id instead of doing nothing. (`a9fffba9`)
- **D-012** The one confirm names the account and the destination. (`a9fffba9`)
- **D-044** Ctrl+S saves the file in front of you and never publishes. (`a9fffba9`)
- **D-112** A dry run of a publish change is refused, so it cannot write. (`091dba43`)
- **D-116** A merge with no changes rewrites nothing. (`091dba43`)
- **D-128** The agent can merge into the open model, and the whole merge is one undo step.
  (`d8112adb`)

### Sign-in, accounts and the licence

- **D-013** A signed-in account is remembered across a Code restart on Linux. (`72ac8c21`)
- **D-014** A cancelled sign-in shows as cancelled, and the page stops waiting. (`e88a7e5e`)
- **D-015** Connections shows the saved account name after a restart instead of unknown.
  (`e7113665`)
- **D-016** A stuck open can be cancelled and times out on its own. (`8bafd7ed`)
- **D-017** An expired token asks you to sign in again. (`8bafd7ed`)
- **D-018** A missing OS keyring is not reported as a working keychain. (`45cb70b0`)
- **D-019** The Pro token is read from stdin, never from the command line. (`45cb70b0`)
- **D-020** The keychain claim and the keyring error cannot both appear. (`45cb70b0`)
- **D-021** Every sign-in error carries a support id, and no secret. (`8bafd7ed`)
- **D-155** A button that needs Pro says why at the click. (`45cb70b0`)
- **D-168** The Pro options button shows the licence, not the marketing page. (`45cb70b0`)

### Connections and opening

- **D-004** Typing in Add a published model keeps what you type. (`8fa5c68b`)
- **D-032** A .bim file opens and saves in place on Linux. (`6580996b`)
- **D-033** Tabular Editor folder models open with their backslash paths. (`6580996b`)
- **D-035** The Reference Model tree loads. (`c4fdca15`)
- **D-143** A model you opened from a local file joins Recent. (`8fa5c68b`)
- **D-144** The open file or folder is named on screen, not only on a hover. (`8fa5c68b`)
- **D-145** A rejected path keeps the box open and puts the cursor back in it. (`8fa5c68b`)
- **D-146** The chooser can pick a file as well as a folder. (`c4fdca15`)
- **D-162** Open Model always opens the hub, not a second picker. (`8fa5c68b`)

### Nothing loses your work

- **D-023** A DAX tab for a gone object or a gone model cannot write. (`5c7ab7cb`)
- **D-024** A folder save is written to a temp tree and swapped in, or rolled back. (`bba2e122`)
- **D-025** The first save of a new model asks where to put it. (`75246ede`)
- **D-026** A stale Change Plan item refuses to apply over a newer edit. (`7bbfeb78`)
- **D-029** Opening or switching models asks before unsaved work is lost. (`75246ede`)
- **D-030** Restarting the engine asks before unsaved work is lost. (`75246ede`)
- **D-031** A newer edit on disk is found before an overwrite. (`bba2e122`)
- **D-039** A restored .dax tab opens. (`5c7ab7cb`)
- **D-042** The AI badge is honest about who made a change. (`7bbfeb78`)
- **D-043** An open measure gets one editor, not two. (`5c7ab7cb`)
- **D-047** Sidecar files are written outside the definition tree. (`bba2e122`)
- **D-054** A plan item's status follows undo. (`7bbfeb78`)
- **D-055** The M Code tab resets when the model switches. (`51ce9b27`)
- **D-062** The unsaved marker clears after a real save. (`bba2e122`)
- **D-064** A rename no longer adds an extra native prompt. (`bba2e122`)
- **D-067** The Properties grid refreshes. (`51ce9b27`)
- **D-075** DAX Lab resets when the model switches. (`51ce9b27`)
- **D-080** A checkout that changes files on disk is reported. (`75246ede`)
- **D-082** A window reload keeps the engine, so the session can come back. (`75246ede`)
- **D-089** A checkpoint includes untracked model folders. (`bba2e122`)
- **D-093** A checkpoint copy says what it did. (`bba2e122`)
- **D-096** Restore says what it did. (`bba2e122`)
- **D-117** Project notes travel with a save-as. (`bba2e122`)
- **D-127** The engine log no longer blocks git. (`bba2e122`)
- **D-165** A Pro refusal points at a control that really exists. (`51ce9b27`)
- **D-169** The Properties grid stays honest after a model swap. (`51ce9b27`)

### Editing is correct

- **D-022** A calendar mapping refuses a column of the wrong type. (`b2d11524`)
- **D-027** A range filter stays valid, because comments are skipped when the step name is read.
  (`c2d052e9`)
- **D-028** Calendar delete works, with a confirm inside the panel. (`b2d11524`)
- **D-034** A field parameter partition is just the list. (`c2d052e9`)
- **D-036** A stored calculation-item format stays Dynamic after reopen. (`a397d4cb`)
- **D-037** A static format with an unclosed bracket is refused. (`a397d4cb`)
- **D-038** A relationship between mismatched column types is refused. (`88d12550`)
- **D-040** Only one active relationship is allowed between the same tables. (`88d12550`)
- **D-041** Summarization is checked against the column type. (`88d12550`)
- **D-045** Deleting a column made by a table formula is refused, not faked. (`88d12550`)
- **D-046** Deleting a role asks first and names the filters. (`14a92e9c`)
- **D-048** A refresh window wider than the store window is refused. (`1ed778f9`)
- **D-049** Invalid RLS DAX is refused before save. (`14a92e9c`)
- **D-050** New Table no longer leaves a phantom data source. (`c2d052e9`)
- **D-051** Deleting several objects undoes in one step. (`bf79d6d1`)
- **D-052** Member identities are checked. (`14a92e9c`)
- **D-053** Sort by column is readable and settable on the shared grid. (`88d12550`)
- **D-056** OLS is not offered on a calculation group. (`14a92e9c`)
- **D-057** Edit DAX in the palette uses the tree selection. (`a397d4cb`)
- **D-058** Generate Time-Intelligence uses the tree selection from the palette. (`b2d11524`)
- **D-059** The perspective matrix counts the right members. (`bf79d6d1`)
- **D-060** Saving a refresh policy re-reads the current M, so the source cannot go stale.
  (`1ed778f9`)
- **D-061** A save refusal names the real line. (`a397d4cb`)
- **D-063** Renaming a table also renames its matching default partition. (`c2d052e9`)
- **D-065** F2 opens the rename box. (`88d12550`)
- **D-066** The date-column picker only offers columns loaded from the source. (`1ed778f9`)
- **D-070** Offline Format keeps a leading comment on a VAR/RETURN body. (`a397d4cb`)
- **D-073** Query errors no longer show raw markup tags. (`a397d4cb`)
- **D-074** SUMX with one argument gets a clear warning. (`a397d4cb`)
- **D-077** A broken date mapping shows as a readiness finding. (`b2d11524`)
- **D-095** The calendar playbook no longer asks for fields its help says to leave blank.
  (`b2d11524`)
- **D-113** A refused batch keeps the previous redo. (`bf79d6d1`)
- **D-115** Moving a calculation item's order undoes. (`bf79d6d1`)
- **D-148** The Add button stays inside the roles rail and is clickable. (`14a92e9c`)
- **D-156** A rename warns that names in reports are not rewritten. (`88d12550`)
- **D-158** An empty M is not shown as a valid chip. (`c2d052e9`)
- **D-159** A duplicate member is reported and does not move the revision. (`14a92e9c`)
- **D-161** Inactive relationships are marked in the tree. (`88d12550`)
- **D-167** The validity count opens on click. (`a397d4cb`)
- **D-170** Calculation-group precedence redraws after undo. (`bf79d6d1`)

### Evidence tells the truth

- **D-081** A thin test suite cannot wear grade A. (`020add1d`)
- **D-083** A run shows as Verified only when its checks really verified. (`020add1d`)
- **D-084** The step badge follows the record. (`020add1d`)
- **D-085** A skipped probe cannot wave a hard gate through. (`020add1d`)
- **D-086** A playbook cannot certify an unchanged model. (`020add1d`)
- **D-087** You can write, edit and delete a test, a question and a mapping from the Tests tab.
  (`4c16b391`)
- **D-088** A refusal is written for a person. (`4c16b391`)
- **D-090** A gate input can be satisfied from the UI alone. (`4c16b391`)
- **D-091** You can delete a workflow the product made. (`031eb697`)
- **D-092** An edited export no longer claims to be tamper-evident. (`020add1d`)
- **D-094** You can pick a subset before a run. (`4c16b391`)
- **D-097** The evidence dialog shows evidence, and Export exports. (`020add1d`)
- **D-098** The Customise control exists, or the copy that promised it is gone. (`031eb697`)
- **D-099** Free can review the latest run; signing stays a Pro step. (`020add1d`)
- **D-100** Home search lists matches as you type. (`031eb697`)
- **D-101** A parent card shows its callee's gate. (`29ab8c76`)
- **D-102** Gate inputs are ones a person using only the UI can answer. (`4c16b391`)
- **D-103** A failed git pull shows the real reason, not "From remote". (`b2bfc18e`)
- **D-122** A run with a thin suite cannot show grade A. (`020add1d`)
- **D-126** The grade respects the coverage floor. (`020add1d`)
- **D-131** The warn copy matches what the product does. (`29ab8c76`)
- **D-132** Abort keeps the step state instead of rewinding it. (`29ab8c76`)
- **D-137** Starting a workflow answers with the one it created. (`29ab8c76`)
- **D-138** One model identity is used across the sidecar files. (`020add1d`)
- **D-152** A hand-off warning speaks plainly. (`4c16b391`)

### The agent door

- **D-114** A bad DAX block rolls the whole script back. (`8aa1a37a`)
- **D-118** The agent door joins the engine that is already running, using the path the extension
  advertises. (`17b2784b`)
- **D-119** The model spec keeps a formula sent as an expression. (`8aa1a37a`)
- **D-120** Updating a measure validates it. (`8aa1a37a`)
- **D-121** A merge that would break a relationship by changing a column type is refused.
  (`8aa1a37a`)
- **D-123** Every DAX write validates, and a bad block rolls back. (`8aa1a37a`)
- **D-124** Autogenerate assigns roles by the shape of the model. (`8aa1a37a`)
- **D-125** Saving a model overwrites its source and speaks plainly. (`f65172d4`)
- **D-133** The primer keeps a blank line. (`f65172d4`)
- **D-134** Bare table names are accepted in tool calls. (`f65172d4`)
- **D-135** Direction words are accepted. (`f65172d4`)
- **D-136** The diff shows untracked files. (`f65172d4`)
- **D-139** A restore reports true when the files really went back. (`f65172d4`)
- **D-140** A downvote gets an answer. (`f65172d4`)
- **D-141** The team profile is visible. (`f65172d4`)
- **D-076** A duplicate BPA rule id is refused out loud. (`dcbfaa09`)
- **D-078** A fixed tick clears when you undo or rescan. (`643f69e5`)
- **D-079** The waived count opens the accepted list. (`643f69e5`)
- **D-129** Untrusted learning is held unapproved and shown with a marker. (`dcbfaa09`)
- **D-130** Waived findings are counted, not hidden. (`643f69e5`)

### DAX Lab

- **D-068** A capped result says more rows exist. (`0d6eeee0`)
- **D-069** Stop cancels the running query. (`0d6eeee0`)
- **D-071** The result pane names the query that produced it. (`0d6eeee0`)
- **D-072** A timing under a millisecond is labelled, not shown as a round trip. (`0d6eeee0`)

### Spec, docs and knowledge

- **D-104** Authored overview prose gets a provenance marker. (`b2f2b1eb`)
- **D-105** Building into the model warns when the spec has unsaved changes. (`b2f2b1eb`)
- **D-106** Markdown export keeps the Prep for AI section. (`b2f2b1eb`)
- **D-107** Insights, Recall and Purge are back on the Knowledge tab. (`9ed6bb9f`)
- **D-108** The rescan step compares against the record. (`9dff2346`)
- **D-109** Perspectives are included in the export. (`b2f2b1eb`)
- **D-110** Print says so when nothing opened. (`b2f2b1eb`)
- **D-111** Save spec does not add a second .json. (`b2f2b1eb`)

### Copy and keyboard

- **D-142** The hidden palette commands are visible. (`413f287a`)
- **D-147** Compatibility level can be raised from the model property grid. (`413f287a`)
- **D-149** Shortcuts pass through a focused webview. (`704cbd3d`)
- **D-150** UI copy no longer names MCP operations, environment variables or dotfiles.
  (`413f287a`)
- **D-151** Pickers show the current value and offer a clear entry. (`704cbd3d`)
- **D-153** An empty model offers New Table. (`704cbd3d`)
- **D-154** A long description scrolls inside its box instead of pushing the next field down.
  (`704cbd3d`)
- **D-157** The missing space is fixed. (`413f287a`)
- **D-160** Enter commits a description. (`704cbd3d`)
- **D-163** Grid labels are readable at the default width. (`704cbd3d`)
- **D-164** Studio navigation collapses into a More menu. (`704cbd3d`)
- **D-166** The build TODO is gone from the Data Agent empty state. (`413f287a`)

### Fresh-eyes walks

- The Publish surface was read with no builder context, against the ratified design (C1.6).
  Fourteen findings, each naming a picture from the walk. No product code changed. (`bf2064ff`)
- The Tests authoring surface was read the same way (C6.3). Fourteen findings, no product code
  changed. (`e94a3bfe`)

### The 1.1.1 build

C11.1 sets the package version to 1.1.1 and writes the build note. No installer was built and
nothing was installed: three suites were already red on main before this card ran, so it names them
and stops before packaging instead of shipping over them. (`0bcc1e66`, `9f8f2ce9`)

### Not closed in 1.1.1

Nothing. Every defect the survey found has a fix on main.

## [1.1.0] - 2026-07-17

The authoring-and-evidence wave. Studio task surfaces now keep their results and progress while you move
between tabs; the Storage and M Code tabs are redesigned into scan/act/verify loops; report selection and
connections get far easier; and the verified-measure workflow is hardened into a fail-closed, anchor-and-witness
proof. Underneath, a large model-session, RPC and cloud-operation hardening pass lands and the deterministic
AI-readiness catalog is reconciled. Live-tenant capabilities still depend on the user's tenant, capacity,
permissions and credentials; the documented human acceptance checks remain mandatory for the artifact that is
published.

### Added: Studio surfaces keep their work across tab switches

Lineage report analysis, DAX Lab results, and the Storage scan now survive moving between Studio tabs instead of
being discarded when you leave. Their in-flight work continues in the background and its result is waiting when
you return, and a subtle monochrome ring on the tab bar marks a converted surface that has an operation running
while you are on another tab. Opening a different model or connection still clears every model-scoped result and
orphans in-flight work, so one model's numbers can never surface under another. The M Code tab and its Profile
stay cancellation-scoped and are deliberately excluded from the preserved set.

### Added: A live progress channel for long operations

Analysing several published reports ran a silent sequential loop, so a multi-report run looked frozen. A new
progress channel streams per-report advancement to the Lineage reports pane ("Analyzing N of M: name"),
correlated by a run id so overlapping runs never cross their counts. The AI Assistant door is unchanged, and a
throwing progress listener can never fail the operation it is reporting on.

### Changed: The Statistics tab is now the Storage tab, redesigned around decisions

The former Statistics tab becomes "Storage" and turns the column-storage scan into a scan/act/verify loop over
the existing engine reads (no new scan op). The header states total known column storage with Data, Dictionary
and Hash-index composition and a measured delta against a pinned baseline; ranked component bars replace the
treemap and click through to the object. An Opportunities panel groups honest findings (unused large column,
dictionary-dominated, hash-index-heavy, identifier summarized by default), each carrying its effect; Ask AI
copies a grounded prompt and bulk or destructive actions hand off to the Change Plan. Storage mode is reported as
import, Direct Lake, or an honest "unknown" that suppresses totals and disables byte-grounded deletes until the
scan is proven to describe the editing model. Delete is disabled when usage is Unknown or the object's name is
slash-ambiguous, and plan-routed removals recompute the unused verdict at apply time (the new `delete_if_unused`
plan kind) so a stale delete is skipped with its reason. Comparison baselines key on a stable model-and-storage
identity, never the rotating endpoint. "VertiPaq" leaves every label and button, kept only as a nominative
reference in help text.

### Changed: The M Code tab redesign

The M Code tab moves to a single Applied Steps rail beside the editor, a full-width preview, and a collapsible
incremental-refresh panel below it. Every action shows keyed busy state with its own progressive label and honest
inline errors, so one running action no longer blanks every button. Column profiling has an explicit lifecycle
(idle, running with progress, per-column results with failure reasons, stale on preview change, manual refresh),
built on the fault isolation and source-name discipline described in the M Code fixes below.

### Added: Diagram table-kind filters and field-parameter identity

The Diagram canvas gains filters to show or hide tables by kind, and field-parameter tables now render with a
distinct muted violet and an "FP" marker instead of appearing as plain calculated tables. Detection is
deterministic and metadata-only in the engine (both doors see the same truth), keyed on the extended-property
marker Power BI Desktop writes, so it never misfires on an ordinary calculated table.

### Added: Easier report selection in Lineage

The Lineage published-reports pane no longer demands hand-typed paths and workspace GUIDs. A Browse button opens
the host folder picker for local `.pbip` projects, and the workspace GUID box becomes a picker that lists your
workspaces by name and auto-selects the one matching the open model, with an "enter id manually" escape hatch.
Stale async responses, model switches, and mid-flight edits are all invalidated by a generation token so an
analysis can never target the previous model's workspace.

### Changed: The Workflows tab is a section workbench

The Workflows tab is rebuilt into a stable section sidebar (Home, Library, Runs, Governance, Author) with an
ambient live-run banner so a long AI Assistant run never takes the tab over. Library rows read in plain language
with availability toggles and distinct Off, Hidden-now and File-error states; the run view shows one expanded step
with receipts and decline-or-skip-with-reason and an honest enforcement-off state; Governance is the single home
for enforcement profiles and per-workflow rules with a consequence preview. DAX Lab is filed under Understand,
since its journey starts at "what does this query return?".

### Changed: Zero-dialog authoring for DAX objects

Creating a measure, calculated column, calculated table, calculation item or function no longer opens a name
dialog. The object is created immediately with a generated, collision-checked name, and the editor opens with the
name as an editable header line; saving applies the name and DAX together, and a header rename routes through the
rename seam so references and Q&A synonyms cascade. New Relationship and New Folder collapse to a single picker.

### Added: Account identity and history in Connections

Remembered connections now show which account they signed in as, `connect_xmla` carries the tenant the way
`open_live` already did (closing the connect-versus-open asymmetry), and a device-local, credential-free history
logs connect, open and switch beside the registry. The status bar surfaces the current account and tenant.

### Added: Connect the AI Assistant without the Command Palette

Connecting the AI Assistant over MCP is now discoverable three ways: a plug button in the Model view title bar, a
status-bar item shown only until the workspace is connected, and a one-time first-run prompt. The post-connect
message leads with the restart step, since an `.mcp.json` change is only picked up on the assistant's next start.
The standalone timeline tab now reads "Edits" instead of "Edit History" so it stops resizing awkwardly.

### Changed: Verified DAX is evaluated as a deployed measure

Probe and equivalence candidates were spliced into the query as inline scalar columns, which skip the implicit
CALCULATE a deployed measure carries and could evaluate context-transition-heavy bodies with cheaper, and
sometimes different, semantics. Both builders now emit the measure-faithful `DEFINE MEASURE` shape and define both
sides of a comparison in one block so they share an identical filter context. This strengthens `probe_measure` and
the shipped `verify_dax_equivalence` Pro gate, which previously could pass a rewrite a deployed measure would not.

### Changed: Workflow verification is fail-closed with mandatory evidence

Workflow verify outcomes gain a three-way-plus vocabulary: passed, failed, not_applicable, unavailable. A
conditional check that does not apply advances legitimately; an applicable check without authoritative evidence
(offline, missing witness, zero coverage, truncation, candidate drift, a degraded comparison) is "unavailable" and
blocks a hard step exactly like a failure, naming what was missing. A skip with a recorded reason remains the only
audited override, and the run's shape ledger, evidence receipts and history are immutable.

### Added: expected_values verify kind

A new Pro-mode workflow verify kind proves a target measure reproduces a set of locked value anchors (context and
expected-value assertions the workflow author commits to). Anchors are parsed defensively, compared at a gold-style
tolerance (BLANK matches only BLANK), evaluated in the measure-faithful shape, and locked with a revision receipt
so a later change to the accepted values is admissible only with typed evidence.

### Changed: The verified-measure workflow, benchmark-winning form

The shipped verified-measure playbook and its featured template are rebuilt into the anchor-and-witness form
proven by the benchmark: conventions are pinned from the requirement's own words, expected values are locked from
raw rows before any candidate exists, one canonical candidate is reconciled against an independent, efficient
witness (SARGable, never a bare FILTER over a whole fact table), disagreements are adjudicated cell by cell into a
FULL or PARTIAL certificate, and the hard equivalence gate proves equality only on pinned shapes via a per-run
partition that a later performance rewrite cannot silently re-open. Anchor revisions require a live receipt (the
original and corrected values plus a row-returning query executed through the verify lane), and the whole run is
audit-trailed.

### Added: The deterministic AI-readiness catalog is reconciled

Six completion passes reconcile the deterministic AI-readiness catalog against the model, accepting the rules that
can be proven deterministically and leaving the rest dormant so they never inflate a category. New deterministic
rules land for a URL, image or barcode column with no data category (`CAT-URL`), a visible numeric or date column
with no format string (`FMT-COLUMN`), an ordered text column with no Sort By Column (`FMT-SORTBY`), a bare ID or
code column name (`NAME-COLUMN-ID`), a visible business table in no relationship (`REL-DISCONNECTED`), a visible
business table with no synonyms (`SYN-TABLE`), an ambiguous primary synonym (`SYN-COLLIDE`), and a legacy XML
linguistic schema that cannot be patched safely (`SYN-LSDL-XML`).

### Fixed: The Model Primer survives Power BI Desktop restarts

A local Power BI Desktop connection rotates both its port and its per-session database GUID on every restart, so
the Primer's identity key changed each reload and the user saw a blank Primer. A local source's Primer now rests
on the Desktop model's captured provenance (its file path when known, else its display-name stem), stamped once at
open and hash-vouched so a structural edit can never move it and one model can never be served another's Primer.
Cloud and disk keys are unchanged.

### Fixed: M Code profile fault isolation and source-name discipline

Two reported M Code defects. Column profiling used to fail the entire distinct-and-null strip when one column
errored, swallowing the reason; it now probes columns in IFERROR-guarded chunks that bisect to the offending
column, renders "n/a" with the reason as a tooltip, reports how many were skipped, and adds numeric min and max.
Separately, M writes now target the partition-output source name (the column's `SourceColumn`, which can
legitimately differ from its model name) on every path, standing down honestly when the source name cannot be
proven rather than writing a guessed name; a date column that disagrees with the wired incremental-refresh
predicate is refused with both fields named.

### Fixed: The AI activity feed stops sending every operation to DAX Lab

Feed entries whose operation had no owning tab all fell back to a link into DAX Lab, so a metadata edit or workflow
step dropped you in an unrelated tab. Unmapped entries are now inert (no link, no misleading tooltip) while mapped
entries keep their navigate behavior, and tooltips speak the tab's display name instead of its internal id.

### Changed: The public-repo mirror is a self-proving script

The release mirror to the public repository is codified as a script that enforces its exclusion set and fails
loudly when a docs file a csproj embeds as a build resource is dropped (the failure that turned public CI red).
This is release tooling and is not part of the shipped product.

### Fixed: rename_object cascades into the linguistic schema

A rename rewrote every DAX / RLS reference but left the culture's LSDL untouched, so the renamed object's
linguistic entity kept a Binding naming the OLD object — an orphan. Since the set_synonyms duplicate-name fix
prunes entities bound to gone objects, that orphan was deleted by the very next set_synonyms / enable_qna call:
a rename silently destroyed authored synonyms one write later, and you could not rename your way out of a
Q&A name collision because the stale entity still collided. Every rename path (rename_object, find-and-replace's
rename branch, apply_plan's rename item, and the property grid's Name writes via set_property / set_properties —
now all routed through the one Session.Rename seam) cascades into every JSON culture, walking the whole document
(Relationships / SemanticSlots phrasings carry the same Binding shape as Entities) and following each Binding to
the new name: ConceptualEntity for tables (including the table name carried by its columns / measures /
hierarchies / levels and by relationship bindings), ConceptualProperty for columns and measures, and the
Hierarchy / HierarchyLevel slots for hierarchies and levels. Fail-safe like the pruner — only bindings whose
shape resolves positively are touched — and non-throwing by contract: a culture whose recommit the live
validator rejects (that schema was already uncommittable) is left untouched and surfaced as a warning, never a
throw that would strand apply_plan's per-item isolation with a half-cascaded rename. A refused culture is
retried once at the end of the mutation batch — in a multi-rename batch a later rename can remove the very
collision that blocked an earlier cascade — and only failures that survive the retry warn, prescribing the
collision remedy only when the failure positively is the duplicate-key refusal. The cascade runs inside the
rename's mutation, so one undo reverts the name and the LSDL together and dry_run rehearses both. Two follow-ups
from the set_synonyms review land with it: the prune's over-pinning no longer treats enum / metadata tokens
(State, Type, Version, …) in SemanticSlots / Relationships as entity references, and enable_qna's collision
advisory now distinguishes the refused exact tier from the write-through plural-fold tier per member (a mixed
group names only its exact pair as refused). Verified: 13 new xUnit cases (RenameLsdlCascadeTests) + the full
suite, McpSmoke, coverage-oracle.

### Fixed: set_synonyms works on models with duplicate object names

The AS linguistic validator folds every LSDL entity's bound name into one term dictionary (lower-case,
camel-split, lemmatise), so entities generated for hidden shadow columns duplicating visible names made every
synonym write fail model-wide with an opaque duplicate-key error. set_synonyms (and the apply_plan /
find-and-replace synonym paths, which surface the same advisories) now refuses hidden targets, self-heals by
pruning entities bound to hidden or deleted objects (entities it can't read, and entities referenced from
SemanticSlots/Relationships, are kept untouched), pre-flights name collisions in two tiers (an exact
normalised match refuses with the colliding objects named; a plural-fold match only warns, since the fold
approximates the validator), and rewraps the validator's residual throw with the remedy. The LSDL write is
undo-tracked, so undo_change restores it and dry_run rehearsals roll it back. enable_qna runs the same
self-heal on an existing schema through both doors and surfaces surviving collisions as a warning instead of
failing. New deterministic readiness rule DAC-QNA-NAME-COLLISIONS (DataAgentConfig, High) reports one finding
per collision group of visible objects, sharing the writer's two-tier normaliser; it is dormant when the
model has no linguistic schema and no Q&A signal. Verified: 13 new xUnit cases (SetSynonymsCollisionTests)
+ the full suite, McpSmoke, AirSmoke.

### Fixed: Installed extension auto-heals its engine pairing

Marketplace builds ignore the development-only engine DLL override and verify genuine executable mismatches, while
an alive legacy owner without recorded provenance remains attachable with a one-time warning. Activation refreshes
the Semanticus MCP launch entry when an extension update changes the bundled engine path, and a Development Host can
attach to an already-running owner even when no local launch candidate is available. Installed-extension restarts
never rebuild development source; the Development Host retains its explicit DLL and rebuild workflow.

### Fixed: Shared connection operations fail closed to agent attribution

Remembering a connection, preparing a working copy and setting a publish destination now default omitted or blank
shared-engine origins to agent attribution. The UI boundary remains human and the MCP boundary remains agent. This
also prevents an origin-less working-copy request from entering interactive authentication fallback.

### Fixed: Bounded and cancellable cloud operations

Fabric and Power BI REST calls now enforce response, pagination and decoded-definition limits, fail loudly when
pagination is truncated, and reuse pooled HTTP connections. Cancellation now crosses both public doors into token
acquisition, throttling waits, response reads and long-running-operation polling, while an internal HTTP timeout in
a setup read or live write is returned as a scrubbed result error rather than misreported as caller cancellation. Git commands no longer wait for
interactive credentials and are stopped with their process tree after a bounded timeout; clone, push, pull and fetch
receive a separate 15-minute transfer budget.
### Fixed: Atomic model-session context ownership

Opening or creating a model now exchanges one complete context containing the model session, live query binding,
Change Plan, spec, baselines, workflow runs and AI-readiness cache. Work admitted to an old session or connection
drains before teardown, stale callers are refused, and guarded DAX, DMV, table-preview and pivot results prepared
against a replaced endpoint cannot appear as current query results. Active workflow runs and their verification
snapshots survive a model-opening step; model-scoped stores reset. An XMLA connection also records the current session's
deploy origin and in-memory authentication cache, so later deploy and refresh operations target the attached source.
### Fixed: RPC authority is connection-bound

Named-pipe clients can no longer choose whether a request is attributed to a person or an AI Assistant by changing
an `origin` argument. VS Code proves a per-workspace challenge before JSON-RPC begins, while MCP proxy connections
are bound to the agent role. Human governance mutations are registered only on the authenticated VS Code
connection, so an agent connection cannot discover or invoke them. The challenge is held in encrypted VS Code
SecretStorage and delivered to the owner engine over stdin rather than process arguments, environment variables,
logs, or broker files. The server now explicitly accepts or rejects the proof before JSON-RPC begins. If an
MCP-owned engine rejects the human proof, VS Code stops it and starts the authenticated UI owner; the AI Assistant
then reconnects through the normal resilient agent proxy instead of leaving the UI silently locked out.

### Fixed: Model compare container metadata

Model compare now includes authored model, table and calculation-group container metadata instead of comparing
only their child objects. Supported changes round-trip through selective apply and live push. Live push also carries
table data categories and measure/column annotations. Metadata that a path cannot carry is reported and withheld
from the synchronized set, preventing a false equal or false synchronized result.

### Fixed: Concurrent DAX trace isolation

Server timings, query plans, and EVALUATEANDLOG captures now own a per-connection query lane from trace setup
through teardown. Ordinary UI or AI Assistant queries on that XMLA session wait for the capture, preventing
their events from being mixed into another request's evidence while unrelated model connections remain
independent.

### Fixed: Docs relationship diagrams and workspace

Documentation relationship diagrams now lay out connected components before tiling isolated tables, preserve
their routed relationship edges, and never magnify a narrow graph into giant cards. The shared renderer fixes
both the live preview and exported HTML. The Docs authoring surface now uses the full Studio workspace instead
of the centered report-width cap.

### Added: Coverage-oracle freshness is gated by CI

CI now has a dedicated coverage-oracle job that fails when the committed public-surface inventory no longer
matches tracked source. An auto-discovered extension contract prevents the job or its check command from being
removed silently.

### Fixed: Cloud report listing fails before sign-in on invalid input

Published-report listing now rejects a missing workspace id before attempting authentication. Direct MCP and UI
door regression coverage proves the failure is independent of ambient sign-in state and leaves the shared model
session, revision, activity stream and change broadcast untouched. This closes the supported-operation reference
floor: every supported MCP operation now has at least one tracked test-source reference.

### Fixed: Fabric spec generation fails before sign-in on invalid input

The Fabric SQL spec generator now rejects a missing endpoint, missing database or unsupported storage mode before
attempting authentication. Public-door regression coverage proves these failures preserve the current spec and emit
no false success activity or change broadcast.

### Added: Public Primer write contract

Direct regression coverage now proves that the Free MCP and UI doors write the same model-scoped Primer,
attribute the activity correctly, leave the semantic model revision untouched, and preserve the last valid file
when malformed Markdown is refused.

## [1.0.1] - 2026-07-14

### Fixed: Marketplace listing and repository links

Corrected the public limitation text to match the shipped product: cloud report discovery is supported behind
consent, while Fabric Git and Data Agent writes preview as a dry run and write only on explicit confirmation.
Repository, homepage and issue links now point to `tenfingerseddy/semanticus-vscode`.

### Added: Five matching-host platform packages

Added target-specific packages for Windows x64, Windows ARM64, Linux x64, macOS Intel and macOS Apple Silicon.
Each package is built on its matching GitHub Actions runner, then its bundled engine is extracted and executed
before the artifact is accepted. The aggregate release gate verifies all five SHA-256 digests and their runner,
commit and Actions-run evidence before producing the upload bundle.

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
  signed into a DIFFERENT tenant than the model's XMLA session (hit live: one tenant's workspaces listed against
  a model bound to a different tenant). The header gains a persisted auth-mode picker (az cli / Entra interactive / device
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
curated Finance PBIP / a private-tenant Fabric service principal).

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
- **Live-tenant verification** (private-tenant Fabric service principal) — read-only Fabric REST + XMLA model lanes,
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
