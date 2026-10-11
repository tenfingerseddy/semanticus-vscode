// Jev on the cleanup row: it can only UNTICK (THE CLEANUP ROW, round 5; Kane, 30 Sep 2026: "i would think Jev could choose what
// to exclude").
//
// THE ROW IS A SUGGESTION LIST for a lean model. The rule (CleanupGuard) decides what starts ticked; on Pro, Jev gets one
// typed question for each column the rule ticked: suggest removing it, or leave it out of the suggestion. A "leave it out"
// unticks the line and puts a one-line reason on it. That is ALL Jev can do:
//   it never ticks a column the rule left unticked (only ticked columns are asked about, and an answer for any other
//   column is ignored),
//   it never adds a column to the list or takes one off it,
//   it never removes anything (the person reviews the list and approves it),
//   it writes no text: the reason on the line is the engine's own sentence.
//
// WHAT JEV SEES. Names, types and shapes only, through the declared snapshot (DecisionRegistry.CleanupSuggest): the column's
// name, table and data type, whether it is calculated, and, once per table, which end of its relationships the table sits on,
// whether it holds number columns and the names and types of its columns. Never a value, a count or a sample.
//
// OFF OR SLOW MEANS THE RULE STANDS. Free: this file is not reached (Pro is checked before anything is built). No service, no
// permission, the point switched off, the budget spent, a timeout, a malformed answer: every one leaves the column ticked exactly
// as the rule left it. A call that DID answer is read the careful way: a column stays ticked only when Jev is sure it should go. The wait is bounded (<see cref="Wait"/>), and it is a wait
// the person started, by pressing Work it out. Compose (the Plan read) never asks.

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Decisions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal static class CleanupSuggest
    {
        /// <summary>Every question name starts with this and then names the column's ref.</summary>
        public const string Prefix = "cleanup:";

        public static string Q(string columnRef) => Prefix + columnRef;

        /// <summary>How many columns one call asks about (the relay's own bound for this point).</summary>
        public const int MaxPerCall = 40;

        /// <summary>How many calls one Work it out makes. A cap, not a sample: a column past it stays ticked exactly as the
        /// rule left it, and the person reviews it like every other.</summary>
        public const int MaxCalls = 3;

        /// <summary>The longest a person waits for Jev on Work it out. Past it the rule's ticks stand.</summary>
        public static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

        /// <summary>Above this a "leave it out" reads "sure", between the bar and this it reads "likely".</summary>
        private const double SureFloor = 0.85;

        internal sealed class Candidate
        {
            public string Ref;
            public string Table;
            public string Name;
            public string DataType;
            public bool Calculated;
            public CleanupGuard.Shape Shape;
        }

        /// <summary>The facts for one column the rule ticked. Read on the dispatcher.</summary>
        internal static Candidate CandidateOf(string columnRef, Column c, CleanupGuard.Shape shape) => new Candidate
        {
            Ref = columnRef,
            Table = c.Table.Name,
            Name = c.Name,
            DataType = c.DataType.ToString(),
            Calculated = c is CalculatedColumn,
            Shape = shape,
        };

        /// <summary>The criteria are about the world, never about our rule and never about the answer we hope for (the same
        /// rule as every other point). They are deliberately not symmetrical: what a person keeps a model lean of is a contact
        /// detail, a note or a technical leftover. A column that identifies, names, classifies or dates the rows of its table, or
        /// whose purpose cannot be told from its name and place, is one somebody may still want.</summary>
        private static readonly IReadOnlyList<DecisionOption> Options = new[]
        {
            new DecisionOption("suggest",
                "the column is clearly a contact detail, a free-text note or comment, a technical or staging field, or a copy of another column, and nothing reads it"),
            new DecisionOption("leave_out",
                "anything else: the column may identify, name, classify or date the rows of its table, or hold a category, "
                + "or what it is for cannot be told from its name and place"),
        };

        /// <summary>One request per call: at most <see cref="MaxPerCall"/> columns and <see cref="MaxCalls"/> calls, in the order
        /// the columns were given, so the same list asks the same questions (and hits the same cache entries).</summary>
        internal static IReadOnlyList<(IReadOnlyDictionary<string, object> Snapshot, IReadOnlyList<DecisionQuestion> Questions, IReadOnlyList<Candidate> Asked)>
            Requests(IReadOnlyList<Candidate> candidates)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.CleanupSuggest);
            var calls = new List<(IReadOnlyDictionary<string, object>, IReadOnlyList<DecisionQuestion>, IReadOnlyList<Candidate>)>();
            foreach (var batch in (candidates ?? Array.Empty<Candidate>()).Take(MaxPerCall * MaxCalls)
                         .Select((c, i) => (c, i)).GroupBy(x => x.i / MaxPerCall).OrderBy(g => g.Key))
            {
                var asked = batch.Select(x => x.c).ToList();
                var tables = asked.Select(c => c.Shape).Where(s => s != null)
                    .GroupBy(s => s.Table, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .Select(s => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "table", s.Table },
                        { "role", s.Role },
                        { "holds_numbers", s.HoldsNumbers },
                        { "has_a_column_that_names_its_rows", s.LabelFound },
                        { "columns", s.Columns.Take(40).Select(x => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                            { { "name", x.Name }, { "data_type", x.Type } }).ToList() },
                    }).ToList();
                var columns = asked.Select(c => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "id", c.Ref },
                    { "table", c.Table },
                    { "name", c.Name },
                    { "data_type", c.DataType },
                    { "is_calculated", c.Calculated },
                    // What the name checks saw: nothing, or the rule would not have ticked it.
                    { "name_checks_that_fired", new List<object>() },
                }).ToList();
                var snapshot = DecisionSnapshot.Build(point, new[]
                {
                    new KeyValuePair<string, object>("cleanup_tables", tables),
                    new KeyValuePair<string, object>("cleanup_columns", columns),
                });
                var questions = asked.Select(c => new DecisionQuestion(Q(c.Ref), DecisionQuestionKind.Choice,
                    "This model is being kept lean, and nothing in it uses the column " + c.Table + "[" + c.Name
                    + "]. Is it clearly a leftover that can be suggested for removal, or is it something somebody may still want?",
                    Options)).ToList();
                calls.Add((snapshot, questions, asked));
            }
            return calls;
        }

        /// <summary>Turn one call's answers into unticks. A ticked column STAYS ticked only when Jev answers "suggest" and is at least
        /// as sure as the point's bar; a "leave it out" at any confidence, or a "suggest" it is not sure of, unticks it. Unsure
        /// resolves toward the person's own look, never away from it. Only a column that is in <paramref name="picks"/> and still
        /// ticked can change: an answer for a column the rule left unticked, or for one that was never asked, changes nothing, so
        /// Jev can never tick anything. A call that was not decided changes nothing at all (the caller never reaches here).</summary>
        internal static int Apply(IDictionary<string, CleanupGuard.Pick> picks, DecisionAnswers answers, double bar)
        {
            if (picks == null || answers == null || !answers.Decided) return 0;
            var left = 0;
            foreach (var pair in answers.Answers)
            {
                if (!pair.Key.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                var columnRef = pair.Key.Substring(Prefix.Length);
                if (!picks.TryGetValue(columnRef, out var pick) || !pick.Ticked) continue;
                var answer = pair.Value;
                if (answer == null) continue;
                var sureItGoes = string.Equals(answer.Choice, "suggest", StringComparison.Ordinal) && answer.Confidence >= bar;
                if (sureItGoes) continue;
                var leftOut = string.Equals(answer.Choice, "leave_out", StringComparison.Ordinal);
                pick.Ticked = false;
                pick.JevConfidence = Math.Round(answer.Confidence, 4);
                pick.JevWord = leftOut && answer.Confidence >= SureFloor ? JevFamily.Sure
                             : leftOut && answer.Confidence >= bar ? JevFamily.Likely : JevFamily.Unsure;
                pick.Reason = leftOut
                    ? "Not ticked: Jev would leave it out of the suggestion (" + pick.JevWord + "). Tick it if you want it removed."
                    : "Not ticked: Jev is not sure it should go. Tick it if you want it removed.";
                left++;
            }
            return left;
        }
    }
}
