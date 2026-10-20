// The Next line on the engine (1.5, option A; Kane picked it on 11 Oct 2026).
//
// One suggested next step, and where the person is on the seven-part journey, worked out from what the engine already keeps:
// unsaved objects, the Plan's ready and done tasks (read off each compose), the last check run against the last save, a
// History run of the same steps on three or more objects, and the assistant's own suggest_next.
//
// SOURCE ORDER. 1) The assistant's current suggest_next, until a newer note takes it away (the rule notes-store.ts uses).
// 2) Pro: Jev (next.step) chooses among the kinds the rules found eligible, above its bar; it never invents a step and every
// word is the rules' table. 3) The rules, first match wins: the Free answer, and Pro's answer below the bar or with no key.
//
// NEVER ON THE COMMIT PATH, NEVER WAITS FOR JEV. Bus events only schedule a recompute after the changes settle (750 ms);
// Jev is asked there, once per distinct input digest, on Pro only. getNextStep computes the rules' answer at once and adds
// a Jev pick only if one is already known for these exact inputs. studio/nextStep goes out only when the view changes.
//
// The person's preference (off, stopped kinds) is a small file in the per-user home; hiding a step is this session only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>1.5: Jev's next.step pick is live in this build.</summary>
        internal const bool NextStepJevDecides = true;
        internal bool? NextStepJevDecidesForTest;
        private bool NextStepJevLive => NextStepJevDecidesForTest ?? NextStepJevDecides;

        internal const string NextStepFileName = "next-step.json";
        internal const string NextStepQuestion = "next_kind";
        /// <summary>How long the changes must be quiet before the line is worked out again (and Jev asked, on Pro).</summary>
        internal TimeSpan NextStepSettle { get; set; } = TimeSpan.FromMilliseconds(750);
        /// <summary>A test's own per-user home for the preference file. Null on every real path (SemanticusHome.Root()).</summary>
        internal string NextStepHomeForTest;

        private readonly object _ns = new object();
        private bool _nsWired, _nsActive;
        private string _nsSession;
        private readonly HashSet<string> _nsUnsavedRefs = new HashSet<string>(StringComparer.Ordinal);
        private bool _nsSavedSinceChange, _nsChecksRun, _nsPlanJustFinished;
        private int _nsChecksFailed;
        private string _nsPlanSession;
        private int _nsReady, _nsDone;
        private string _nsTopTitle, _nsTopKind;
        private readonly List<(string Ref, string Sig)> _nsRecent = new List<(string, string)>();
        private const int NsRecentCap = 60;
        private readonly HashSet<string> _nsHidden = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _nsJevPick = new Dictionary<string, string>(StringComparer.Ordinal);   // digest -> kind ("" = rules stand)
        private string _nsLastSent;
        private long _nsGeneration;
        private Task _nsBackground = Task.CompletedTask;
        private NextStepPrefs _nsPrefs;
        /// <summary>Why the last next.step ask did or did not pick, for tests and the log. Never on the wire.</summary>
        internal string NextStepJevNote;

        private sealed class NextStepPrefs
        {
            public bool Off { get; set; }
            public List<string> Stopped { get; set; } = new List<string>();
        }

        // ---- the two doors ---------------------------------------------------------------------------------------------

        public async Task<NextStepView> GetNextStepAsync()
        {
            WireNextStep();
            lock (_ns) _nsActive = true;
            var view = await ComputeNextStepAsync(askJev: false).ConfigureAwait(false);
            PublishNextStepIfChanged(view);
            return view;
        }

        public async Task<NextStepView> SetNextStepAsync(string action, string arg)
        {
            WireNextStep();
            lock (_ns) _nsActive = true;
            var a = (action ?? "").Trim();
            var v = (arg ?? "").Trim();
            switch (a)
            {
                case "hide":
                case "unhide":
                    if (v.Length == 0) throw new ArgumentException($"{a} needs the step id from get_next_step (it starts with ns-).");
                    lock (_ns) { if (a == "hide") _nsHidden.Add(v); else _nsHidden.Remove(v); }
                    break;
                case "stopKind":
                case "resumeKind":
                    var known = NextStepRules.Assistant == v || DecisionRegistry.NextStepKinds.Contains(v);
                    if (!known) throw new ArgumentException($"'{v}' is not a kind of next step. Use one of: {string.Join(", ", DecisionRegistry.NextStepKinds)}, {NextStepRules.Assistant}.");
                    MutatePrefs(p => { p.Stopped.Remove(v); if (a == "stopKind") p.Stopped.Add(v); });
                    break;
                case "turnOff": MutatePrefs(p => p.Off = true); break;
                case "turnOn": MutatePrefs(p => p.Off = false); break;
                default:
                    throw new ArgumentException($"'{action}' is not an action. Use one of: hide, unhide, stopKind, resumeKind, turnOff, turnOn.");
            }
            var view = await ComputeNextStepAsync(askJev: false).ConfigureAwait(false);
            PublishNextStepIfChanged(view);
            return view;
        }

        // ---- listening ---------------------------------------------------------------------------------------------------

        /// <summary>Subscribe once (the constructor calls it), so the counts are right from the first commit. Nothing is worked out
        /// in the background until something has read the line once (the Studio on mount, or get_next_step). Never throws.</summary>
        internal void WireNextStep()
        {
            lock (_ns) { if (_nsWired) return; _nsWired = true; }
            _sessions.Bus.Changed += OnNextStepChange;
            _sessions.Bus.StudioSuggest += _ => ScheduleNextStep();
            _sessions.Bus.StudioShow += _ => ScheduleNextStep();
        }

        private void OnNextStepChange(ChangeNotification n)
        {
            try
            {
                if (n == null) return;
                lock (_ns)
                {
                    if (n.IsSwap || (n.SessionId != null && _nsSession != null && n.SessionId != _nsSession)) ResetNextStepLocked(n.SessionId);
                    _nsSession ??= n.SessionId;
                    if (n.IsSwap) { }
                    else if (!n.IsCommit && n.Label == "save")
                    {
                        _nsUnsavedRefs.Clear();
                        _nsSavedSinceChange = true;
                        _nsChecksRun = false;
                    }
                    else if (n.IsCommit)
                    {
                        _nsSavedSinceChange = false;
                        _nsChecksRun = false;
                        var refs = (n.Deltas ?? Array.Empty<ChangeDelta>()).Where(d => d?.Ref != null).ToList();
                        foreach (var d in refs) _nsUnsavedRefs.Add(d.Ref);
                        // A repeat counts steps done by hand: one object per commit. A bulk write is one step, not a run.
                        var distinct = refs.Select(d => d.Ref).Distinct(StringComparer.Ordinal).ToList();
                        if (n.Replay == null && distinct.Count == 1)
                        {
                            var sig = string.Join("|", refs.Select(d => d.Kind + ":" + RefKind(d.Ref) + ":" + string.Join(",", (d.Props ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal))).Distinct().OrderBy(x => x, StringComparer.Ordinal));
                            _nsRecent.Add((distinct[0], sig));
                            if (_nsRecent.Count > NsRecentCap) _nsRecent.RemoveRange(0, _nsRecent.Count - NsRecentCap);
                        }
                    }
                }
                ScheduleNextStep();
            }
            catch { /* never into a commit */ }
        }

        private void ResetNextStepLocked(string sessionId)
        {
            _nsSession = sessionId;
            _nsUnsavedRefs.Clear();
            _nsSavedSinceChange = _nsChecksRun = _nsPlanJustFinished = false;
            _nsChecksFailed = 0;
            _nsPlanSession = null;
            _nsReady = _nsDone = 0;
            _nsTopTitle = _nsTopKind = null;
            _nsRecent.Clear();
            _nsHidden.Clear();
        }

        /// <summary>Called at the end of every plan compose: the Plan's ready and done counts, and its top task.</summary>
        private void NoteNextStepPlan(ModelPlanView view, string sessionId)
        {
            try
            {
                if (view == null) return;
                WireNextStep();
                var ready = view.Items.Where(i => i.Status == ModelPlanStatuses.Ready).ToList();
                var done = view.Items.Count(i => i.Status == ModelPlanStatuses.Done);
                lock (_ns)
                {
                    if (_nsSession != null && sessionId != null && sessionId != _nsSession) ResetNextStepLocked(sessionId);
                    _nsSession ??= sessionId;
                    var wasReady = _nsPlanSession == sessionId ? _nsReady : 0;
                    if (ready.Count > 0) _nsPlanJustFinished = false;
                    else if (wasReady > 0 && done > 0) _nsPlanJustFinished = true;
                    _nsPlanSession = sessionId;
                    _nsReady = ready.Count;
                    _nsDone = done;
                    _nsTopTitle = ready.FirstOrDefault()?.Title;
                    _nsTopKind = ready.FirstOrDefault()?.Section;
                }
                ScheduleNextStep();
            }
            catch { /* the plan read must never fail for the line */ }
        }

        /// <summary>Called when a check run finishes (RememberTestRun).</summary>
        private void NoteNextStepChecks(TestSuiteRunResult run)
        {
            try
            {
                if (run == null || run.Error != null || run.Scope?.Mode == "refused") return;
                lock (_ns)
                {
                    _nsChecksRun = true;
                    _nsChecksFailed = run.Health?.Failed ?? 0;
                    _nsPlanJustFinished = false;
                }
                ScheduleNextStep();
            }
            catch { }
        }

        /// <summary>Debounce: every event moves the settle point; one recompute runs once the changes are quiet.</summary>
        private void ScheduleNextStep()
        {
            long gen;
            lock (_ns) { if (!_nsActive) return; gen = ++_nsGeneration; }
            var settle = NextStepSettle;
            var t = Task.Run(async () =>
            {
                await Task.Delay(settle).ConfigureAwait(false);
                lock (_ns) if (gen != _nsGeneration) return;
                try { PublishNextStepIfChanged(await ComputeNextStepAsync(askJev: true).ConfigureAwait(false)); }
                catch { /* the line is a help, never a failure */ }
            });
            lock (_ns) _nsBackground = Task.WhenAll(_nsBackground, t);
        }

        /// <summary>For tests: wait until every scheduled recompute has run.</summary>
        internal async Task NextStepIdleForTest()
        {
            for (var i = 0; i < 50; i++)
            {
                Task t; lock (_ns) t = _nsBackground;
                await t.ConfigureAwait(false);
                lock (_ns) if (ReferenceEquals(t, _nsBackground)) return;
            }
        }

        private void PublishNextStepIfChanged(NextStepView view)
        {
            if (view == null) return;
            var json = JsonSerializer.Serialize(view);
            lock (_ns)
            {
                if (json == _nsLastSent) return;
                _nsLastSent = json;
            }
            _sessions.Bus.PublishStudioNextStep(view);
        }

        // ---- working it out ----------------------------------------------------------------------------------------------

        private async Task<NextStepView> ComputeNextStepAsync(bool askJev)
        {
            var prefs = ReadPrefs();
            var inputs = await NextStepInputsAsync().ConfigureAwait(false);
            var stage = NextStepRules.StageOf(inputs);
            var view = new NextStepView { On = !prefs.Off, Stage = stage, StageName = NextStepRules.StageNames[stage - 1] };
            if (prefs.Off || !inputs.ModelOpen) return Finish(view, null);

            var stopped = new HashSet<string>(prefs.Stopped ?? new List<string>(), StringComparer.Ordinal);

            // 1) The assistant's own suggestion, while no newer note has taken it away.
            var said = CurrentAssistantSuggestion();
            if (said != null && !stopped.Contains(NextStepRules.Assistant))
            {
                var first = said.Steps[0];
                return Finish(view, new NextStepStep
                {
                    Kind = NextStepRules.Assistant, Ask = first.Label, Why = said.Say, Source = NextStepRules.AssistantSource,
                    ChosenBy = NextStepRules.ByAssistant, Prompt = first.Prompt,
                });
            }

            var eligible = NextStepRules.Eligible(inputs).Where(k => !stopped.Contains(k)).ToList();
            if (eligible.Count == 0) return Finish(view, null);

            // 2) Pro: Jev among the eligible kinds. 3) The rules' first match.
            var chosen = eligible[0];
            var byJev = false;
            if (eligible.Count > 1 && Entitlement.FeatureGrants.IsProTier(_entitlement) && NextStepJevLive && DecisionPointEnabled(DecisionRegistry.NextStep))
            {
                var pick = askJev ? await AskJevNextStepAsync(inputs, stage, eligible).ConfigureAwait(false) : KnownJevPick(inputs, stage, eligible);
                if (pick != null && eligible.Contains(pick)) { chosen = pick; byJev = true; }
            }
            var step = NextStepRules.Words(chosen, inputs, pro: byJev);
            if (byJev) { step.ChosenBy = NextStepRules.ByJev; step.Source = NextStepRules.JevSource; }
            return Finish(view, step);
        }

        private NextStepView Finish(NextStepView view, NextStepStep step)
        {
            view.Id = NextStepRules.IdOf(step?.Kind, step?.Ask, view.Stage);
            bool hidden;
            lock (_ns) hidden = step != null && _nsHidden.Contains(view.Id);
            view.Hidden = hidden;
            view.Step = hidden ? null : step;
            return view;
        }

        private StudioSuggestion CurrentAssistantSuggestion()
        {
            var key = _sessions.SessionId ?? "";
            lock (_studioGate)
            {
                if (!_studioSuggestion.TryGetValue(key, out var s) || s?.Steps == null || s.Steps.Length == 0) return null;
                var at = ParseAt(s.At);
                if (_studioNotes.TryGetValue(key, out var ring) && ring.Any(n => ParseAt(n.At) > at)) return null;
                return WithoutDonePublishSteps(s);
            }
        }

        private static DateTime ParseAt(string at) =>
            DateTime.TryParse(at, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : DateTime.MinValue;

        private async Task<NextStepInputs> NextStepInputsAsync()
        {
            var s = _sessions.Current;
            if (s == null) return new NextStepInputs { ModelOpen = false };
            bool needPlan;
            lock (_ns)
            {
                if (_nsSession != null && _nsSession != s.Id) ResetNextStepLocked(s.Id);
                _nsSession ??= s.Id;
                needPlan = _nsPlanSession != s.Id;
            }
            // The first read of a model composes its plan once (the Plan page does the same on open); after that every
            // compose reports here by itself.
            if (needPlan) { try { await GetModelPlanAsync().ConfigureAwait(false); } catch { } }

            bool unsavedEdits = false, live = false;
            try
            {
                var info = await SessionInfoAsync().ConfigureAwait(false);
                // A copy kept of a live model is the save the header would ask for (the service changes only on publish), so it asks for nothing now (UAT 18 Oct N23).
                unsavedEdits = info?.HasUnsavedEdits == true && info.KeptCopyPath == null;
                live = info?.LiveBound == true;
            }
            catch { }
            var checks = 0;
            try { checks = (await ListTestDefinitionsAsync().ConfigureAwait(false))?.Definitions?.Length ?? 0; } catch { }

            lock (_ns)
            {
                if (!unsavedEdits) _nsUnsavedRefs.Clear();
                var (times, steps, noun) = RepeatLocked();
                return new NextStepInputs
                {
                    ModelOpen = true,
                    Unsaved = unsavedEdits ? Math.Max(1, _nsUnsavedRefs.Count) : 0,
                    RepeatTimes = times, RepeatSteps = steps, RepeatNoun = noun,
                    TasksReady = _nsPlanSession == s.Id ? _nsReady : 0,
                    TasksDone = _nsPlanSession == s.Id ? _nsDone : 0,
                    TopTaskTitle = _nsTopTitle, TopTaskKind = _nsTopKind,
                    PlanJustFinished = _nsPlanJustFinished,
                    SavedSinceChange = _nsSavedSinceChange,
                    ChecksRunSinceSave = _nsChecksRun,
                    ChecksFailed = _nsChecksFailed,
                    ChecksCount = checks,
                    PublishedTest = false,          // the engine does not know a test publish yet: treated as not published
                    PublishedProduction = live,
                };
            }
        }

        /// <summary>The same steps on three or more objects, by hand. Per table: the steps that three or more tables each had done to
        /// their objects (hide the key, add a description, set the sort). Per object: one step done to three or more objects of
        /// a kind. A run of several steps beats one step.</summary>
        private (int Times, int Steps, string Noun) RepeatLocked()
        {
            var byTable = _nsRecent.Where(x => TableOf(x.Ref) != null)
                .GroupBy(x => TableOf(x.Ref), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(x => x.Sig), StringComparer.Ordinal), StringComparer.Ordinal);
            var common = byTable.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal)
                .Where(sig => byTable.Values.Count(v => v.Contains(sig)) >= 3).ToList();
            if (common.Count > 1)
            {
                var tables = byTable.Values.Count(v => common.All(v.Contains));
                if (tables >= 3) return (tables, common.Count, "tables");
            }
            var single = _nsRecent.GroupBy(x => (Kind: RefKind(x.Ref), x.Sig))
                .Select(g => (Times: g.Select(x => x.Ref).Distinct(StringComparer.Ordinal).Count(), Noun: Plural(g.Key.Kind)))
                .OrderByDescending(g => g.Times).FirstOrDefault();
            return single.Times >= 3 ? (single.Times, 1, single.Noun) : (0, 0, null);
        }

        private static string RefKind(string r) { var i = r?.IndexOf(':') ?? -1; return i > 0 ? r.Substring(0, i) : "object"; }
        private static string TableOf(string r)
        {
            var i = r?.IndexOf(':') ?? -1; if (i < 0) return null;
            var rest = r.Substring(i + 1); var j = rest.IndexOf('/');
            return j > 0 ? rest.Substring(0, j) : RefKind(r) == "table" ? rest : null;
        }
        private static string Plural(string kind) => kind switch
        {
            "measure" => "measures", "column" => "columns", "table" => "tables", "hierarchy" => "hierarchies",
            "relationship" => "relationships", "partition" => "partitions", _ => "objects",
        };

        // ---- Jev (next.step), Pro only ----------------------------------------------------------------------------------

        private static IReadOnlyDictionary<string, object> NextStepSnapshot(NextStepInputs i, int stage, IReadOnlyList<string> eligible)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.NextStep);
            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("journey_stage", stage),
                new KeyValuePair<string, object>("journey_counts", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["unsaved"] = i.Unsaved, ["tasks_ready"] = i.TasksReady, ["tasks_done"] = i.TasksDone,
                    ["checks"] = i.ChecksCount, ["checks_failed"] = i.ChecksFailed, ["repeat_times"] = i.RepeatTimes, ["repeat_steps"] = i.RepeatSteps,
                }),
                new KeyValuePair<string, object>("journey_flags", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["saved"] = i.SavedSinceChange, ["checks_run_since_save"] = i.ChecksRunSinceSave, ["plan_just_finished"] = i.PlanJustFinished,
                    ["published_test"] = i.PublishedTest, ["published_production"] = i.PublishedProduction,
                }),
                new KeyValuePair<string, object>("candidate_kinds", eligible.ToList()),
                new KeyValuePair<string, object>("plan_top_kind", (i.TopTaskKind ?? "none").ToLowerInvariant()),
            });
        }

        private static IReadOnlyList<DecisionQuestion> NextStepQuestions(IReadOnlyList<string> eligible) => new[]
        {
            new DecisionQuestion(NextStepQuestion, DecisionQuestionKind.Choice,
                "Which one of the steps in candidate_kinds helps this person most right now, given where they are (journey_stage, journey_counts, journey_flags)?",
                eligible.Select(k => new DecisionOption(k, KindCriterion(k))).ToList()),
        };

        private static string KindCriterion(string kind) => kind switch
        {
            NextStepRules.Save => "save the changes that are not saved yet",
            NextStepRules.Workflow => "save the steps the person repeated by hand as a workflow",
            NextStepRules.RunPlan => "have the assistant run the ready tasks on the Plan",
            NextStepRules.RunChecks => "run the checks, which have not run since the last save",
            NextStepRules.CheckThenPublish => "the plan is done: run the checks, then publish to test",
            NextStepRules.PublishTest => "publish the model to the test workspace",
            NextStepRules.PublishProduction => "publish the model to production",
            NextStepRules.KeepHealthy => "run a health check on the live model",
            _ => kind,
        };

        private static string NextStepDigest(IReadOnlyDictionary<string, object> snapshot) =>
            DecisionDigest.Sha256Hex(DecisionDigest.CanonicalSnapshotJson(snapshot));

        private string KnownJevPick(NextStepInputs i, int stage, IReadOnlyList<string> eligible)
        {
            try
            {
                var digest = NextStepDigest(NextStepSnapshot(i, stage, eligible));
                lock (_ns) return _nsJevPick.TryGetValue(digest, out var k) && k.Length > 0 ? k : null;
            }
            catch { return null; }
        }

        private async Task<string> AskJevNextStepAsync(NextStepInputs i, int stage, IReadOnlyList<string> eligible)
        {
            try
            {
                var snapshot = NextStepSnapshot(i, stage, eligible);
                var digest = NextStepDigest(snapshot);
                lock (_ns) if (_nsJevPick.TryGetValue(digest, out var known)) return known.Length > 0 ? known : null;
                var s = _sessions.Current;
                if (s == null) return null;
                var point = DecisionRegistry.Require(DecisionRegistry.NextStep);
                var fence = new DecisionSessionFence(s.Id, s.Revision, () =>
                {
                    var current = _sessions.Current;
                    return (current?.Id, current?.Revision ?? -1L);
                });
                var asked = await Decisions.AskAsync(point.Name, snapshot, NextStepQuestions(eligible),
                    isPro: true, enabled: true, fence: fence, ct: CancellationToken.None).ConfigureAwait(false);
                string pick = "";
                if (asked.Asked && asked.Answers?.Decided == true)
                {
                    var a = asked.Answers.For(NextStepQuestion);
                    if (a?.Choice != null && a.Confidence >= point.ConfidenceBar && eligible.Contains(a.Choice)) pick = a.Choice;
                }
                // Remember only real answers: a skipped ask (no key, no permission) may be answered once those change.
                NextStepJevNote = asked.Asked ? (pick.Length > 0 ? "picked " + pick : "not decided or below the bar") : "skipped: " + asked.SkippedReason;
                if (asked.Asked) lock (_ns) _nsJevPick[digest] = pick;
                return pick.Length > 0 ? pick : null;
            }
            catch (Exception ex) { NextStepJevNote = "failed: " + ex.Message; return null; }
        }

        // ---- the preference file ----------------------------------------------------------------------------------------

        private string NextStepPrefsFile() => Path.Combine(NextStepHomeForTest ?? SemanticusHome.Root(), NextStepFileName);

        private NextStepPrefs ReadPrefs()
        {
            lock (_ns)
            {
                if (_nsPrefs != null) return _nsPrefs;
                try
                {
                    var f = NextStepPrefsFile();
                    _nsPrefs = File.Exists(f)
                        ? JsonSerializer.Deserialize<NextStepPrefs>(File.ReadAllText(f), PrefsJson) ?? new NextStepPrefs()
                        : new NextStepPrefs();
                }
                catch { _nsPrefs = new NextStepPrefs(); }
                _nsPrefs.Stopped ??= new List<string>();
                return _nsPrefs;
            }
        }

        private void MutatePrefs(Action<NextStepPrefs> change)
        {
            var p = ReadPrefs();
            lock (_ns)
            {
                change(p);
                var f = NextStepPrefsFile();
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                var tmp = f + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(p, PrefsJson));
                File.Move(tmp, f, overwrite: true);
            }
        }

        private static readonly JsonSerializerOptions PrefsJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }
}
