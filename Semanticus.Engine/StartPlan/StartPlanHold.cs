// THE ONE PLACE a line made by a hand is kept when the object it is about goes out of the start plan (the wiring step, W6).
//
// WHY ONE PLACE. Words the assistant wrote, and a value the person pinned, kept or dropped, are the plan's most expensive content: someone made them. They
// were lost twice, by two different paths that each had their own way of removing lines (leaving a TABLE out held them; leaving a COLUMN out deleted the words on
// its base measure, and a verifier found it the second time). Closing one path at a time is how the class stays open, so every path that takes lines off the plan
// now goes through RemoveOrHold or Settle below, whether a table, a column, a measure, a link or a pick was excluded or replaced.
//
// WHAT HELD MEANS. The line stays on the plan, says why (StartLine.Held), is shown, is NOT built (the build and the scratch skip it, so the plan never promises
// a write the model cannot take), and comes back with its object: the moment the object is in the plan again the hold is released and the line builds. A line the
// LOOP made (a rule's, Jev's) is not hand-made: it is removed, because the loop proposes it again when its object returns.
//
// WHAT IS NEVER REMOVED. A base measure whose column or table was left out stays in the manifest (StructureManifest.IsBuilt says whether it builds): the person's
// own pick on it, and the name their words are about, return with the column. Nothing in this file or in an edit removes a hand-made line.

using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine.StartPlan
{
    internal static class StartPlanHold
    {
        /// <summary>The person's own line, or their assistant's.</summary>
        internal static bool IsHandMade(StartLine line) => line?.Item != null && StartPlanDecide.IsHandMade(line.DecidedBy);

        // ------------------------------------------------------------------ the one function every removing path calls

        /// <summary>Take these lines off the plan because their object went out, or was replaced. A hand-made line is HELD (kept, marked, shown, not built) and a
        /// knock-on says so; any other line is removed and a knock-on says that. <paramref name="because"/> is a plain clause, "the column Qty was left out by you".</summary>
        internal static void RemoveOrHold(StartPlanDoc doc, IEnumerable<StartLine> lines, string because, StartEditOutcome outcome)
        {
            foreach (var l in (lines ?? Enumerable.Empty<StartLine>()).ToList())
            {
                if (IsHandMade(l))
                {
                    l.Held = "Held, because " + because + ". It comes back if you put that back in.";
                    outcome?.KnockOns.Add(new StartKnockOn
                    {
                        Ref = l.Item.ObjectRef, Key = StartPlanDecide.LineKey(l), Action = "held",
                        Because = "Held, because " + because + ". " + (StartPlanDecide.IsAssistantWords(l) ? (l.DecidedBy == StartDecidedBy.Person ? "Your words" : "Your assistant's words") : "What you chose") + " come back if you put it back in.",
                    });
                    continue;
                }
                doc.Lines.Remove(l);
                outcome?.KnockOns.Add(new StartKnockOn { Ref = l.Item.ObjectRef, Key = StartPlanDecide.LineKey(l), Action = "removed", Because = "Removed, because " + because + "." });
            }
        }

        // ------------------------------------------------------------------ before the loop removes or replaces a base measure

        /// <summary>Hold the hand-made lines about base measures the loop is about to remove or replace (the first round takes the rule's starters away before it asks Jev; a new
        /// pick can rename a measure). <paramref name="before"/> is the (table, name) of every base measure BEFORE the change; call this after it. A line about a measure that
        /// is gone (or one of its time measures, named from it) is held, not left for <see cref="Settle"/>, which would find no object and let it go (Sol, join 8, K8). It is
        /// released by Settle or Refresh the moment the measure is back under the same name; if the replacement has another name it stays held and says why.</summary>
        internal static int HoldLinesOnGoneBases(StartPlanDoc doc, IEnumerable<(string Table, string Name)> before)
        {
            var now = new HashSet<string>((doc.Manifest?.BaseMeasures ?? new List<ManifestBaseMeasure>()).Select(b => StarterMeasuresFamily.MeasureRef(b.Table, b.Name)), StringComparer.OrdinalIgnoreCase);
            var gone = before.Select(b => StarterMeasuresFamily.MeasureRef(b.Table, b.Name)).Where(r => !now.Contains(r)).ToList();
            if (gone.Count == 0) return 0;
            var held = 0;
            foreach (var l in doc.Lines ?? new List<StartLine>())
            {
                if (!IsHandMade(l) || l.Held != null) continue;
                var r = l.Item.ObjectRef ?? "";
                if (!gone.Any(g => Same(g, r) || r.StartsWith(g + " ", StringComparison.OrdinalIgnoreCase))) continue;
                l.Held = MeasureBeingDecidedReason;
                held++;
            }
            return held;
        }

        internal const string MeasureBeingDecidedReason = "Held, because the total it is about is being decided again. It comes back if the measure does.";

        // ------------------------------------------------------------------ what a round does with a line nothing proposed again

        /// <summary>The start of the hold on a hand-made line the executor refused to write on the model the plan builds (a synonym on a column the plan hides,
        /// a format that is not provable once the batch's other lines are made). It is a HOLD for the same reasons as the rest: the person's words are kept and shown with
        /// the reason, and the build does not promise a write the model will not take (it would refuse the whole build for one line the person can still fix). Unlike a
        /// hold for a missing object it is not released by the object being there: each round tries the line again, so unhiding the column lets its synonyms land.</summary>
        internal const string CannotLandPrefix = "Held, because it cannot be made right now: ";

        internal static bool IsCannotLand(StartLine l) => l?.Held != null && l.Held.StartsWith(CannotLandPrefix, StringComparison.Ordinal);

        /// <summary>Let every line held as "cannot be made" try again (a round starts with this, so what was blocked is looked at afresh).</summary>
        internal static void ClearCannotLand(StartPlanDoc doc)
        {
            foreach (var l in doc.Lines ?? new List<StartLine>()) if (IsCannotLand(l)) l.Held = null;
        }

        internal enum Fate { Stands, Held, Goes }

        /// <summary>The fate of a line that stood and that the families proposed nothing new for, once its object is looked for in the scratch.
        ///  - The object is there: the line stands, and a hold it carried is released (it came back with its object).
        ///  - Hand-made and held already, or its object is out by the person's choice: it is HELD (marked now if it was not).
        ///  - Anything else is gone for another reason (a base measure the loop re-picked, a line the loop made): it goes.
        /// Mutates only <see cref="StartLine.Held"/>.</summary>
        internal static Fate Settle(StartPlanDoc doc, StartLine was, Func<string, bool> resolves, ISet<string> present)
        {
            var objectRef = was.Item.ObjectRef;
            var there = resolves == null || resolves(objectRef) || (present?.Contains(objectRef) == true);
            if (there) { if (!IsCannotLand(was)) was.Held = null; return Fate.Stands; }
            if (!IsHandMade(was)) return Fate.Goes;
            if (was.Held != null) return Fate.Held;
            if (IsOutByChoice(doc, objectRef) || IsOutByChoice(doc, StartPlanWords.Forward(doc, objectRef)))
            {
                was.Held = OutReason;
                return Fate.Held;
            }
            return Fate.Goes;
        }

        // ------------------------------------------------------------------ after an edit, without a model to ask

        internal const string OutReason = "Held, because the thing it is about is left out of the plan. It comes back if you put it back in.";

        /// <summary>After an edit (no scratch to ask): mark every hand-made line whose object the manifest says is out by choice, and release a hold whose object the
        /// manifest says is back. A plan Jev never decides (Free) has no round to do this, so an edit does it. A hold the manifest cannot judge (a measure a new pick replaced)
        /// is left as it is until a round, or the pick going back, settles it. Returns how many lines changed.</summary>
        internal static int Refresh(StartPlanDoc doc)
        {
            var changed = 0;
            foreach (var l in doc.Lines ?? new List<StartLine>())
            {
                if (!IsHandMade(l)) continue;
                var r = l.Item.ObjectRef;
                var out_ = IsOutByChoice(doc, r) || IsOutByChoice(doc, StartPlanWords.Forward(doc, r));
                if (out_ && l.Held == null) { l.Held = OutReason; changed++; }
                else if (!out_ && l.Held != null && PresentInManifest(doc, r)) { l.Held = null; changed++; }
            }
            return changed;
        }

        /// <summary>True when the manifest positively has the object: a table or column that is brought in, a base measure that builds (and is not none), a time measure
        /// the manifest derives, a measure a kept line makes, a link whose ends are in. Anything else the manifest cannot judge reads as there.</summary>
        internal static bool PresentInManifest(StartPlanDoc doc, string objectRef)
        {
            var m = doc?.Manifest;
            if (m == null || string.IsNullOrEmpty(objectRef)) return true;
            var colon = objectRef.IndexOf(':');
            if (colon < 0) return true;
            var kind = objectRef.Substring(0, colon);
            var body = objectRef.Substring(colon + 1);
            var slash = body.IndexOf('/');
            if (kind == "relationship")
            {
                var link = m.Links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.Identity) && Same(l.Identity, body));
                return link == null || (!link.Unresolved && !link.Dropped && !LinkEndOut(m, link.FromTable, link.FromColumns) && !LinkEndOut(m, link.ToTable, link.ToColumns));
            }
            var table = StartRefs.FindTable(m, slash < 0 ? body : body.Substring(0, slash));
            if (table == null) return true;
            if (!table.Include) return false;
            var rest = slash < 0 ? null : body.Substring(slash + 1);
            switch (kind)
            {
                case "column":
                    return table.Columns.Any(c => c.Include && Same(c.Name, rest));
                case "measure":
                {
                    var bm = m.BaseMeasures?.FirstOrDefault(b => Same(b.Table, table.Name) && Same(b.Name, rest));
                    if (bm != null) return m.IsBuilt(bm) && !string.Equals(bm.Pick, StarterMeasureTemplates.None, StringComparison.OrdinalIgnoreCase);
                    var derived = m.DeriveTimeIntelligence().BaseMeasures ?? Array.Empty<string>();
                    foreach (var b in m.BaseMeasures ?? new List<ManifestBaseMeasure>())
                        if (Same(b.Table, table.Name))
                            foreach (var v in StructureManifest.TimeIntelligenceVariants)
                                if (Same(b.Name + " " + v, rest)) return derived.Any(n => Same(n, b.Name)) && !(b.DroppedTimeKinds?.ContainsKey(v) ?? false);
                    return (doc.Lines ?? new List<StartLine>()).Any(l => l.Kept && l.Held == null && l.Item != null && l.Item.Kind == "create_measure" && Same(l.Item.ObjectRef, objectRef));
                }
                default:
                    return true;
            }
        }

        // ------------------------------------------------------------------ the build and the scratch skip what is held

        /// <summary>True when a hand-made line is about an object the model does not have (the plan's own spelling of it, before or after the plan's renames).
        /// It is kept on the plan and NOT applied. <paramref name="present"/> holds the refs the model has (see <see cref="PresentRefs"/>).</summary>
        internal static bool IsHeld(StartLine line, ISet<string> present) =>
            IsHandMade(line) && (IsCannotLand(line) || !present.Contains(StartPlanWords.IsModelRef(line.Item.ObjectRef) ? StartPlanWords.ModelRef : line.Item.ObjectRef ?? ""));

        /// <summary>The refs of the hand-made lines that the model has: the ref as stored, as the plan's renames leave it, or as it was BEFORE them. A line can be
        /// stored against either spelling: the assistant's words are written against the old name, and a line the loop made is made on the scratch, where the renames
        /// have already run, so it is stored against the NEW one. A model that is checked before the renames run (the build, the first scratch) has only the old name, and a
        /// line on the new one read as "not there": a person's kept format on a time measure the assistant had renamed was skipped, silently, with the plan still
        /// saying "Kept by you".</summary>
        internal static HashSet<string> PresentRefs(StartPlanDoc doc, Model model)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in doc?.Lines ?? new List<StartLine>())
            {
                if (!IsHandMade(l)) continue;
                var r = l.Item.ObjectRef ?? "";
                if (StartPlanWords.IsModelRef(r)) { present.Add(StartPlanWords.ModelRef); continue; }   // the model is always there
                if (Exists(model, r) || Exists(model, StartPlanWords.Forward(doc, r)) || Exists(model, StartPlanWords.Back(doc, r))) present.Add(r);
            }
            return present;
        }

        private static bool Exists(Model m, string r)
        {
            try { return ObjectRefs.Resolve(m, r) != null; } catch { return false; }
        }

        // ------------------------------------------------------------------ where an object is out by the person's choice

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the object is out of the plan because the person (or their assistant) left something out, judged from the manifest alone: a table
        /// left out and everything on it, a column left out, a base measure whose column or table is out or whose pick is none, a time measure whose base or
        /// date link is out, a link whose table or column is out. A thing that is gone for another reason is NOT out by choice.</summary>
        internal static bool IsOutByChoice(StartPlanDoc doc, string objectRef)
        {
            var m = doc?.Manifest;
            if (m == null || string.IsNullOrEmpty(objectRef)) return false;
            var colon = objectRef.IndexOf(':');
            if (colon < 0) return false;
            var kind = objectRef.Substring(0, colon);
            var body = objectRef.Substring(colon + 1);
            var slash = body.IndexOf('/');

            if (kind == "relationship")
            {
                var link = m.Links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.Identity) && Same(l.Identity, body));
                if (link == null) return false;
                return link.Dropped || LinkEndOut(m, link.FromTable, link.FromColumns) || LinkEndOut(m, link.ToTable, link.ToColumns);
            }

            var table = StartRefs.FindTable(m, slash < 0 ? body : body.Substring(0, slash));
            if (table == null) return false;
            if (!table.Include) return true;
            var rest = slash < 0 ? null : body.Substring(slash + 1);
            switch (kind)
            {
                case "column":
                {
                    var col = table.Columns?.FirstOrDefault(c => Same(c.Name, rest));
                    return col != null && !col.Include;
                }
                case "measure":
                {
                    var bm = m.BaseMeasures?.FirstOrDefault(b => Same(b.Table, table.Name) && Same(b.Name, rest));
                    if (bm != null) return !m.IsBuilt(bm) || string.Equals(bm.Pick, StarterMeasureTemplates.None, StringComparison.OrdinalIgnoreCase);
                    // A time measure ("<base> YTD") is out when its base is, when the base no longer gets them (its date link was left out), or when the person dropped it.
                    foreach (var b in m.BaseMeasures ?? new List<ManifestBaseMeasure>())
                    {
                        if (!Same(b.Table, table.Name)) continue;
                        foreach (var v in StructureManifest.TimeIntelligenceVariants)
                            if (Same(b.Name + " " + v, rest))
                                return !(m.DeriveTimeIntelligence().BaseMeasures ?? Array.Empty<string>()).Any(n => Same(n, b.Name)) || (b.DroppedTimeKinds?.ContainsKey(v) ?? false);
                    }
                    return false;
                }
                default:
                    return false;
            }
        }

        private static bool LinkEndOut(StructureManifest m, string tableName, string[] columns)
        {
            var t = StartRefs.FindTable(m, tableName);
            if (t == null) return false;
            if (!t.Include) return true;
            return (columns ?? Array.Empty<string>()).Any(c => t.Columns.Any(x => Same(x.Name, c) && !x.Include));
        }
    }
}
