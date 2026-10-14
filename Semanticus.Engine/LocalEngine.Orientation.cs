using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// §6 "orientation over recall" (docs/harness-engineering.md): the ONE blessed session-start primer.
    /// An agent arrives amnesiac (fresh session / post-compaction / handoff); this composite hands it the
    /// map in a single round-trip, ruthlessly minimal (a test enforces ≤~2k tokens) and pointing at the
    /// drill-down op per section. It lives in the engine (not McpTools) because two of its sections —
    /// active workflow runs and the L0 experience tail — have no standalone IEngine surface to compose from,
    /// and because ONE composite is one RPC hop for the attaching MCP door instead of seven.
    /// </summary>
    public sealed partial class LocalEngine
    {
        public async Task<ModelSummary> GetOrientationAsync()
        {
            var overview = await SessionInfoAsync();               // safe with no session (returns an empty SessionInfo)
            var hasSession = _sessions.Current != null;
            var conn = ConnectionBrief.From(await ConnectionStatusAsync());
            var ent = StampedInfo(_entitlement);

            ReadinessSummary readiness = null;
            ModelGraphSummary graph = null;
            bool calTiNoCalendars = false;
            if (hasSession)
            {
                // ONE readiness scan feeds both the grade summary AND the CAL-TI suggestion below — the
                // CAL-TI-NO-CALENDAR rule already computes "classic TI present + calendars supported + none
                // defined" for us, so we reuse its finding rather than re-deriving the signal.
                var sc = await AiReadinessScanAsync();
                readiness = ReadinessSummary.From(sc);
                graph = ModelGraphSummary.From(await GetModelGraphAsync());
                calTiNoCalendars = sc.Findings.Any(f => !f.Waived && f.RuleId == "CAL-TI-NO-CALENDAR");
            }

            var summary = new ModelSummary
            {
                Overview = overview,
                Connection = conn,
                Entitlement = new EntitlementBrief
                {
                    Tier = ent?.Tier ?? "free",
                    Reason = ent != null && !string.Equals(ent.Tier, "pro", StringComparison.Ordinal) ? ent.Reason : null,
                    TrialDaysLeft = ent != null && ent.IsTrial ? ent.TrialDaysLeft : null,
                    TrialEnded = ent != null && ent.TrialEnded ? true : null,
                },
                Readiness = readiness,
                Graph = graph,
                // Model notes became free on 22 September, so every orientation carries the Primer. It used to
                // be withheld here because get_model_primer refused a free read of the same document, and that
                // refusal is gone.
                Primer = hasSession
                    ? PrimerBrief.From(await GetPrimerCoreAsync(_sessions.CurrentContext, PrimerLocusFor(_sessions.CurrentContext)))
                    : null,
                ActiveWork = await BuildActiveWorkAsync(),
                LastSession = BuildLastSession(),
                Note = "Session-start orientation. Drill down per section: connection_status · get_entitlement · get_model_primer · " +
                       "ai_readiness_scan (findings) · get_model_graph (full ER) · get_workflow_run · get_plan · " +
                       "get_spec · list_calendars. Deliberately omits the full findings list, per-object detail, and DAX bodies.",
            };
            // The suggestions ARE the plan's top three now (Jev M2). The field keeps its name and shape for one
            // release so nothing reading it breaks, but what fills it is get_next_actions, which is the top of
            // the same plan the Plan page shows. Two lists of "what to do next" that could disagree is exactly
            // the thing section 2a set out to kill.
            //
            // It falls back to the old fixed ladder when the plan could not be composed. That is not a hedge: a
            // plan needs a readiness scan, a cleanup sweep and a sidecar read, and orientation must still answer
            // on a session where one of those cannot run. The fallback is the same four hints this file has
            // always produced.
            NextAction[] actions;
            ModelPlanView plan = null;
            try
            {
                plan = hasSession ? await GetModelPlanAsync().ConfigureAwait(false) : null;
                actions = plan != null ? TopThree(plan) : Array.Empty<NextAction>();
            }
            catch
            {
                actions = Array.Empty<NextAction>();
            }

            if (actions.Length == 0)
                actions = SuggestNextActions(summary, hasSession, calTiNoCalendars, graph, readiness);

            // The tasks the person gave the assistant (Plan at the heart, contract 6.2), read off the same plan, so
            // the summary, the fold line and the Plan page cannot disagree. Omitted when there are none.
            var tasks = TasksBrief(plan);
            if (tasks != null)
            {
                summary.ActiveWork ??= new ActiveWorkBrief();
                summary.ActiveWork.Tasks = tasks;
                // A draft that needs the person says so first (composer contract 5.2), then the parts waiting.
                foreach (var d in (tasks.Drafts ?? Array.Empty<Jev.ActiveDraftRef>()).Where(d => d.State == PlanStateWords.NeedsYou).Reverse())
                    actions = new[]
                    {
                        new NextAction { Op = "get_draft", Args = "draftId: " + d.DraftId, Reason = "'" + d.Title + "' needs the person: " + d.Note },
                    }.Concat(actions).ToArray();
                if (tasks.Ready is { Length: > 0 } ready)
                    actions = new[]
                    {
                        new NextAction
                        {
                            Op = "list_assistant_requests",
                            Reason = "Your person has " + ready.Length + (ready.Length == 1 ? " task" : " tasks")
                                   + " ready for you on the Plan. Call list_assistant_requests.",
                        },
                    }.Concat(actions).ToArray();
            }

            summary.SuggestedNextActions = actions.Length == 0 ? null : actions;   // omit when terminal (§1: no busywork)
            return summary;
        }

        /// <summary>ActiveWork.Tasks (contract 6.2): the switch, the Ready tasks for the assistant with whether each
        /// is independent, how many wait, and which are held. Null when there is nothing to say.</summary>
        internal static Jev.ActiveTasksBrief TasksBrief(ModelPlanView plan)
        {
            if (plan == null) return null;
            var offer = Jev.PlanOffer.From(plan);
            var paused = offer.Switch == PlanAssistantQueue.Paused;
            var open = (plan.Items ?? Array.Empty<ModelPlanItem>())
                .Count(i => PlanTasks.IsTaskId(i.Id) && i.Doer == PlanDoers.Assistant && PlanStateWords.IsOpen(i.StateWord));
            var listed = offer.Listed();
            var drafts = DraftBriefs(plan);
            if (listed.Count == 0 && offer.Waiting.Count == 0 && offer.Claimed.Count == 0 && !(paused && open > 0) && drafts.Length == 0) return null;
            return new Jev.ActiveTasksBrief
            {
                Switch = offer.Switch,
                Ready = listed.Count == 0 ? null : listed.Select(r => new Jev.ActiveTaskRef
                {
                    Id = r.Id, Title = r.Title, Independent = offer.IsIndependent(r.Id),
                    Draft = r.OriginLink?.Kind == PlanOriginKinds.Draft ? r.OriginLink.Ref : null,
                }).ToArray(),
                Drafts = drafts.Length == 0 ? null : drafts,
                Waiting = offer.Waiting.Count,
                Claimed = offer.Claimed.Count == 0 ? null : offer.Claimed.Select(r => new Jev.ActiveTaskRef { Id = r.Id, Title = r.Title }).ToArray(),
                Note = paused ? "Tasks are paused by the person." : null,
            };
        }

        /// <summary>Active workflow runs + a loaded change-plan's size + a loaded spec's name — each omitted
        /// when absent. Runs are read under the captured context's workflow gate; plan/spec each own
        /// their own gate, so they're read OUTSIDE it to avoid nesting the locks.</summary>
        private async Task<ActiveWorkBrief> BuildActiveWorkAsync()
        {
            var context = _sessions.CurrentContext;
            ActiveRunBrief[] runs = null;
            await context.WorkflowGate.WaitAsync();
            try
            {
                EnsureContextCurrent(context, "Active work read");
                var active = context.WorkflowRuns.ActiveRuns();
                if (active.Count > 0)
                    runs = active.Select(r => new ActiveRunBrief
                    {
                        RunId = r.RunId,
                        Workflow = r.Def?.Name,
                        CurrentStep = r.CurrentStep?.Id,
                    }).ToArray();
            }
            finally { context.WorkflowGate.Release(); }

            int? planItems = null;
            await context.PlanGate.WaitAsync();
            ChangePlanView plan;
            try
            {
                EnsureContextCurrent(context, "Active work read");
                plan = BuildView(context.Plans.Current, context.Session?.Revision ?? 0);
            }
            finally { context.PlanGate.Release(); }
            if (plan?.Items is { Length: > 0 } items) planItems = items.Length;

            string specName = null;
            await context.SpecGate.WaitAsync();
            SpecView spec;
            try
            {
                EnsureContextCurrent(context, "Active work read");
                spec = context.Spec.View();
            }
            finally { context.SpecGate.Release(); }
            if (spec?.Spec != null) specName = string.IsNullOrWhiteSpace(spec.Spec.Name) ? "(unnamed spec)" : spec.Spec.Name;

            if (runs == null && planItems == null && specName == null) return null;
            return new ActiveWorkBrief { Workflows = runs, PlanItems = planItems, SpecName = specName };
        }

        /// <summary>The current session's experience-log file, mirroring ExperienceTee's placement rule: beside
        /// the model for a durable file-backed session; the workspace fallback for a live/unsaved (ephemeral)
        /// anchor, or the GLOBAL store when there's no workspace either — so orientation/harness_report read back
        /// exactly where the tee wrote (an ephemeral-no-workspace session's log now lands in the per-user Semanticus home, not /dev/null).</summary>
        private string CurrentExperienceFile()
        {
            var workspaceFallback = _workspaceDir == null
                ? ExperienceStore.GlobalFile()
                : Path.Combine(_workspaceDir, LayoutStore.DirName, ExperienceStore.FileName);
            var s = _sessions.Current;
            if (s == null) return workspaceFallback;
            return !ExperienceStore.IsEphemeralAnchor(s.SourcePath) ? ExperienceStore.FileFor(s.SourcePath) : workspaceFallback;
        }

        /// <summary>The last ~5 entries of the L0 log for continuity. FAIL-SOFT by contract: a missing log is
        /// null; an unreadable/corrupt log yields a Note (never an exception) — orientation must never die on
        /// its own memory.</summary>
        private LastSessionBrief BuildLastSession()
        {
            try
            {
                var file = CurrentExperienceFile();
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;

                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch { return new LastSessionBrief { Note = "Experience log present but unreadable: skipped." }; }

                var entries = new List<LastEntry>();
                var corrupt = false;
                foreach (var line in lines.Reverse())   // newest-first; append-only JSONL
                {
                    if (entries.Count >= 5) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        entries.Add(new LastEntry
                        {
                            When = r.TryGetProperty("when", out var w) ? w.GetString() : null,
                            Origin = r.TryGetProperty("origin", out var o) ? o.GetString() : null,
                            Label = r.TryGetProperty("label", out var l) ? l.GetString() : null,
                        });
                    }
                    catch { corrupt = true; }   // one bad tail line must not sink the whole read
                }
                if (entries.Count == 0)
                    return corrupt ? new LastSessionBrief { Note = "Experience log unparseable: no readable entries." } : null;
                entries.Reverse();   // present chronologically
                return new LastSessionBrief
                {
                    Recent = entries.ToArray(),
                    Note = corrupt ? "Some log lines were unparseable and skipped." : null,
                };
            }
            catch { return null; }   // absolute backstop — the log can never break orientation
        }

        /// <summary>DETERMINISTIC next-step hints (§1 result contract — no inference). Ordered most-valuable
        /// first and capped at 4; empty when genuinely terminal so the caller omits the section.</summary>
        private static NextAction[] SuggestNextActions(
            ModelSummary s, bool hasSession, bool calTiNoCalendars, ModelGraphSummary graph, ReadinessSummary readiness)
        {
            var list = new List<NextAction>();

            if (!hasSession)
            {
                // Nothing else is actionable without a model — this is the whole suggestion.
                list.Add(new NextAction { Op = "open_model", Args = "path: <.pbip | TMDL folder | .bim>", Reason = "No model is open: orientation needs a session." });
                return list.ToArray();
            }

            if (s.ActiveWork?.Workflows is { Length: > 0 } wf)
                list.Add(new NextAction { Op = "get_workflow_run", Args = "runId: " + wf[0].RunId, Reason = "A workflow run is active: resume it before starting new work." });

            if (s.Overview?.HasUnsavedChanges == true)
                list.Add(new NextAction { Op = "save_model", Reason = "The model has unsaved changes." });

            if (readiness != null && (readiness.Grade == "D" || readiness.Grade == "F"))
                list.Add(new NextAction { Op = "make_model_ai_ready", Reason = $"AI-readiness grade is {readiness.Grade}: apply the safe fixes (or get_fix_prompt for the AI-authored ones)." });

            if (graph?.DisconnectedTables is { Length: > 0 } dt)
                list.Add(new NextAction { Op = "get_model_graph", Reason = $"{dt.Length} visible table(s) are in no relationship: the star-schema smell." });

            if (calTiNoCalendars)
                list.Add(new NextAction { Op = "define_calendar_from_template", Reason = "Classic time-intelligence DAX but no calendars defined (the compatibility level supports them)." });

            return list.Take(4).ToArray();
        }
    }
}
