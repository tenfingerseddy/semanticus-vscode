# The one run of the V1i held-out set: protocol, fixed before the run

The set (`../set`, committed in e1acfc85 with its sha256) has not been run. This file, the harness and the scorer beside it
are fixed now, before the run. The run happens once. Whatever it shows is reported; nothing in the product or the harness is
tuned afterwards.

## What runs

- The engine under test: m11/feedback-rules at 36c61aa5, built Debug in a throwaway worktree (`~/.cache/v1iv/probe`, the
  TabularEditor submodule at 7029129a), with the harness `VerifierProbeV1i.cs` (copied here as `VerifierProbeV1i.cs.txt`)
  added as a test file. The harness was shaken down on a model of the verifier's own (`DryRun`, 13 objects, 4 changes), never
  on this set.
- The set is read from a copy checked against `../SHA256SUMS` just before the run.
- Every engine runs in-process with a scratch HOME and a scratch per-user home (DecisionFixture), no key in the environment,
  except the live runs, which read the Typesafe key from its one home into that one test process.

## The runs (185)

| Kind | Modes | Doors | Paths | Runs |
|---|---|---|---|---|
| Whole flow | free, whole, label, slow, silent | studio, mcp | all seven, in order | 50 |
| One path, Free | free | studio, mcp | each of the seven alone | 70 |
| One path, Pro | whole, label | studio, mcp | run_plan, apply_plan, row_approve, each alone | 60 |
| Live | live | studio | all seven | 5 |

The seven write paths, on the run's door (studio = the person's pipe door `EngineRpcTarget`, mcp = `McpTools`):
`run_plan` (Run the plan: `applyPlanItems` / `apply_plan_items` with the plan's readyDigest, up to four passes), `apply_plan`
(Pending changes' apply of every approved item: `applyPlan` / `apply_plan`), `row_approve` (every Model Plan row that holds a
format or best-practice line, approved on its own with its digest: `modelPlanAction` / `model_plan_action`), `one_press` (the
fix of every auto-fixable BPA finding, one at a time: `bpaFix` / `apply_fix`), `fix_all` (`bpaFixAll` / `apply_safe_fixes bpa`),
`safe_fixes` (`applySafeFixes` / `apply_safe_fixes air`), `ai_ready` (`makeAiReady` / `make_model_ai_ready`).

Modes. free: Free, no Jev. Pro modes (consent given; the folder question is always answered "no folder"; confidence 0.97):
whole: Jev answers `whole_number` for every measure and column, all the time (the most harmful family for this harm).
label: Jev answers the family of the object's label as it stands (`jev-script.json`, made by `make-jev-script.py` from Sol's
labels: count to whole_number; money to currency; percent to percent; decimal, fx, ratio, days, seconds, milliseconds to
decimal; date to date_or_time; year, key, fixed5, fixed6 and every U label to text_or_none; once a change is made to an
object, the family of its label after the change). slow: as label until the first suggestion is read, then every ask waits
(the relay is slow) through the changes, the ticks and all seven paths; then Jev is released, answers, and `run_plan` and
`apply_plan` run once more. silent: as label until the first suggestion is read, then every ask is not decided (the service
does not answer). live: the real Jev (the vendor route), once per model, studio door, default session budget.

## The flow of one run

1. Copy the model folder into the run's scratch workspace and open it. Pro: consent, then Jev.
2. THE SUGGESTION. `propose_plan` (whole model, AI items on, up to 200) on the door; wait for Jev; read S0 on the door: every
   format line on the Model Plan, every format item on the change plan, and every BPA finding on a format rule with the words
   its fix prompt gives (a format it names as "the rule suggests "X"" is a suggestion of X).
3. The person decides at S0: every line that is a suggestion (a Model Plan suggestion line not yet kept, or a change plan item
   in state proposed) whose format Sol's judge calls RIGHT for the object as it stands.
4. Sol's changes marked "before the person ticks" are made as the person (engine calls with origin human: rename, formula,
   data type, description, delete; source column and partition schema as one edit), in the order of `changes.json`. Read S1.
5. The person ticks, on the studio door (`setPlanItem(id, approved: true)`, the person's tick), every line decided at S0 that
   the page still shows with the same format (a line the page now shows with another format is judged again instead), then
   every suggestion shown at S1 that the judge calls RIGHT for the object as it now stands. The product may refuse a tick.
6. Sol's changes marked "after the person ticks, before the write" are made the same way. Read S2 and, read only,
   `FormatFill.MayFill(object, "#,0")` for every measure and column.
7. THE WRITES: the run's paths in the order above. The format string of every measure, column and calculation item is read
   before and after each path; every difference is a write of that path.
8. Read S4 (and S3 in slow mode after the release).

## Scoring (`score.py`, Sol's `judge.py` unedited)

- SAFETY. Every format written, in every run, judged against the object's label after the changes (`after_changes: true`).
  A WRONG write is "after the person's tick" when the object carried a tick the product recorded as the person's keep, else
  "automatic".
- RECALL, the brief's "objects with a RIGHT format that a rule proves". Denominator: the objects of the run that are blank
  just before the writes, not U, for which the format fill rule proves a whole-number fill at that moment (MayFill true) and
  "#,0" is RIGHT by the judge. Numerator: those that got a RIGHT format written, or suggested (a suggestion line or item, or
  advice naming a format, that the judge calls RIGHT, read after every change; S0 and S1 count only for an object no change
  touched). Reported also without the advice words.
- SECONDARY, reported with no bar: the same share over Sol's `provable_from_model` objects, and over every non-U object.
- U: what was written or suggested on the objects Sol labelled unsure.

## The stop rule, as it is applied

1. 0 WRONG formats written, in every one of the 185 runs (any mode, any door, any path, automatic or after the tick).
2. Recall (primary) at least 70% in the whole-flow runs of free, label (each door) and live. The whole, slow and silent modes
   are adversarial or degraded Jev; their recall is reported, not gated.
3. U reported.

## Also recorded

Every tick and its answer, every change and how it was made, every path's own words, Jev's asks (and, live, the requests and
input tokens), and any error a step threw (a step that throws is recorded and the run goes on). The 180 scripted runs go in one
test invocation and the 5 live runs in another. If a run hangs past ten minutes and the test host is stopped, that run is
recorded as a hang and only the runs not yet executed are started again; a finished run is never run a second time.
