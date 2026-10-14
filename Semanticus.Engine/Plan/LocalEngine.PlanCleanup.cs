// The Plan's cleanup row on the engine: the sweep it lists from, where each column starts, and Jev's one voice.
//
// THE ROW'S JOB (Kane, 30 Sep 2026): a lean model. It lists the columns nothing in the model uses, as a SUGGESTION the
// person reviews. It is not a Copilot check and it is fine on a model built from scratch. The person unticks any line or sets
// the whole row aside, and a column may still be wanted for a report that does not exist yet, which only they can weigh.
//
// TWO VIEWS OF ONE SWEEP, so the row and its Work it out can never disagree about what a column is:
//   Unused  what the ROW shows (its title, its diff, its count): the columns that START TICKED, plus every item the sweep
//           did not call safe. Compose reads this and never waits for Jev.
//   Listed  what the LIST holds: EVERY safe column, ticked or not, each with where it starts and one plain reason. Work it
//           out reads this, so nothing is hidden and a column the rule would not suggest is still a line, unticked.
// Both come from the same pick per column (CleanupGuard.Judge), so the row's "N start ticked" is exactly the ticked set the
// list starts from, before Jev.
//
// MEASURES ARE NOT ON THIS LIST. A measure's removal files on its proposer's row (ItemIdForLine), and Keep all on the cleanup
// would take a measure along. The row says how many unused measures it left off, so they are not hidden either.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Lineage;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>The unused sweep as the cleanup row uses it. See the file comment.</summary>
        internal sealed class CleanupSweep
        {
            public UnusedResult Unused;
            public UnusedResult Listed;
            /// <summary>Where every safe column starts, by ref. Mutable: Jev may untick a ticked one (Work it out only).</summary>
            public Dictionary<string, CleanupGuard.Pick> Picks = new Dictionary<string, CleanupGuard.Pick>(StringComparer.OrdinalIgnoreCase);
            /// <summary>The safe columns the sweep called unused, in sweep order.</summary>
            public UnusedItem[] Columns = Array.Empty<UnusedItem>();
            /// <summary>The unused measures the list leaves off.</summary>
            public UnusedItem[] Measures = Array.Empty<UnusedItem>();
            /// <summary>Facts for Jev about each column that starts ticked. Built on the dispatcher; never read by compose.</summary>
            public List<CleanupSuggest.Candidate> Candidates = new List<CleanupSuggest.Candidate>();
            public int ReportsRead, ReportsUnreadable;
            public int Unticked => Columns.Count(c => Picks.TryGetValue(c.Ref ?? "", out var p) && !p.Ticked);
        }

        /// <summary>The sweep the Plan's cleanup row lists from, and the ONLY one: the row and its Work it out both read this.
        /// It is report-aware: what a read report shows is not unused, and the row says how many reports were read. Reading one
        /// report proves nothing about the others, so the row is optional either way. Where each column starts is
        /// <see cref="CleanupGuard"/>'s, and it changes only what is ticked, never what is listed.</summary>
        private async Task<CleanupSweep> PlanUnusedAsync(Session s)
        {
            var unused = await UnusedObjectsCoreAsync(keepVisibleUntilReportsChecked: false).ConfigureAwait(false);
            // Only a report whose use of the model was actually read counts as read. A chosen report that was checked and
            // could not be read (a .pbix) has evidence too, and the sweep ignores it (AnalyzeReports: read = 0 means no
            // report use and no demotion), so counting it would tell a person a report vouched for these (verify, 29 Sep, D2).
            var coverage = CheckedCoverageFor(s);
            var sweep = new CleanupSweep
            {
                ReportsRead = coverage.UsageRead,
                ReportsUnreadable = coverage.Read - coverage.UsageRead,
            };
            if (unused == null) return sweep;

            // A VISIBLE ATTRIBUTE IS LISTED like any other unused column (the later ruling, Kane 30 Sep: this row is a suggestion list
            // the person reviews, and every column the sweep calls unused is a line). The sweep keeps that verdict for itself
            // (unused_objects and remove_safe_objects never sweep such a column away on their own, dogfood round 2), and
            // RemovalRecheck lets an approved plan line remove one, so here it is read as unused and CleanupGuard decides where it starts.
            var items = (unused.Items ?? Array.Empty<UnusedItem>()).Select(AsListed).ToArray();
            bool Safe(UnusedItem i) => string.Equals(i.Verdict, "safe", StringComparison.Ordinal);
            bool IsMeasure(UnusedItem i) => string.Equals(i.Kind, "measure", StringComparison.OrdinalIgnoreCase);
            sweep.Measures = items.Where(i => Safe(i) && IsMeasure(i)).ToArray();
            sweep.Columns = items.Where(i => Safe(i) && !IsMeasure(i)).ToArray();

            var read = await s.ReadAsync(m =>
            {
                var guard = new CleanupGuard(s, m);
                var picks = new Dictionary<string, CleanupGuard.Pick>(StringComparer.OrdinalIgnoreCase);
                var candidates = new List<CleanupSuggest.Candidate>();
                foreach (var i in sweep.Columns)
                {
                    var obj = ObjectRefs.Resolve(m, i.Ref);
                    var pick = guard.Judge(obj);
                    picks[i.Ref ?? ""] = pick;
                    if (pick.Ticked && obj is Column c) candidates.Add(CleanupSuggest.CandidateOf(i.Ref, c, guard.ShapeOf(c)));
                }
                return (picks, candidates);
            }).ConfigureAwait(false);
            sweep.Picks = read.picks;
            sweep.Candidates = read.candidates;

            // What a ticked line is: a safe column the rule starts ticked. Everything the sweep did not call safe stays in both
            // views (the caution counts and the caveat read it), and the row's counts read only the safe ones.
            var tickedRefs = new HashSet<string>(sweep.Columns.Where(c => sweep.Picks[c.Ref ?? ""].Ticked).Select(c => c.Ref ?? ""), StringComparer.OrdinalIgnoreCase);
            var columnRefs = new HashSet<string>(sweep.Columns.Select(c => c.Ref ?? ""), StringComparer.OrdinalIgnoreCase);

            UnusedResult View(Func<UnusedItem, bool> keep)
            {
                var kept = items.Where(keep).ToArray();
                return new UnusedResult
                {
                    Items = kept,
                    SafeCount = kept.Count(Safe),
                    UsedByUnusedOnlyCount = unused.UsedByUnusedOnlyCount,
                    CautionCount = unused.CautionCount,
                    Caveat = unused.Caveat,
                };
            }
            sweep.Unused = View(i => !Safe(i) || tickedRefs.Contains(i.Ref ?? ""));
            sweep.Listed = View(i => !Safe(i) || columnRefs.Contains(i.Ref ?? ""));
            return sweep;
        }

        private static UnusedItem AsListed(UnusedItem i) =>
            !string.Equals(i.Verdict, "visibleAttribute", StringComparison.Ordinal) ? i : new UnusedItem
            {
                Ref = i.Ref, Name = i.Name, Kind = i.Kind, Table = i.Table, IsHidden = i.IsHidden, Verdict = "safe",
                RefCount = i.RefCount, BlockedBy = i.BlockedBy, Reason = i.Reason,
            };

        /// <summary>A test's own wait for Jev on Work it out, in place of <see cref="CleanupSuggest.Wait"/>. Null on every real path.</summary>
        internal TimeSpan? CleanupJevWaitForTest;

        /// <summary>Pro only: ask Jev which of the columns that start ticked to leave out of the suggestion. It can only untick.
        /// Returns how many it unticked. Free returns from the first line, before anything is built, read or counted. No service,
        /// no permission, the point switched off, a timeout or an unanswered call leave every tick as the rule set it; an answer that is not a sure "suggest" unticks the line.</summary>
        private async Task<int> AskCleanupJevAsync(Session s, CleanupSweep sweep)
        {
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement)) return 0;
            if (sweep == null || sweep.Candidates.Count == 0) return 0;
            var point = DecisionRegistry.Require(DecisionRegistry.CleanupSuggest);
            if (!DecisionPointEnabled(point.Name)) return 0;
            var decisions = Decisions;
            if (!decisions.Oracle.Available) return 0;

            var fence = new DecisionSessionFence(s.Id, s.Revision, () =>
            {
                var current = _sessions.Current;
                return (current == null ? null : current.Id, current == null ? -1L : current.Revision);
            });
            var wait = CleanupJevWaitForTest ?? CleanupSuggest.Wait;
            using var cts = new CancellationTokenSource(wait);
            var asks = CleanupSuggest.Requests(sweep.Candidates)
                .Select(async r =>
                {
                    try { return await decisions.AskAsync(point.Name, r.Snapshot, r.Questions, true, true, fence, cts.Token).ConfigureAwait(false); }
                    catch { return null; }
                }).ToArray();
            if (asks.Length == 0) return 0;

            // Bounded, whatever the oracle does with the token: a call that is not back by then is simply not counted.
            var all = Task.WhenAll(asks);
            await Task.WhenAny(all, Task.Delay(wait + TimeSpan.FromMilliseconds(250))).ConfigureAwait(false);
            var left = 0;
            foreach (var ask in asks.Where(t => t.IsCompletedSuccessfully))
            {
                var outcome = ask.Result;
                if (outcome == null || !outcome.Asked) continue;
                left += CleanupSuggest.Apply(sweep.Picks, outcome.Answers, point.ConfidenceBar);
            }
            return left;
        }
    }
}
