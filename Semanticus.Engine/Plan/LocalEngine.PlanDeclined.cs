// A waiver declines a CHANGE, not a rule id (m11/rule-clashes rounds 5 and 6).
//
// Two rules often ask for the same write: readiness VIS-FK and BPA HIDE_FOREIGN_KEYS both hide a foreign key,
// FMT-SUMMARIZE and NUMERIC_COLUMN_SUMMARIZE_BY both stop a key column summing, FMT-MEASURE and INTEGER_FORMATTING both
// format a measure. The plan keeps one line per (object, property) and stamps it with one rule id, so a waiver matched
// on rule ids only never reached the twin: a person who accepted "Hide foreign keys" on a key they show on purpose had
// it hidden anyway by the plan, by Run the plan, by apply_safe_fixes and by Fix all.
//
// What a stored acceptance declines is what its rule is ABOUT (Semanticus.Analysis/RuleAbout.cs, DeclinedChanges.cs):
// read from the stored acceptances and the rule definitions, never from which findings fire now, so it holds after the
// finding stops firing, and whatever the rule's fix kind is (a mapped fix, a Proposal, or none). One set, asked by every
// path that writes or queues a change. A line added by hand (add_plan_item) carries no rule id, is an explicit request,
// and is never declined.
//
// One plan line can carry several findings' asks (the AI-instructions line: too long, undefined codes, none; a rename:
// a clearer name, the table's own name). Each ask is its own decision, so a line is left out only when EVERY ask on it is
// declined; while one remains, the line stays and the declined ask's words leave its rationale.

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>One finding's ask on a plan line: the rule, the title it gives the line and the words it adds to the rationale.</summary>
    internal sealed class PlanAsk
    {
        public string RuleId { get; init; }
        /// <summary>The page the rule is on ("air" or "bpa"); null means the line's own (a merged ask can come from the other page).</summary>
        public string System { get; init; }
        public string Title { get; init; }
        public string Message { get; init; }
    }

    public sealed partial class LocalEngine
    {
        internal const string DeclinedTwinReason = DeclinedChanges.TwinReason;

        /// <summary>The changes every stored acceptance on the model declined. Read from the stored records and the rule
        /// definitions: no scan runs. The rules in force can be passed in; a missing list is read from the model.</summary>
        private static DeclinedChanges DeclinedChangesOf(Model m, List<BpaRule> rules = null) => DeclinedChanges.Read(m, rules);

        /// <summary>How a plan line is covered by an acceptance. Own: one of the line's own findings was accepted and the rest of its asks
        /// are covered with it. Other: every ask is covered by ANOTHER rule's acceptance (or by the value an accepted rule would write).</summary>
        internal enum Cover { None, Own, Other }

        /// <summary>The acceptances the model holds now, asked about a plan line: does an accepted finding cover the change this
        /// SEEDED line makes? Every ask on the line must be covered. The declined set is read once.</summary>
        internal sealed class SeedWaivers
        {
            private readonly Model _m;
            private readonly IReadOnlyList<WaiverRecord> _waivers;
            private readonly DeclinedChanges _declined;

            public SeedWaivers(Model m, List<BpaRule> rules = null)
            {
                _m = m;
                _waivers = WaiverStore.Load(m);
                _declined = DeclinedChangesOf(m, rules);
            }

            public DeclinedChanges Declined => _declined;

            /// <summary>Kept for the apply batch, which reads the set before its own writes; it is read at construction now.</summary>
            public void Prime() { }

            private Cover AskCover(string system, string ruleId, string objectRef, string property)
            {
                if (WaiverStore.Match(_waivers, system, ruleId, objectRef) != null) return Cover.Own;
                // A finding on the model whose write is on one of the objects of the rule's kinds (DATE-MARK: the finding is on the
                // model, the write is a table's data category): the acceptance of the model's finding is this rule's own.
                var about = system == "bpa" ? null : RuleAboutTable.ForReadiness(ruleId);
                if (about != null && about.FromModel && WaiverStore.Match(_waivers, system, ruleId, WaiverStore.ModelRef) != null) return Cover.Own;
                // BPA also honours Tabular Editor's own per-object ignore, which the analyzer tags as waived the same way.
                if (system == "bpa")
                {
                    try { if (ObjectRefs.Resolve(_m, objectRef) is ITabularObject o && WaiverStore.IsTeIgnored(o, ruleId)) return Cover.Own; }
                    catch { }
                }
                return _declined.DeclinesAsk(system, ruleId, objectRef, property) ? Cover.Other : Cover.None;
            }

            /// <summary>Is this ask on the line covered now.</summary>
            public bool AskCovered(ChangeItem it, PlanAsk ask) =>
                IsSeeded(it, out var system) && AskCover(ask.System ?? system, ask.RuleId, it.ObjectRef, it.Target) != Cover.None;

            public Cover Coverage(ChangeItem it)
            {
                if (!IsSeeded(it, out var system)) return Cover.None;
                var asks = it.Asks != null && it.Asks.Count > 0 ? it.Asks.ToList() : new List<PlanAsk> { new PlanAsk { RuleId = it.RuleId } };
                var covers = asks.Select(a => AskCover(a.System ?? system, a.RuleId, it.ObjectRef, it.Target)).ToList();
                // Own when the person accepted one of the line's own findings, even if the others are covered because they ask the same
                // change: the note then says "you accepted this finding" and not "under another rule" (round 7, L1 residue).
                if (covers.All(c => c != Cover.None)) return covers.Any(c => c == Cover.Own) ? Cover.Own : Cover.Other;
                // The value an accepted rule would write, when that rule's ask is its own alone (a rename to the table's own name).
                return it.After != null && _declined.Declines(it.ObjectRef, it.Target, it.After) ? Cover.Other : Cover.None;
            }

            public bool Covers(ChangeItem it) => Coverage(it) != Cover.None;

            /// <summary>Put the line's title and rationale as the asks that are still standing say them. A line that carries
            /// one ask, or whose asks are all covered, is left as it is. True when the line's words moved.</summary>
            public bool Recompose(ChangeItem it)
            {
                if (it.Asks == null || it.Asks.Count < 2) return false;
                var standing = it.Asks.Where(a => !AskCovered(it, a)).ToList();
                if (standing.Count == 0) return false;
                var rationale = string.Join("\n", standing.Select(a => a.Message).Where(x => !string.IsNullOrEmpty(x)).Distinct());
                var title = standing[0].Title;
                if (rationale == it.Rationale && title == it.Title) return false;
                it.Rationale = rationale;
                it.Title = title;
                return true;
            }
        }

        /// <summary>The note that says why a covered line is left out: the person accepted THIS line's finding, or another
        /// rule's acceptance covers the change. Worked out from the cause each time, so it stays right after undo and redo.</summary>
        private static string NoteFor(Cover cover) => cover == Cover.Own ? WaivedAfterPlannedNote : DeclinedChangeNote;

        /// <summary>A SEEDED line is one a check put there: source deterministic, bpa or ai, carrying the rule id it came from.
        /// A line added with add_plan_item carries no rule id; it is an explicit request and keeps applying.</summary>
        internal static bool IsSeeded(ChangeItem it, out string system)
        {
            system = null;
            if (it == null || string.IsNullOrWhiteSpace(it.RuleId)) return false;
            system = it.Source switch { "bpa" => "bpa", "deterministic" or "ai" => "air", _ => null };
            return system != null;
        }
    }
}
