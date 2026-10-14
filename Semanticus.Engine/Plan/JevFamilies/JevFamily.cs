// What every Jev family on the Plan shares (jev-groundwork-contract.md section 2, T268).
//
// A FAMILY, IN ONE PARAGRAPH. A family is one decision point applied to every object of one kind. The engine
// enumerates the options per object, checks them, asks Jev (Pro only), and turns each choice that differs
// from what the model says now into a change line: the existing line type, with provenance {kind: decision,
// point, confidence}, plus a reason and a check that the ENGINE built. Jev picks from a fixed list and writes
// no text, so nothing a person reads on a Jev line was written by Jev. One Plan item per table per family,
// already proposed, owned by Jev.
//
// WHERE THE LINES LIVE. On the session's change plan, like every other proposal, so approve, keep, drop,
// cancel and the digest all work on them exactly as they work on an assistant's lines, with no second path.
// The family puts them there before the plan is composed (LocalEngine.JevFamilies.cs) and the plan builds
// its Jev items FROM those lines, so the lines and the items cannot disagree about what is proposed.
//
// WHAT THIS FILE HOLDS. The item ids, the three confidence words and where they are cut, and the one
// function that turns a table's pending Jev lines into a Plan item. Nothing here asks anything or reads a
// model.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine
{
    /// <summary>The model facts a Jev family and the plan executor read (jev-groundwork-contract.md section
    /// 1, ModelFacts). ONLY the two facts this slice needs, named the way the contract names them, so the
    /// facts lane can implement this over its snapshot without a second vocabulary.
    ///
    /// Facts.JevModelFactsAdapter implements it over one snapshot. Every reader treats a missing fact as NOT
    /// MEASURED, which is never the same as false: a sort-by nobody measured is refused as unmeasured, not as
    /// wrong.</summary>
    public interface IJevModelFacts
    {
        /// <summary>ColumnFacts.measured.oneToOneWith for one column: the refs of the columns on the same
        /// table whose distinct pairs equal each one's distinct values. Null when the column was not
        /// measured (no data, or the facts lane has not run).</summary>
        IReadOnlyList<string> OneToOneWith(string columnRef);

        /// <summary>ColumnFacts.measured.wholeNumbers for one column. Null when not measured.</summary>
        bool? WholeNumbers(string columnRef);
    }

    internal static class JevFamily
    {
        /// <summary>The Source every Jev line carries on the change plan. One constant, because the filing, the
        /// provenance and the sync all have to agree on it.</summary>
        internal const string Source = "jev";

        /// <summary>The id prefix of every Jev item. "jev.&lt;family&gt;.&lt;tableRef&gt;" is the contract's shape.</summary>
        internal const string ItemPrefix = "jev.";

        internal static string ItemId(string family, string tableRef) => ItemPrefix + family + "." + tableRef;

        internal static bool IsJevItem(string id) => id != null && id.StartsWith(ItemPrefix, StringComparison.Ordinal);

        /// <summary>The format a Jev format family (the measures' and the columns') fills in from the format library: the
        /// family's own format when the library holds it, otherwise the category's first static template. A percent is
        /// "0.0%", as the engine's own YoY % and MoM % are: the library's first percent template is "0%", which shows a 15.6%
        /// rate as 16%, one tick from the harm the format fill rule exists to stop (v-rules-v1i, section 9, decision 3).</summary>
        internal static string LibraryFormat(IReadOnlyList<FormatTemplateInfo> templates, string family, string category)
        {
            var statics = (templates ?? Array.Empty<FormatTemplateInfo>())
                .Where(t => string.Equals(t.Category, category, StringComparison.Ordinal)
                         && !string.Equals(t.Kind, "dynamic", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.FormatString)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();
            return family != null && FamilyFormat.TryGetValue(family, out var own) && statics.Contains(own, StringComparer.Ordinal)
                ? own : statics.FirstOrDefault();
        }

        private static readonly Dictionary<string, string> FamilyFormat = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["percent"] = "0.0%",
        };

        internal static bool IsJevLine(ChangeItem line) =>
            line != null && string.Equals(line.Source, Source, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(line.JevItem);

        /// <summary>Below the point's own bar and at or above this, a choice is "likely". Under it, "unsure".
        /// The bar itself is the point's (DecisionRegistry), so "sure" means exactly what the point already
        /// meant by confident enough to propose.</summary>
        internal const double LikelyFloor = 0.6;

        internal const string Sure = "sure";
        internal const string Likely = "likely";
        internal const string Unsure = "unsure";

        internal static string ConfidenceWord(double confidence, double bar) =>
            confidence >= bar ? Sure : confidence >= LikelyFloor ? Likely : Unsure;

        /// <summary>An unsure line arrives DROPPED, so it is applied only if a person keeps it. Sure and
        /// likely arrive kept. On the change plan "kept" is the line's own approval ("approved") and dropped
        /// is "rejected", which is still a waiting line and is shown, visibly dropped.</summary>
        internal static string StatusFor(string word) =>
            string.Equals(word, Unsure, StringComparison.Ordinal) ? "rejected" : "approved";

        /// <summary>What a line is ABOUT, for telling one proposal from another across two reads. Two lines with
        /// the same key propose the same write, so a person's keep or drop on the first carries to the second.</summary>
        internal static string KeyOf(ChangeItem line) =>
            (line.JevItem ?? "") + "\u001f" + (line.Kind ?? "") + "\u001f" + (line.ObjectRef ?? "")
            + "\u001f" + (line.Target ?? "") + "\u001f" + (line.After ?? "");

        /// <summary>What a line is about WITHOUT its value: the same proposal whatever text it carries, so a value somebody wrote over
        /// a line can be matched to the line the family proposes again (<see cref="KeyOf"/> includes the value).</summary>
        internal static string ShapeOf(ChangeItem line) =>
            (line.JevItem ?? "") + "\u001f" + (line.Kind ?? "") + "\u001f" + (line.ObjectRef ?? "") + "\u001f" + (line.Target ?? "");

        /// <summary>What a held line says about itself, so its words stay on the plan and nobody reads them as a suggestion of the family's.</summary>
        internal const string HeldReason = "Your words are kept here. Semanticus no longer suggests this change, so it waits for you to tick it. When you run it, it is checked against the model as it is then.";

        /// <summary>What a line says when a new plan (propose_plan, propose_replace) or a redraw of its row came along while it carried somebody's words:
        /// the words are kept, and nothing about them was ticked for the new plan.</summary>
        internal const string HeldCarriedReason = "Your words are kept here from the plan before. They wait for you to tick them on this plan. When you run them, they are checked against the model as it is then.";

        internal const string HeldMissingReason = "Your words are kept here, but what they were written for is not in the model now, so they cannot be used until it is back. Semanticus no longer suggests this change.";
        internal const string HeldChangedReason = "Your words are kept here. What they were written over has changed since. Using them would write over something new, so Semanticus skips them unless that change is undone. Semanticus no longer suggests this change.";

        /// <summary>A line somebody wrote over is HELD when its family stops offering it: its text stays visible on the plan, it is no
        /// longer ticked for anyone (the family's reason for it is gone, and so is the line it needed), and applying it is checked against
        /// the object as it is then (the apply-time check on a changed value, ApplyOneItem's caller). A line the person dropped stays dropped.
        /// A line that is ALREADY held for this same reason is not taken off a tick: that tick is the person's, given to the held line as it
        /// stands, and every sync holds the line again, so taking it back each time would leave a held line nobody can apply while syncs run
        /// (v-sf-gate-2). A new reason, or <paramref name="keepTick"/> false (a new plan: nothing on it was ticked yet), still takes it back.
        /// True when the line changed.</summary>
        internal static bool HoldAuthored(ChangeItem line, string reason = null, bool keepTick = true)
        {
            var changed = false;
            reason ??= HeldReason;
            var ticked = string.Equals(line.Status, "approved", StringComparison.OrdinalIgnoreCase);
            var tickStands = ticked && keepTick && string.Equals(line.Reason, reason, StringComparison.Ordinal);
            if ((ticked && !tickStands) || string.Equals(line.Status, "needs_content", StringComparison.OrdinalIgnoreCase))
            { line.Status = "proposed"; changed = true; }
            if (line.Needs != null) { line.Needs = null; changed = true; }
            if (!string.Equals(line.Reason, reason, StringComparison.Ordinal)) { line.Reason = line.Rationale = reason; changed = true; }
            if (line.DecisionConfidence != null || line.DecisionWord != null) { line.DecisionConfidence = null; line.DecisionWord = null; changed = true; }
            return changed;
        }

        /// <summary>How a line names the line it needs (<see cref="ChangeItem.Needs"/>): its kind and its object, on the same item.</summary>
        internal static string NeedsKey(string kind, string objectRef) => (kind ?? "") + "\u001f" + (objectRef ?? "");

        /// <summary>The lines a keep or a drop on <paramref name="line"/> moves with it, among the waiting lines of its
        /// item. A drop takes every line that needs it (and what needs those). A keep undoes exactly that: it keeps what
        /// the line needs, up to the line nothing else is needed for, and everything that needs that line, so keeping
        /// any one line of a column's base measure brings back the measure with its format, description, hide and
        /// Excel lines; a person who wants the column in view drops the hide again. The line itself is not in the answer.</summary>
        internal static IEnumerable<ChangeItem> Follow(ChangeItem line, IEnumerable<ChangeItem> waiting, bool keep)
        {
            if (!IsJevLine(line)) return Array.Empty<ChangeItem>();
            var same = waiting.Where(l => IsJevLine(l) && string.Equals(l.JevItem, line.JevItem, StringComparison.Ordinal)).ToList();
            if (!same.Contains(line)) same.Add(line);
            IEnumerable<ChangeItem> Needed(ChangeItem at) =>
                same.Where(l => at.Needs != null && string.Equals(NeedsKey(l.Kind, l.ObjectRef), at.Needs, StringComparison.OrdinalIgnoreCase));
            IEnumerable<ChangeItem> Needing(ChangeItem at) =>
                same.Where(l => l.Needs != null && string.Equals(l.Needs, NeedsKey(at.Kind, at.ObjectRef), StringComparison.OrdinalIgnoreCase));
            HashSet<ChangeItem> Walk(IEnumerable<ChangeItem> start, Func<ChangeItem, IEnumerable<ChangeItem>> step)
            {
                var seen = new HashSet<ChangeItem>(ReferenceEqualityComparer.Instance);
                var next = new Queue<ChangeItem>(start);
                foreach (var f in next) seen.Add(f);
                while (next.Count > 0)
                    foreach (var l in step(next.Dequeue()))
                        if (seen.Add(l)) next.Enqueue(l);
                return seen;
            }
            var moved = keep
                ? Walk(Walk(new[] { line }, Needed).Where(l => !Needed(l).Any()), Needing).Union(Walk(new[] { line }, Needed))
                : Walk(new[] { line }, Needing);
            return moved.Where(l => !ReferenceEquals(l, line)).Distinct().ToList();
        }

        /// <summary>Drops every line whose needed line (same item) is dropped, until none is left: the state a keep or a
        /// drop through <see cref="Follow"/> always leaves, restored after lines are worked out again.</summary>
        internal static void DropWhereNeedDropped(IReadOnlyCollection<ChangeItem> lines)
        {
            bool moved;
            do
            {
                moved = false;
                foreach (var l in lines)
                {
                    if (l.Needs == null || string.Equals(l.Status, "rejected", StringComparison.OrdinalIgnoreCase)) continue;
                    var needed = lines.FirstOrDefault(o => !ReferenceEquals(o, l) && string.Equals(o.JevItem, l.JevItem, StringComparison.Ordinal)
                                                           && string.Equals(NeedsKey(o.Kind, o.ObjectRef), l.Needs, StringComparison.OrdinalIgnoreCase));
                    if (needed == null || !string.Equals(needed.Status, "rejected", StringComparison.OrdinalIgnoreCase)) continue;
                    l.Status = "rejected";
                    moved = true;
                }
            } while (moved);
        }

        /// <summary>What of a person's decision on <paramref name="prior"/> carries onto <paramref name="line"/>, the same
        /// write worked out again. A drop carries only when somebody made it (a person or their assistant): the engine's own drop
        /// of an unsure line is not a decision, so a line Jev now answers "sure" and the rule proves is not held down by it. A keep
        /// carries only when a PERSON made it, the line's Before is still the text they were looking at, AND the object still computes
        /// what it computed when they ticked (<paramref name="currentDigest"/> is the digest of the object the line names now), so a
        /// tick for "add #,0 over nothing" is never a tick for "add #,0 over the blank spaces typed since", and never a tick for a
        /// different measure that took the name. Anything else, the engine's own approval included, leaves the line with the status
        /// its family gave it.</summary>
        internal static void CarryDecision(ChangeItem prior, ChangeItem line, string currentDigest)
        {
            // EVERY OTHER FAMILY'S LINE keeps what it had (m11/colfair, ai.schema, relationships): a decision on a base measure, a category, a
            // summary, a join or a Copilot line holds until Jev answers again for that object, exactly as it did before the format fill rule.
            // Only a Jev FORMAT line (the fill rule's suggestions) carries by who ticked it and what the object computes.
            if (!LocalEngine.IsJevFormatLine(line))
            {
                line.Status = prior.Status;
                return;
            }
            if (string.Equals(prior.Status, "rejected", StringComparison.OrdinalIgnoreCase))
            {
                if (prior.DroppedBy == null) return;
                line.Status = "rejected";
                line.DroppedBy = prior.DroppedBy;
                return;
            }
            if (string.Equals(prior.Status, "approved", StringComparison.OrdinalIgnoreCase)
                && prior.KeptByPerson && string.Equals(prior.KeptAgainst ?? "", line.Before ?? "", StringComparison.Ordinal))
            {
                if (prior.KeptDigest == null || !string.Equals(prior.KeptDigest, currentDigest, StringComparison.Ordinal))
                {
                    // The object is not the one the person ticked for. The line stays the suggestion it arrived as, and says why.
                    if (JevFormatSuggestion.IsSuggestion(line.CheckText)) line.CheckText = JevFormatSuggestion.KeepVoidNote;
                    return;
                }
                line.Status = "approved";
                line.KeptByPerson = true;
                line.KeptAgainst = prior.KeptAgainst;
                line.KeptDigest = prior.KeptDigest;
            }
        }

        /// <summary>The words one family's items use. A new family adds one entry here.</summary>
        internal sealed class Words
        {
            public string Section;
            public Func<int, string, string> Title;

            /// <summary>For a family whose title depends on what its lines do (the date table: mark one, or add one; the base
            /// measure family: set columns not to add up, and add measures). Wins over <see cref="Title"/> when set.</summary>
            public Func<IReadOnlyList<ChangeItem>, string, string> TitleFromLines;
            public string ObjectOne;
            public string ObjectMany;
            public Func<int, string, string> Missing;
            public ModelPlanJump Jump;

            /// <summary>What Jev chose from, in words, when it is not "the names, types and settings on this computer".
            /// The two column points send no setting; they send names, types and facts worked out here.</summary>
            public string Basis;

            /// <summary>The who-chose sentence for a family whose rows also carry good-practice lines nobody picked
            /// (<see cref="PolicyRuleIds"/>). Given the Jev-chosen and the rule-chosen counts, those lines left out.</summary>
            public Func<int, int, string> WhoChose;
        }

        /// <summary>Rule ids of lines that apply good practice to every object a family hears back on (the base measure
        /// family's "stop adding up by itself"). Neither Jev's pick nor the rule standing in for an unsure Jev, so the
        /// who-chose counts leave them out.</summary>
        internal static readonly HashSet<string> PolicyRuleIds = new HashSet<string>(StringComparer.Ordinal) { ColumnSummarizeFamily.OffRuleId, ColumnSummarizeFamily.HideRuleId };

        internal static readonly Dictionary<string, Words> Families = new Dictionary<string, Words>(StringComparer.Ordinal)
        {
            [MeasuresFamily.Name] = new Words
            {
                Section = ModelPlanSections.Meaning,
                Title = (n, table) => "Add folders and formats to " + Count(n, "measure", "measures") + " in " + table,
                ObjectOne = "measure",
                ObjectMany = "measures",
                Missing = (n, table) => Count(n, "measure", "measures") + " in " + table
                    + (n == 1 ? " has" : " have") + " no folder or no number format yet. "
                    + (n == 1 ? "It shows" : "They show") + " as plain numbers in one long list.",
                Jump = new ModelPlanJump { Tool = "optimize", Label = "Pending changes" },
            },
            [StarterMeasuresFamily.Name] = new Words
            {
                Section = ModelPlanSections.Meaning,
                Title = (n, table) => "Add " + Count(n, "simple measure", "simple measures") + " in " + table,
                ObjectOne = "simple measure",
                ObjectMany = "simple measures",
                Missing = StarterMeasuresFamily.Missing,
                Jump = new ModelPlanJump { Tool = "optimize", Label = "Pending changes" },
            },
            [ColumnFormatFamily.Name] = new Words
            {
                Section = ModelPlanSections.Numbers,
                Title = (n, table) => "Format " + Count(n, "column", "columns") + " in " + table,
                ObjectOne = "column",
                ObjectMany = "columns",
                Missing = ColumnFormatFamily.Missing,
                Jump = new ModelPlanJump { Tool = "optimize", Label = "Pending changes" },
            },
            [RelationshipFamily.Name] = new Words
            {
                Section = ModelPlanSections.Structure,
                Title = (n, table) => "Add " + Count(n, "relationship", "relationships") + " in " + table,
                ObjectOne = "relationship",
                ObjectMany = "relationships",
                Missing = RelationshipFamily.Missing,
                Jump = new ModelPlanJump { Tool = "diagram", Label = "Diagram" },
            },
            [AiSchemaFamily.Name] = new Words
            {
                Section = ModelPlanSections.Trust,
                Title = (n, table) => "Choose which " + Count(n, "field", "fields") + " Copilot can see in " + table,
                ObjectOne = "thing",
                ObjectMany = "things",
                Missing = AiSchemaFamily.Missing,
                Jump = new ModelPlanJump { Tool = "optimize", Label = "Pending changes" },
            },
            [ColumnSummarizeFamily.Name] = new Words
            {
                Section = ModelPlanSections.Numbers,
                Title = (n, table) => "Add measures for the numbers in " + table,
                TitleFromLines = ColumnSummarizeFamily.Title,
                WhoChose = ColumnSummarizeFamily.WhoChose,
                ObjectOne = "column or measure",
                ObjectMany = "columns and measures",
                Missing = ColumnSummarizeFamily.Missing,
                Basis = "the names, types and facts worked out on this computer",
                Jump = new ModelPlanJump { Tool = "optimize", Label = "Pending changes" },
            },
            [ColumnCategoryFamily.Name] = new Words
            {
                Section = ModelPlanSections.Meaning,
                Title = (n, table) => "Mark what " + Count(n, "column", "columns") + " in " + table + " hold",
                ObjectOne = "column",
                ObjectMany = "columns",
                Missing = ColumnCategoryFamily.Missing,
                Basis = "the names, types and facts worked out on this computer",
                Jump = new ModelPlanJump { Tool = "optimize", Label = "Pending changes" },
            },
            [DateTableFamily.Name] = new Words
            {
                Section = ModelPlanSections.Structure,
                Title = (n, table) => "Mark " + table + " as the date table",
                TitleFromLines = DateTableFamily.Title,
                ObjectOne = "table",
                ObjectMany = "tables",
                Missing = DateTableFamily.Missing,
                Jump = new ModelPlanJump { Tool = "advmodels", Label = "Advanced modelling" },
            },
        };

        /// <summary>One Plan item per (family, table), built from the Jev lines that are still waiting on the
        /// change plan. The change set itself is hung on by the ordinary projection (StampChangeState), which
        /// files these same lines under this item through <see cref="ChangeItem.JevItem"/>.</summary>
        internal static IEnumerable<ModelPlanItem> Items(IEnumerable<ChangeItem> pending)
        {
            foreach (var group in (pending ?? Array.Empty<ChangeItem>())
                         .Where(IsJevLine)
                         .GroupBy(l => l.JevItem, StringComparer.Ordinal)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var parts = Parse(group.Key);
                if (parts.Family == null || !Families.TryGetValue(parts.Family, out var words)) continue;

                var lines = group.ToArray();
                var objects = lines.Select(l => l.ObjectRef).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var names = lines.Select(l => l.ObjectName).Where(n => !string.IsNullOrWhiteSpace(n))
                                 .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // Only a line Jev was unsure of arrives dropped (StatusFor). Any other dropped line was dropped by someone
                // reviewing the row, and saying Jev was unsure of it would be false: the base measure family never drops a
                // line itself, and a sure line a person drops is still a sure line.
                // A line that puts back something the person left out (ai.schema only, IsPutBack) is its own sentence and is
                // never counted as "unsure" or as dropped on review (ai.schema verify round 2, R2-4).
                var droppedLines = lines.Where(l => string.Equals(l.Status, "rejected", StringComparison.OrdinalIgnoreCase)).ToArray();
                var putBack = droppedLines.Count(IsPutBack);
                var dropped = droppedLines.Count(l => !IsPutBack(l) && string.Equals(l.DecisionWord, Unsure, StringComparison.Ordinal));
                var droppedOnReview = droppedLines.Length - putBack - dropped;
                // "+ " for a line that adds an object (a starter measure, a relationship), "~ " for one that changes a setting, the
                // same prefixes the item's change set lines use.
                var diff = lines.Select(l => (string.Equals(l.Kind, "create_measure", StringComparison.Ordinal)
                                            || string.Equals(l.Kind, "create_relationship", StringComparison.Ordinal)
                                            || string.Equals(l.Kind, "generate_date_table", StringComparison.Ordinal) ? "+ " : "~ ")
                                           + (l.Title == null ? l.ObjectName : (l.ObjectName + ": " + LowerFirst(l.Title))))
                                .Take(8).ToArray();

                // WHO CHOSE, SAID AS IT IS. A family's line is Jev's when it carries Jev's confidence, and the rule's when
                // Jev was not sure and the family's own rule decided it instead (the columns' and ai.schema's Pro path). A
                // row is not "from Jev" when Jev chose none of it, and a row both chose says how many each. The same split
                // the ai.schema lane made (m11/m4-aischema, JevFamily.Items), with each family's own words for what Jev saw.
                var policy = lines.Count(l => PolicyRuleIds.Contains(l.RuleId ?? ""));
                var ruled = lines.Count(l => !l.DecisionConfidence.HasValue && !string.IsNullOrWhiteSpace(l.RuleId)) - policy;
                var byJev = lines.Length - ruled - policy;
                const string Wrote = "Semanticus wrote every change and checked it before showing it.";
                var basis = words.Basis ?? "the names, types and settings on this computer";
                var why = new List<string> { words.Missing(objects, parts.Table) };
                if (words.WhoChose != null)
                    why.Add(words.WhoChose(Measures(lines, true), Measures(lines, false)) + Wrote);
                else if (ruled == 0)
                    why.Add("Jev chose each setting from a fixed list using " + basis + ". " + Wrote);
                else if (byJev == 0)
                    why.Add("Semanticus's own rules chose each setting, because Jev was not sure of any of them. " + Wrote);
                else
                    why.Add("Jev chose " + byJev.ToString("N0", CultureInfo.InvariantCulture) + " of these from a fixed list using "
                        + basis + ". Semanticus's own rules chose the other "
                        + ruled.ToString("N0", CultureInfo.InvariantCulture) + ", because Jev was not sure of "
                        + (ruled == 1 ? "it" : "them") + ". " + Wrote);
                if (dropped > 0)
                    why.Add(Count(dropped, "change is", "changes are") + " unticked because Jev was not sure. Tick "
                        + (dropped == 1 ? "it" : "them") + " to include " + (dropped == 1 ? "it" : "them") + ".");
                if (putBack > 0)
                    why.Add(Count(putBack, "change puts", "changes put") + " back something you left out, so "
                        + (putBack == 1 ? "it is" : "they are") + " unticked and "
                        + (putBack == 1 ? "waits" : "wait") + " for you. Tick " + (putBack == 1 ? "it" : "them") + " to include "
                        + (putBack == 1 ? "it" : "them") + ".");
                if (droppedOnReview > 0)
                    why.Add(Count(droppedOnReview, "change is", "changes are") + " unticked, so running the plan leaves "
                        + (droppedOnReview == 1 ? "it" : "them") + " out. Tick " + (droppedOnReview == 1 ? "it" : "them")
                        + " to include " + (droppedOnReview == 1 ? "it" : "them") + ".");
                var suggestions = lines.Select(l => (l.Status, l.CheckText)).ToArray();
                var suggestedWhy = JevFormatSuggestion.Why(suggestions);
                if (suggestedWhy != null) why.Add(suggestedWhy);

                yield return new ModelPlanItem
                {
                    Id = group.Key,
                    Section = words.Section,
                    Title = words.TitleFromLines != null ? words.TitleFromLines(lines, parts.Table) : words.Title(objects, parts.Table),
                    Sub = parts.Family == ColumnSummarizeFamily.Name
                        ? "These columns and measures change: " + Join(names, 4) : Join(names, 4),
                    Value = string.Empty,
                    // A row that holds only format suggestions is not Semanticus's: Run the plan never applies a suggestion. Checking that a
                    // format fits the number is something the assistant can do (it reads the DAX and asks in chat when it cannot tell), so the
                    // row is the assistant's, not the person's (Kane, 13 Oct 2026: the person is the last resort).
                    Who = JevFormatSuggestion.WaitsOnPerson(suggestions) ? ModelPlanOwners.Assistant : ModelPlanOwners.Jev,
                    // Where the row came from: Jev when Jev chose any of it, else what the rules found. StampDerived
                    // keeps a source set here rather than reading one off the id.
                    Source = byJev > 0 ? PlanSources.Jev : PlanSources.Found,
                    Status = ModelPlanStatuses.Proposed,
                    Why = why.ToArray(),
                    Diff = diff,
                    Touches = Count(objects, words.ObjectOne, words.ObjectMany) + " in " + parts.Table,
                    Tables = new[]
                    {
                        new ModelPlanItemTable
                        {
                            Name = parts.Table,
                            Count = objects,
                            What = Count(objects, words.ObjectOne, words.ObjectMany),
                            Diff = diff,
                        },
                    },
                    Jump = words.Jump,
                };
            }
        }

        /// <summary>How many objects a row's non-policy lines touch that Jev chose (true) or the rule chose (false).</summary>
        private static int Measures(IEnumerable<ChangeItem> lines, bool jev) =>
            lines.Where(l => !PolicyRuleIds.Contains(l.RuleId ?? "") && (jev ? l.DecisionConfidence.HasValue : !l.DecisionConfidence.HasValue))
                 .Select(l => l.ObjectRef).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        /// <summary>Whether a line restores something the person left out: only "what Copilot sees" has such a line,
        /// one that lets Copilot see an object that is excluded now (AiSchemaFamily.Line). No other family's line
        /// undoes a person's choice, so no other family's row ever says it does.</summary>
        internal static bool IsPutBack(ChangeItem l) =>
            l != null
            && string.Equals(l.DecisionPoint, Semanticus.Decisions.DecisionRegistry.AiSchema, StringComparison.Ordinal)
            && string.Equals(l.Kind, "set_ai_data_schema", StringComparison.Ordinal)
            && string.Equals(l.Before, "Excluded", StringComparison.Ordinal)
            && string.Equals(l.After, "Included", StringComparison.Ordinal);

        /// <summary>"jev.measures.table:Sales" into its family and its table name.</summary>
        internal static (string Family, string Table) Parse(string itemId)
        {
            if (!IsJevItem(itemId)) return (null, null);
            var rest = itemId.Substring(ItemPrefix.Length);
            var dot = rest.IndexOf('.');
            if (dot <= 0) return (null, null);
            var family = rest.Substring(0, dot);
            var tableRef = rest.Substring(dot + 1);
            var table = tableRef.StartsWith("table:", StringComparison.Ordinal) ? tableRef.Substring("table:".Length) : tableRef;
            return (family, table);
        }

        internal static string Count(int n, string one, string many) =>
            n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

        private static string LowerFirst(string s) =>
            string.IsNullOrEmpty(s) || (s.Length > 1 && char.IsUpper(s[0]) && char.IsUpper(s[1]))
                ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

        private static string Join(IReadOnlyList<string> names, int max) =>
            names.Count <= max
                ? string.Join(", ", names)
                : string.Join(", ", names.Take(max)) + " and " + (names.Count - max).ToString("N0", CultureInfo.InvariantCulture) + " more";
    }
}
