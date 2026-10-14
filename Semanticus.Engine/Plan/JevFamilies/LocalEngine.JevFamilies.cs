// The Jev families on the engine: run them, and keep the change plan's Jev lines in step with what they
// chose (jev-groundwork-contract.md section 2, T268).
//
// THE ONE CALL SITE. ComposeAsync calls KickJevFamiliesAsync once, BEFORE it reads the change plan, and then
// builds its Jev items from the lines it reads (JevFamily.Items). Nothing about a Jev item is kept anywhere but
// those lines and the last run's result for the revision it was worked out at.
//
// THE PLAN NEVER WAITS FOR JEV (Kane: gather once, decide many; the Plan re-plans in the background). A read
// puts back what was already decided for this revision and returns. When nothing has been decided for it yet,
// the read starts ONE background run for (session, revision) and says so with jevPending. A model change
// cancels a run about an older revision and starts one for the new revision. When a run lands its lines go on
// the change plan and plan/didChange tells the page, which re-reads; the agent door sees them on its next call.
//
// PRO ONLY, AND "ONLY" MEANS BEFORE ANYTHING. A free session returns from the first line of JevLiveAsync:
// it does not read the model for the family, it does not build a request, it does not count anything, and it
// never reaches DecisionService. Kane's rule is that Jev runs only for Pro, "not even to count".
//
// GATES, AND WHICH ONES TAKE THE LINES AWAY. There are two kinds of "no answer" and they are treated
// differently on purpose:
//   OFF      not Pro, the point switched off, no service, no permission for this model. The family is not
//            allowed to run, so any Jev line still waiting on the change plan is taken off it. A person who
//            withdraws permission does not keep looking at advice they have just said may not be bought.
//   QUIET    the service shrugged, the budget ran out, the model moved while it was asked. The family was
//            allowed and simply has nothing new for that measure, so the line already waiting for it stays
//            exactly as it was, stamped with the revision it was worked out at. If the model has moved since,
//            the set reads out of date, which is the honest thing for it to say.
// DecisionService still makes every check itself on every ask. The OFF check here only decides whether to
// clear lines; it never admits an ask.
//
// THE SYNC IS IDEMPOTENT, AND THAT IS WHAT KEEPS A DIGEST APPROVABLE. A second read that changes nothing on
// the model must change nothing on the change plan: the same lines, the same handles, the same order, so the
// digest a person read is still the one approve demands. The sync only rewrites a family's lines when what
// was chosen differs from what is there, or when the lines were worked out at an older revision; and when
// it rewrites, a keep or a drop a person made on a line that is still proposed is carried to the new line.
//
// LATENCY, SAID PLAINLY. A run asks once per measure or column, off the dispatcher, a few at a time, capped at
// each family's MaxMeasuresPerRun or MaxColumnsPerRun. Nobody waits for it: the cost of a slow service is that the Jev items
// arrive a little later, never that the plan does.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>A test's own facts in place of the snapshot. Null on every real path.</summary>
        internal Func<IJevModelFacts> JevFactsForTest;

        /// <summary>The model facts the plan executor reads for the two checks that need data (sort-by one to
        /// one, and a narrowing data type): the snapshot already built for the open model at its current
        /// revision (Facts/LocalEngine.Facts.cs, TryGetCurrentFacts), read through one adapter. It never builds
        /// and never waits, because the executor calls it from inside a model write. No snapshot for this
        /// revision means null, so every such fact reads as not measured and the executor refuses rather than
        /// guesses. The family run below builds the snapshot first, so a Jev line is normally applied with
        /// facts in hand.</summary>
        private IJevModelFacts JevFacts()
        {
            if (JevFactsForTest != null) return JevFactsForTest();
            var snapshot = TryGetCurrentFacts();
            return snapshot == null ? null : new Facts.JevModelFactsAdapter(snapshot);
        }

        /// <summary>A test's own cap on NEW column asks per run for each column family. Null on every real path, which
        /// reads the family's MaxColumnsPerRun.</summary>
        internal int? ColumnCategoryCapForTest;
        internal int? ColumnSummarizeCapForTest;

        /// <summary>A test's own cap on the asks per run for the measures family and the column format family, which read
        /// every eligible object and ask a run's worth at a time. Null on every real path.</summary>
        internal int? MeasuresCapForTest;
        internal int? ColumnFormatCapForTest;

        /// <summary>A test's own answer to "did this column point clear its re-test". Null on every real path, which reads
        /// the family's own constant (ColumnCategoryFamily.JevDecides, ColumnSummarizeFamily.JevDecides).</summary>
        internal Func<string, bool?> ColumnJevDecidesForTest;

        /// <summary>A test's own answer to "does this point ask a table at a time" (JevTableBatch.cs). Null on every real path, which
        /// reads the family's own constant (MeasuresFamily.Batched and its three siblings): the comparison run of 30 September sets
        /// those (docs/redesign/jev-batch-comparison.md), and a point that did not pass it asks one object at a time.</summary>
        internal Func<string, bool?> JevBatchedForTest;

        private bool BatchedFor(string point) =>
            JevBatchedForTest?.Invoke(point)
            ?? (point == DecisionRegistry.MeasureFolderAndFormat ? MeasuresFamily.Batched
                : point == DecisionRegistry.ColumnFormat ? ColumnFormatFamily.Batched
                : point == DecisionRegistry.ColumnSummarize ? ColumnSummarizeFamily.Batched
                : point == DecisionRegistry.AiSchema ? AiSchemaFamily.Batched : false);

        private bool ColumnJevDecides(string point) =>
            ColumnJevDecidesForTest?.Invoke(point)
            ?? (point == DecisionRegistry.ColumnCategory ? ColumnCategoryFamily.JevDecides
                : point == DecisionRegistry.ColumnSummarize ? ColumnSummarizeFamily.JevDecides : true);

        /// <summary>How many Jev asks may be in flight at once from one family run. The oracle has its own
        /// cap; this one keeps a run from queueing every measure on it at once.</summary>
        private const int JevAsksInFlight = 6;

        /// <summary>One family's progress through the objects it is eligible to ask about. Every eligible object is exactly one of:
        /// answered (Jev gave a usable answer, now or earlier), tried without a usable answer (<see cref="Unusable"/>, which is
        /// also what a request that heard nothing counts as), or <see cref="Remaining"/> (not asked yet). So Total is always the
        /// sum of the three, and a run that stopped short says so.</summary>
        internal sealed class JevFamilyProgress
        {
            public int Total;
            public int Answered;
            public int Unusable;
            public int Remaining;

            /// <summary>How many objects this run sent.</summary>
            public int AskedThisRun;

            /// <summary>The family stopped with objects left: the service stopped answering (nothing came back, or two runs in a row
            /// came back with nothing usable). The next run for this model starts afresh.</summary>
            public bool Stopped;
            public string StopReason;

            /// <summary>The next run should ask about the objects still left.</summary>
            public bool Continues => Remaining > 0 && !Stopped;

            public JevFamilyProgress Copy() => (JevFamilyProgress)MemberwiseClone();
        }

        /// <summary>What one family run found. <see cref="Off"/> means the family was not allowed to run and
        /// its waiting lines come off the plan; <see cref="Unanswered"/> are the objects it asked about and got
        /// nothing new for, whose waiting lines stay as they are. <see cref="Answered"/> counts the asks that
        /// came back decided: a run whose every ask failed has asked Jev and heard nothing, and the strip must
        /// not say Jev updated the plan.</summary>
        internal sealed class JevRun
        {
            public bool Off;
            public long Revision;
            public List<ChangeItem> Lines = new List<ChangeItem>();
            public HashSet<string> Unanswered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>"Nothing new" belongs to ONE point: two families can ask about the same column, and one family's
            /// silence must not keep the other's waiting line as well as putting its new one back (that doubled it on
            /// every read). So the set holds point and object together.</summary>
            public static string UnansweredKey(string point, string objectRef) => (point ?? "") + "\u001f" + (objectRef ?? "");
            public int Answered;

            /// <summary>The objects a family DECIDED this run, keyed like Unanswered (point and object): a sure pick,
            /// or, for the category family, the value-share rule on a measured column. The readiness rules that write
            /// the same setting stand aside for these (<see cref="JevDecidedFinding"/>), whether or not a line came of
            /// it, so a decision to keep a column as it is holds too.</summary>
            public HashSet<string> Decided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>The objects a column family HEARD BACK on this run, keyed like <see cref="Decided"/>. A decision is
            /// replaced only for these: a column this run did not hear back on (not asked yet, or its ask failed) keeps
            /// the decision it had (<see cref="RememberJevDecisions"/>).</summary>
            public HashSet<string> Heard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>The column families in batches (<see cref="ColumnCategoryFamily.MaxColumnsPerRun"/> new asks per
            /// run): per family, how many of its columns Jev has an answer for, of how many, while some are still to be
            /// asked. A run with a batch still to come is not the last word on its revision: the next read starts the
            /// next batch.</summary>
            public Dictionary<string, (int Looked, int Total)> ColumnProgress =
                new Dictionary<string, (int Looked, int Total)>(StringComparer.Ordinal);

            /// <summary>How far each live family has got, keyed by family name (m11/sf-progress): every family writes one, so a
            /// family that stopped short can never be mistaken for one that is done.</summary>
            public Dictionary<string, JevFamilyProgress> Progress = new Dictionary<string, JevFamilyProgress>(StringComparer.Ordinal);

            /// <summary>A batch is still to come for a family: a column family, what Copilot sees, or any family whose progress
            /// record has objects it has not looked at and is not stopped.</summary>
            public bool Partial => ColumnProgress.Count > 0 || AiSchemaProgress?.RestFollow == true || Progress.Values.Any(p => p.Continues);

            /// <summary>The column families whose asks carried no measured facts at all (no data connected, or none read):
            /// Jev saw names only, and their rows say so.</summary>
            public HashSet<string> NamesOnly = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>What each family looked at and left out, keyed by family name, so a refusal files under
            /// that family's item for its table and never under another family's.</summary>
            public Dictionary<string, List<ModelPlanChangeRefusal>> Refused =
                new Dictionary<string, List<ModelPlanChangeRefusal>>(StringComparer.Ordinal);

            /// <summary>What Copilot sees, in batches (AiSchemaFamily.MaxObjectsPerRun new asks per run): how many of the
            /// model's objects Jev has an answer for, of how many, when some are still to be asked. Null when every
            /// object has been looked at. RestFollow says whether the next batch comes in this session: a run with a
            /// batch still to come is not the last word on its revision, and the next read starts it. A run that
            /// STOPPED short keeps its progress, so its rows still say Jev has not looked at everything, but starts
            /// nothing more (verify round 2, R2-2). StopReason is what stopped it, in the words of whatever stopped
            /// it, and null means this session's own suggestion budget, which has its own sentence.</summary>
            public (int Looked, int Total, bool RestFollow, string StopReason)? AiSchemaProgress;

            /// <summary>Every ai.schema answer this run holds, by request digest. The next batch for the same revision
            /// starts from these, so a batch never depends on a cache to remember what the one before it heard (a cache
            /// can miss: it evicts, and it serves nothing before the service's version is known).</summary>
            public Dictionary<string, DecisionOutcome> AiSchemaAnswers = new Dictionary<string, DecisionOutcome>(StringComparer.Ordinal);

            /// <summary>The pick each column the base measure family HEARD BACK on came to, whether or not a line came of it (a measure
            /// the model already has makes no line, but its pick is still the answer). The start plan folds these into its manifest's
            /// base measures (spec-first start, S4); the Plan reads lines only.</summary>
            public List<SummarizePick> SummarizePicks = new List<SummarizePick>();

            /// <summary>Why asks went unanswered, in the words of whatever stopped them (<see cref="BatchStopReason"/>), or null when
            /// every ask that was made came back with an answer. Never a reason for an object nobody has asked yet.</summary>
            public string StopReason;
        }

        /// <summary>One column's base measure pick, as the family decided it: Jev's when sure, else the principled rule's.</summary>
        internal sealed class SummarizePick
        {
            public string ColumnRef, TableName, ColumnName, Pick, MeasureName;
            /// <summary>True when Jev's own answer decided it (sure and not guarded), false when the rule stood in.</summary>
            public bool Sure;
        }

        // ---- the families that look at every eligible object, a run's worth at a time ----------------------

        /// <summary>HOW MANY RUNS IN A ROW MAY COME BACK WITH NOTHING USABLE before the family is left for now. One batch of
        /// answers the reader could not use must not block the objects behind it, so a run that heard nothing usable is followed by
        /// another; but a service that keeps doing it is not asked batch after batch for a whole model.</summary>
        internal const int MaxEmptyRunsInRow = 2;

        /// <summary>What the measures family and the column format family remember across the runs of one session (m11/sf-progress).
        /// <list type="bullet">
        /// <item>The answers heard, by the request digest of one object's own evidence. At the revision it was heard an answer is
        /// always read back. After a change it is read back only as the decision service's own cache would serve it: a whole
        /// answer, while the model version serving answers now is known and is the one that gave it. Otherwise the object is
        /// asked again, so an answer the service would not serve is never shown as Jev answering now (a silent Jev leaves the
        /// object unanswered, and the engine rechecks its waiting line itself). An answer that came back with a question
        /// refused is kept for this revision only, so it is tried again after the next change.</item>
        /// <item>Which objects were tried at which revision, and in what order. An object tried at this revision is not asked again
        /// at it, whether or not it was answered, and the objects with no usable answer move BEHIND the ones nobody has asked
        /// yet (the oldest try first), so a first batch the reader cannot use never starves the objects after it.</item>
        /// <item>How many runs in a row heard nothing usable (<see cref="MaxEmptyRunsInRow"/>).</item>
        /// </list>
        /// Bound to one session: another session, or Jev switched off, starts it empty.</summary>
        internal sealed class JevProgressMemory
        {
            private readonly object _gate = new object();
            private string _session;
            private long _tick;
            private readonly Dictionary<string, (DecisionOutcome Outcome, bool Complete, long Revision)> _answers =
                new Dictionary<string, (DecisionOutcome, bool, long)>(StringComparer.Ordinal);
            private readonly Dictionary<string, (long Tick, long Revision)> _tried = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
            private readonly Dictionary<string, (long Revision, int Runs)> _empty = new Dictionary<string, (long, int)>(StringComparer.Ordinal);

            private void Bind(string sessionId)
            {
                if (string.Equals(_session, sessionId, StringComparison.Ordinal)) return;
                _answers.Clear(); _tried.Clear(); _empty.Clear();
                _session = sessionId;
            }

            public void Reset() { lock (_gate) { _answers.Clear(); _tried.Clear(); _empty.Clear(); _session = null; } }

            public bool TryAnswer(string sessionId, string digest, long revision, string servedNow, out DecisionOutcome outcome)
            {
                lock (_gate)
                {
                    Bind(sessionId);
                    outcome = null;
                    if (!_answers.TryGetValue(digest, out var a)) return false;
                    var servable = a.Revision == revision
                        || (a.Complete && servedNow != null && string.Equals(servedNow, a.Outcome?.Answers?.ServedModelVersion, StringComparison.Ordinal));
                    if (!servable) return false;
                    outcome = a.Outcome;
                    return true;
                }
            }

            public bool TriedAt(string sessionId, string digest, long revision)
            {
                lock (_gate) { Bind(sessionId); return _tried.TryGetValue(digest, out var t) && t.Revision == revision; }
            }

            public long TickOf(string sessionId, string digest)
            {
                lock (_gate) { Bind(sessionId); return _tried.TryGetValue(digest, out var t) ? t.Tick : 0; }
            }

            public void Tried(string sessionId, string digest, long revision)
            {
                lock (_gate) { Bind(sessionId); _tried[digest] = (++_tick, revision); }
            }

            public void Heard(string sessionId, string digest, DecisionOutcome outcome, bool complete, long revision)
            {
                lock (_gate) { Bind(sessionId); _answers[digest] = (outcome, complete, revision); }
            }

            public int EmptyRuns(string sessionId, string family, long revision)
            {
                lock (_gate) { Bind(sessionId); return _empty.TryGetValue(family, out var e) && e.Revision == revision ? e.Runs : 0; }
            }

            /// <summary>A stopped job is asked again: what was heard stays, but nothing is "already tried" and no family is blocked by its empty runs.</summary>
            public void ForgetStops()
            {
                lock (_gate) { _tried.Clear(); _empty.Clear(); }
            }

            public void SetEmptyRuns(string sessionId, string family, long revision, int runs)
            {
                lock (_gate) { Bind(sessionId); _empty[family] = (revision, runs); }
            }
        }

        private readonly JevProgressMemory _jevProgressMemory = new JevProgressMemory();

        private enum JevAskResult { Answered, Unusable, NoReply }

        /// <summary>What one object's ask came to. NoReply: nothing came back for it (never sent, cancelled, a lost connection, a
        /// deadline, a spent budget). Unusable: a reply came back and this object's part of it could not be used (refused, left
        /// out, or the service said why it would not answer). The first says something about the service, the second about the
        /// answer.</summary>
        private static JevAskResult ClassifyAsk(DecisionOutcome outcome)
        {
            if (outcome == null || !outcome.Asked) return JevAskResult.NoReply;
            if (outcome.Answers.Decided) return JevAskResult.Answered;
            return outcome.Answers.OverBudget || UnheardReasons.Contains(outcome.Answers.NotDecidedReason ?? "")
                   || string.Equals(outcome.Answers.NotDecidedReason, DecisionService.BudgetSpentReason, StringComparison.Ordinal)
                   || string.Equals(outcome.Answers.NotDecidedReason, JevDecisionOracle.AskedThenStoppedReason, StringComparison.Ordinal)
                ? JevAskResult.NoReply : JevAskResult.Unusable;
        }

        // ---- what a family looked at and left out ---------------------------------------------------------

        private readonly object _jevRefusedGate = new object();
        private string _jevRefusedSession;
        private Dictionary<string, ModelPlanChangeRefusal[]> _jevRefused =
            new Dictionary<string, ModelPlanChangeRefusal[]>(StringComparer.Ordinal);

        /// <summary>Hang what the last family run left out, with its reason, on the Jev item for that table, so
        /// a person reads it BEFORE deciding (change-contract.md, the Refused half). A Jev set has no stored
        /// header for these to live in, because it is never proposed through the propose verb; they are held
        /// for this session beside the lines instead, and never go into the digest, exactly like a stored
        /// set's refusals.</summary>
        private void StampJevRefusals(IEnumerable<ModelPlanItem> items, string sessionId)
        {
            Dictionary<string, ModelPlanChangeRefusal[]> byItem;
            lock (_jevRefusedGate)
            {
                if (!string.Equals(_jevRefusedSession, sessionId, StringComparison.Ordinal)) return;
                byItem = _jevRefused;
            }
            foreach (var item in items ?? Array.Empty<ModelPlanItem>())
            {
                if (item?.ChangeSet == null || !JevFamily.IsJevItem(item.Id)) continue;
                if (byItem.TryGetValue(item.Id, out var refused) && refused.Length > 0) item.ChangeSet.Refused = refused;
            }
        }

        private void RememberJevRefusals(string sessionId, Dictionary<string, List<ModelPlanChangeRefusal>> refused,
                                         IEnumerable<ChangeItem> lines = null)
        {
            // The date table family decides once per MODEL, so what it left out (the tables it would not offer) files
            // under its one item, whichever table that item names, and never under a per-table id nothing carries.
            var dateItem = (lines ?? Array.Empty<ChangeItem>())
                .FirstOrDefault(l => string.Equals(l.DecisionPoint, DecisionRegistry.DateTable, StringComparison.Ordinal))?.JevItem;
            var byItem = (refused ?? new Dictionary<string, List<ModelPlanChangeRefusal>>())
                .SelectMany(f => f.Value.Select(r => (Family: f.Key, Refusal: r)))
                .Where(x => x.Refusal != null && !string.IsNullOrWhiteSpace(x.Refusal.Target))
                .GroupBy(x => string.Equals(x.Family, DateTableFamily.Name, StringComparison.Ordinal) && dateItem != null
                                  ? dateItem
                                  : JevFamily.ItemId(x.Family, "table:" + TableOfObjectRef(x.Refusal.Target)), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Refusal).ToArray(), StringComparer.Ordinal);
            lock (_jevRefusedGate)
            {
                _jevRefusedSession = sessionId;
                _jevRefused = byItem;
            }
        }

        // ---- what the column rows say about how far Jev got and what it saw -------------------------------------

        private Dictionary<string, (int Looked, int Total)> _jevColumnProgress = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        private HashSet<string> _jevNamesOnly = new HashSet<string>(StringComparer.Ordinal);
        private Dictionary<string, JevFamilyProgress> _jevFamilyProgress = new Dictionary<string, JevFamilyProgress>(StringComparer.Ordinal);
        private string _jevColumnNotesSession;

        private void RememberJevColumnNotes(string sessionId, JevRun run)
        {
            lock (_jevRefusedGate)
            {
                _jevColumnNotesSession = sessionId;
                _jevColumnProgress = run == null || run.Off
                    ? new Dictionary<string, (int, int)>(StringComparer.Ordinal)
                    : new Dictionary<string, (int, int)>(run.ColumnProgress, StringComparer.Ordinal);
                _jevNamesOnly = run == null || run.Off
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(run.NamesOnly, StringComparer.Ordinal);
                _jevFamilyProgress = run == null || run.Off
                    ? new Dictionary<string, JevFamilyProgress>(StringComparer.Ordinal)
                    : run.Progress.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal);
            }
        }

        /// <summary>Two plain lines a column row may need. While Jev has looked at only some of the model's columns (the
        /// batches), the row says how far it has got, so it is never read as the whole answer. With no data connected,
        /// the row says Jev saw names only, because what it saw is part of why it chose.</summary>
        private void StampJevColumnNotes(IEnumerable<ModelPlanItem> items, string sessionId)
        {
            Dictionary<string, (int Looked, int Total)> progress;
            HashSet<string> namesOnly;
            Dictionary<string, JevFamilyProgress> familyProgress;
            lock (_jevRefusedGate)
            {
                if (!string.Equals(_jevColumnNotesSession, sessionId, StringComparison.Ordinal)) return;
                progress = _jevColumnProgress;
                namesOnly = _jevNamesOnly;
                familyProgress = _jevFamilyProgress;
            }
            foreach (var item in items ?? Array.Empty<ModelPlanItem>())
            {
                var family = JevFamily.Parse(item?.Id).Family;
                if (family == null) continue;
                var extra = new List<string>();
                if (namesOnly.Contains(family)) extra.Add(NamesOnlyLine);
                if (progress.TryGetValue(family, out var p)) extra.Add(ColumnProgressLine(p.Looked, p.Total));
                if (familyProgress.TryGetValue(family, out var fp) && FamilyProgressLine(family, fp) is string said) extra.Add(said);
                if (extra.Count > 0) item.Why = (item.Why ?? Array.Empty<string>()).Concat(extra).ToArray();
            }
        }

        internal const string NamesOnlyLine = "No data was read for these columns, so Jev saw names only: their names, types and how the model uses them, not what the values look like.";

        internal static string ColumnProgressLine(int looked, int total) =>
            "Jev has looked at " + looked.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " of "
            + JevFamily.Count(total, "column", "columns") + " in this model so far. The rest follow.";

        /// <summary>What a row of the measures family or the column format family says about how far Jev got, or null when it has
        /// nothing to add (every object looked at, every answer used). Said only for what is true: how many were looked at, how many
        /// got no usable answer, and whether the rest follow or were left because the service stopped answering.</summary>
        internal static string FamilyProgressLine(string family, JevFamilyProgress p)
        {
            if (p == null || p.Total == 0) return null;
            var noun = string.Equals(family, MeasuresFamily.Name, StringComparison.Ordinal) ? new[] { "measure", "measures" }
                : string.Equals(family, ColumnFormatFamily.Name, StringComparison.Ordinal) ? new[] { "column", "columns" }
                : null;
            if (noun == null) return null;
            var looked = p.Answered + p.Unusable;
            var unusable = p.Unusable == 0 ? "" : ", and gave no usable answer for " + p.Unusable.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " of them";
            if (p.Continues)
                return "Jev has looked at " + looked.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " of "
                       + JevFamily.Count(p.Total, noun[0], noun[1]) + " in this model so far" + unusable + ". The rest follow.";
            if (p.Remaining > 0)
            {
                // Nothing looked at and Jev not available: the Plan's order line says so once, and a count of nothing is noise.
                if (looked == 0 && JevDecisionOracle.IsNotAvailable(p.StopReason)) return null;
                return "Jev stopped after looking at " + looked.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " of "
                       + JevFamily.Count(p.Total, noun[0], noun[1]) + " in this model" + unusable
                       + ", so the rest have not been looked at.";
            }
            if (p.Unusable > 0)
                return "Jev gave no usable answer for " + p.Unusable.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " of "
                       + JevFamily.Count(p.Total, noun[0], noun[1]) + ", so they are left as they are.";
            return null;
        }

        // ---- how far the batches have got -----------------------------------------------------------------

        private (int Looked, int Total, bool RestFollow, string StopReason)? _jevProgress;
        private string _jevProgressSession;

        private void RememberJevProgress(string sessionId, (int Looked, int Total, bool RestFollow, string StopReason)? progress)
        {
            lock (_jevRefusedGate)
            {
                _jevProgressSession = sessionId;
                _jevProgress = progress;
            }
        }

        /// <summary>While Jev has looked at only some of the model (the batches of AiSchemaFamily.MaxObjectsPerRun), every
        /// "what Copilot sees" row says so plainly, so a row is never read as the whole answer when it is not.</summary>
        private void StampJevProgress(IEnumerable<ModelPlanItem> items, string sessionId)
        {
            (int Looked, int Total, bool RestFollow, string StopReason)? progress;
            lock (_jevRefusedGate)
            {
                if (!string.Equals(_jevProgressSession, sessionId, StringComparison.Ordinal)) return;
                progress = _jevProgress;
            }
            // While asks wait their turn at a crowded relay the row says so, with how long (the person never has to retry).
            var waitingSince = _decisions?.WaitingSince;
            var waitLine = waitingSince == null ? null : AiSchemaFamily.WaitingLine(DateTime.UtcNow - waitingSince.Value);
            var line = progress == null ? null
                : AiSchemaFamily.ProgressLine(progress.Value.Looked, progress.Value.Total, progress.Value.RestFollow, progress.Value.StopReason);
            // Null is "nothing was looked at and nobody can act": the Plan's one line says it (RankingFrom), not every row.
            if (line == null && waitLine == null) return;
            // The crowded stop and the live wait say the same thing; the live line wins while asks are waiting.
            if (waitLine != null && progress != null && JevDecisionOracle.IsCrowded(progress.Value.StopReason))
                line = AiSchemaFamily.ProgressLine(progress.Value.Looked, progress.Value.Total, true, null);
            var lines = new[] { line, waitLine }.Where(l => l != null).ToArray();
            foreach (var item in items ?? Array.Empty<ModelPlanItem>())
                if (item?.Id != null && item.Id.StartsWith(JevFamily.ItemPrefix + AiSchemaFamily.Name + ".", StringComparison.Ordinal))
                    item.Why = (item.Why ?? Array.Empty<string>()).Concat(lines).ToArray();
        }

        /// <summary>Why one ask did not come back with an answer, in the words of whatever refused it, or null when
        /// it was answered. An object nobody has asked about yet is not a stop, so its "not asked yet" is null too:
        /// those are the ones the next batch is for.</summary>
        private static string StopReasonOf(DecisionOutcome outcome)
        {
            if (outcome == null) return NoAnswerReason;
            if (!outcome.Asked)
                return string.Equals(outcome.SkippedReason, DecisionService.NotCachedReason, StringComparison.Ordinal)
                    ? null : outcome.SkippedReason;
            return outcome.Answers.Decided ? null : outcome.Answers.NotDecidedReason;
        }

        private const string NoAnswerReason = JevDecisionOracle.NoAnswerReason;

        /// <summary>The reasons that mean NO REPLY CAME BACK for that one ask (a lost connection, a deadline, a queue that
        /// was too slow, a caller that moved on), as against a service that answered and said why it would not. One ask
        /// that heard nothing says little about the service, and a refusal says exactly what the service is doing.</summary>
        private static readonly HashSet<string> UnheardReasons = new HashSet<string>(StringComparer.Ordinal)
        {
            NoAnswerReason,
            JevDecisionOracle.TimedOutReason,
            JevDecisionOracle.UnreachableReason,
            JevDecisionOracle.CancelledReason,
            JevDecisionOracle.QueueDroppedReason,
            JevDecisionOracle.CrowdedReason,
        };

        /// <summary>The relay stayed crowded for this ask's whole wait. Nothing was charged and nothing is recorded as tried: the next run asks again.</summary>
        private static bool IsCrowdedOutcome(DecisionOutcome outcome) =>
            outcome != null && outcome.Asked && !outcome.Answers.Decided && JevDecisionOracle.IsCrowded(outcome.Answers.NotDecidedReason);

        /// <summary>WHY THE BATCHES STOPPED, from every ask that did not come back decided. What the service itself said
        /// wins over an ask that heard nothing: one dropped connection used to be named as the reason, because it was
        /// the first object in the model's order, while the service refused every other ask with its pause (ai.schema
        /// verify round 5, D2). Among the reasons of the same kind, the one most asks came back with wins, and a tie
        /// goes to the model's own order, so the choice never depends on which reply landed first. Null when nothing
        /// was stopped.</summary>
        internal static string BatchStopReason(IEnumerable<string> reasons)
        {
            var seen = (reasons ?? Array.Empty<string>()).Where(r => r != null).Select((r, i) => (Reason: r, Order: i)).ToArray();
            if (seen.Length == 0) return null;
            var heard = seen.Where(x => !UnheardReasons.Contains(x.Reason)).ToArray();
            return (heard.Length > 0 ? heard : seen)
                .GroupBy(x => x.Reason, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.Order))
                .First().Key;
        }

        /// <summary>THE ROW THAT EXISTS SO THE PAGE NEVER READS AS "NOTHING TO CHANGE FOR COPILOT". Rows for what
        /// Copilot sees are built from the lines Jev's answers produced, so a run that stopped before Jev answered
        /// ANY object leaves no row at all, and the Trust section then says nothing about Copilot although Jev
        /// looked at none of it (verify round 4, r4-11). This adds one row, with no changes on it, whose progress
        /// line says how far Jev got (0 of N) and what stopped it. It is added only while a stop is remembered for
        /// this session and no other "what Copilot sees" row exists; the moment one does, the line goes on the real
        /// rows instead and this row is gone. It is not added at all when Jev was not available and looked at none of
        /// it: nobody can act on that, so the Plan says it once, in its order line (Kane, 8 Oct).</summary>
        private ModelPlanItem JevProgressRow(IReadOnlyCollection<ModelPlanItem> items, string sessionId)
        {
            (int Looked, int Total, bool RestFollow, string StopReason)? progress;
            lock (_jevRefusedGate)
            {
                if (!string.Equals(_jevProgressSession, sessionId, StringComparison.Ordinal)) return null;
                progress = _jevProgress;
            }
            if (progress == null) return null;
            if (progress.Value.RestFollow && !JevDecisionOracle.IsCrowded(progress.Value.StopReason)
                && !string.Equals(progress.Value.StopReason, AiSchemaFamily.LookingAgain, StringComparison.Ordinal)) return null;
            // Jev could not be reached and looked at nothing: there is no row to read and nothing for the person to do. The Plan's
            // order line says Jev is not available (RankingFrom), once. A sentence the person can act on (a pause, an ended licence)
            // keeps this row: the order line does not always carry it (measured in the r1 join), and it must not vanish.
            if (progress.Value.Looked == 0 && JevDecisionOracle.IsNotAvailable(progress.Value.StopReason)) return null;
            var prefix = JevFamily.ItemPrefix + AiSchemaFamily.Name + ".";
            if (items.Any(i => i?.Id != null && i.Id.StartsWith(prefix, StringComparison.Ordinal))) return null;

            return new ModelPlanItem
            {
                Id = AiSchemaFamily.NotFinishedItemId,
                Section = ModelPlanSections.Trust,
                Title = "Jev has not finished choosing what Copilot sees",
                Sub = string.Empty,
                Value = "Copilot and data agents answer from the right parts of this model",
                // Nothing here runs on its own and nothing is proposed yet, so this is not work Semanticus can take
                // off the person: Jev tries again after a later change to the model or in the next session (a later change or the next session asks the rest),
                // and the person decides whether to wait. It is a waiting state on the plan, not a task for anyone (Kane, 13 Oct 2026): Semanticus
                // is the doer and the row reads Waiting (PlanTasks.StampState), so Run the plan, the assistant's queue and Needs you all skip it.
                Who = ModelPlanOwners.Auto,
                // Jev chose none of it, so the row is not from Jev (JevFamily.Items says the same about its rows).
                Source = PlanSources.Found,
                Status = ModelPlanStatuses.Ready,
                Why = new[]
                {
                    progress.Value.RestFollow
                        ? "Jev is still looking at this model, so what Copilot sees is not settled yet."
                        : "Jev stopped before it had looked at everything in this model, so what Copilot sees is not settled yet.",
                },
                Diff = Array.Empty<string>(),
                Touches = "the model",
                Jump = new ModelPlanJump { Tool = "readiness", Label = "AI readiness" },
            };
        }

        /// <summary>"measure:Sales/Margin" to "Sales". The refs are the engine's own, so there is nothing to
        /// resolve; a ref that is not table-owned files under its whole body.</summary>
        private static string TableOfObjectRef(string objectRef)
        {
            var colon = objectRef.IndexOf(':');
            var body = colon >= 0 ? objectRef.Substring(colon + 1) : objectRef;
            var slash = body.IndexOf('/');
            return slash > 0 ? body.Substring(0, slash) : body;
        }

        // ---- a dropped set stays dropped ------------------------------------------------------------------

        private readonly object _jevDismissGate = new object();
        private string _jevDismissedSession;
        private readonly HashSet<string> _jevDismissed = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Remember the exact lines a person dropped with "drop all" on a Jev item, so the next read
        /// does not put the same proposals straight back. Only those lines: a later choice that differs is new
        /// advice and is shown. Held for this session only, which is the life of the change plan they were on.</summary>
        internal void RememberJevDismissed(ModelPlanItem item)
        {
            if (item == null || !JevFamily.IsJevItem(item.Id) || item.ChangeSet?.Lines == null) return;
            var sessionId = _sessions.Current?.Id;
            lock (_jevDismissGate)
            {
                if (!string.Equals(_jevDismissedSession, sessionId, StringComparison.Ordinal))
                {
                    _jevDismissed.Clear();
                    _jevDismissedSession = sessionId;
                }
                foreach (var line in item.ChangeSet.Lines)
                    _jevDismissed.Add(JevFamily.KeyOf(new ChangeItem
                    {
                        JevItem = item.Id, Kind = line.Op, ObjectRef = line.Target, Target = line.Property, After = line.After,
                    }));
            }
        }

        private bool JevDismissed(string sessionId, ChangeItem line)
        {
            lock (_jevDismissGate)
                return string.Equals(_jevDismissedSession, sessionId, StringComparison.Ordinal)
                       && _jevDismissed.Contains(JevFamily.KeyOf(line));
        }

        // ---- the one call site's work ---------------------------------------------------------------------

        /// <summary>One background run of the families for one (session, revision). At most one is current per
        /// engine; starting another cancels the one before it. <see cref="Result"/> is what it decided, kept so
        /// every later read of the same revision can put the same lines back without asking again.</summary>
        private sealed class JevBackgroundRun
        {
            public string SessionId;
            public long Revision;

            /// <summary>Which family points were live when the run started (<see cref="LiveKeyOf"/>). A decision is
            /// only reused while the same points are live: switching one point off or on is not a model change, so
            /// the revision alone cannot tell a stale decision from a current one.</summary>
            public string LiveKey;
            public CancellationTokenSource Cancel = new CancellationTokenSource();
            public Task Task = Task.CompletedTask;
            public bool Done;
            public JevRun Result;
            public DateTime FinishedUtc;
        }

        // ---- a stopped job is asked again ---------------------------------------------------------------------------

        /// <summary>Set by Refresh plan: the next read starts the job again if it stopped on a refusal (a crowded relay, a failed forward, a
        /// timeout), whatever its age. A person who presses Refresh plan after the cause is gone must see Jev look again (Kane, 13 Oct).</summary>
        private int _jevRetryNow;

        /// <summary>How old a stopped job must be before a plain plan read starts it again by itself, when what stopped it was nobody's answer
        /// (no reply, a crowd, a service fault). The wait keeps a stop that cannot lift yet from becoming a loop of runs.</summary>
        internal TimeSpan JevAutoRetryAfter = TimeSpan.FromSeconds(60);

        /// <summary>Ask again for what a stopped job left, on the next read. Refresh plan calls it.</summary>
        internal void RetryStoppedJevOnNextRead() => Interlocked.Exchange(ref _jevRetryNow, 1);

        /// <summary>True when this finished run stopped before Jev had looked at everything and a refusal said why.
        /// <paramref name="byItself"/> is true for the stops a plain read may start again without being asked: no reply, a crowd, a fault.</summary>
        private static bool JevStopped(JevRun r, out bool byItself)
        {
            var stopped = false;
            string reason = null;
            var ai = r.AiSchemaProgress;
            if (ai != null && !ai.Value.RestFollow && ai.Value.StopReason != null) { stopped = true; reason = ai.Value.StopReason; }
            foreach (var p in r.Progress.Values)
                if (p.Stopped) { stopped = true; reason ??= p.StopReason; }
            if (r.StopReason != null) { stopped = true; reason ??= r.StopReason; }
            // A stop with no reason of its own (this session's budget, a family left for now) is not a refusal that can lift: nothing is asked again for it.
            if (reason == null) { byItself = false; return false; }
            byItself = stopped
                && (UnheardReasons.Contains(reason) || JevDecisionOracle.IsNotAvailable(reason) || JevDecisionOracle.IsCrowded(reason));
            return stopped;
        }

        private readonly object _jevRunGate = new object();
        private JevBackgroundRun _jevRun;
        private int _jevRunTrackingAttached;
        private int _jevRunsStarted;

        /// <summary>How many background runs this engine has started. Tests count it: a free session must
        /// leave it at zero.</summary>
        internal int JevRunsStartedForTest => Volatile.Read(ref _jevRunsStarted);

        /// <summary>Wait until no run is in flight, following a restart to the run that replaced it. Tests
        /// only; a real caller never waits for Jev.</summary>
        internal async Task JevSettledForTest()
        {
            for (var i = 0; i < 100; i++)
            {
                JevBackgroundRun run;
                lock (_jevRunGate) run = _jevRun;
                if (run == null || run.Done) return;
                try { await run.Task.ConfigureAwait(false); } catch { }
            }
        }

        /// <summary>Wait until no run is in flight, or <paramref name="max"/> has passed, following a restart to the run that replaced it.
        /// Run the plan waits here between batches, so a Jev row the last batch made stale is read again after Jev has looked at it.
        /// True when Jev settled, false when the wait ran out.</summary>
        internal async Task<bool> JevSettledAsync(TimeSpan max)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 100; i++)
            {
                JevBackgroundRun run;
                lock (_jevRunGate) run = _jevRun;
                if (run == null || run.Done) return true;
                var left = max - clock.Elapsed;
                if (left <= TimeSpan.Zero) return false;
                try
                {
                    var finished = await Task.WhenAny(run.Task, Task.Delay(left)).ConfigureAwait(false);
                    if (!ReferenceEquals(finished, run.Task)) return false;
                    await run.Task.ConfigureAwait(false);
                }
                catch { }
            }
            return false;
        }

        /// <summary>What ComposeAsync learns from the families: whether their point is live for this session
        /// (which also decides which of two duplicate lines the plan keeps), and whether a run is still
        /// looking at this model.</summary>
        private readonly struct JevState
        {
            public JevState(bool live, bool pending, bool starterLive = false, int answered = 0, IReadOnlyCollection<string> livePoints = null,
                            int lines = 0)
            { Live = live; Pending = pending; StarterLive = starterLive; Answered = answered; Lines = lines; LivePoints = livePoints ?? Array.Empty<string>(); }

            /// <summary>Jev decides the date table for this session, so the rule's "Mark the date table" row stands
            /// aside (DateTableFamily.cs, "who decides"), as the starter rule items do for StarterLive.</summary>
            public bool DateTableLive => LivePoints.Contains(DecisionRegistry.DateTable);
            public bool Live { get; }

            /// <summary>The family points that are live for this session. A Jev line wins over a rule's line for the
            /// same write only while its OWN point is live (<see cref="OneLinePerChange(ChangeItem[], Func{ChangeItem, bool})"/>).</summary>
            public IReadOnlyCollection<string> LivePoints { get; }
            public bool Pending { get; }

            /// <summary>How many asks the finished run for this revision got a decided answer to. Zero while it is
            /// pending, when it asked nothing, and when every ask failed.</summary>
            public int Answered { get; }

            /// <summary>How many lines that run put on the change plan. An answer that agrees with the model changes
            /// nothing, so a run can be answered in full and leave this at zero, and then Jev did not update the
            /// plan and the strip must not say it did.</summary>
            public int Lines { get; }

            /// <summary>Jev decides the starter measures for this session, so the rule's starter items stand
            /// aside (StarterMeasuresFamily.cs, "who decides"). False whenever the rule decides.</summary>
            public bool StarterLive { get; }
        }

        /// <summary>THE PLAN NEVER WAITS FOR JEV. Called once by ComposeAsync, before it reads the change plan.
        ///
        /// Not live (free, point off, no service, no permission): any Jev line still waiting comes off the plan
        /// here and now, which asks nothing and is cheap, and no run is started or remembered. A free session
        /// returns from the first line of <see cref="JevLiveAsync"/> before reading anything.
        ///
        /// Live: the lines already decided for THIS revision are put back on the change plan (idempotent, so
        /// a digest stays approvable), and when nothing has been decided for this revision yet a background
        /// run is started and this returns at once with Pending set. The run writes its lines when it lands and
        /// tells the page through plan/didChange; the agent door sees them on its next call.</summary>
        private async Task<JevState> KickJevFamiliesAsync(PlanMemoryBinding binding)
        {
            var s = binding?.Session;
            var context = binding?.Context;
            if (s == null || context == null) return new JevState(false, false);

            bool live, starterLive;
            HashSet<string> livePoints;
            try
            {
                livePoints = await LiveJevPointsAsync().ConfigureAwait(false);
                live = livePoints.Count > 0;
                starterLive = livePoints.Contains(DecisionRegistry.MeasureStarter);
            }
            catch { live = false; starterLive = false; livePoints = new HashSet<string>(StringComparer.Ordinal); }

            if (!live)
            {
                ForgetJevRun();
                try { await ApplyJevRunAsync(s, context, new JevRun { Off = true }, publish: true).ConfigureAwait(false); }
                catch { }
                return new JevState(false, false);
            }

            // A point that is not live has no line on the plan, now, even while other points are live. This asks
            // nothing, and it is what the OFF path above does for every point at once.
            try { await DropJevLinesOffPointsAsync(s, context, livePoints).ConfigureAwait(false); }
            catch { }

            AttachJevRunTracking();
            var liveKey = LiveKeyOf(livePoints);
            JevRun decided = null;
            bool pending;
            lock (_jevRunGate)
            {
                var run = _jevRun;
                if (run != null && string.Equals(run.SessionId, s.Id, StringComparison.Ordinal) && run.Revision == s.Revision
                    && string.Equals(run.LiveKey, liveKey, StringComparison.Ordinal))
                {
                    pending = !run.Done;
                    decided = run.Done ? run.Result : null;
                    // A batch landed and more are to come: what it decided goes back on the plan now, and the next
                    // batch starts. Answers already heard are not asked again (the columns' and Copilot's answers are
                    // both kept), so only new columns and objects are.
                    if (decided != null && decided.Partial)
                    {
                        StartJevRunLocked(s, context, liveKey, decided.AiSchemaAnswers);
                        pending = true;
                    }
                    // A job that stopped on a refusal is asked again: at once on Refresh plan, and on a plain read once the stop is old
                    // enough (JevAutoRetryAfter) when nobody's answer stopped it. What it heard stays; its old refusal text goes with it.
                    else if (decided != null)
                    {
                        var forced = Interlocked.Exchange(ref _jevRetryNow, 0) != 0;
                        if (JevStopped(decided, out var byItself)
                            && (forced || (byItself && DateTime.UtcNow - run.FinishedUtc >= JevAutoRetryAfter)))
                        {
                            _jevProgressMemory.ForgetStops();
                            // The old refusal text goes now. The row stays and says Jev is looking again, so the page does not flicker.
                            RememberJevProgress(s.Id, decided.AiSchemaProgress is { } was
                                ? (was.Looked, was.Total, true, AiSchemaFamily.LookingAgain)
                                : ((int, int, bool, string)?)null);
                            StartJevRunLocked(s, context, liveKey, decided.AiSchemaAnswers);
                            pending = true;
                            decided = null;
                        }
                    }
                }
                else
                {
                    StartJevRunLocked(s, context, liveKey);
                    pending = true;
                }
            }

            // Put back what was already decided for this revision. The change plan may have been rebuilt since
            // (a fresh proposal starts a new one), and the lines must be there for the items to be composed.
            if (decided != null)
            {
                try { await ApplyJevRunAsync(s, context, CloneRun(decided), publish: true).ConfigureAwait(false); }
                catch { }
            }
            return new JevState(true, pending, starterLive, decided?.Answered ?? 0, livePoints, decided?.Lines?.Count ?? 0);
        }

        /// <summary>The family points live for this session right now. Pro is checked FIRST and alone, so a free
        /// session reads nothing else and gets an empty set.</summary>
        private async Task<HashSet<string>> LiveJevPointsAsync()
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement)) return live;
            if (await JevPointLiveAsync(DecisionRegistry.MeasureFolderAndFormat).ConfigureAwait(false)) live.Add(DecisionRegistry.MeasureFolderAndFormat);
            if (await JevPointLiveAsync(DecisionRegistry.ColumnFormat).ConfigureAwait(false)) live.Add(DecisionRegistry.ColumnFormat);
            if (await JevPointLiveAsync(DecisionRegistry.ColumnCategory).ConfigureAwait(false)) live.Add(DecisionRegistry.ColumnCategory);
            if (await JevPointLiveAsync(DecisionRegistry.ColumnSummarize).ConfigureAwait(false)) live.Add(DecisionRegistry.ColumnSummarize);
            if (await StarterJevLiveAsync().ConfigureAwait(false)) live.Add(DecisionRegistry.MeasureStarter);
            if (await RelationshipJevLiveAsync().ConfigureAwait(false)) live.Add(DecisionRegistry.RelationshipJoin);
            if (await AiSchemaJevLiveAsync().ConfigureAwait(false)) live.Add(DecisionRegistry.AiSchema);
            if (await DateTableJevLiveAsync().ConfigureAwait(false)) live.Add(DecisionRegistry.DateTable);
            return live;
        }

        private static string LiveKeyOf(IEnumerable<string> points) =>
            string.Join("|", (points ?? Array.Empty<string>()).OrderBy(p => p, StringComparer.Ordinal));

        /// <summary>Take off the plan every waiting Jev line whose own point is no longer live. Lines of live points
        /// are left exactly as they are, so their digest stays approvable.</summary>
        private async Task DropJevLinesOffPointsAsync(Session s, SessionContext context, HashSet<string> livePoints)
        {
            await context.PlanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_sessions.CurrentContext, context) || !ReferenceEquals(_sessions.Current, s)) return;
                // A point switched off hands its columns straight back to the rules.
                ForgetJevDecisionsOffPoints(livePoints);
                var plan = context.Plans.Current;
                if (plan == null) return;
                var gone = new HashSet<ChangeItem>(
                    plan.Snapshot().Where(l => JevFamily.IsJevLine(l) && IsPendingLine(l) && !livePoints.Contains(l.DecisionPoint ?? "")),
                    ReferenceEqualityComparer.Instance);
                if (gone.Count == 0) return;
                // Words somebody wrote stay, held (ChangePlanState.RemoveSynced), so a point switched off never takes a person's text with it.
                await HoldKeptAsync(s, gone.Where(l => l.AuthoredAfter != null).ToList(), JevFamily.HeldReason).ConfigureAwait(false);
                plan.RemoveSynced(l => gone.Contains(l));
                PublishPlan(plan, s.Revision);
            }
            finally { context.PlanGate.Release(); }
        }

        /// <summary>Whether any family may run for this session right now (measures, column format, column category, starter). The Pro check is FIRST and
        /// reads nothing but the entitlement, so a free session never touches the model or Jev.
        /// DecisionService still makes every check itself on every ask; this only decides whether to run
        /// and whether waiting lines come off the plan.</summary>
        private async Task<bool> JevLiveAsync()
        {
            // FIRST, before anything else is read: a free session is never live, not for any family.
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement)) return false;
            if (await JevPointLiveAsync(DecisionRegistry.MeasureFolderAndFormat).ConfigureAwait(false)) return true;
            return (await LiveJevPointsAsync().ConfigureAwait(false)).Count > 0;
        }

        /// <summary>A test's own answer to "has the starter replay bar been met". Null on every real path, which
        /// reads the family's own constant (StarterMeasuresFamily.JevDecides).</summary>
        internal bool? StarterJevDecidesForTest;

        private bool StarterJevDecides => StarterJevDecidesForTest ?? StarterMeasuresFamily.JevDecides;

        /// <summary>Whether Jev decides the starter measures for this session: the replay bar is met, and the
        /// point is live exactly as any other point is. When this is false the rule decides, for Pro and free
        /// alike, and the rule's starter items are on the Plan.</summary>
        private async Task<bool> StarterJevLiveAsync()
        {
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement)) return false;
            if (!StarterJevDecides) return false;
            return await JevPointLiveAsync(DecisionRegistry.MeasureStarter).ConfigureAwait(false);
        }

        /// <summary>A test's own answer to "has the relationship replay bar been met". Null on every real path, which
        /// reads the family's own constant (RelationshipFamily.JevDecides).</summary>
        internal bool? RelationshipJevDecidesForTest;

        /// <summary>A test's own cap on how many columns one relationship run asks about. Null on every real path.</summary>
        internal int? RelationshipMaxAsksForTest;

        private bool RelationshipJevDecides => RelationshipJevDecidesForTest ?? RelationshipFamily.JevDecides;

        /// <summary>Whether Jev decides relationships for this session: the replay bar is met and the point is live
        /// exactly as any other point is. False means nobody is asked, and the Plan keeps its rule item.</summary>
        private async Task<bool> RelationshipJevLiveAsync()
        {
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement)) return false;
            if (!RelationshipJevDecides) return false;
            return await JevPointLiveAsync(DecisionRegistry.RelationshipJoin).ConfigureAwait(false);
        }

        /// <summary>A test's own answer to "has the ai.schema replay bar been met". Null on every real path, which
        /// reads the family's own constant (AiSchemaFamily.JevDecides).</summary>
        internal bool? AiSchemaJevDecidesForTest;

        /// <summary>A test's own cap on new ai.schema asks per run. Null on every real path, which reads
        /// AiSchemaFamily.MaxObjectsPerRun.</summary>
        internal int? AiSchemaCapForTest;

        private bool AiSchemaJevDecides => AiSchemaJevDecidesForTest ?? AiSchemaFamily.JevDecides;

        /// <summary>Whether Jev decides what Copilot sees for this session: the replay bar is met, and the point is
        /// live exactly as any other point is. False means nothing is asked and the Plan has no ai.schema item.</summary>
        private async Task<bool> AiSchemaJevLiveAsync()
        {
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement)) return false;
            if (!AiSchemaJevDecides) return false;
            return await JevPointLiveAsync(DecisionRegistry.AiSchema).ConfigureAwait(false);
        }

        /// <summary>A test's own answer to "has the date table replay bar been met". Null on every real path, which
        /// reads the family's own constant (DateTableFamily.JevDecides).</summary>
        internal bool? DateTableJevDecidesForTest;

        private bool DateTableJevDecides => DateTableJevDecidesForTest ?? DateTableFamily.JevDecides;

        /// <summary>A test's own model facts snapshot for the date table family, in place of the one built for this
        /// revision. Null on every real path.</summary>
        internal Func<Facts.ModelFacts> DateTableFactsForTest;

        /// <summary>Whether Jev decides the date table for this session: the replay bar is met, and the point is live
        /// exactly as any other point is. False means the rule's row is the date table row, for Pro and free alike.</summary>
        private async Task<bool> DateTableJevLiveAsync()
        {
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement)) return false;
            if (!DateTableJevDecides) return false;
            return await JevPointLiveAsync(DecisionRegistry.DateTable).ConfigureAwait(false);
        }

        /// <summary>One point's switch, service and permission. The Pro check is the caller's, and comes first.</summary>
        private async Task<bool> JevPointLiveAsync(string pointName)
        {
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement)) return false;
            var point = DecisionRegistry.Require(pointName);
            if (!DecisionPointEnabled(point.Name)) return false;
            // A column point that did not clear its re-test is off whatever the switch says (its JevDecides constant).
            if (!ColumnJevDecides(point.Name)) return false;
            var decisions = Decisions;
            if (!decisions.Oracle.Available) return false;
            var scope = decisions.ReadScope == null ? null : await decisions.ReadScope().ConfigureAwait(false);
            if (string.IsNullOrEmpty(scope)) return false;
            return decisions.ReadConsent(scope).Covers(point, DecisionRegistry.ConsentSchemaVersion, out _);
        }

        /// <summary>Start a run for the session's current revision, cancelling whatever ran before. Caller
        /// holds <see cref="_jevRunGate"/>.</summary>
        private void StartJevRunLocked(Session s, SessionContext context, string liveKey,
                                       IReadOnlyDictionary<string, DecisionOutcome> carry = null)
        {
            var old = _jevRun;
            var run = new JevBackgroundRun { SessionId = s.Id, Revision = s.Revision, LiveKey = liveKey };
            _jevRun = run;
            if (old != null && !old.Done) { try { old.Cancel.Cancel(); } catch { } }
            Interlocked.Increment(ref _jevRunsStarted);
            run.Task = Task.Run(() => RunJevInBackgroundAsync(run, s, context, carry));
        }

        private async Task RunJevInBackgroundAsync(JevBackgroundRun run, Session s, SessionContext context,
                                                   IReadOnlyDictionary<string, DecisionOutcome> carry)
        {
            JevRun result = null;
            try
            {
                // FACTS FIRST, inside this same background run: the snapshot for this revision is built (or found)
                // before anything is asked, so the executor's data checks have facts to read when a person applies
                // a line. Pro only, like everything else in the run; nobody waits for it but the run itself.
                if (Entitlement.FeatureGrants.IsProTier(_entitlement))
                    await EnsureFactsForRevisionAsync(context, s, run.Cancel.Token).ConfigureAwait(false);
                result = await RunFamiliesAsync(s, run.Cancel.Token, carry).ConfigureAwait(false);
                if (result != null && !run.Cancel.IsCancellationRequested)
                    await ApplyJevRunAsync(s, context, CloneRun(result), publish: false).ConfigureAwait(false);
            }
            catch { result = null; }

            bool current;
            lock (_jevRunGate)
            {
                run.Result = run.Cancel.IsCancellationRequested ? null : result;
                run.Done = true;
                run.FinishedUtc = DateTime.UtcNow;
                current = ReferenceEquals(_jevRun, run);
                // A run that came back with nothing (the model moved while it asked, or it failed) is not a
                // decision for this revision. Forget it, so the next read or change starts a fresh one.
                if (current && run.Result == null) _jevRun = null;
            }

            // Tell the page once, whether or not a line moved: "Jev is looking" has to go away either way.
            if (current && !run.Cancel.IsCancellationRequested) AnnounceJevRun(context, s);
        }

        /// <summary>The plan-changed notification on the RPC door, which the Plan page re-reads on. Only when
        /// the run is still about the model that is open.</summary>
        private void AnnounceJevRun(SessionContext context, Session s)
        {
            try
            {
                if (!ReferenceEquals(_sessions.CurrentContext, context) || !ReferenceEquals(_sessions.Current, s)) return;
                PublishPlan(context.Plans.Current, s.Revision);
            }
            catch { }
        }

        /// <summary>Stop the background run now, without forgetting what it heard. A point was switched off, so requests that
        /// have not started must not start (the ones already sent cannot be recalled). The next read starts a run for the points
        /// still live.</summary>
        internal void CancelJevRun()
        {
            lock (_jevRunGate)
            {
                var old = _jevRun;
                _jevRun = null;
                if (old != null && !old.Done) { try { old.Cancel.Cancel(); } catch { } }
            }
        }

        private void ForgetJevRun()
        {
            lock (_jevRunGate)
            {
                var old = _jevRun;
                _jevRun = null;
                if (old != null && !old.Done) { try { old.Cancel.Cancel(); } catch { } }
            }
            // Nothing is live any more (free, switched off, no permission): what Jev was heard to say and decide goes
            // with it, so a permission given again buys fresh advice rather than serving the withdrawn run's.
            lock (_jevDecisionsGate)
            {
                _jevDecisions.Clear();
                _jevColumnAnswers.Clear();
            }
            _jevProgressMemory.Reset();
        }

        /// <summary>After a model change, re-plan in the background: a run about an older revision is
        /// cancelled and one for the new revision starts. Only for a session that has already had a run, so a
        /// session nobody has read the plan in never asks, and never for a free session.</summary>
        private void AttachJevRunTracking()
        {
            if (Interlocked.Exchange(ref _jevRunTrackingAttached, 1) != 0) return;
            _sessions.Bus.Changed += OnModelChangedForJev;
        }

        private void OnModelChangedForJev(ChangeNotification n)
        {
            try
            {
                if (n == null || !Entitlement.FeatureGrants.IsProTier(_entitlement)) return;
                var s = _sessions.Current;
                var context = _sessions.CurrentContext;
                if (s == null || context == null || !string.Equals(s.Id, n.SessionId, StringComparison.Ordinal)) return;
                lock (_jevRunGate)
                {
                    var run = _jevRun;
                    if (run == null || !string.Equals(run.SessionId, s.Id, StringComparison.Ordinal)) return;
                    if (run.Revision == s.Revision) return;
                    // The same points as the run it replaces: a model change does not switch a point.
                    StartJevRunLocked(s, context, run.LiveKey);
                }
            }
            catch { }
        }

        // ---- the columns a family decided, and the readiness rules that stand aside for them ----------------------

        // A DECISION HOLDS UNTIL JEV ANSWERS AGAIN FOR THAT COLUMN. What a family decided is kept per (point, column)
        // for the session, not per revision: an edit changes what Jev must be asked, and until it answers for the
        // column's new facts the decision it made stands, so the rules do not take the column back for the length of
        // an ask (or for good, when that ask fails). A later answer for the column replaces it, whichever way it goes.
        private readonly object _jevDecisionsGate = new object();
        private string _jevDecisionsSession;
        private readonly HashSet<string> _jevDecisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The column families' answers heard this session, by request digest. A column whose evidence has not
        /// changed is never asked twice, whatever the service's own cache holds, and the batches after the first ask
        /// only columns with no answer yet.</summary>
        private readonly Dictionary<string, DecisionOutcome> _jevColumnAnswers = new Dictionary<string, DecisionOutcome>(StringComparer.Ordinal);

        /// <summary>Keep the decisions straight for the session a run is about: a column the run heard back on is decided
        /// exactly as the run says (decided, or handed back to the rules); every other column keeps what it had.</summary>
        private void RememberJevDecisions(string sessionId, JevRun run)
        {
            lock (_jevDecisionsGate)
            {
                if (!string.Equals(_jevDecisionsSession, sessionId, StringComparison.Ordinal))
                {
                    _jevDecisions.Clear();
                    _jevColumnAnswers.Clear();
                    _jevDecisionsSession = sessionId;
                }
                if (run.Off) { _jevDecisions.Clear(); return; }
                foreach (var key in run.Heard)
                {
                    if (run.Decided.Contains(key)) _jevDecisions.Add(key);
                    else _jevDecisions.Remove(key);
                }
            }
        }

        private void ForgetJevDecisionsOffPoints(ICollection<string> livePoints)
        {
            lock (_jevDecisionsGate)
                _jevDecisions.RemoveWhere(k => !livePoints.Contains(k.Substring(0, Math.Max(0, k.IndexOf('\u001f')))));
        }

        private bool TryKnownColumnAnswer(string sessionId, string digest, out DecisionOutcome outcome)
        {
            lock (_jevDecisionsGate)
            {
                outcome = null;
                return string.Equals(_jevDecisionsSession, sessionId, StringComparison.Ordinal) && _jevColumnAnswers.TryGetValue(digest, out outcome);
            }
        }

        private void RememberColumnAnswer(string sessionId, string digest, DecisionOutcome outcome)
        {
            lock (_jevDecisionsGate)
            {
                if (!string.Equals(_jevDecisionsSession, sessionId, StringComparison.Ordinal))
                {
                    _jevDecisions.Clear();
                    _jevColumnAnswers.Clear();
                    _jevDecisionsSession = sessionId;
                }
                _jevColumnAnswers[digest] = outcome;
            }
        }

        /// <summary>The point that decides instead of a rule ON THE PLAN, per rule id, so one column has one proposal.
        /// For category, CAT-GEO. For summarize (Kane, 26 September: every number column Jev looks at stops adding up, and
        /// Jev picks its explicit base measure instead), FMT-SUMMARIZE and SUMMARIZE-DIMENSION, whose safe fix writes the
        /// same None. Only the Plan's rows and the rules' safe-fix writes stand aside: every scan, the Readiness page and
        /// the health check still read every rule whole (Kane's ruling, 26 September). DAC-IMPLICIT-MEASURE's "Add N
        /// explicit measures" row is NOT taken off: a row that leaves the Plan reads as done, and its gap closes only when
        /// the family's row is approved and the columns stop adding up (UAT lane n, 26 September).</summary>
        internal static readonly Dictionary<string, string> PointForRule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CAT-GEO"] = DecisionRegistry.ColumnCategory,
            ["FMT-SUMMARIZE"] = DecisionRegistry.ColumnSummarize,
            ["SUMMARIZE-DIMENSION"] = DecisionRegistry.ColumnSummarize,
        };

        /// <summary>Whether a live Jev family has decided this rule's object in the open session, so the rule's row, its
        /// lines and its apply leave that object alone. The decision holds across edits until Jev answers again for that
        /// column (<see cref="RememberJevDecisions"/>). False whenever nothing matches exactly.</summary>
        internal bool JevDecidedFinding(string ruleId, string objectRef) => JevDecidedFor(_sessions.Current?.Id, ruleId, objectRef);

        private bool JevDecidedFor(string sessionId, string ruleId, string objectRef)
        {
            if (sessionId == null || ruleId == null || objectRef == null) return false;
            if (!PointForRule.TryGetValue(ruleId, out var point)) return false;
            lock (_jevDecisionsGate)
                return string.Equals(_jevDecisionsSession, sessionId, StringComparison.Ordinal)
                       && _jevDecisions.Contains(JevRun.UnansweredKey(point, objectRef));
        }

        /// <summary>A copy of a run whose lines can go on the change plan without sharing an object with the
        /// remembered result: adding a line to the plan stamps it, and the same decision is put back on every
        /// read of this revision.</summary>
        private static JevRun CloneRun(JevRun run) => new JevRun
        {
            Off = run.Off,
            Revision = run.Revision,
            Lines = run.Lines.Select(l => l.Clone()).ToList(),
            Unanswered = new HashSet<string>(run.Unanswered, StringComparer.OrdinalIgnoreCase),
            Decided = new HashSet<string>(run.Decided, StringComparer.OrdinalIgnoreCase),
            Heard = new HashSet<string>(run.Heard, StringComparer.OrdinalIgnoreCase),
            ColumnProgress = new Dictionary<string, (int Looked, int Total)>(run.ColumnProgress, StringComparer.Ordinal),
            Progress = run.Progress.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal),
            NamesOnly = new HashSet<string>(run.NamesOnly, StringComparer.Ordinal),
            Answered = run.Answered,
            Refused = run.Refused.ToDictionary(f => f.Key, f => f.Value.ToList(), StringComparer.Ordinal),
            AiSchemaProgress = run.AiSchemaProgress,
            AiSchemaAnswers = new Dictionary<string, DecisionOutcome>(run.AiSchemaAnswers, StringComparer.Ordinal),
            SummarizePicks = run.SummarizePicks.ToList(),
            StopReason = run.StopReason,
        };

        /// <summary>Bring the change plan's Jev lines into step with one run. Never throws for an ordinary
        /// failure: advice going quiet must never turn into a failed plan read.</summary>
        private async Task ApplyJevRunAsync(Session s, SessionContext context, JevRun run, bool publish)
        {
            if (s == null || context == null || run == null) return;

            var sourceVersion = run.Off ? "" : SourceVersionOf(s);
            await context.PlanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // The model this run is about must still be the open one, at the revision the lines were
                // worked out at. Anything else and writing now would put one model's advice on another's
                // list, or old advice on a newer model.
                if (!ReferenceEquals(_sessions.CurrentContext, context) || !ReferenceEquals(_sessions.Current, s)) return;
                if (!run.Off && s.Revision != run.Revision) return;
                if (!run.Off) RememberJevRefusals(s.Id, run.Refused, run.Lines);
                RememberJevDecisions(s.Id, run);
                RememberJevColumnNotes(s.Id, run);
                RememberJevProgress(s.Id, run.Off ? null : run.AiSchemaProgress);

                var plan = context.Plans.Current;
                var waiting = plan == null
                    ? new List<ChangeItem>()
                    : plan.Snapshot().Where(l => JevFamily.IsJevLine(l) && IsPendingLine(l)).ToList();

                if (run.Off)
                {
                    if (waiting.Count == 0) return;
                    var gone = new HashSet<ChangeItem>(waiting, ReferenceEqualityComparer.Instance);
                    await HoldKeptAsync(s, waiting.Where(l => l.AuthoredAfter != null).ToList(), JevFamily.HeldReason).ConfigureAwait(false);
                    plan.RemoveSynced(l => gone.Contains(l));
                    if (publish) PublishPlan(plan, s.Revision);
                    return;
                }

                var desired = run.Lines.Where(l => !JevDismissed(s.Id, l)).ToList();
                var replaceable = waiting.Where(l => !run.Unanswered.Contains(JevRun.UnansweredKey(l.DecisionPoint, l.ObjectRef))).ToList();

                // A VALUE SOMEBODY WROTE OVER A LINE STAYS (Sol 6.1, finding 1.3). The line is matched without its value, so the family
                // proposing its own text again does not undo the person's words.
                // A LINE SOMEBODY WROTE IS NEVER REMOVED BY A SYNC (Sol 6.1 on the M11 delta, finding A1, the second strike of 1.3). The first fix kept the
                // words only while the family went on offering the line; the family stopped offering it when the measure was made elsewhere, and
                // the removal below took the words with the family's own lines. So an authored line the family does not propose again is HELD:
                // it stays on the plan with its text, waits for a fresh tick, and is checked against the object as it is when applied
                // (ChangePlanState.RemoveSynced is the only way a sync removes lines, and it spares every authored one).
                var held = new List<ChangeItem>();
                var moved = new List<ChangeItem>();
                foreach (var authored in replaceable.Where(l => l.AuthoredAfter != null))
                {
                    var shape = JevFamily.ShapeOf(authored);
                    var again = desired.FirstOrDefault(d => string.Equals(JevFamily.ShapeOf(d), shape, StringComparison.Ordinal));
                    if (again == null) { held.Add(authored); continue; }
                    again.After = authored.AuthoredAfter;
                    again.AuthoredAfter = authored.AuthoredAfter;
                    again.Generated = true;
                    moved.Add(authored);
                }
                var heldChanged = false;
                if (held.Count > 0)
                {
                    // Held once, for the reason the recheck finds: a first pass under a stand-in reason would make every sync look like a new reason.
                    heldChanged |= await RecheckHeldLinesAsync(s, held).ConfigureAwait(false);
                    replaceable = replaceable.Where(l => !held.Contains(l)).ToList();
                }

                // JEV IS SILENT FOR THESE OBJECTS (the relay is down, or the session's budget is spent), so their waiting lines stay. But the
                // engine's own approval of a format line was given because the rule proved it on the model as it was, and a person's tick
                // was given for the object as it was. Neither may stay on a model that has changed under it: ask the rule again now, and a
                // line it stops proving goes back to an unticked suggestion with a note, so a stale approval never sits on the plan with a
                // "sure" beside it. A line Jev worked out for an older version of its object says so, is never shown as current, and can
                // be neither ticked nor written; the row's other lines (a folder writes no value) are brought up to date so the row is
                // not stuck behind a Jev that cannot answer (THE FORMAT FILL RULE, addendum 3 items 12 and 17, addendum 4 items 23 and 24).
                var silent = waiting.Except(replaceable).Except(held).ToList();
                var revalidated = silent.Count == 0 ? false : await RevalidateSilentLinesAsync(s, plan, silent, run.Revision).ConfigureAwait(false);

                var desiredKeys = new HashSet<string>(desired.Select(JevFamily.KeyOf), StringComparer.Ordinal);
                var currentKeys = new HashSet<string>(replaceable.Select(JevFamily.KeyOf), StringComparer.Ordinal);
                if (desiredKeys.SetEquals(currentKeys) && replaceable.All(l => l.ProposedAtRevision == run.Revision))
                {
                    if ((revalidated || heldChanged) && publish) PublishPlan(plan, s.Revision);
                    return;
                }

                // A person's decision about a line carries to the same line worked out again: their DROP always (a drop
                // writes nothing), and their KEEP only over the same text they saw when they ticked it. The engine's own
                // approval never carries. It was given because the rule proved the line on the model as it was; worked out
                // again, the line has the status the rule gives it now, and a line the rule stopped proving must arrive as a
                // suggestion (THE FORMAT FILL RULE, addendum 2 item 8).
                var decided = new Dictionary<string, ChangeItem>(StringComparer.Ordinal);
                foreach (var l in replaceable) decided[JevFamily.KeyOf(l)] = l;

                if (plan == null)
                {
                    if (desired.Count == 0) return;
                    plan = context.Plans.GetOrStart();
                }
                var old = new HashSet<ChangeItem>(replaceable, ReferenceEqualityComparer.Instance);
                // What each ticked object computes now, so a keep is carried only onto the same object (a name is not an object).
                var digests = decided.Values.Any(p => p.KeptByPerson)
                    ? await s.ReadAsync(m => decided.Values.Where(p => p.KeptByPerson).Select(p => p.ObjectRef).Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(r => r, r => { try { return Semanticus.Analysis.FormatFill.ComputeDigest(ObjectRefs.Resolve(m, r)); } catch { return null; } }, StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // A tick that finds no line under the same name is either about an object that is gone or about one that was RENAMED: a tick
                // goes with the object and its name, so a renamed measure's line comes back unticked, and it says why (addendum 4 item 28, L2).
                // Recognised by what the object computes (the digest carries no name), so a line about another object never says it.
                var orphanKeeps = decided.Values
                    .Where(p => p.KeptByPerson && p.KeptDigest != null && string.Equals(p.Status, "approved", StringComparison.OrdinalIgnoreCase)
                                && !desiredKeys.Contains(JevFamily.KeyOf(p)))
                    .ToList();
                foreach (var line in moved) line.AuthoredAfter = null;   // the words live on the line that replaces this one, so this one may now go
                plan.RemoveSynced(l => old.Contains(l));
                foreach (var line in desired)
                {
                    if (decided.TryGetValue(JevFamily.KeyOf(line), out var prior))
                        JevFamily.CarryDecision(prior, line, digests.TryGetValue(prior.ObjectRef ?? "", out var d) ? d : null);
                    else if (orphanKeeps.Count > 0 && Semanticus.Engine.Decisions.JevFormatSuggestion.IsSuggestion(line.CheckText)
                             && orphanKeeps.Any(o => string.Equals(o.Kind, line.Kind, StringComparison.Ordinal) && string.Equals(o.After, line.After, StringComparison.Ordinal)
                                                     && string.Equals(o.KeptDigest, line.WorkedOutDigest, StringComparison.Ordinal)))
                        line.CheckText = Semanticus.Engine.Decisions.JevFormatSuggestion.RenamedKeepNote;
                }
                // A line that is new on this run (the Excel line, once a sort-by has gone) arrives kept, but it cannot
                // stand without the line it needs (ChangeItem.Needs): while that one stays dropped, so does this.
                JevFamily.DropWhereNeedDropped(desired);
                foreach (var line in desired) plan.Add(line, run.Revision, sourceVersion);
                if (publish) PublishPlan(plan, s.Revision);
            }
            finally { context.PlanGate.Release(); }
        }

        /// <summary>Say, on each held line (an authored line its family stopped offering), whether the object it names is still there and still holds
        /// what the line was written over. The apply-time check says the same again when somebody applies it; this is what the plan shows until then.
        /// True when a line changed.</summary>
        private async Task<bool> RecheckHeldLinesAsync(Session s, List<ChangeItem> lines, string whole = null)
        {
            var look = await s.ReadAsync(m => lines.Select(l =>
            {
                TabularEditor.TOMWrapper.ITabularNamedObject obj = null;
                try { obj = ObjectRefs.Resolve(m, l.ObjectRef); } catch { }
                return (Line: l, Here: obj != null, Live: obj == null ? null : LiveValueForPlanItem(m, l));
            }).ToList()).ConfigureAwait(false);
            var changed = false;
            foreach (var (line, here, live) in look)
            {
                var reason = !here ? JevFamily.HeldMissingReason
                    : live != null && !PlanTextEquals(live, line.Before) && !PlanTextEquals(live, line.After) ? JevFamily.HeldChangedReason
                    : whole ?? JevFamily.HeldReason;
                changed |= JevFamily.HoldAuthored(line, reason);
            }
            return changed;
        }

        /// <summary>Bring the waiting lines of objects Jev did not answer for at this revision up to date, using only what the engine itself can
        /// check. A FORMAT line the engine approved that the rule no longer proves, and a tick whose object no longer computes what it did,
        /// go back to unticked suggestions with a note. A format line worked out for an older version of its object (its stamp does not
        /// match the object now) becomes an unticked suggestion that says Jev has not answered again: its old reading is never shown as
        /// current, and a tick or a write on it is refused. A display folder line writes no value, so it is stamped current when its
        /// measure still resolves and still holds what the line was about, and dropped when it does not, so a row that carries folder
        /// lines is never left stuck behind a Jev that is silent. True when a line changed or went.</summary>
        private async Task<bool> RevalidateSilentLinesAsync(Session s, ChangePlanState plan, List<ChangeItem> lines, long revision)
        {
            var look = await s.ReadAsync(m => lines.Select(l =>
            {
                TabularEditor.TOMWrapper.ITabularNamedObject obj = null;
                try { obj = ObjectRefs.Resolve(m, l.ObjectRef); } catch { }
                var format = IsJevFormatLine(l);
                return (Line: l, Format: format,
                        Proved: format && obj != null && Semanticus.Analysis.FormatFill.MayFill(obj, l.After),
                        Digest: obj == null ? null : Semanticus.Analysis.FormatFill.ComputeDigest(obj),
                        Folder: (obj as TabularEditor.TOMWrapper.Measure)?.DisplayFolder,
                        Resolved: obj != null);
            }).ToList()).ConfigureAwait(false);
            var changed = false;
            var gone = new HashSet<ChangeItem>(ReferenceEqualityComparer.Instance);
            foreach (var (line, format, proved, digest, folder, resolved) in look)
            {
                if (!format)
                {
                    // Only a display folder is brought up to date here (it writes no value). Any other kind of Jev line keeps the revision
                    // it was worked out at, exactly as before.
                    if (!string.Equals(line.Kind, "set_display_folder", StringComparison.Ordinal)) continue;
                    var before = string.Equals(line.Before, "(none)", StringComparison.Ordinal) ? "" : line.Before ?? "";
                    if (!resolved || (!string.Equals(folder ?? "", before, StringComparison.Ordinal) && !string.Equals(folder ?? "", line.After ?? "", StringComparison.Ordinal)))
                    { gone.Add(line); changed = true; }
                    else line.ProposedAtRevision = revision;
                    continue;
                }
                // Checked by the engine just now, so the set is not stale for want of a Jev answer nobody can get.
                line.ProposedAtRevision = revision;
                var stale = line.WorkedOutDigest != null && !string.Equals(line.WorkedOutDigest, digest, StringComparison.Ordinal);
                if (line.KeptByPerson)
                {
                    if (!stale && (proved || (line.KeptDigest != null && string.Equals(line.KeptDigest, digest, StringComparison.Ordinal)))) continue;
                    line.KeptByPerson = false; line.KeptAgainst = null; line.KeptDigest = null;
                    ToSuggestion(line, Semanticus.Engine.Decisions.JevFormatSuggestion.KeepVoidNote);
                    changed = true;
                }
                else if (stale && !string.Equals(line.Status, "rejected", StringComparison.OrdinalIgnoreCase))
                {
                    ToSuggestion(line, Semanticus.Engine.Decisions.JevFormatSuggestion.StaleNote);
                    line.Reason = line.Rationale = "Jev's earlier answer is out of date, because the model changed after Jev chose it.";
                    changed = true;
                }
                else if (Semanticus.Engine.Decisions.JevFormatSuggestion.IsSuggestion(line.CheckText)) continue;   // already only a suggestion
                else if (!proved && string.Equals(line.Status, "approved", StringComparison.OrdinalIgnoreCase))
                {
                    ToSuggestion(line, Semanticus.Engine.Decisions.JevFormatSuggestion.SilentNote);
                    changed = true;
                }
            }
            // A display folder line somebody wrote over stays, held, even when its measure is gone or holds something else now.
            await HoldKeptAsync(s, gone.Where(l => l.AuthoredAfter != null).ToList(), JevFamily.HeldReason).ConfigureAwait(false);
            if (gone.Count > 0) plan.RemoveSynced(l => gone.Contains(l));
            return changed;
        }

        /// <summary>WHEN JEV DOES NOT ANSWER, SEMANTICUS STANDS IN FOR IT (Kane, 13 Oct 2026: "clicking run plan here does nothing", "stuck on a
        /// message about skipped Jev"). A Jev row worked out before the model last changed reads stale, and Run the plan used to leave it out
        /// and wait for a Jev that might never come back, with the rows behind it Waiting for it. Jev's earlier answer is still Jev's best
        /// answer; the engine can check for itself whether it still fits the model as it is now. Each line is checked against the live
        /// object: it still fits when the object holds what the line was written over (or already holds what it writes), or when it is a
        /// new thing whose name is still free. When every line of a row fits, the row is stamped as current and runs; the approval guard
        /// still checks every value again inside the write. A row with a line that no longer fits goes off the plan whole, and Jev puts it
        /// back, worked out again, when it answers. True when anything on the plan changed.</summary>
        internal async Task<bool> StandInForSilentJevAsync(Session s, PlanMemoryBinding bound, IReadOnlyCollection<ModelPlanItem> rows, List<(string Id, string Title)> wentBack = null)
        {
            var context = bound?.Context;
            if (s == null || context == null || rows == null || rows.Count == 0) return false;
            await context.PlanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_sessions.CurrentContext, context) || !ReferenceEquals(_sessions.Current, s)) return false;
                var plan = context.Plans.Current;
                if (plan == null) return false;
                var byRow = rows.ToDictionary(r => r.Id, r => (r.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Select(l => l.PlanItemId).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
                var all = plan.Snapshot().Where(l => JevFamily.IsJevLine(l) && IsPendingLine(l)).ToList();
                var look = await s.ReadAsync(m =>
                {
                    var missing = Semanticus.Analysis.FormulaReferences.HostChecker(m);
                    return all.Select(l => (Line: l, Fits: JevLineStillFits(m, l, missing))).ToList();
                }).ConfigureAwait(false);
                var fits = look.ToDictionary(x => x.Line, x => x.Fits, ReferenceEqualityComparer.Instance);
                var revision = s.Revision;
                var source = SourceVersionOf(s);
                var gone = new HashSet<ChangeItem>(ReferenceEqualityComparer.Instance);
                var fresh = new List<string>();
                foreach (var (rowId, ids) in byRow.Select(kv => (kv.Key, kv.Value)))
                {
                    var mine = all.Where(l => l.Id != null && ids.Contains(l.Id)).ToList();
                    if (mine.Count == 0) continue;
                    if (mine.All(l => (bool)fits[l]))
                    {
                        foreach (var l in mine) { l.ProposedAtRevision = revision; l.ProposedAtSource = source ?? ""; }
                        fresh.Add(rowId);
                    }
                    else
                    {
                        foreach (var l in mine) gone.Add(l);
                        var row = rows.First(r => r.Id == rowId);
                        wentBack?.Add((rowId, string.IsNullOrWhiteSpace(row.Title) ? rowId : row.Title));
                    }
                }
                if (fresh.Count == 0 && gone.Count == 0) return false;
                if (fresh.Count > 0)
                    TryWritePlanMemory(bound, state =>
                    {
                        foreach (var id in fresh)
                            if (state.Changes.TryGetValue(id, out var header) && header != null)
                            {
                                header.Revision = revision; header.SourceVersion = source ?? ""; header.StaleAtProposal = false;
                            }
                    });
                if (gone.Count > 0)
                {
                    await HoldKeptAsync(s, gone.Where(l => l.AuthoredAfter != null).ToList(), JevFamily.HeldReason).ConfigureAwait(false);
                    plan.RemoveSynced(l => gone.Contains(l));
                }
                PublishPlan(plan, s.Revision);
                return true;
            }
            finally { context.PlanGate.Release(); }
        }

        private static bool JevLineStillFits(TabularEditor.TOMWrapper.Model m, ChangeItem l, Func<string, string, string[]> missing = null)
        {
            TabularEditor.TOMWrapper.ITabularNamedObject obj = null;
            try { obj = ObjectRefs.Resolve(m, l.ObjectRef); } catch { }
            var kind = (l.Kind ?? "").Trim();
            // A line that carries a formula fits only when every table, column and measure it names is still in the model, and a
            // column it adds up is still a number (a column deleted, renamed or turned to text since Jev answered).
            if (!DaxStillFits(m, l, kind, missing)) return false;
            if (kind.StartsWith("create", StringComparison.Ordinal)) return obj == null;   // a name that is taken now is a different job
            if (obj == null) return PlanTextEquals(l.Before, "");                          // a line on a thing the row itself creates
            var live = LiveValueForPlanItem(m, l);
            return live == null || PlanTextEquals(live, l.Before) || PlanTextEquals(live, l.After);
        }

        private static readonly System.Text.RegularExpressions.Regex AddsUpColumn = new System.Text.RegularExpressions.Regex(
            @"\b(?:SUM|AVERAGE|MEDIAN)\s*\(\s*(?:'((?:[^']|'')+)'|([A-Za-z_]\w*))\s*\[([^\]]+)\]\s*\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static bool DaxStillFits(TabularEditor.TOMWrapper.Model m, ChangeItem l, string kind, Func<string, string, string[]> missing)
        {
            if (kind != "create_measure" && kind != "set_dax" && !string.Equals(l.Target, "expression", StringComparison.Ordinal)) return true;
            var dax = l.After;
            if (string.IsNullOrWhiteSpace(dax)) return true;
            string host = null;
            var at = (l.ObjectRef ?? "").IndexOf(':');
            var slash = (l.ObjectRef ?? "").IndexOf('/');
            if (at >= 0 && slash > at) host = l.ObjectRef.Substring(at + 1, slash - at - 1);
            if (missing != null && missing(dax, host).Length > 0) return false;
            foreach (System.Text.RegularExpressions.Match hit in AddsUpColumn.Matches(dax))
            {
                var table = hit.Groups[1].Success ? hit.Groups[1].Value.Replace("''", "'") : hit.Groups[2].Value;
                var col = m.Tables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase))?
                    .Columns.OfType<TabularEditor.TOMWrapper.DataColumn>().FirstOrDefault(c => string.Equals(c.Name, hit.Groups[3].Value, StringComparison.OrdinalIgnoreCase));
                if (col != null && !StarterMeasureTemplates.IsNumber(col.DataType)) return false;
            }
            return true;
        }

        private static void ToSuggestion(ChangeItem line, string note)
        {
            line.Status = "proposed";
            line.Risk = "suggestion";
            line.CheckText = note;
            // A line that is already a suggestion reads "Jev's reading, not proved: ..." once, never twice.
            var reason = Semanticus.Engine.Decisions.JevFormatSuggestion.IsSuggestion(line.CheckText) && (line.Reason ?? "").StartsWith("Jev's reading, not proved:", StringComparison.Ordinal)
                ? line.Reason : Semanticus.Engine.Decisions.JevFormatSuggestion.ReasonFor(true, line.Reason);
            line.Reason = reason;
            line.Rationale = reason;
        }

        /// <summary>The families: enumerate, ask, map. Each family runs only while its own point is live, and the
        /// run's lines are the union of theirs, so a family that is not live has no line on the plan after the
        /// sync. Returns Off when no family may run, and null when the model moved (or the run was cancelled)
        /// while it was being asked, so the caller leaves everything alone.</summary>
        private Task<JevRun> RunFamiliesAsync(Session s, CancellationToken ct,
                                              IReadOnlyDictionary<string, DecisionOutcome> carry = null) =>
            RunFamiliesAsync(PlanJevTarget(s), ct, carry);

        /// <summary>WHAT ONE FAMILY RUN READS, as a seam (spec-first start, lane sf-decide). The Plan passes today's values
        /// (<see cref="PlanJevTarget"/>); the start plan's decide loop passes its hidden scratch model, the scratch's own facts, a fence
        /// that follows the scratch and not the open model, and a book of answers of its own, so a scratch never writes into the open
        /// model's session-keyed state (the answers kept per session, the set of decided columns) nor reads it.</summary>
        internal sealed class JevRunTarget
        {
            public Session Session;
            /// <summary>The model facts the families read. Null on the function or in its result means none measured.</summary>
            public Func<Facts.ModelFacts> Facts;
            /// <summary>The date table family's facts, when they differ from <see cref="Facts"/> (a test's own). Null reads <see cref="Facts"/>.</summary>
            public Func<Facts.ModelFacts> DateFacts;
            /// <summary>The fence for a request about this revision of <see cref="Session"/>: stale once the thing it was made for has moved on.</summary>
            public Func<long, DecisionSessionFence> Fence;
            /// <summary>The column families' answers already heard, by request digest (null for none), and where a new one is kept.</summary>
            public Func<string, DecisionOutcome> KnownAnswer;
            public Action<string, DecisionOutcome> RememberAnswer;
            /// <summary>The measures a person removed, so the starter rule never offers one back. Null reads none.</summary>
            public Func<HashSet<string>> RemovedMeasures;
            /// <summary>The columns a start plan heard Jev on for column.summarize (refs), so the Plan does not ask a column the build set to None again. Null reads none.</summary>
            public Func<ISet<string>> StartDecidedColumns;
            /// <summary>The facts the plan executor's data checks read when the caller applies lines to this model (null: none measured).</summary>
            public Func<IJevModelFacts> ExecutorFacts;
            /// <summary>True when the caller is a batch that loops until nothing is left (the start plan's decide loop): the per-run cap on NEW asks
            /// is lifted, so a run looks at every object and the batching by table is what bounds the cost. The Plan reads on a page and keeps its caps.</summary>
            public bool UncappedAsks;
            /// <summary>What the measures and column format families remember across runs, and the id it is bound to (a different id starts it empty).
            /// Null reads the engine's own, bound to <see cref="Session"/>; the start plan passes its own, bound to the plan, so a scratch rebuilt every round
            /// neither clears the Plan's memory nor loses the plan's own.</summary>
            public JevProgressMemory Memory;
            public string MemoryId;
        }

        /// <summary>Today's values for the Plan: the open model's session, its current facts, the fence that follows the open model, the
        /// answers kept for the session, the History's removed measures.</summary>
        private JevRunTarget PlanJevTarget(Session s) => new JevRunTarget
        {
            Session = s,
            Facts = TryGetJevAskFacts,   // what a request may carry: the sample of rows only under the person's stored permission for it (SampleDisclosedAsync)
            DateFacts = DateTableFactsForTest,
            Fence = revision => new DecisionSessionFence(s.Id, revision, () =>
            {
                var current = _sessions.Current;
                return (current == null ? null : current.Id, current == null ? -1L : current.Revision);
            }),
            KnownAnswer = digest => TryKnownColumnAnswer(s.Id, digest, out var heard) ? heard : null,
            RememberAnswer = (digest, outcome) => RememberColumnAnswer(s.Id, digest, outcome),
            RemovedMeasures = () => RemovedMeasureRefs(s),
            StartDecidedColumns = () => string.Equals(_startSummarizeSession, s.Id, StringComparison.Ordinal)
                ? new HashSet<string>(_startSummarizeDecided.Keys, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),   // decided in another model (or none): nothing carries over
            ExecutorFacts = JevFacts,
        };

        internal async Task<JevRun> RunFamiliesAsync(JevRunTarget target, CancellationToken ct,
                                                     IReadOnlyDictionary<string, DecisionOutcome> carry = null)
        {
            var s = target.Session;
            // FIRST, before any read of the model: a free session never runs Jev, not even to count.
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement)) return new JevRun { Off = true };
            var measuresLive = await JevPointLiveAsync(DecisionRegistry.MeasureFolderAndFormat).ConfigureAwait(false);
            var starterLive = await StarterJevLiveAsync().ConfigureAwait(false);
            var columnFormatLive = await JevPointLiveAsync(DecisionRegistry.ColumnFormat).ConfigureAwait(false);
            var relationshipLive = await RelationshipJevLiveAsync().ConfigureAwait(false);
            var aiSchemaLive = await AiSchemaJevLiveAsync().ConfigureAwait(false);
            var columnCategoryLive = await JevPointLiveAsync(DecisionRegistry.ColumnCategory).ConfigureAwait(false);
            var columnSummarizeLive = await JevPointLiveAsync(DecisionRegistry.ColumnSummarize).ConfigureAwait(false);
            var dateTableLive = await DateTableJevLiveAsync().ConfigureAwait(false);
            if (!measuresLive && !starterLive && !columnFormatLive && !relationshipLive && !aiSchemaLive && !columnCategoryLive && !columnSummarizeLive && !dateTableLive) return new JevRun { Off = true };
            if (ct.IsCancellationRequested) return null;

            var measuresPoint = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
            var starterPoint = DecisionRegistry.Require(DecisionRegistry.MeasureStarter);
            var columnFormatPoint = DecisionRegistry.Require(DecisionRegistry.ColumnFormat);
            var relationshipPoint = DecisionRegistry.Require(DecisionRegistry.RelationshipJoin);
            var aiSchemaPoint = DecisionRegistry.Require(DecisionRegistry.AiSchema);
            var columnCategoryPoint = DecisionRegistry.Require(DecisionRegistry.ColumnCategory);
            var columnSummarizePoint = DecisionRegistry.Require(DecisionRegistry.ColumnSummarize);
            var dateTablePoint = DecisionRegistry.Require(DecisionRegistry.DateTable);
            var decisions = Decisions;
            // The snapshot the background run built for this revision, before anything was asked. Null means no
            // data or not measured, and the starter request then says so rather than sending zeros.
            var facts = starterLive || relationshipLive || columnCategoryLive || columnSummarizeLive ? target.Facts?.Invoke() : null;
            var relationshipCap = target.UncappedAsks ? int.MaxValue : RelationshipMaxAsksForTest ?? RelationshipFamily.MaxColumnsPerRun;
            var dateFacts = dateTableLive ? (target.DateFacts != null ? target.DateFacts() : target.Facts?.Invoke()) : null;

            var removed = starterLive || columnSummarizeLive ? target.RemovedMeasures?.Invoke() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
            var startDecided = columnSummarizeLive ? target.StartDecidedColumns?.Invoke() : null;
            var prepared = await s.ReadAsync(m =>
            {
                var measures = measuresLive ? MeasuresFamily.Prepare(m, DisplayFolderSuggestions) : new MeasuresFamily.Prepared();
                var starter = starterLive ? StarterMeasuresFamily.Prepare(m, facts, removed) : new StarterMeasuresFamily.Prepared();
                var columnFormat = columnFormatLive ? ColumnFormatFamily.Prepare(m) : new ColumnFormatFamily.Prepared();
                var relationship = relationshipLive ? RelationshipFamily.Prepare(m, facts, relationshipCap) : new RelationshipFamily.Prepared();
                // Every eligible object is prepared; how many are ASKED per run is the batch cap, below.
                var aiSchema = aiSchemaLive ? AiSchemaFamily.Prepare(m) : new AiSchemaFamily.Prepared();
                var columnCategory = columnCategoryLive ? ColumnCategoryFamily.Prepare(m, facts) : new ColumnCategoryFamily.Prepared();
                var dateTable = dateTableLive ? DateTableFamily.Prepare(m, dateFacts) : new DateTableFamily.Prepared();
                measures.Revision = s.Revision;
                starter.Revision = s.Revision;
                columnFormat.Revision = s.Revision;
                dateTable.Revision = s.Revision;
                var columnSummarize = columnSummarizeLive ? ColumnSummarizeFamily.Prepare(m, facts, removed, skipAnswered: !target.UncappedAsks, startDecided: startDecided) : new ColumnSummarizeFamily.Prepared();
                columnCategory.Revision = s.Revision;
                columnSummarize.Revision = s.Revision;
                relationship.Revision = s.Revision;
                aiSchema.Revision = s.Revision;
                return (Measures: measures, Starter: starter, ColumnFormat: columnFormat, Relationship: relationship, AiSchema: aiSchema, ColumnCategory: columnCategory,
                        ColumnSummarize: columnSummarize, DateTable: dateTable, Revision: s.Revision);
            }).ConfigureAwait(false);

            var run = new JevRun { Revision = prepared.Revision };
            if (prepared.Measures.Asks.Count == 0 && prepared.Starter.Asks.Count == 0 && prepared.ColumnFormat.Asks.Count == 0
                && prepared.Relationship.Asks.Count == 0 && prepared.AiSchema.Asks.Count == 0 && prepared.ColumnCategory.Asks.Count == 0 && prepared.ColumnSummarize.Asks.Count == 0
                && prepared.DateTable.Ask == null)
                return run;

            var fence = target.Fence(prepared.Revision);

            var stops = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var inFlight = new SemaphoreSlim(JevAsksInFlight, JevAsksInFlight);
            async Task<DecisionOutcome> AskOne(string pointName, IReadOnlyDictionary<string, object> snapshot,
                                               IReadOnlyList<DecisionQuestion> questions, bool perQuestion = false)
            {
                try { await inFlight.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
                try
                {
                    // THE REAL SWITCH, when this request starts, not the one the run began with: a request that waited behind six others
                    // while the person switched the point off must send nothing (Sol 6.1, finding 2.1).
                    var outcome = await decisions.AskAsync(pointName, snapshot, questions,
                        isPro: true, enabled: DecisionPointEnabled(pointName), fence: fence, ct: ct, perQuestion: perQuestion).ConfigureAwait(false);
                    if (StopReasonOf(outcome) is { } stop) stops.Enqueue(stop);
                    return outcome;
                }
                catch { stops.Enqueue(NoAnswerReason); return null; }
                finally { inFlight.Release(); }
            }

            // ONE REQUEST PER TABLE (JevTableBatch.cs). Each batch is asked once (AskOne holds six in flight at a time) and its
            // outcome goes back to every object in it, narrowed to that object's own answers under the plain question names its
            // family always read, so a family's Map does not know whether it was asked one at a time or a table at a time. An
            // object whose answer was refused (or left out of the reply) reads as "not decided" and keeps whatever it had.
            static DecisionOutcome NarrowOutcome<TAsk>(DecisionOutcome outcome, TableBatch<TAsk> batch, TAsk ask) where TAsk : class =>
                outcome == null || !outcome.Asked ? outcome : DecisionOutcome.Answered(outcome.Answers.Narrow(batch.Names[ask]));
            (TAsk Ask, DecisionOutcome Outcome)[] Spread<TAsk>(IReadOnlyList<TAsk> asks, IEnumerable<(TableBatch<TAsk> Batch, DecisionOutcome Outcome)> results) where TAsk : class
            {
                var byAsk = new Dictionary<TAsk, DecisionOutcome>(ReferenceEqualityComparer.Instance as IEqualityComparer<TAsk>);
                foreach (var (batch, outcome) in results)
                    foreach (var ask in batch.Asks) byAsk[ask] = NarrowOutcome(outcome, batch, ask);
                return asks.Select(a => (Ask: a, Outcome: byAsk.TryGetValue(a, out var o) ? o : null)).ToArray();
            }
            async Task<(TAsk Ask, DecisionOutcome Outcome)[]> AskBatches<TAsk>(DecisionPoint point, IReadOnlyList<TAsk> asks,
                IReadOnlyList<TableBatch<TAsk>> batches) where TAsk : class
            {
                var asked = await Task.WhenAll(batches.Select(async b =>
                    (Batch: b, Outcome: await AskOne(point.Name, b.Snapshot, b.Questions, perQuestion: true).ConfigureAwait(false)))).ConfigureAwait(false);
                return Spread(asks, asked);
            }

            // THE FAMILIES THAT LOOK AT EVERY ELIGIBLE OBJECT (measures and column format; m11/sf-progress). Prepare lists them all;
            // this asks a run's worth (the cap), the objects nobody has asked first, and the run's progress record says how many
            // are answered, how many got no usable answer, and how many are still to be asked, so the next run starts for the rest
            // and a family that stopped short says so. An object whose answer was heard is read back and sent nowhere; an object
            // already tried at this revision is not asked again at it, so a first batch the reader cannot use is not asked twice
            // and does not hold the objects behind it.
            async Task<(TAsk Ask, DecisionOutcome Outcome)[]> AskEveryAsync<TAsk>(string family, DecisionPoint point, IReadOnlyList<TAsk> asks, int cap,
                Func<TAsk, IReadOnlyDictionary<string, object>> snapshotOf, Func<TAsk, IReadOnlyList<DecisionQuestion>> questionsOf,
                Func<IReadOnlyList<TAsk>, Task<(TAsk Ask, DecisionOutcome Outcome)[]>> askThese) where TAsk : class
            {
                var memory = target.Memory ?? _jevProgressMemory;
                var memoryId = target.MemoryId ?? s.Id;
                var revision = prepared.Revision;
                var digests = asks.Select(a => new DecisionRequest(point.Name, point.SchemaVersion, snapshotOf(a), questionsOf(a)).Digest).ToArray();
                var outcomes = new DecisionOutcome[asks.Count];
                var fresh = new List<int>();
                var servedNow = decisions?.ServedVersionNow;
                for (var i = 0; i < asks.Count; i++)
                {
                    if (memory.TryAnswer(memoryId, digests[i], revision, servedNow, out var heard)) outcomes[i] = heard;
                    else if (!memory.TriedAt(memoryId, digests[i], revision)) fresh.Add(i);
                }
                // The objects nobody has asked first, in the family's order; then the ones that got no usable answer, the oldest try first.
                var ordered = fresh.OrderBy(i => memory.TickOf(memoryId, digests[i])).ToList();
                // This family already came back with nothing usable as often as it may at this revision: nothing more goes out at it.
                var blocked = memory.EmptyRuns(memoryId, family, revision) >= MaxEmptyRunsInRow;
                var now = blocked ? new List<int>() : ordered.Take(Math.Max(1, cap)).ToList();
                var asked = now.Count == 0 ? Array.Empty<(TAsk Ask, DecisionOutcome Outcome)>()
                    : await askThese(now.Select(i => asks[i]).ToList()).ConfigureAwait(false);

                // A run that was cancelled or overtaken by an edit records no tries: what it did not hear is asked again.
                var settled = !ct.IsCancellationRequested && s.Revision == revision;
                int answered = 0, noReply = 0, crowded = 0;
                var reasons = new List<string>();
                for (var k = 0; k < now.Count; k++)
                {
                    var i = now[k];
                    var outcome = asked[k].Outcome;
                    var result = ClassifyAsk(outcome);
                    if (result == JevAskResult.Answered)
                    {
                        outcomes[i] = outcome;
                        answered++;
                        memory.Heard(memoryId, digests[i], outcome, outcome.Answers.Answers.Count >= questionsOf(asks[i]).Count, revision);
                        continue;
                    }
                    var crowdedAsk = IsCrowdedOutcome(outcome);
                    if (crowdedAsk) crowded++;
                    else if (result == JevAskResult.NoReply) noReply++;
                    reasons.Add(StopReasonOf(outcome));
                    // A crowded relay says nothing about this object: it is not marked tried, so the next run asks it again.
                    if (settled && !crowdedAsk) memory.Tried(memoryId, digests[i], revision);
                }

                var remaining = fresh.Count - now.Count + crowded;
                var answeredAll = outcomes.Count(o => o != null);
                var progress = new JevFamilyProgress
                {
                    Total = asks.Count, Answered = answeredAll, Remaining = remaining, Unusable = asks.Count - answeredAll - remaining,
                    AskedThisRun = now.Count,
                };
                if (blocked) { progress.Stopped = remaining > 0; }
                else if (now.Count > 0 && settled && answered == 0 && crowded == now.Count)
                {
                    // Every ask waited its turn and none was heard: that is not an empty run, so it is not counted toward blocking
                    // the family, and the family continues (Remaining > 0, not Stopped) with the next run.
                }
                else if (now.Count > 0 && settled)
                {
                    var runs = answered > 0 ? 0 : memory.EmptyRuns(memoryId, family, revision) + 1;
                    // Nothing came back at all: the service is not answering, so the next batch is not started.
                    if (answered == 0 && noReply > 0) runs = MaxEmptyRunsInRow;
                    memory.SetEmptyRuns(memoryId, family, revision, runs);
                    if (remaining > 0 && runs >= MaxEmptyRunsInRow)
                    {
                        progress.Stopped = true;
                        progress.StopReason = BatchStopReason(reasons);
                    }
                }
                run.Progress[family] = progress;
                return Enumerable.Range(0, asks.Count).Select(i => (Ask: asks[i], Outcome: outcomes[i])).ToArray();
            }

            Task<(MeasuresFamily.Ask Ask, DecisionOutcome Outcome)[]> AskSomeMeasures(IReadOnlyList<MeasuresFamily.Ask> some) =>
                BatchedFor(DecisionRegistry.MeasureFolderAndFormat)
                    ? AskBatches(measuresPoint, some, MeasuresFamily.Batches(new MeasuresFamily.Prepared { Asks = some.ToList(), Shares = prepared.Measures.Shares }))
                    : Task.WhenAll(some.Select(async ask =>
                        (Ask: ask, Outcome: await AskOne(measuresPoint.Name, ask.Snapshot, ask.Questions).ConfigureAwait(false))));
            Task<(ColumnFormatFamily.Ask Ask, DecisionOutcome Outcome)[]> AskSomeColumnFormats(IReadOnlyList<ColumnFormatFamily.Ask> some) =>
                BatchedFor(DecisionRegistry.ColumnFormat)
                    ? AskBatches(columnFormatPoint, some, ColumnFormatFamily.Batches(new ColumnFormatFamily.Prepared { Asks = some.ToList(), TableColumns = prepared.ColumnFormat.TableColumns }))
                    : Task.WhenAll(some.Select(async ask =>
                        (Ask: ask, Outcome: await AskOne(columnFormatPoint.Name, ask.Snapshot, ask.Questions).ConfigureAwait(false))));
            var measureAsks = AskEveryAsync(MeasuresFamily.Name, measuresPoint, prepared.Measures.Asks, target.UncappedAsks ? int.MaxValue : MeasuresCapForTest ?? MeasuresFamily.MaxMeasuresPerRun,
                a => a.Snapshot, a => a.Questions, AskSomeMeasures);
            var starterAsks = Task.WhenAll(prepared.Starter.Asks.Select(async ask =>
                (Ask: ask, Outcome: await AskOne(starterPoint.Name, ask.Snapshot, ask.Questions).ConfigureAwait(false))));
            var columnFormatAsks = AskEveryAsync(ColumnFormatFamily.Name, columnFormatPoint, prepared.ColumnFormat.Asks, target.UncappedAsks ? int.MaxValue : ColumnFormatCapForTest ?? ColumnFormatFamily.MaxColumnsPerRun,
                a => a.Snapshot, a => a.Questions, AskSomeColumnFormats);
            var relationshipAsks = Task.WhenAll(prepared.Relationship.Asks.Select(async ask =>
                (Ask: ask, Outcome: await AskOne(relationshipPoint.Name, ask.Snapshot, ask.Questions).ConfigureAwait(false))));
            // WHAT COPILOT SEES, IN BATCHES. Every object's answer that is already known (this session's cache, or kept on
            // this computer from before) is read first and sent nowhere; then at most the cap of NEW objects is asked, in
            // the family's order. The rest wait for the next run, which the next read starts, and the rows say so. Over
            // enough runs every object is asked once, which is what the replay scored.
            async Task<(AiSchemaFamily.Ask Ask, DecisionOutcome Outcome)[]> AiSchemaInBatchesAsync()
            {
                if (BatchedFor(DecisionRegistry.AiSchema)) return await AiSchemaByTableAsync().ConfigureAwait(false);
                string DigestOf(AiSchemaFamily.Ask ask) =>
                    new DecisionRequest(aiSchemaPoint.Name, aiSchemaPoint.SchemaVersion, ask.Snapshot, ask.Questions).Digest;
                var known = await Task.WhenAll(prepared.AiSchema.Asks.Select(async ask =>
                {
                    // Heard by an earlier batch for this same revision: kept, not looked up again.
                    if (carry != null && carry.TryGetValue(DigestOf(ask), out var carried)) return (Ask: ask, Outcome: carried);
                    try
                    {
                        return (Ask: ask, Outcome: await decisions.AskAsync(aiSchemaPoint.Name, ask.Snapshot, ask.Questions,
                            isPro: true, enabled: true, fence: fence, ct: ct, cachedOnly: true).ConfigureAwait(false));
                    }
                    catch { return (Ask: ask, Outcome: (DecisionOutcome)null); }
                })).ConfigureAwait(false);
                var cap = target.UncappedAsks ? int.MaxValue : Math.Max(1, AiSchemaCapForTest ?? AiSchemaFamily.MaxObjectsPerRun);
                var fresh = known.Where(k => k.Outcome != null && !k.Outcome.Asked
                                             && string.Equals(k.Outcome.SkippedReason, DecisionService.NotCachedReason, StringComparison.Ordinal))
                                 .Select(k => k.Ask).ToList();
                var now = new HashSet<AiSchemaFamily.Ask>(fresh.Take(cap));
                var asked = await Task.WhenAll(now.Select(async ask =>
                    (Ask: ask, Outcome: await AskOne(aiSchemaPoint.Name, ask.Snapshot, ask.Questions).ConfigureAwait(false)))).ConfigureAwait(false);
                var byAsk = asked.ToDictionary(a => a.Ask, a => a.Outcome);
                var later = fresh.Count - now.Count;
                var all = known.Select(k => byAsk.TryGetValue(k.Ask, out var o) ? (k.Ask, o) : (k.Ask, k.Outcome)).ToArray();
                foreach (var (ask, o) in all)
                    if (o != null && o.Asked && o.Answers.Decided) run.AiSchemaAnswers[DigestOf(ask)] = o;
                // Looked = the objects Jev has an answer for, counted, never the total less what is left to ask.
                var total = prepared.AiSchema.Asks.Count;
                var looked = all.Count(x => x.Item2 != null && x.Item2.Asked && x.Item2.Answers.Decided);
                // Only a batch that heard back carries on: a service that answered nothing is not asked again and again.
                var more = later > 0 && asked.Any(a => a.Outcome != null && a.Outcome.Asked && a.Outcome.Answers.Decided);
                // A run the session's budget stopped short is still partial to the person: every row says so, and says
                // the rest wait, because nothing more goes out in this session.
                // An ask the development client sent once and whose retry the budget could not fit is the same stop: Jev
                // was asked, and nothing more goes out in this session. It used to become the batch's stop REASON, so the
                // rows told a budget stop as a wait for Jev (ai.schema verify round 5, L1).
                var spent = asked.Any(a => a.Outcome != null
                                           && (!a.Outcome.Asked
                                               ? string.Equals(a.Outcome.SkippedReason, DecisionService.BudgetSpentReason, StringComparison.Ordinal)
                                               : !a.Outcome.Answers.Decided
                                                 && string.Equals(a.Outcome.Answers.NotDecidedReason, JevDecisionOracle.AskedThenStoppedReason,
                                                                  StringComparison.Ordinal)));
                // WHY THE BATCHES STOPPED, IN THE WORDS OF WHATEVER STOPPED THEM. The budget is only one of them, and
                // on the production route it is not even the common one: a licensed session asks through the relay,
                // and the relay refuses in its own words when the licence's month is spent, when advice is paused, or
                // when the vendor behind it failed. Each of those comes back asked and not decided, so before round 4
                // the run was neither "more" nor "spent", the progress went, and rows Jev answered read as the whole
                // answer while a table Jev never looked at had no row at all (verify round 4, R4-1). The reason is kept
                // even when nothing was answered at all, and a service's own refusal wins over an ask that heard
                // nothing (BatchStopReason).
                var stopReason = BatchStopReason(all.Select(x => StopReasonOf(x.Item2)));
                // THE STOP WINS OVER "MORE" (verify round 4, item 3). A batch that heard back AND had one ask refused
                // for the budget used to promise "The rest follow." and start one more run that could only be refused.
                // A spent budget sends nothing else in this session, so the rows say that and nothing more starts.
                if (spent && looked < total) run.AiSchemaProgress = (looked, total, false, null);
                else if (more) run.AiSchemaProgress = (looked, total, true, null);
                // The relay was crowded for the whole wait: nothing is stopped. The next read starts the next run, which asks what is left.
                else if (looked < total && JevDecisionOracle.IsCrowded(stopReason)) run.AiSchemaProgress = (looked, total, true, stopReason);
                else if (looked < total && stopReason != null) run.AiSchemaProgress = (looked, total, false, stopReason);
                return all;
            }
            // WHAT COPILOT SEES, ONE REQUEST PER TABLE. The same batches as above with a table's objects as the unit: the chunks are
            // cut over every prepared object first (so the same model always cuts the same chunks), a chunk already answered (this
            // session, an earlier batch of this revision, or kept on this computer) is read and sent nowhere, and then whole chunks
            // of NEW objects are asked until the cap on objects is reached. The stop reasons are read the way the per-object run reads
            // them: off what each asked request came back with, and what a whole object heard.
            async Task<(AiSchemaFamily.Ask Ask, DecisionOutcome Outcome)[]> AiSchemaByTableAsync()
            {
                var batches = AiSchemaFamily.Batches(prepared.AiSchema);
                string DigestOf(TableBatch<AiSchemaFamily.Ask> b) => b.RequestFor(aiSchemaPoint).Digest;
                var known = await Task.WhenAll(batches.Select(async b =>
                {
                    if (carry != null && carry.TryGetValue(DigestOf(b), out var carried)) return (Batch: b, Outcome: carried);
                    try
                    {
                        return (Batch: b, Outcome: await decisions.AskAsync(aiSchemaPoint.Name, b.Snapshot, b.Questions,
                            isPro: true, enabled: true, fence: fence, ct: ct, cachedOnly: true, perQuestion: true).ConfigureAwait(false));
                    }
                    catch { return (Batch: b, Outcome: (DecisionOutcome)null); }
                })).ConfigureAwait(false);
                var cap = target.UncappedAsks ? int.MaxValue : Math.Max(1, AiSchemaCapForTest ?? AiSchemaFamily.MaxObjectsPerRun);
                var fresh = known.Where(k => k.Outcome != null && !k.Outcome.Asked
                                             && string.Equals(k.Outcome.SkippedReason, DecisionService.NotCachedReason, StringComparison.Ordinal))
                                 .Select(k => k.Batch).ToList();
                var now = JevTableBatch.TakeWithinCap(fresh, cap);
                var asked = await Task.WhenAll(now.Select(async b =>
                    (Batch: b, Outcome: await AskOne(aiSchemaPoint.Name, b.Snapshot, b.Questions, perQuestion: true).ConfigureAwait(false)))).ConfigureAwait(false);
                var byBatch = asked.ToDictionary(a => a.Batch, a => a.Outcome);
                var laterObjects = fresh.Where(b => !now.Contains(b)).Sum(b => b.Asks.Count);
                var answered = known.Select(k => (k.Batch, Outcome: byBatch.TryGetValue(k.Batch, out var o) ? o : k.Outcome)).ToArray();
                // Only a WHOLE reply is carried to the next batch: a request with an answer refused is asked again.
                foreach (var (b, o) in answered)
                    if (o != null && o.Asked && o.Answers.Complete) run.AiSchemaAnswers[DigestOf(b)] = o;
                var all = Spread(prepared.AiSchema.Asks, answered);
                var total = prepared.AiSchema.Asks.Count;
                var looked = all.Count(x => x.Outcome != null && x.Outcome.Asked && x.Outcome.Answers.Decided);
                var more = laterObjects > 0 && asked.Any(a => a.Outcome != null && a.Outcome.Asked && a.Outcome.Answers.Decided);
                var spent = asked.Any(a => a.Outcome != null
                                           && (!a.Outcome.Asked
                                               ? string.Equals(a.Outcome.SkippedReason, DecisionService.BudgetSpentReason, StringComparison.Ordinal)
                                               : !a.Outcome.Answers.Decided
                                                 && string.Equals(a.Outcome.Answers.NotDecidedReason, JevDecisionOracle.AskedThenStoppedReason,
                                                                  StringComparison.Ordinal)));
                var stopReason = BatchStopReason(all.Select(x => StopReasonOf(x.Outcome)));
                if (spent && looked < total) run.AiSchemaProgress = (looked, total, false, null);
                else if (more) run.AiSchemaProgress = (looked, total, true, null);
                // The relay was crowded for the whole wait: nothing is stopped. The next read starts the next run, which asks what is left.
                else if (looked < total && JevDecisionOracle.IsCrowded(stopReason)) run.AiSchemaProgress = (looked, total, true, stopReason);
                else if (looked < total && stopReason != null) run.AiSchemaProgress = (looked, total, false, stopReason);
                return all;
            }
            var aiSchemaAsks = AiSchemaInBatchesAsync();
            // THE COLUMN FAMILIES, IN BATCHES. A column whose evidence was already answered this session is read back and
            // sent nowhere; then at most the cap of NEW columns is asked, in the family's order. The rest wait for the
            // next run, which the next read starts, and the rows say how far Jev has got. Over enough runs every column
            // is asked once, as the replay asked every one. A column left waiting keeps whatever it had: its lines and
            // its decision (Unanswered, and RememberJevDecisions).
            async Task<(TAsk Ask, DecisionOutcome Outcome)[]> ColumnBatchAsync<TAsk>(string family, DecisionPoint point, IReadOnlyList<TAsk> asks,
                Func<TAsk, IReadOnlyDictionary<string, object>> snapshotOf, Func<TAsk, IReadOnlyList<DecisionQuestion>> questionsOf, int cap)
            {
                string DigestOf(TAsk a) => new DecisionRequest(point.Name, point.SchemaVersion, snapshotOf(a), questionsOf(a)).Digest;
                var known = new Dictionary<int, DecisionOutcome>();
                var fresh = new List<int>();
                for (var i = 0; i < asks.Count; i++)
                {
                    if (target.KnownAnswer(DigestOf(asks[i])) is { } heard) known[i] = heard;
                    else fresh.Add(i);
                }
                var now = fresh.Take(Math.Max(1, cap)).ToList();
                var asked = await Task.WhenAll(now.Select(async i =>
                    (Index: i, Outcome: await AskOne(point.Name, snapshotOf(asks[i]), questionsOf(asks[i])).ConfigureAwait(false)))).ConfigureAwait(false);
                foreach (var (i, o) in asked)
                    if (o != null && o.Asked && o.Answers.Decided) { target.RememberAnswer(DigestOf(asks[i]), o); known[i] = o; }
                var later = fresh.Count - now.Count;
                // Only a batch that heard back carries on: a service that answered nothing is not asked again and again.
                if (later > 0 && asked.Any(a => a.Outcome != null && a.Outcome.Asked && a.Outcome.Answers.Decided))
                    run.ColumnProgress[family] = (asks.Count - later, asks.Count);
                return Enumerable.Range(0, asks.Count)
                    .Select(i => (Ask: asks[i], Outcome: known.TryGetValue(i, out var o) ? o : asked.FirstOrDefault(a => a.Index == i).Outcome))
                    .ToArray();
            }
            var columnCategoryAsks = ColumnBatchAsync(ColumnCategoryFamily.Name, columnCategoryPoint, prepared.ColumnCategory.Asks,
                a => a.Snapshot, a => a.Questions, target.UncappedAsks ? int.MaxValue : ColumnCategoryCapForTest ?? ColumnCategoryFamily.MaxColumnsPerRun);
            // The same batching by table for the base-measure family: the chunks are cut over every prepared column, a chunk answered
            // this session is read back and sent nowhere, and whole chunks of NEW columns are asked until the cap on columns is reached.
            async Task<(ColumnSummarizeFamily.Ask Ask, DecisionOutcome Outcome)[]> ColumnSummarizeByTableAsync(int cap)
            {
                var asks = prepared.ColumnSummarize.Asks;
                var point = columnSummarizePoint;
                var batches = ColumnSummarizeFamily.Batches(prepared.ColumnSummarize);
                string DigestOf(TableBatch<ColumnSummarizeFamily.Ask> b) => b.RequestFor(point).Digest;
                var known = new List<(TableBatch<ColumnSummarizeFamily.Ask> Batch, DecisionOutcome Outcome)>();
                var fresh = new List<TableBatch<ColumnSummarizeFamily.Ask>>();
                foreach (var b in batches)
                {
                    if (target.KnownAnswer(DigestOf(b)) is { } heard) known.Add((b, heard));
                    else fresh.Add(b);
                }
                var now = JevTableBatch.TakeWithinCap(fresh, cap);
                var asked = await Task.WhenAll(now.Select(async b =>
                    (Batch: b, Outcome: await AskOne(point.Name, b.Snapshot, b.Questions, perQuestion: true).ConfigureAwait(false)))).ConfigureAwait(false);
                // Only a WHOLE reply is remembered: a request with an answer refused is asked again on the next run.
                foreach (var (b, o) in asked)
                    if (o != null && o.Asked && o.Answers.Complete) target.RememberAnswer(DigestOf(b), o);
                var laterObjects = fresh.Where(b => !now.Contains(b)).Sum(b => b.Asks.Count);
                // Only a batch that heard back carries on: a service that answered nothing is not asked again and again.
                if (laterObjects > 0 && asked.Any(a => a.Outcome != null && a.Outcome.Asked && a.Outcome.Answers.Decided))
                    run.ColumnProgress[ColumnSummarizeFamily.Name] = (asks.Count - laterObjects, asks.Count);
                return Spread(asks, known.Concat(asked));
            }
            var columnSummarizeAsks = BatchedFor(DecisionRegistry.ColumnSummarize)
                ? ColumnSummarizeByTableAsync(target.UncappedAsks ? int.MaxValue : ColumnSummarizeCapForTest ?? ColumnSummarizeFamily.MaxColumnsPerRun)
                : ColumnBatchAsync(ColumnSummarizeFamily.Name, columnSummarizePoint, prepared.ColumnSummarize.Asks,
                    a => a.Snapshot, a => a.Questions, target.UncappedAsks ? int.MaxValue : ColumnSummarizeCapForTest ?? ColumnSummarizeFamily.MaxColumnsPerRun);
            var dateAsk = prepared.DateTable.Ask;
            var dateTableAsk = dateAsk == null ? Task.FromResult<DecisionOutcome>(null)
                : AskOne(dateTablePoint.Name, dateAsk.Snapshot, dateAsk.Questions);
            var measureOutcomes = await measureAsks.ConfigureAwait(false);
            var starterOutcomes = await starterAsks.ConfigureAwait(false);
            var columnFormatOutcomes = await columnFormatAsks.ConfigureAwait(false);
            var relationshipOutcomes = await relationshipAsks.ConfigureAwait(false);
            var columnCategoryOutcomes = await columnCategoryAsks.ConfigureAwait(false);
            var columnSummarizeOutcomes = await columnSummarizeAsks.ConfigureAwait(false);
            var aiSchemaOutcomes = await aiSchemaAsks.ConfigureAwait(false);
            var dateTableOutcome = await dateTableAsk.ConfigureAwait(false);

            if (ct.IsCancellationRequested || s.Revision != prepared.Revision) return null;

            // EVERY LIVE FAMILY WRITES A PROGRESS RECORD (measures and column format wrote theirs as they asked): how many of its
            // objects Jev has an answer for, and how many are still to be asked, from the same counts the families' own rows use.
            void Note(string family, int total, int answered, int remaining)
            {
                if (total > 0) run.Progress[family] = new JevFamilyProgress { Total = total, Answered = answered, Remaining = remaining, Unusable = total - answered - remaining };
            }
            int Heard<TAsk>(IEnumerable<(TAsk Ask, DecisionOutcome Outcome)> outcomes) =>
                outcomes.Count(o => o.Outcome != null && o.Outcome.Asked && o.Outcome.Answers.Decided);
            Note(RelationshipFamily.Name, prepared.Relationship.Asks.Count, Heard(relationshipOutcomes), 0);
            Note(StarterMeasuresFamily.Name, prepared.Starter.Asks.Count, Heard(starterOutcomes), 0);
            Note(AiSchemaFamily.Name, prepared.AiSchema.Asks.Count, Heard(aiSchemaOutcomes),
                 run.AiSchemaProgress is { RestFollow: true } ai ? Math.Max(0, ai.Total - ai.Looked) : 0);
            Note(ColumnCategoryFamily.Name, prepared.ColumnCategory.Asks.Count, Heard(columnCategoryOutcomes),
                 run.ColumnProgress.TryGetValue(ColumnCategoryFamily.Name, out var catProgress) ? Math.Max(0, catProgress.Total - catProgress.Looked) : 0);
            Note(ColumnSummarizeFamily.Name, prepared.ColumnSummarize.Asks.Count, Heard(columnSummarizeOutcomes),
                 run.ColumnProgress.TryGetValue(ColumnSummarizeFamily.Name, out var sumProgress) ? Math.Max(0, sumProgress.Total - sumProgress.Looked) : 0);
            if (prepared.DateTable.Ask != null)
                Note(DateTableFamily.Name, 1, dateTableOutcome != null && dateTableOutcome.Asked && dateTableOutcome.Answers.Decided ? 1 : 0, 0);

            var measuresRefused = new List<ModelPlanChangeRefusal>(prepared.Measures.Refused);
            if (measuresLive)
            {
                var templates = FormatTemplates();
                foreach (var (ask, outcome) in measureOutcomes)
                {
                    if (outcome == null || !outcome.Asked || !outcome.Answers.Decided)
                    {
                        run.Unanswered.Add(JevRun.UnansweredKey(measuresPoint.Name, ask.Ref));
                        continue;
                    }
                    run.Answered++;
                    MeasuresFamily.Map(ask, outcome.Answers, measuresPoint.ConfidenceBar, templates, run.Lines, measuresRefused);
                }
                run.Refused[MeasuresFamily.Name] = measuresRefused;
            }

            if (columnFormatLive)
            {
                var templates = FormatTemplates();
                var columnFormatRefused = new List<ModelPlanChangeRefusal>(prepared.ColumnFormat.Refused);
                foreach (var (ask, outcome) in columnFormatOutcomes)
                {
                    if (outcome == null || !outcome.Asked || !outcome.Answers.Decided)
                    {
                        run.Unanswered.Add(JevRun.UnansweredKey(columnFormatPoint.Name, ask.Ref));
                        continue;
                    }
                    run.Answered++;
                    ColumnFormatFamily.Map(ask, outcome.Answers, columnFormatPoint.ConfidenceBar, templates, run.Lines, columnFormatRefused);
                }
                run.Refused[ColumnFormatFamily.Name] = columnFormatRefused;
            }

            if (relationshipLive)
            {
                // Every column's pick first, then ONE resolution over all of them: which relationships can be
                // active is a question about the whole model, never about one column. Each pick is the Pro path, Jev
                // when sure and else the rule, per question (RelationshipFamily.ProPick), so every pick is kept; and
                // Resolve seats kept picks first in any case, so a dropped one could never take a kept one's place.
                var relationshipRefused = new List<ModelPlanChangeRefusal>(prepared.Relationship.Refused);
                var picks = new List<RelationshipFamily.Pick>();
                foreach (var (ask, outcome) in relationshipOutcomes)
                {
                    if (outcome == null || !outcome.Asked || !outcome.Answers.Decided)
                    {
                        run.Unanswered.Add(JevRun.UnansweredKey(relationshipPoint.Name, ask.Ref));
                        continue;
                    }
                    run.Answered++;
                    var pick = RelationshipFamily.ProPick(ask, outcome.Answers, relationshipPoint.ConfidenceBar);
                    if (pick != null) picks.Add(pick);
                }
                foreach (var pick in RelationshipFamily.Resolve(picks, prepared.Relationship.Existing, prepared.Relationship.ActivePairs, relationshipRefused))
                    run.Lines.Add(RelationshipFamily.JevLine(pick, relationshipPoint.ConfidenceBar));
                run.Refused[RelationshipFamily.Name] = relationshipRefused;
            }

            if (aiSchemaLive)
            {
                var aiSchemaRefused = new List<ModelPlanChangeRefusal>(prepared.AiSchema.Refused);
                var picks = new List<AiSchemaFamily.Pick>();
                foreach (var (ask, outcome) in aiSchemaOutcomes)
                {
                    var answer = outcome != null && outcome.Asked && outcome.Answers.Decided ? outcome.Answers.For(AiSchemaFamily.Question) : null;
                    if (answer == null)
                    {
                        run.Unanswered.Add(JevRun.UnansweredKey(aiSchemaPoint.Name, ask.Ref));
                        continue;
                    }
                    run.Answered++;
                    // Jev when sure, else the rule (AiSchemaFamily.ProPick).
                    picks.Add(AiSchemaFamily.ProPick(ask, answer.Choice, answer.Confidence, aiSchemaPoint.ConfidenceBar));
                }
                // The schema's own rule is checked over every answer at once: whether a field may be left out depends
                // on what happens to the measures that use it.
                AiSchemaFamily.Resolve(prepared.AiSchema, picks, aiSchemaPoint.ConfidenceBar, run.Lines, aiSchemaRefused);
                run.Refused[AiSchemaFamily.Name] = aiSchemaRefused;
            }

            if (columnCategoryLive)
            {
                var columnCategoryRefused = new List<ModelPlanChangeRefusal>(prepared.ColumnCategory.Refused);
                foreach (var (ask, outcome) in columnCategoryOutcomes)
                {
                    if (outcome == null || !outcome.Asked || !outcome.Answers.Decided)
                    {
                        run.Unanswered.Add(JevRun.UnansweredKey(columnCategoryPoint.Name, ask.Ref));
                        continue;
                    }
                    ColumnCategoryFamily.Map(ask, outcome.Answers, columnCategoryPoint.ConfidenceBar, run.Lines, columnCategoryRefused);
                    var picked = outcome.Answers.For(ColumnCategoryFamily.Question);
                    run.Heard.Add(JevRun.UnansweredKey(columnCategoryPoint.Name, ask.Ref));
                    if (ColumnCategoryFamily.Decides(picked?.Confidence ?? 0, columnCategoryPoint.ConfidenceBar, ask.Measured != null))
                        run.Decided.Add(JevRun.UnansweredKey(columnCategoryPoint.Name, ask.Ref));
                }
                run.Refused[ColumnCategoryFamily.Name] = columnCategoryRefused;
                if (prepared.ColumnCategory.Asks.Count > 0 && prepared.ColumnCategory.Asks.All(a => a.Measured == null))
                    run.NamesOnly.Add(ColumnCategoryFamily.Name);
            }

            if (columnSummarizeLive)
            {
                var columnSummarizeRefused = new List<ModelPlanChangeRefusal>(prepared.ColumnSummarize.Refused);
                var summarizeLines = new List<ChangeItem>();
                // Two heard columns whose picked measures would share a name are settled before any line is made.
                ColumnSummarizeFamily.SettleNames(columnSummarizeOutcomes
                    .Where(o => o.Item2 != null && o.Item2.Asked && o.Item2.Answers.Decided)
                    .Select(o => (o.Item1, ColumnSummarizeFamily.PickOf(o.Item1, o.Item2.Answers, columnSummarizePoint.ConfidenceBar))));
                foreach (var (ask, outcome) in columnSummarizeOutcomes)
                {
                    if (outcome == null || !outcome.Asked || !outcome.Answers.Decided)
                    {
                        // What was waiting stays: the column's own line, and any measure it would add.
                        run.Unanswered.Add(JevRun.UnansweredKey(columnSummarizePoint.Name, ask.Ref));
                        foreach (var r in ask.MeasureRefs) run.Unanswered.Add(JevRun.UnansweredKey(columnSummarizePoint.Name, r));
                        continue;
                    }
                    ColumnSummarizeFamily.Map(ask, outcome.Answers, columnSummarizePoint.ConfidenceBar, summarizeLines, columnSummarizeRefused);
                    var (pickedOption, pickSure) = ColumnSummarizeFamily.ChoiceOf(ask, outcome.Answers, columnSummarizePoint.ConfidenceBar);
                    if (pickedOption != null)
                        run.SummarizePicks.Add(new SummarizePick
                        {
                            ColumnRef = ask.Ref, TableName = ask.TableName, ColumnName = ask.ColumnName, Pick = pickedOption, Sure = pickSure,
                            MeasureName = pickedOption == StarterMeasureTemplates.None ? null
                                : ask.Existing.TryGetValue(pickedOption, out var existingName) ? existingName
                                : ask.NameOf.TryGetValue(pickedOption, out var newName) ? newName : null,
                        });
                    // Heard back: the family decided the column's summarize setting and its base measure, so the rules
                    // that write the same None, and the explicit-measure row, leave it to this family's row.
                    run.Heard.Add(JevRun.UnansweredKey(columnSummarizePoint.Name, ask.Ref));
                    run.Decided.Add(JevRun.UnansweredKey(columnSummarizePoint.Name, ask.Ref));
                }
                // The model's own DAX check on every new measure, on the dispatcher, before any line is offered.
                if (summarizeLines.Any(l => l.Kind == "create_measure"))
                    await s.ReadAsync(m => { StarterMeasuresFamily.Validate(m, summarizeLines, columnSummarizeRefused); return true; })
                           .ConfigureAwait(false);
                if (ct.IsCancellationRequested || s.Revision != prepared.Revision) return null;
                run.Lines.AddRange(summarizeLines);
                run.Refused[ColumnSummarizeFamily.Name] = columnSummarizeRefused;
                if (prepared.ColumnSummarize.Asks.Count > 0 && prepared.ColumnSummarize.Asks.All(a => a.Measured == null))
                    run.NamesOnly.Add(ColumnSummarizeFamily.Name);
            }

            if (dateTableLive)
            {
                var dateTableRefused = new List<ModelPlanChangeRefusal>(prepared.DateTable.Refused);
                if (dateAsk != null)
                {
                    if (dateTableOutcome == null || !dateTableOutcome.Asked || !dateTableOutcome.Answers.Decided)
                    {
                        // What was waiting stays: the line already on the plan is about one of these tables or the new one.
                        foreach (var c in dateAsk.Candidates) run.Unanswered.Add(JevRun.UnansweredKey(dateTablePoint.Name, c.TableRef));
                        run.Unanswered.Add(JevRun.UnansweredKey(dateTablePoint.Name, "table:" + dateAsk.GeneratedTable));
                    }
                    else
                    {
                        run.Answered++;
                        DateTableFamily.Map(dateAsk, dateTableOutcome.Answers, dateTablePoint.ConfidenceBar, run.Lines, dateTableRefused);
                    }
                }
                run.Refused[DateTableFamily.Name] = dateTableRefused;
            }

            if (starterLive)
            {
                var starterLines = new List<ChangeItem>();
                var starterRefused = new List<ModelPlanChangeRefusal>(prepared.Starter.Refused);
                foreach (var (ask, outcome) in starterOutcomes)
                {
                    if (outcome == null || !outcome.Asked || !outcome.Answers.Decided)
                    {
                        // What was waiting for this column stays: the measure it would add is the object.
                        foreach (var r in ask.MeasureRefs) run.Unanswered.Add(JevRun.UnansweredKey(starterPoint.Name, r));
                        continue;
                    }
                    run.Answered++;
                    StarterMeasuresFamily.Map(ask, outcome.Answers, starterPoint.ConfidenceBar, starterLines, starterRefused);
                }
                // The model's own DAX check, on the dispatcher, before any line is offered.
                if (starterLines.Count > 0)
                    await s.ReadAsync(m => { StarterMeasuresFamily.Validate(m, starterLines, starterRefused); return true; })
                           .ConfigureAwait(false);
                if (ct.IsCancellationRequested || s.Revision != prepared.Revision) return null;
                run.Lines.AddRange(starterLines);
                run.Refused[StarterMeasuresFamily.Name] = starterRefused;
            }
            run.StopReason = BatchStopReason(stops.OrderBy(x => x, StringComparer.Ordinal));
            return run;
        }

        // ---- one line per change ----------------------------------------------------------------------

        /// <summary>ONE LINE PER CHANGE. A Jev line and a rule's line (FMT-MEASURE proposing a measure's
        /// format, today) can both be waiting to write the same property of the same object. The plan shows,
        /// files, digests and applies exactly one of them: the Jev line while the family's point is live for
        /// this session, and the rule's line otherwise, which is the contract's "rules stay where Jev has not
        /// beaten them". The other line is left on the change plan untouched, so nothing a person or an
        /// assistant put there is thrown away; it simply is not a second proposal for the same write.
        ///
        /// Only waiting lines take part. A line that already ran is a receipt and is never hidden.</summary>
        internal static ChangeItem[] OneLinePerChange(ChangeItem[] lines, bool jevLive) =>
            OneLinePerChange(lines, _ => jevLive);

        /// <summary>The same, with liveness decided per Jev line: its own point must be live for it to win. One
        /// point switched off hands its writes back to the rules while the other families keep theirs.</summary>
        internal static ChangeItem[] OneLinePerChange(ChangeItem[] lines, Func<ChangeItem, bool> jevLineLive)
        {
            if (lines == null || lines.Length == 0) return lines ?? Array.Empty<ChangeItem>();
            static string Key(ChangeItem l) => (l.ObjectRef ?? "").ToLowerInvariant() + "\u001f" + (l.Target ?? "").ToLowerInvariant();
            static bool IsRuleLine(ChangeItem l) => !JevFamily.IsJevLine(l) && !string.IsNullOrWhiteSpace(l.RuleId);

            // A Jev format SUGGESTION takes no part: it is not a proposal the engine stands behind (the rule could not prove it),
            // so it must not hide the rule's line that puts the choice to the person's assistant. Both stay on the plan.
            var jevLines = lines.Where(l => JevFamily.IsJevLine(l) && IsPendingLine(l) && !JevFormatSuggestion.IsSuggestion(l.CheckText)).ToArray();
            var jev = new HashSet<string>(jevLines.Select(Key), StringComparer.Ordinal);
            if (jev.Count == 0) return lines;
            var rule = new HashSet<string>(lines.Where(l => IsRuleLine(l) && IsPendingLine(l)).Select(Key), StringComparer.Ordinal);
            jev.IntersectWith(rule);
            if (jev.Count == 0) return lines;
            // A write goes to Jev when a live Jev line proposes it; otherwise the rule keeps it.
            var jevWins = new HashSet<string>(jevLines.Where(l => jev.Contains(Key(l)) && jevLineLive(l)).Select(Key), StringComparer.Ordinal);

            return lines.Where(l =>
            {
                if (!IsPendingLine(l) || !jev.Contains(Key(l))) return true;
                return jevWins.Contains(Key(l)) ? !IsRuleLine(l) : !JevFamily.IsJevLine(l);
            }).ToArray();
        }
    }
}
