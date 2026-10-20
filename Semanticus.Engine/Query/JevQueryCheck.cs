// The check of a reading before a number is shown (the ruling "Jev data questions v2", 29 Sep; round 5 changed only the gate).
//
// WHY. A list of words cannot tell a word's meaning, so the engine stopped counting words. The wrong numbers the
// verifier found were not misreadings: Jev picked, correctly, the parts it was offered. They were SHAPE gaps: the question
// asks for something the one query shape cannot do (a change, a share, an absence, "only here", a forecast, stock, an unnamed
// "it"), and nothing was ever asked whether the shape can do it. Jev is the only part that reads meaning, so it is asked,
// once the reading is settled and just before the run.
//
// THE GATE IS THE GAP QUESTION, ALONE (round 4's pilot: the yes/no question never got past 0.83, the gap question separated).
//
//   q:check:gap   Choice. What, if anything, the reading leaves out. The engine's closed list, "nothing missing" first. The
//                 call carries the question's words, the measures and columns the reading uses (with their descriptions), and
//                 the reading told in the question's own words (StatedReading.ForJev), which says what the query shows (the
//                 periods side by side, a ratio measure, the period read) BEFORE the gap is asked for. value_matches goes
//                 empty: no value leaves.
//
// THE ENGINE CLEARS A GAP THE READING ALREADY COVERS, from the reading's own facts (StatedReading.Clears): a change when the
// periods or versions are side by side, a share when the measure is a ratio, another kind of number when the measure is that
// kind, a period when the period read is stated. A cleared gap counts as "nothing missing", and the answer says what the
// person reads off the rows. No fact clears an absence, a forecast, an unnamed thing, a narrowing, a ranking or a split.
//
// THE SECOND OPINION. When the gap mass is not enough (nothing missing plus the cleared gaps is under the bar) but voiding the
// two biggest uncleared gaps could reach it, each of those (at most two, in parallel) is asked as its own yes/no: does the
// question really ask for this, and the reading not show it? A gap keeps only the share of its mass the second opinion
// believes (mass x P(yes)) when its "no" is a clear majority (ClearNo); the gate is then the same sum against the same bar. Nothing else moves the gate.
//
// THE CHECK NEVER STOPS A RUN (K16, Kane 8 Oct: "We should always return something"). A gap it is sure of is said beside the
// number, first in the answer's one assumption line; a check that was not asked, not decided or not sure says nothing. It
// never lets through what a deterministic check refuses: it comes after them all.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Decisions;

namespace Semanticus.Engine.Query
{
    internal static class JevQueryCheck
    {
        public const string Nothing = "nothing";

        /// <summary>The line when the check was not asked or not decided: nothing is run on a reading nobody checked.</summary>
        public const string CouldNotCheck = "Semanticus could not double-check this reading, so nothing was run.";

        /// <summary>The line when the check answered but did not say the reading fits, and named no gap it was sure of.</summary>
        public const string NotSure = "Semanticus is not sure this reading answers the question.";

        /// <summary>The engine's list of what a reading can leave out, in the order it is asked. Each option's criterion
        /// describes the question's world in neutral words; the line is what the engine says to the person if it wins.</summary>
        internal static readonly (string Id, string Criterion, string Line)[] Gaps =
        {
            (Nothing, "the reading gives everything the question asks for; nothing is missing", null),
            ("change", "the question asks how numbers changed, rose, fell, grew or shrank, or how they compare with another period, and the reading shows no periods or versions side by side to read that off",
                "The question asks how the numbers changed, and Semanticus does not work out a change yet."),
            ("share", "the question asks for a share, a percent, a ratio, a rate or an average, and no measure in the reading is one",
                "The question asks for a share, a percent or an average that no measure here gives, and Semanticus does not work it out for you yet."),
            ("none_or_only", "the question asks about things with no rows, things that stopped, things sold only in one place, or things that were not sold, which totals cannot show",
                "The question asks about things with no sales, or sold in one place only, and Semanticus does not work that out yet."),
            ("future_or_stock", "the question asks about the future, a forecast, a target, or what is held in stock now rather than what was sold",
                "The question asks about the future or about what is in stock, and Semanticus shows what was sold."),
            ("unnamed", "the question points at something it does not name (it, this, that, them) and the reading picked one",
                "The question points at something it does not name, and Semanticus cannot tell which."),
            ("other_number", "the question asks for another kind of number than any measure in the reading is, such as a count of stores or orders, a profit or a price",
                "The question asks for a different kind of number than the one shown."),
            ("narrowing", "the question narrows the numbers to something (a place, a product, a kind, a word) that the reading leaves out or reads differently",
                "The question narrows the numbers in a way this reading does not show."),
            ("period", "the question names a time period that the reading does not state, or states as a different span",
                "The question names a period that this reading does not cover."),
            ("ranking", "the question asks for a ranking, an order, a first or last few, or a best or worst that the reading does not do the same way",
                "The question asks for a ranking or an order that this reading does not give."),
            ("split", "the question asks for the numbers per something (each, by, per) that the reading does not split by, or splits another way",
                "The question asks for the numbers split in a way this reading does not show."),
            ("versions", "the question asks for two versions of a number side by side (with and without, this and that), and the reading shows one",
                "The question asks for two versions of a number side by side, and this reading shows one."),
        };

        /// <summary>What each gap would be asking for, said as a noun phrase: the second opinion's question and the person's reading of it.</summary>
        private static readonly Dictionary<string, string> Asks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["change"] = "how the numbers changed, rose, fell or grew, or how they compare with another period",
            ["share"] = "a share, a percent, a ratio, a rate or an average",
            ["none_or_only"] = "things with no rows, things that stopped, or things sold in one place only",
            ["future_or_stock"] = "the future, a forecast, a target, or what is held in stock now",
            ["unnamed"] = "something it does not name (it, this, that, them)",
            ["other_number"] = "another kind of number than the reading shows, such as a count, a profit or a price",
            ["narrowing"] = "the numbers narrowed to something that the reading leaves out or reads differently",
            ["period"] = "a time period that the reading leaves out or reads as a different span",
            ["ranking"] = "a ranking, an order, a first or last few, or a best or worst that the reading does not do the same way",
            ["split"] = "the numbers per something (each, by, per) that the reading does not split by, or splits another way",
            ["versions"] = "two versions of a number side by side",
        };

        /// <summary>How the second opinion says a gap: what the question would be asking for.</summary>
        internal static string AskOf(string gap) => Asks.TryGetValue(gap, out var a) ? a : gap;

        private static IReadOnlyDictionary<string, object> SnapshotOf(string words, StatedReading stated, out List<QueryMeasure> measures, out List<QueryColumn> columns)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.QuerySummarize);
            var spec = stated.Spec;
            measures = stated.UsedMeasures.ToList();
            columns = spec.GroupBy.Concat(spec.Filters.Select(f => f.Column)).Concat(new[] { spec.SortColumn }.Where(c => c != null)).Distinct().ToList();
            var ms = measures;
            var cs = columns;
            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("question_words", (words ?? "").Trim()),
                new KeyValuePair<string, object>("model_fields", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "measures", ms.Select(m => JevQueryReader.MeasureField(m, withDescriptions: true)).ToList() },
                    { "columns", cs.Select(c => JevQueryReader.ColumnField(c, withDescriptions: true)).ToList() },
                }),
                // The point's fields are fixed: the matches were settled in the first call, so this one goes empty.
                new KeyValuePair<string, object>("value_matches", new List<object>()),
                new KeyValuePair<string, object>("stated_reading", stated.ForJev),
            });
        }

        /// <summary>The check's request: the question, the parts the reading uses, and the reading told in its words.</summary>
        public static JevQueryReader.Request Build(string words, QueryCatalog cat, StatedReading stated)
        {
            var r = new JevQueryReader.Request { Words = (words ?? "").Trim() };
            r.Snapshot = SnapshotOf(words, stated, out var measures, out var columns);
            r.Measures = measures;
            r.Columns = columns;
            r.Questions = new List<DecisionQuestion>
            {
                new DecisionQuestion(JevQueryReader.CheckGapQ, DecisionQuestionKind.Choice,
                    "Read stated_reading first: it says what the query shows. Then say what, if anything, it leaves out of what the question in question_words asks for. "
                    + "A reading that shows every period, or the periods or versions the question names, side by side, so the person reads any change or difference off the rows, leaves nothing out of a question about a change. "
                    + "A measure that is itself a ratio, a rate or an average (it says what it divides) gives a share, a percent or an average of exactly what it divides, and leaves nothing out of a question for that; "
                    + "a share of something else (orders against value, one kind of thing against another) is not given by it. "
                    + "A period the reading states leaves no period out, and so does a reading that says the period words could not be pinned to one period and shows every period side by side for the person to read off the one they meant. A name typed with a slip (a typo) that the reading has right leaves nothing out. "
                    + "But hold every narrowing in stated_reading against the exact words of the question: 'is' keeps only that one value, so words like from, since, after, before, until or between need 'or later', 'after', 'before' or 'or earlier', "
                    + "and a question about what we have, hold or keep asks for stock, not for what was sold. "
                    + "Pick 'nothing' when the reading gives everything asked. Otherwise pick the one thing that is missing most.",
                    Gaps.Select(g => new DecisionOption(g.Id, g.Criterion))),
            };
            DecisionSnapshot.RequireWithin(r.Snapshot, 60 * 1024);
            return r;
        }

        /// <summary>The second opinion on one gap: does the question really ask for it, and the reading not show it?</summary>
        public static JevQueryReader.Request BuildSecond(string words, QueryCatalog cat, StatedReading stated, string gap)
        {
            var r = new JevQueryReader.Request { Words = (words ?? "").Trim() };
            r.Snapshot = SnapshotOf(words, stated, out var measures, out var columns);
            r.Measures = measures;
            r.Columns = columns;
            r.Questions = new List<DecisionQuestion>
            {
                new DecisionQuestion(JevQueryReader.CheckRealQ, DecisionQuestionKind.Noul,
                    "The question in question_words asks for " + AskOf(gap) + ", and the reading in stated_reading leaves it out. "
                    + "Read stated_reading first: a reading that shows the periods or versions side by side leaves a change to be read off the rows, a measure that is itself a ratio gives a share or an average of what it divides, a reading that shows every period side by side leaves no period out, "
                    + "and a name typed with a slip that the reading has right leaves nothing out. Say yes only when the question really asks for it and the reading does not give it.",
                    new[]
                    {
                        new DecisionOption("yes", "the question asks for it and the reading leaves it out"),
                        new DecisionOption("no", "the reading gives it, or the question does not ask for it"),
                    }),
            };
            DecisionSnapshot.RequireWithin(r.Snapshot, 60 * 1024);
            return r;
        }

        /// <summary>Where the first answer leaves the check.</summary>
        internal sealed class Verdict
        {
            /// <summary>The reading may run.</summary>
            public bool Run;
            /// <summary>The line the run is stopped with, when it is.</summary>
            public string Stop;
            /// <summary>What the person reads off the rows, when a cleared gap let the run go on (also after a second opinion).</summary>
            public string Note;
            /// <summary>The gaps the reading provably covers and Jev was not sure of: counted as "nothing missing".</summary>
            public HashSet<string> Cleared = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>The gaps a second opinion could still void, biggest first (at most two); empty when none can help.</summary>
            public List<string> Second = new List<string>();
            internal Dictionary<string, double> Mass;
            internal double Effective;
        }

        private static Dictionary<string, double> MassOf(DecisionAnswer gap)
        {
            var mass = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var g in Gaps) mass[g.Id] = gap.Probabilities.TryGetValue(g.Id, out var p) ? p : 0;
            // A service that sent no probabilities sent the pick and its confidence: that is all the mass known.
            if (gap.Probabilities.Count == 0 && mass.ContainsKey(gap.Choice ?? "")) mass[gap.Choice] = gap.Confidence;
            return mass;
        }

        /// <summary>How sure Jev is of a gap when the engine never overrides it (round 6, D3): a fact of the reading may add a note
        /// but never lifts a stop on a gap named at this or more.</summary>
        internal const double Sure = 0.7;

        /// <summary>Judge the first answer: run, stop, or which second opinions could change it.</summary>
        public static Verdict First(DecisionAnswers answers, double bar, StatedReading stated, string words)
        {
            var gap = answers?.For(JevQueryReader.CheckGapQ);
            if (gap == null) return new Verdict { Stop = CouldNotCheck };
            var mass = MassOf(gap);
            var cleared = new Dictionary<string, string>(StringComparer.Ordinal);
            // A gap Jev is sure of is never cleared by a fact of the reading: only a weaker one, and only what it provably covers.
            foreach (var g in Gaps.Where(g => g.Id != Nothing && mass[g.Id] < Sure))
            {
                var says = stated.Clears(g.Id, words);
                if (says != null) cleared[g.Id] = says;
            }
            var effective = mass[Nothing] + cleared.Keys.Sum(k => mass[k]);
            var v = new Verdict { Mass = mass, Effective = effective };
            foreach (var k in cleared.Keys) v.Cleared.Add(k);
            // The person is told what a cleared gap left to them, when Jev thought the question wanted it.
            v.Note = string.Join(" ", cleared.Where(c => c.Value.Length > 0 && mass[c.Key] >= 0.3).OrderByDescending(c => mass[c.Key]).Select(c => c.Value).Distinct());
            if (v.Note.Length == 0) v.Note = null;
            if (effective >= bar) { v.Run = true; return v; }
            var blocking = Gaps.Where(g => g.Id != Nothing && !cleared.ContainsKey(g.Id)).OrderByDescending(g => mass[g.Id]).ToList();
            // A gap Jev is sure of is a stop: no second opinion is spent on it.
            if (blocking.Count > 0 && mass[blocking[0].Id] >= Sure) { v.Stop = StopLine(mass, v.Cleared, bar, stated, new HashSet<string>()); return v; }
            var top = blocking.Where(g => mass[g.Id] >= 0.05).Take(2).Select(g => g.Id).ToList();
            // Relieving the two biggest gaps entirely must be able to reach the bar, or a call is spent for nothing.
            if (effective + top.Sum(t => mass[t]) >= bar) v.Second = top;
            else v.Stop = StopLine(mass, v.Cleared, bar, stated, secondYes: new HashSet<string>());
            return v;
        }

        /// <summary>How sure a second opinion's "no" must be to relieve the gap it was asked about (a clear majority).</summary>
        internal const double ClearNo = 0.7;

        /// <summary>Judge the second opinions. A gap asked about keeps only the share of its mass the second opinion believes: a
        /// gap first put at 0.17 that the second opinion doubts (P(yes) 0.2) is 0.03 of doubt, and a gap put at 0.93 that it
        /// confirms stays at 0.7. A second opinion not asked, not decided or unsure relieves nothing.</summary>
        public static string Second(Verdict first, IReadOnlyDictionary<string, DecisionAnswers> opinions, double bar, StatedReading stated, string words, out bool run)
        {
            var relieved = new HashSet<string>(StringComparer.Ordinal);
            var confirmed = new HashSet<string>(StringComparer.Ordinal);
            var effective = first.Effective;
            foreach (var kv in opinions)
            {
                var a = kv.Value?.For(JevQueryReader.CheckRealQ);
                if (a?.Noul == null) continue;
                var pYes = a.Noul == true ? a.Confidence : 1 - a.Confidence;
                // A second opinion relieves a gap only when it clearly doubts it: a "no" short of a clear majority is a shrug.
                if (1 - pYes >= ClearNo) effective += first.Mass[kv.Key] * (1 - pYes);
                relieved.Add(kv.Key);
                if (pYes >= bar) confirmed.Add(kv.Key);
            }
            run = effective >= bar;
            if (run) return null;
            return StopLine(first.Mass, first.Cleared, bar, stated, confirmed);
        }

        /// <summary>The line a stopped run says: the top gap still standing when it was picked with the bar's confidence (or with
        /// a majority and a second opinion that agrees), else that Jev is not sure.</summary>
        private static string StopLine(IReadOnlyDictionary<string, double> mass, IEnumerable<string> cleared, double bar, StatedReading stated, ISet<string> secondYes)
        {
            var skip = new HashSet<string>(cleared, StringComparer.Ordinal) { Nothing };
            var top = Gaps.Where(g => !skip.Contains(g.Id) && g.Line != null).OrderByDescending(g => mass[g.Id]).FirstOrDefault();
            if (top.Line != null && (mass[top.Id] >= Sure || (mass[top.Id] >= 0.5 && secondYes.Contains(top.Id))))
                return top.Line;
            return NotSure;
        }
    }
}
