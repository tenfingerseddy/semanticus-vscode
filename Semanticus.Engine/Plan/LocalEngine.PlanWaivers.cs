// A finding the person accepts AFTER the change plan was proposed (m11/rule-clashes round 4, V1).
//
// propose_plan seeds a pre-approved line from each finding (BuildSeeds) and already leaves out a finding that is
// waived at that moment. A waiver recorded later used to leave the line approved, and apply_plan carried it out: the
// declined linguistic schema was created, a column the person said is not the key was made the key. So, in one place:
//   - apply re-checks the waivers INSIDE the write (the single-writer batch in ApplyPlanCoreAsync), and a seeded line
//     whose finding is waived now is skipped with a plain note, however it is ticked;
//   - waive, unwaive, undo and redo re-mark the pending seeded lines, so the list stops showing a waived line ticked
//     and puts it back as it was once the waiver goes.
// A SEEDED line is one a check put there: source deterministic, bpa or ai, carrying the rule id it came from. A line
// added with add_plan_item carries no rule id; it is an explicit request and keeps applying.
// A waiver declines a CHANGE, not a rule id (rounds 5 and 6): a seeded line is covered when every ask on it is covered,
// by its own finding's acceptance or by another rule's acceptance about the same property of the same object
// (Plan/LocalEngine.PlanDeclined.cs). The note says which.
//
// A Model Plan approval (the guarded path) needs none of this: a waiver is a model write, so the guard's revision check
// refuses the approval before this check is reached.

using System.Collections.Generic;
using System.Threading.Tasks;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        internal const string WaivedAfterPlannedNote = "Left out because you accepted this finding after it was planned.";

        /// <summary>The note on a line seeded already left out: another rule's finding, accepted before the plan was made,
        /// asked for the same change on the same object.</summary>
        internal const string DeclinedChangeNote = "Left out because you accepted this change under another rule.";

        /// <summary>Apply's check, inside the write: leave out a seeded line whose change an accepted finding declined now.</summary>
        internal static bool LeaveOutWaivedSeed(SeedWaivers waivers, ChangeItem it)
        {
            var cover = waivers.Coverage(it);
            if (cover == Cover.None) return false;
            it.HeldFromStatus = it.Status;
            it.Status = "skipped";
            it.Note = NoteFor(cover);
            return true;
        }

        /// <summary>The status a left-out line goes back to when its waiver is gone. A plan read back without the held status
        /// (it is not on the wire) gets the status the line is seeded with.</summary>
        private static string RestoredStatus(ChangeItem it) =>
            it.HeldFromStatus
            ?? (RequiresAfter(it.Kind) && string.IsNullOrEmpty(it.After) ? "needs_content"
                : OptInKind(it.Kind, it.VerifyGroupBy) ? "proposed" : "approved");

        internal static bool IsLeftOut(ChangeItem it) =>
            it.Status == "skipped" && (it.Note == WaivedAfterPlannedNote || it.Note == DeclinedChangeNote);

        /// <summary>Re-mark the pending seeded lines against the acceptances the model holds now: a covered one reads as left
        /// out, with the note for its real cause; one this marking left out goes back to what it was once nothing covers it;
        /// and a line that carries several findings' asks says only the asks still standing. True when a line moved.</summary>
        private static bool MarkWaivedSeeds(Model m, IEnumerable<ChangeItem> items)
        {
            var waivers = new SeedWaivers(m);
            var moved = false;
            foreach (var it in items)
            {
                var cover = waivers.Coverage(it);
                var waived = cover != Cover.None;
                var pending = it.Status == "approved" || it.Status == "proposed" || it.Status == "needs_content";
                var restored = false;
                if (waived && pending)
                {
                    it.HeldFromStatus = it.Status;
                    it.Status = "skipped";
                    it.Note = NoteFor(cover);
                    moved = true;
                }
                else if (waived && IsLeftOut(it))
                {
                    var note = NoteFor(cover);   // the cause can change while the line is left out (another rule's acceptance, then this finding's own)
                    if (it.Note != note) { it.Note = note; moved = true; }
                }
                else if (!waived && IsLeftOut(it))
                {
                    it.Status = RestoredStatus(it);
                    it.HeldFromStatus = null;
                    it.Note = null;
                    moved = true;
                    restored = true;
                }
                // A line put back is worded again from the asks still standing: its words were frozen when it was left out, and
                // they may be the words of an ask the person still declines (round 7, F4).
                if (!waived && (pending || restored || IsLeftOut(it)) && waivers.Recompose(it)) moved = true;
            }
            return moved;
        }

        /// <summary>After a waiver is written or removed: re-mark the plan and tell the Studio when a line moved.</summary>
        private async Task SyncPlanWithWaiversAsync(SessionContext context, Session s)
        {
            await context.PlanGate.WaitAsync();
            try
            {
                var plan = context.Plans.Current;
                if (plan == null || !ReferenceEquals(_sessions.CurrentContext, context)) return;
                if (await s.ReadAsync(m => MarkWaivedSeeds(m, plan.Snapshot()))) PublishPlan(plan, s.Revision);
            }
            finally { context.PlanGate.Release(); }
        }
    }
}
