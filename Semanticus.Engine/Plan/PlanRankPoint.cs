// The order of the plan, and only the order.
//
// What this point can and cannot move. The items on the plan, their reasons, their exact changes, who does
// them and what they touch are all worked out by the rules before this point is asked, and they are byte for
// byte the same whether it is asked or not. This point answers one score per item and the plan is sorted by
// it. With no service, on a free session, without permission, or below the bar, the plan is sorted by the
// deterministic order instead and no item carries a number. Both plans are complete. That is the promise in
// docs/redesign/ui-hierarchy.md: "Free users see the plan's gaps in rule order with a do-it-by-hand jump on
// each; Pro ranks, scores and runs them."
//
// One call, not one per item. Every item is a question inside a single request, so the cost is one round
// trip and one cache entry for the whole plan. It also means the digest covers the WHOLE candidate set: add
// one item and the plan is rescored rather than one score being reused beside eleven stale ones.
//
// Criteria wording, the same rule as Decisions/MeasureDefaultsPoint.cs. The rungs below describe what makes
// a piece of work worth doing to the person who owns the model. None of them mentions our rules, our grade,
// our confidence, or which item we would like chosen. The item's own facts travel in the snapshot, where
// they are evidence, not in the criteria, where they would be a hint.

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Decisions;

namespace Semanticus.Engine
{
    public static class PlanRankPoint
    {
        /// <summary>How many items may be scored in one call. A cap, not a sample: the rest keep the
        /// deterministic order and carry no number, and the plan says so. A plan longer than this is already
        /// past the point where an order is what a person needs.</summary>
        public const int MaxScoredItems = 40;

        /// <summary>The rungs, lowest first, with criteria about the work rather than about us.</summary>
        private static readonly Dictionary<string, string> RungCriteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "none",   "doing this would change nothing anyone would notice, or it undoes a choice somebody made on purpose" },
            { "small",  "it tidies something up, and a person reading or using the model would barely notice the difference" },
            { "useful", "it makes the model clearer or safer to use, and somebody would notice within a week of working with it" },
            { "large",  "it fixes something that is actively making answers from this model harder to get or harder to trust" },
            { "urgent", "it fixes something that is producing wrong answers now, or is about to, and everything else can wait" },
        };

        /// <summary>The question name for one item. Question names are unique inside a request, and an item id
        /// is already unique on the plan, so the id is the name with a prefix that says what is being asked.</summary>
        public static string QuestionFor(string itemId) => "value:" + itemId;

        /// <summary>The item id back out of a question name, or null when the name is not one of ours.</summary>
        public static string ItemOf(string questionName) =>
            questionName != null && questionName.StartsWith("value:", StringComparison.Ordinal)
                ? questionName.Substring("value:".Length)
                : null;

        /// <summary>The declared snapshot: the shape of the model, and the facts already on each candidate.
        /// Nothing is added here that a person cannot already read on the plan page, and no formula, row or
        /// value is sent.</summary>
        public static IReadOnlyDictionary<string, object> Snapshot(
            string modelName, int tableCount, int columnCount, int measureCount, bool connected,
            IReadOnlyList<ModelPlanItem> candidates)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            var point = DecisionRegistry.Require(DecisionRegistry.PlanRank);

            var shape = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "model_name", modelName },
                { "tables", tableCount },
                { "columns", columnCount },
                { "measures", measureCount },
                { "connected_to_a_live_model", connected },
            };

            var items = candidates
                .Select(i => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "id", i.Id },
                    { "goal", i.Section },
                    { "heading", i.Title },
                    { "line", i.Sub },
                    { "who_would_do_it", i.Who },
                    { "changes", i.Diff == null ? 0 : i.Diff.Length },
                    { "touches", i.Touches },
                    { "reasons", i.Why == null ? Array.Empty<string>() : i.Why },
                })
                .ToList();

            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("model_shape", shape),
                new KeyValuePair<string, object>("plan_items", items),
            });
        }

        /// <summary>One score question per candidate, in the order the candidates were given, so the digest
        /// covers the whole set and its order.</summary>
        public static IReadOnlyList<DecisionQuestion> Questions(IReadOnlyList<ModelPlanItem> candidates)
        {
            var rungs = DecisionRegistry.PlanValueRungs
                .Select(r => new DecisionOption(r, RungCriteria[r]))
                .ToList();

            return (candidates ?? Array.Empty<ModelPlanItem>())
                .Select(i => new DecisionQuestion(
                    QuestionFor(i.Id), DecisionQuestionKind.Score,
                    "How much is it worth doing this to the model: " + i.Title
                        + (string.IsNullOrWhiteSpace(i.Sub) ? "" : " (" + i.Sub + ")") + "?",
                    rungs))
                .ToList();
        }

        /// <summary>A rung's place on the ladder, lowest ("none") first at zero, or -1 for a rung the ladder does not
        /// carry. An item with no sure score is sorted as zero and keeps the deterministic order (LocalEngine.Plan.cs,
        /// Order), and an item Jev is sure is worth nothing sorts after those, which is why a partly answered plan is
        /// still a coherent plan rather than a half-sorted one.</summary>
        public static int RungValue(string rung)
        {
            for (var i = 0; i < DecisionRegistry.PlanValueRungs.Count; i++)
                if (string.Equals(DecisionRegistry.PlanValueRungs[i], rung, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>Where an item Jev gave no answer for sorts: between "none" (0) and "small" (1). An item Jev is sure is worth
        /// nothing sinks below the ones it could not score, and an item worth at least a little sits above them, as the value
        /// order has always read a plan that was only partly answered.</summary>
        public const double UnscoredValue = 0.5;

        /// <summary>The scores for a whole plan, as item id to (expected value, confidence). The value is Jev's EXPECTED position
        /// on the ladder (a mean, counted from zero), not the rung it is likeliest to be, and it is kept whatever the
        /// confidence: an order is a harmless preference, so an answer under the bar still orders the plan (m11/jev-batch R1;
        /// the paid call used to yield little more than rule order, because a real reply's confidence sits near 0.6). What the
        /// bar gates is only the NUMBER shown beside an item: under it the confidence is reported as zero, which the plan
        /// reads as "no number" (ModelPlanItem.Confidence), never its place. Items with no answer or an unknown rung are
        /// simply absent, and the caller leaves those in the deterministic order.</summary>
        public static IReadOnlyDictionary<string, (double Value, double Confidence)> Scores(DecisionAnswers answers, double bar)
        {
            var scores = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
            if (answers == null || !answers.Decided) return scores;

            foreach (var pair in answers.Answers)
            {
                var id = ItemOf(pair.Key);
                if (id == null) continue;
                var answer = pair.Value;
                var rung = RungValue(answer.Score);
                if (rung < 0) continue;
                scores[id] = (answer.Expected ?? rung, answer.Confidence >= bar ? answer.Confidence : 0);
            }

            return scores;
        }
    }
}
