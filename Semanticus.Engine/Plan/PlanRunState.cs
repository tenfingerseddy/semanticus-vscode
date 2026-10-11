// Run the plan's fence and the strip's data (Plan at the heart, lane E5; contract 7.2 and 9).
//
// Both are ARITHMETIC OVER THE COMPOSED ROWS. The compose has already stamped every row's doer, state word and
// held flag (LocalEngine.Plan.cs, last), so the Ready set, its digest and the strip's counts are read off the
// rows the caller is shown and cannot disagree with them. Nothing here reads the model.
//
// THE READY DIGEST, AND WHY THE REVISION IS NOT ENOUGH (Sol 24 Sep). A hold, a doer switch, a skip and a
// proposal all change which rows Run the plan would take without moving the model revision
// (LocalEngine.Plan.cs recomposes per revision, but these are plan decisions, not model edits). So the fence
// is over the set itself: each Ready Semanticus row as its id, its change set digest (or "none") and its held
// flag, sorted by id, plus the revision. The engine recomputes it under the approve gate and refuses a run
// quoting any other value.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Semanticus.Engine
{
    internal static class PlanReadySet
    {
        /// <summary>A row Run the plan takes: Semanticus does it, the engine reads it Ready, and nobody paused it.
        /// Ready already excludes held, waiting, claimed and running rows (contract 5.3); held is checked again
        /// because a pause must never be overruled by a word computed somewhere else.</summary>
        public static bool IsReady(ModelPlanItem i) =>
            i != null
            && !string.IsNullOrWhiteSpace(i.Id)
            && string.Equals(i.Doer, PlanDoers.Semanticus, StringComparison.Ordinal)
            && string.Equals(i.StateWord, PlanStateWords.Ready, StringComparison.Ordinal)
            && !i.Held
            // A composer draft runs with its own Approve and run (run_draft), never inside Run the plan's batch: its
            // change group is one undo entry of its own (composer contract 3.2, lane L4).
            && !Composer.DraftRows.IsDraftRow(i.Id);

        /// <summary>The Ready Semanticus rows in plan order (the order the plan lists them), each id once.</summary>
        public static List<ModelPlanItem> Rows(IEnumerable<ModelPlanItem> items)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return (items ?? Array.Empty<ModelPlanItem>()).Where(IsReady).Where(i => seen.Add(i.Id)).ToList();
        }

        /// <summary>The Ready Jev rows whose worked-out lines predate the model's last change. The batch before them moved the model, so
        /// Jev looks at them again by itself; a press that took them now would only leave them out. The press waits for Jev instead
        /// (LocalEngine.PlanVerbs.cs, RunReadySetAsync), so one press runs every row. <paramref name="leftOut"/> holds the rows this press already left out.</summary>
        public static List<ModelPlanItem> JevRowsLookingAgain(IEnumerable<ModelPlanItem> items, ISet<string> leftOut) =>
            Rows(items).Where(r => (leftOut == null || !leftOut.Contains(r.Id))
                                   && JevFamily.IsJevItem(r.Id) && r.ChangeSet?.Stale == true).ToList();

        /// <summary>SHA-256, lowercase hex, over the Ready rows sorted by id and the revision.</summary>
        public static string Digest(IEnumerable<ModelPlanItem> items, long revision)
        {
            var text = new StringBuilder("revision:").Append(revision.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var i in Rows(items).OrderBy(i => i.Id, StringComparer.Ordinal))
                text.Append(i.Id).Append('|')
                    .Append(string.IsNullOrWhiteSpace(i.ChangeSet?.Digest) ? "none" : i.ChangeSet.Digest).Append('|')
                    .Append(i.Held ? "held" : "open").Append('\n');
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
            var hex = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return hex.ToString();
        }
    }

    /// <summary>What the strip needs beyond the rows themselves.</summary>
    internal sealed class PlanStripInputs
    {
        /// <summary>When each finished or applied row got there, by item id. A row with no known time is not
        /// counted as done today, because "today" is a claim about a time.</summary>
        public IReadOnlyDictionary<string, DateTimeOffset> DoneWhen = new Dictionary<string, DateTimeOffset>();

        /// <summary>The newest ledger entry, or null when there is none (or no ledger yet).</summary>
        public PlanStripLatest Latest;

        /// <summary>True only when Jev ran on this revision of a Pro session: it re-ranked the plan or its
        /// families' answer for this revision is in. Never true on free (Jev never runs there).</summary>
        public bool JevRan;

        /// <summary>The full BPA scan of this revision, for the Best practice letter. Null when it could not run.</summary>
        public Semanticus.Analysis.BpaScorecard Bpa;

        /// <summary>The engine machine's clock now, which is the person's own machine (coordinator, 24 Sep).</summary>
        public DateTimeOffset Now = DateTimeOffset.Now;
    }

    internal static class PlanStripBuilder
    {
        /// <summary>The three tracks, in the contract's order.</summary>
        public static readonly string[] TrackOrder = { PlanDoers.Semanticus, PlanDoers.You, PlanDoers.Assistant };

        /// <summary>Whether a moment falls on the same local calendar day as now, midnight to midnight.</summary>
        public static bool IsToday(DateTimeOffset when, DateTimeOffset now) =>
            when.ToLocalTime().Date == now.ToLocalTime().Date;

        public static PlanStrip Build(IReadOnlyList<ModelPlanItem> items, ModelPlanWorth worth, PlanStripInputs x)
        {
            x ??= new PlanStripInputs();
            var tracks = TrackOrder.ToDictionary(d => d, d => new PlanStripTrack { Doer = d }, StringComparer.Ordinal);
            var counted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in items ?? Array.Empty<ModelPlanItem>())
            {
                if (i == null || !tracks.TryGetValue(i.Doer ?? PlanDoers.Semanticus, out var t)) continue;
                // One row, one segment: a row listed twice (a done record beside a row that came back) is one.
                if (i.Id != null && !counted.Add(i.Id)) continue;
                switch (i.StateWord)
                {
                    case PlanStateWords.Done:
                    case PlanStateWords.Checking:
                        if (i.Id != null && x.DoneWhen.TryGetValue(i.Id, out var when) && IsToday(when, x.Now)) t.Done++;
                        break;
                    case PlanStateWords.OnIt: t.OnIt++; break;
                    case PlanStateWords.Ready: t.Ready++; break;
                    case PlanStateWords.NeedsYou: t.NeedsYou++; break;
                    // The dashed segments: paused rows plus rows that wait for another (mockup legend).
                    case PlanStateWords.Paused:
                    case PlanStateWords.Waiting: t.Waiting++; break;
                }
            }

            return new PlanStrip
            {
                GradeNow = worth?.From,
                GradeAfter = worth?.To,
                BestPractice = x.Bpa?.BestPractice,
                BestPracticeWhy = x.Bpa?.BestPracticeWhy,
                Tracks = TrackOrder.Select(d => tracks[d]).ToArray(),
                Latest = x.Latest,
                Updated = new PlanStripUpdated
                {
                    // After the person's last change: the newest ledger entry's time when there is one, and the
                    // moment this plan was worked out when there is not.
                    When = x.Latest?.When ?? x.Now,
                    By = x.JevRan ? PlanStripUpdatedBy.Jev : PlanStripUpdatedBy.Semanticus,
                },
            };
        }
    }

    /// <summary>Who brought the plan up to date after the last change: "jev" only when Jev ran on Pro, otherwise
    /// "semanticus" (the rules). The page reads "Jev updated this after your change" or "Updated after your last
    /// change" from it.</summary>
    public static class PlanStripUpdatedBy
    {
        public const string Jev = "jev";
        public const string Semanticus = "semanticus";
    }
}
