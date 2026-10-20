// Jev on the Copilot test: group the misses of one round into a few causes (copilot.misses; Kane, 15 Oct 2026: "yes" to the point, one call per round).
//
// A round of 500 questions can leave 60 misses. Reading each one costs the assistant more than reading seven causes. After the last answer of a round is
// graded, every PRACTICE miss becomes one question to Jev, all in one call, named "miss:<number>". Jev picks one pair of cause and fix from a fixed list
// (DecisionRegistry.CopilotCausePairs). It writes no text: the sentence a person reads is the engine's own (CopilotCause).
//
// WHAT JEV SEES. The words of the question, the kind of miss, and the names of the measures and columns the student used, the right answer used and the
// filters differed on. Never a value, a number, a formula or a held-back question. Every question names the objects (memory jev-costs-nothing: name the
// object, never a list path).
//
// OFF OR SLOW MEANS THE RULES STAND. Free, the point switched off, no service, no permission, a timeout, a malformed answer, an answer below the bar:
// the miss is grouped by CopilotCauseRules.Rule exactly as if Jev did not exist. The loop works without it.

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Decisions;

namespace Semanticus.Engine
{
    internal static class CopilotMissPoint
    {
        /// <summary>Every question name starts with this and then names the miss by its number in the bank.</summary>
        public const string Prefix = "miss:";

        public static string Q(int n) => Prefix + n;

        /// <summary>How many misses one call asks about (the relay's own bound for this point).</summary>
        public const int MaxPerCall = 120;

        /// <summary>The longest a round waits for Jev. Past it the rules' groups stand.</summary>
        public static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

        private static readonly IReadOnlyList<DecisionOption> Options = new[]
        {
            new DecisionOption("unknown_word:other_name", "a word in the question is not a name the model knows, and adding it as another name for the right field would fix it"),
            new DecisionOption("unknown_word:instruction", "a word in the question is not a name the model knows, and a sentence in the AI instructions about what the word means would fix it"),
            new DecisionOption("calendar_vs_fiscal:instruction", "the student used the calendar year where the model's year starts in another month, and a sentence in the AI instructions about the year would fix it"),
            new DecisionOption("calendar_vs_fiscal:description", "the student used the calendar year where the model's year starts in another month, and a description of the date field would fix it"),
            new DecisionOption("sibling_measure:description", "the student picked a similar field (a percent for an amount, units for money), and a description that tells the two apart would fix it"),
            new DecisionOption("sibling_measure:instruction", "the student picked a similar field, and a sentence in the AI instructions about which one a word means would fix it"),
            new DecisionOption("missing_description:description", "the right field has no description, so nothing says what it holds, and a description would fix it"),
            new DecisionOption("no_data:instruction", "the model has no data for the question and the student answered anyway, and a sentence in the AI instructions saying what is not in the model would fix it"),
            new DecisionOption("no_data:ai_schema", "the model has no data for the question and the student answered from a field Copilot should not use, and leaving that field out of what Copilot sees would fix it"),
            new DecisionOption("dax_slip:none", "the DAX has a mistake that is about writing DAX and not about the model, so no change to the model would fix it"),
            new DecisionOption("other:instruction", "none of the other causes fit, and a sentence in the AI instructions might help"),
            new DecisionOption("other:none", "none of the other causes fit and no change to the model is clear"),
        };

        /// <summary>One request per call, at most <see cref="MaxPerCall"/> misses, in the order of the bank, so the same round asks the same questions.</summary>
        internal static IReadOnlyList<(IReadOnlyDictionary<string, object> Snapshot, IReadOnlyList<DecisionQuestion> Questions, IReadOnlyList<CopilotMissFacts> Asked)>
            Requests(IReadOnlyList<CopilotMissFacts> misses)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.CopilotMisses);
            var calls = new List<(IReadOnlyDictionary<string, object>, IReadOnlyList<DecisionQuestion>, IReadOnlyList<CopilotMissFacts>)>();
            foreach (var batch in (misses ?? new List<CopilotMissFacts>()).Select((m, i) => (m, i)).GroupBy(x => x.i / MaxPerCall).OrderBy(g => g.Key))
            {
                var asked = batch.Select(x => x.m).ToList();
                var snapshot = DecisionSnapshot.Build(point, new[]
                {
                    new KeyValuePair<string, object>("question_words", asked.Select(m => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                        { { "id", m.N }, { "words", m.Question ?? "" } }).ToList()),
                    new KeyValuePair<string, object>("miss_kinds", asked.Select(m => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                        { { "id", m.N }, { "kind_of_question", m.Kind ?? "" }, { "kind_of_miss", m.Miss } }).ToList()),
                    new KeyValuePair<string, object>("student_fields", asked.Select(m => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                        { { "id", m.N }, { "names", m.StudentOnly.ToList() } }).ToList()),
                    new KeyValuePair<string, object>("right_fields", asked.Select(m => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                        { { "id", m.N }, { "names", m.ReferenceOnly.ToList() } }).ToList()),
                    new KeyValuePair<string, object>("filter_differences", asked.Select(m => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                        { { "id", m.N }, { "names", m.FilterDifferences.Concat(m.FilterSignals).ToList() } }).ToList()),
                });
                var questions = asked.Select(m => new DecisionQuestion(Q(m.N), DecisionQuestionKind.Choice, Ask(m), Options)).ToList();
                calls.Add((snapshot, questions, asked));
            }
            return calls;
        }

        /// <summary>The question for one miss. It names the objects, never a list path.</summary>
        internal static string Ask(CopilotMissFacts m)
        {
            string Names(IEnumerable<string> n) { var l = n.Distinct().ToList(); return l.Count == 0 ? "no different field" : string.Join(", ", l); }
            return "A student model was asked \"" + (m.Question ?? "") + "\" and got it wrong (" + CopilotMiss.Words(m.Miss) + "). "
                 + "It used " + Names(m.StudentOnly) + " where the right answer used " + Names(m.ReferenceOnly)
                 + (m.FilterDifferences.Count + m.FilterSignals.Count > 0 ? ", and the filters differed on " + Names(m.FilterDifferences.Concat(m.FilterSignals)) : "")
                 + ". What is the main reason it got it wrong, and which one change would fix it?";
        }

        /// <summary>Turn one call's answers into causes and fixes. An answer counts only when it is for a miss that was asked, is one of the fixed pairs and
        /// is at least as sure as the bar. Anything else leaves the rule's answer.</summary>
        internal static int Apply(IDictionary<int, (string Cause, string Fix, bool Jev)> picks, DecisionAnswers answers, double bar)
        {
            if (picks == null || answers == null || !answers.Decided) return 0;
            var used = 0;
            foreach (var pair in answers.Answers)
            {
                if (!pair.Key.StartsWith(Prefix, StringComparison.Ordinal) || !int.TryParse(pair.Key.Substring(Prefix.Length), out var n)) continue;
                var a = pair.Value;
                if (a?.Choice == null || a.Confidence < bar || !picks.ContainsKey(n)) continue;
                var parts = a.Choice.Split(':');
                if (parts.Length != 2 || !CopilotCause.Causes.Contains(parts[0]) || !CopilotCause.Fixes.Contains(parts[1])) continue;
                picks[n] = (parts[0], parts[1], true);
                used++;
            }
            return used;
        }
    }
}
