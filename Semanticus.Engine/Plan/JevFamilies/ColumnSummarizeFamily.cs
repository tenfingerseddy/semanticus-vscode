// The base measure family: column.summarize (T268, M4 columns; Kane's redesign of 26 September).
//
// WHAT KANE DECIDED. Two good practices clashed: Jev picked how a column adds up by itself (Average for a price), and
// the AI-readiness rules say a number column should not add up by itself at all but have an explicit measure instead,
// so approving Jev's pick made the model's health look worse. Kane: "Jev should set summarisation of columns off as a
// best practice. It should suggest an explicit base measure aggregation instead." So for every number column this
// family hears back on:
//   1. the column is set not to add up (summarize None), as the readiness rules and BPA ask, unless it already is;
//   2. Jev's aggregation pick chooses the explicit base measure the column gets: sum, average, count, distinct count,
//      min or max, or none (no measure). The engine writes the measure from a fixed template (StarterMeasureTemplates:
//      the DAX, the format, a plain description); Jev writes no text.
// One Plan row per table; approving it sets the columns and creates the kept measures as one undo step.
//
// THE PRO PATH. Jev's pick when it is sure (the point's bar, 0.85), otherwise the principled rule the fair replay
// compared against (<see cref="PrincipledRule"/>, ported from tools/jev-replay/columns/rules.mjs fairRuleSummarize),
// with one guard the replay's rule did not have (coordinator's ruling, 26 September): a flag, a line number or another
// ordinal never gets a total (<see cref="FlagOrOrdinal"/>), from Jev or from the rule. Whether this path clears Kane's
// bar on the question that ships is the re-test of 26 September (tools/jev-replay/columns/README.md, "Re-test").
//
// NEVER A SECOND COPY. A measure is proposed only when no measure in the model already computes the same thing (the
// same aggregation of the same column, whatever it is called or however its DAX is spaced or quoted), and it takes the
// model's own naming where the model has one for that aggregation ("Quantity Sum" when the model says "Cost Sum").
//
// ONE DECISION PER COLUMN. While this point is live it is the one place a number column's base measure is decided: the
// starter rule's items stand aside (measure.starter, which lost its replay, stays off and is superseded), and the
// rules that write the same None (FMT-SUMMARIZE, SUMMARIZE-DIMENSION) and DAC-IMPLICIT-MEASURE's "add an explicit
// measure" row leave the column to this family's row on the Plan. Every scan still reads every rule whole.
//
// WHAT JEV SEES. The facts the fair replay's call carried, under this point's four fields (ColumnAskFacts.cs): names,
// types, structure and derived facts only. Never a value, and never any column's summarize setting.
//
// WHAT THIS FILE DOES NOT DO. It never asks. Prepare reads the model on the dispatcher; Map is pure. The asking is
// LocalEngine.JevFamilies.cs, through DecisionService, where every gate lives (Pro, consent, budget, the fence).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Decisions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal static class ColumnSummarizeFamily
    {
        internal const string Name = "column-summarize";

        /// <summary>WHETHER JEV DECIDES: YES. The 26 September re-test on the question that ships (the base-measure question,
        /// one per call; tools/jev-replay/columns/retest-bar.json) cleared the committed bar: on the blind hand read the Pro
        /// path 90.6% against the rule's 84.3% on 235 columns, 19 wins to 4, exact McNemar p 0.0026, harmful totals 14
        /// against 29; on the key read (2,118) the rule is ahead, 93.9% against 90.3%, but the Pro path still makes fewer
        /// harmful totals, 30 against 56. A point that fails switches off and the rules decide.</summary>
        internal const bool JevDecides = true;

        internal const string Question = "summarize_by";

        /// <summary>WHETHER THE ASKS GO ONE PER TABLE (m11/jev-batch, JevTableBatch.cs). True: a table's columns are asked in one
        /// request, its shared facts once, one question per column pointing at its entry in `columns`. True, as shipped: ON (Kane, 1 Oct: "the batching is the best mitigation not caps"). The comparison run of 30 September missed its fixed bar (docs/redesign/jev-batch-comparison-result.md: the unsure middle moves, sure answers stay), and Kane accepted that an unsure answer can move and arrive ticked or unticked in the reviewed Plan. JevPerObjectModeTests still proves both modes give the same plan lines. False: one request per object (the test seam JevBatchedForTest sets it).</summary>
        internal const bool Batched = true;

        /// <summary>How many columns one batched request asks about. A table with more goes in more requests.</summary>
        internal const int ChunkSize = 30;

        /// <summary>How many columns one run asks about for the first time. A cap, not a sample: every ask costs budget.
        /// Answers already heard are not asked again, and the rest are asked in the runs that follow.</summary>
        internal const int MaxColumnsPerRun = 60;

        /// <summary>The rule id on the line that sets a column not to add up. It is good practice applied to every column
        /// the family hears back on, not anybody's pick, so its provenance is rule and the row does not count it as Jev's
        /// or as the rule standing in for Jev.</summary>
        internal const string OffRuleId = "SUMMARIZE-OFF";

        /// <summary>The rule id on the line that hides a number column once its base measure exists: BPA's "Hide fact table
        /// columns" asks it of any number column a measure aggregates, so the row does it with the measure rather than
        /// leave a health warning behind. Good practice, like <see cref="OffRuleId"/>, not anybody's pick.</summary>
        internal const string HideRuleId = "BASE-MEASURE-HIDE";

        /// <summary>The rule id on a measure the principled rule chose because Jev was not sure. Its provenance is rule.</summary>
        internal const string PrincipledRuleId = "SUMMARIZE-PRINCIPLED";

        internal sealed class Ask
        {
            public string Ref;
            public string ColumnName;
            public string TableRef;
            public string TableName;

            /// <summary>The display folder most of this table's measures already sit in, or null when none does. A measure this family
            /// creates goes in the same line into that folder, so the folders-and-formats row never has to come back for it.</summary>
            public string Folder;

            /// <summary>The column's summarize setting now, as TOM names it. Kept here for Map and never sent.</summary>
            public string Current;

            public bool IsKey;
            public string Role = "none";

            /// <summary>The column is a part of a calculated key (Department in Department &amp; "-" &amp; Ticket, when a
            /// relationship or a declared key uses that column). A total of it means as little as a total of the key.</summary>
            public bool KeyPart;
            public Facts.MeasuredFacts Measured;

            /// <summary>The column's own format, when it has one, and whether its values are whole numbers.</summary>
            public string ColumnFormat;
            public bool Whole;
            public bool IsHidden;
            /// <summary>The table is hidden, so a new measure in it is hidden from Copilot too.</summary>
            public bool TableHidden;

            /// <summary>The column's Excel/MDX attribute is on and nothing needs it (no sort-by either way, no hierarchy, no
            /// variation), so once hidden it can go off, as BPA's "Set IsAvailableInMdx to false" asks of a hidden column.</summary>
            public bool MdxCanGoOff;

            /// <summary>The person accepted a finding that keeps this column's summarize setting, its visibility or its Excel
            /// availability as it is (DeclinedChanges): the line that writes that one is left out, like every other path that
            /// writes a change (M11 join part 4, D1). The base measure is still proposed; it changes nothing the person kept.</summary>
            public bool KeepsSummarize, KeepsVisibility, KeepsExcel;

            /// <summary>The model already keeps Copilot's synonyms (a linguistic schema), so a new measure gets a few
            /// engine-made ones, as the readiness rule SYN-FIELD asks of every visible field there. Never seeded here.</summary>
            public bool HasLinguistic;

            /// <summary>Per aggregation: the new measure's name (the model's own naming where it has one).</summary>
            public Dictionary<string, string> NameOf = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>Per aggregation: the measure that already computes it, when one does.</summary>
            public Dictionary<string, string> Existing = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>Per aggregation: why its name cannot be used, when something else already has it.</summary>
            public Dictionary<string, string> Taken = new Dictionary<string, string>(StringComparer.Ordinal);

            public IReadOnlyDictionary<string, object> Snapshot;
            public IReadOnlyList<DecisionQuestion> Questions;

            /// <summary>This column's entry in a batched request's `columns` (ColumnAskFacts.Entry).</summary>
            public Dictionary<string, object> Entry;

            /// <summary>Every measure this column could get, so a column not heard back on this run keeps whatever measure
            /// line was already waiting for it.</summary>
            public IEnumerable<string> MeasureRefs => NameOf.Values.Select(n => StarterMeasuresFamily.MeasureRef(TableName, n));
        }

        internal sealed class Prepared
        {
            public List<Ask> Asks = new List<Ask>();
            public List<ModelPlanChangeRefusal> Refused = new List<ModelPlanChangeRefusal>();
            public long Revision;

            /// <summary>What each table's asked columns share, by table ref (the batched request's one copy).</summary>
            public Dictionary<string, ColumnAskFacts.TableShare> Shares =
                new Dictionary<string, ColumnAskFacts.TableShare>(StringComparer.OrdinalIgnoreCase);
        }

        internal static bool Wants(Column c) =>
            c != null && c.Type != ColumnType.RowNumber && !(c.Table is CalculationGroupTable)
            && (c.DataType == DataType.Int64 || c.DataType == DataType.Double || c.DataType == DataType.Decimal);

        /// <param name="skipAnswered">True on the Plan: a column that already has a base measure, or that a start plan decided and set not to add up, is answered and is not asked about again. False on the
        /// start plan's decide loop, which asks every round on purpose (it reads a pick that keeps flipping from the repeated answers).</param>
        /// <param name="startDecided">The columns a start plan heard Jev on (column refs). A tidy column set to None that is not here still gets its measure row.</param>
        internal static Prepared Prepare(Model m, Facts.ModelFacts facts, ISet<string> removed = null, bool skipAnswered = false, ISet<string> startDecided = null)
        {
            var prepared = new Prepared();
            var point = DecisionRegistry.Require(DecisionRegistry.ColumnSummarize);
            var questions = Questions();
            var shape = ColumnAskFacts.Read(m);
            var measures = BaseMeasures.Read(m);
            var keyParts = StarterMeasureTemplates.CalculatedKeyParts(m);
            var declined = Semanticus.Analysis.DeclinedChanges.Read(m);
            var hasLinguistic = (m.Database?.CompatibilityLevel ?? 0) >= 1465
                                && m.Cultures.Any(c => c.ContentType == ContentType.Json && !string.IsNullOrEmpty(c.Content));
            // Every wanted column; how many are ASKED per run is the batch cap (LocalEngine.JevFamilies.cs).
            foreach (var table in m.Tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var c in table.Columns.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!Wants(c)) continue;
                    if (StarterMeasuresFamily.IsRefused(removed, c)) continue;   // the person gave this column no measure when they built the model (UAT-J7-S2-009)
                    var (tableFacts, measured) = ColumnAskFacts.FactsOf(facts, table, c);
                    var tableRef = ObjectRefs.For(table);
                    if (!prepared.Shares.ContainsKey(tableRef)) prepared.Shares[tableRef] = ColumnAskFacts.Share(shape, table, tableFacts);
                    var ask = new Ask
                    {
                        Ref = ObjectRefs.For(c),
                        ColumnName = c.Name,
                        TableRef = ObjectRefs.For(table),
                        TableName = table.Name,
                        Current = c.SummarizeBy.ToString(),
                        IsKey = c.IsKey || Semanticus.Analysis.SourceKeys.IsDeclaredKey(c),   // every part of a declared key, not only the one TOM holds
                        Role = shape.Roles.TryGetValue(c, out var role) ? role : "none",
                        KeyPart = keyParts.Contains(c),
                        Measured = measured,
                        ColumnFormat = string.IsNullOrWhiteSpace(c.FormatString) ? null : c.FormatString,
                        Whole = c.DataType == DataType.Int64 || measured?.WholeNumbers == true,
                        IsHidden = c.IsHidden,
                        TableHidden = table.IsHidden,
                        KeepsSummarize = declined.Declines(ObjectRefs.For(c), "summarizeBy"),
                        KeepsVisibility = declined.Declines(ObjectRefs.For(c), "isHidden"),
                        KeepsExcel = declined.Declines(ObjectRefs.For(c), "isAvailableInMDX"),
                        HasLinguistic = hasLinguistic,
                        MdxCanGoOff = c.IsAvailableInMDX && c.SortByColumn == null && !c.UsedInSortBy.Any()
                                      && !c.UsedInHierarchies.Any() && !c.UsedInVariations.Any(),
                        Snapshot = ColumnAskFacts.Snapshot(shape, table, c, tableFacts, measured, point),
                        Entry = ColumnAskFacts.Entry(shape, table, c, measured),
                        Questions = questions,
                    };
                    foreach (var option in StarterMeasureTemplates.MeasureOptions)
                    {
                        ask.NameOf[option] = measures.NameFor(option, c.Name);
                        var existing = measures.Existing(option, table.Name, c.Name);
                        if (existing != null) ask.Existing[option] = existing;
                    }
                    // A column that already has a base measure is answered: asking Jev again offered a second measure, then re-asked the
                    // table every time a measure was added (Kane, 13 Oct 2026). The summarize setting of the column is the readiness rule's.
                    // A column the start plan decided and the build set to None is answered too. The Plan asked it again in a smaller batch than the start plan's
                    // decide loop sent, so Jev was asked twice and a new digest never matched the stored answer (StartPlanEndToEnd, 13 Oct). Only THOSE columns:
                    // a tidy numeric column that was already None with no base measure still gets its measure row.
                    if (skipAnswered && (ask.Existing.Count > 0 || (c.SummarizeBy == AggregateFunction.None && startDecided != null && startDecided.Contains(ask.Ref)))) continue;
                    ask.Folder = table.Measures.Select(x => x.DisplayFolder).Where(x => !string.IsNullOrWhiteSpace(x))
                        .GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                        .Select(g => g.Key).FirstOrDefault();
                    prepared.Asks.Add(ask);
                }
            }
            NameApart(m, measures, prepared.Asks);
            return prepared;
        }

        /// <summary>ONE NAME PER MEASURE, MODEL-WIDE. A measure's name must be unique in the whole model, but number
        /// columns repeat across tables (Quantity in Sales and in Returns), and two aggregations can spell alike (the
        /// average of Cost as "Total Avg Cost" beside the total of Avg Cost). So a name two tables' columns could both
        /// get, for any aggregation, takes each one's table name too ("Total Sales Quantity", "Total Returns Quantity"),
        /// decided from every wanted column whatever is already approved, so a name never moves when another row is
        /// approved; and a name something else in the model already has is tried the same way. A name that says its
        /// aggregation twice ("Total Avg Avg Cost", when the model's pattern already says Avg) takes the plainer form
        /// ("Total Avg Cost") only while no other column could get that name. A name still not free is refused, and the
        /// refusal says the column was left as it is. Two columns whose PICKS would share a name are settled once the
        /// picks are known (<see cref="SettleNames"/>), so a clash with an aggregation nobody picked costs nothing.</summary>
        private static void NameApart(Model m, BaseMeasures measures, List<Ask> asks)
        {
            var options = StarterMeasureTemplates.MeasureOptions.ToArray();
            var shared = new HashSet<string>(
                asks.SelectMany(a => options.Select(o => (Ask: a, Name: a.NameOf[o])))
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Select(p => p.Ask.TableName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    .Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);
            foreach (var a in asks)
                foreach (var option in options)
                {
                    if (shared.Contains(a.NameOf[option])) a.NameOf[option] = measures.NameFor(option, a.ColumnName, a.TableName);
                    if (a.Existing.ContainsKey(option)) continue;
                    var table = m.Tables[a.TableName];
                    if (StarterMeasureTemplates.NameTaken(m, table, a.NameOf[option]) != null)
                    {
                        var qualified = measures.NameFor(option, a.ColumnName, a.TableName);
                        if (StarterMeasureTemplates.NameTaken(m, table, qualified) == null) a.NameOf[option] = qualified;
                    }
                }
            var named = asks.SelectMany(a => options.Select(o => (Ask: a, Option: o, Name: a.NameOf[o]))).ToList();
            var plainer = named.Select(n => (n.Ask, n.Option, Name: Plainer(n.Name))).Where(n => !string.Equals(n.Name, n.Ask.NameOf[n.Option], StringComparison.Ordinal)).ToList();
            foreach (var (a, option, name) in plainer)
            {
                if (named.Any(n => !ReferenceEquals(n.Ask, a) && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                if (plainer.Any(n => !ReferenceEquals(n.Ask, a) && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                if (!a.Existing.ContainsKey(option) && StarterMeasureTemplates.NameTaken(m, m.Tables[a.TableName], name) != null) continue;
                a.NameOf[option] = name;
            }
            foreach (var a in asks)
                foreach (var option in options.Where(o => !a.Existing.ContainsKey(o)))
                    if (StarterMeasureTemplates.NameTaken(m, m.Tables[a.TableName], a.NameOf[option]) != null)
                        a.Taken[option] = "Something in this model is already named " + a.NameOf[option] + ", so no measure was proposed for "
                                          + a.ColumnName + " and it keeps adding up by itself.";
        }

        private static readonly string[][] SameWord =
        {
            new[] { "avg", "average", "mean" }, new[] { "min", "minimum" }, new[] { "max", "maximum" },
        };

        /// <summary>A name with an aggregation word said twice in a row ("Total Avg Avg Cost", "Average Average Price",
        /// "Total Total Due") with the second one left out. Only the average, minimum and maximum words and exact repeats
        /// count: "Total Sum Insured" keeps its words, because "Sum Insured" is a phrase of its own.</summary>
        internal static string Plainer(string name)
        {
            var w = (name ?? "").Split(' ');
            for (var i = 0; i + 1 < w.Length; i++)
            {
                var same = string.Equals(w[i], w[i + 1], StringComparison.OrdinalIgnoreCase)
                           || SameWord.Any(g => g.Contains(w[i].ToLowerInvariant()) && g.Contains(w[i + 1].ToLowerInvariant()));
                if (same && w.Length > 2) return string.Join(" ", w.Take(i + 1).Concat(w.Skip(i + 2)));
            }
            return name;
        }

        /// <summary>The option the column's line will carry, exactly as <see cref="Map"/> decides it: Jev's pick when it is
        /// sure (and not a total on a flag or a line number), else the principled rule. Null when there is no answer.</summary>
        internal static string PickOf(Ask ask, DecisionAnswers answers, double bar)
        {
            var answer = answers?.Decided == true ? answers.For(Question) : null;
            return answer == null ? null : Choose(ask, answer.Choice, answer.Confidence, bar).Pick;
        }

        private static (string Pick, bool Sure, bool FlagGuard, bool KeyGuard) Choose(Ask ask, string pick, double confidence, double bar)
        {
            var sure = Decides(confidence, bar);
            var isSum = string.Equals(pick, "sum", StringComparison.Ordinal);
            var flag = sure && isSum && FlagOrOrdinal(ask.ColumnName);
            // A key, a relationship end or a part of a calculated key is never totalled, whoever asks (Sol 6.1, 1 Oct): the
            // principled rule refuses it too, so the fallback cannot pick the total Jev's answer was not sure of.
            // A count over a key is the same: "Count of OrderDateKey" counts dates people never asked about (Kane, 11 Oct).
            var counts = pick == StarterMeasureTemplates.Count || pick == StarterMeasureTemplates.DistinctCount;
            var key = sure && (isSum || counts) && StarterMeasureTemplates.IsKeyShaped(ask.IsKey, ask.Role, ask.KeyPart);
            if (flag || key) sure = false;
            return (sure ? pick : PrincipledRule(ask), sure, flag, key && !flag);
        }

        /// <summary>The pick and whether Jev's own answer decided it (sure, and not a guarded total), exactly as <see cref="Map"/> decides
        /// them. The pick is null when there is no answer. The start plan reads it to fold the pick into its base measures.</summary>
        internal static (string Pick, bool Sure) ChoiceOf(Ask ask, DecisionAnswers answers, double bar)
        {
            var answer = answers?.Decided == true ? answers.For(Question) : null;
            if (answer == null) return (null, false);
            var chosen = Choose(ask, answer.Choice, answer.Confidence, bar);
            return (chosen.Pick, chosen.Sure);
        }

        /// <summary>Once every heard column's pick is known: two columns whose new measures would share a name (the same
        /// table, so the table's name cannot tell them apart, and two aggregations that spell alike) both get none, with
        /// a reason, rather than one being made and the other failing on approve.</summary>
        internal static void SettleNames(IEnumerable<(Ask Ask, string Pick)> picks)
        {
            var making = picks.Where(p => p.Ask != null && p.Pick != null && p.Pick != StarterMeasureTemplates.None
                                          && p.Ask.NameOf.ContainsKey(p.Pick) && !p.Ask.Existing.ContainsKey(p.Pick) && !p.Ask.Taken.ContainsKey(p.Pick))
                              .ToList();
            foreach (var group in making.GroupBy(p => p.Ask.NameOf[p.Pick], StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var (a, pick) in group)
                    a.Taken[pick] = "Another column's measure would also be named " + group.Key + ", so no measure was proposed for "
                                    + a.ColumnName + " and it keeps adding up by itself.";
        }

        /// <summary>The fair replay's criteria (tools/jev-replay/columns/build-calls-fair.mjs SUMMARIZE_FAIR), word for word.</summary>
        private static readonly Dictionary<string, string> Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "sum", "additive amounts and quantities, where a total means something" },
            { "none", "keys, ids, codes, years, period numbers and other labels stored as numbers, where a total means nothing" },
            { "average", "rates, ratios, percentages, prices, scores and measurements, where the mean means something and the total does not" },
            { "count", "the number of rows is counted" },
            { "distinctcount", "the number of different values is counted" },
            { "min", "the smallest value is taken" },
            { "max", "the largest value is taken" },
        };

        /// <summary>The question that ships, and the one the 26 September re-test scored (tools/jev-replay/columns/
        /// product-payload.mjs holds the same words). The criteria are the fair replay's; the instructions ask for the
        /// column's base measure, which is what the answer now decides.</summary>
        internal const string Instructions =
            "Following good modelling practice, how should this column's values be combined in its base measure, the explicit measure a report author uses instead of the column itself?";

        internal static IReadOnlyList<DecisionQuestion> Questions() => new[]
        {
            new DecisionQuestion(Question, DecisionQuestionKind.Choice, Instructions,
                DecisionRegistry.SummarizeOptions.Select(o => new DecisionOption(o, Criteria[o])).ToList()),
        };

        /// <summary>The batched requests for the asks: each table's columns in chunks, the table's shared facts once and a question
        /// for each column that starts "For the column `columns[i]`:" and then says exactly what the per-column question says.</summary>
        internal static List<TableBatch<Ask>> Batches(Prepared prepared, int size = ChunkSize)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.ColumnSummarize);
            var options = DecisionRegistry.SummarizeOptions.Select(o => new DecisionOption(o, Criteria[o])).ToList();
            TableBatch<Ask> Build(IReadOnlyList<Ask> chunk)
            {
                var batch = new TableBatch<Ask> { TableRef = chunk[0].TableRef, Asks = chunk.ToList() };
                var questions = new List<DecisionQuestion>();
                for (var i = 0; i < chunk.Count; i++)
                {
                    var name = JevTableBatch.QuestionName(Question, chunk[i].Ref);
                    questions.Add(new DecisionQuestion(name, DecisionQuestionKind.Choice,
                        "For the column " + JevTableBatch.Path("columns", i) + ": " + Instructions, options));
                    batch.Names[chunk[i]] = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(name, Question) };
                }
                batch.Questions = questions;
                batch.Snapshot = ColumnAskFacts.BatchSnapshot(point, prepared.Shares[batch.TableRef], chunk.Select(a => a.Entry));
                return batch;
            }
            return JevTableBatch.Chunks(prepared.Asks, a => a.TableRef, size).SelectMany(c => JevTableBatch.Fit(c, Build)).ToList();
        }

        /// <summary>Whether Jev's pick (rather than the principled rule) decides the column.</summary>
        internal static bool Decides(double confidence, double bar) => confidence >= bar;

        // ---- the principled rule (rules.mjs fairRuleSummarize and its name tests, ported) -------------------------

        private static readonly System.Text.RegularExpressions.Regex NonAdditive = new System.Text.RegularExpressions.Regex(
            @"(?i)(\b(year|month|quarter|week|day|age|rank|order|sort|sequence|latitude|longitude|lat|lon|lng)$|\b(month|quarter|week|day)\s*(number|num|no)\b|\b(monthnum|monthnumber|weeknum|weeknumber|quarternum|daynum|daynumber)\b|\bpin(?:\s*code)?$|\bpostal\s*code\b|\bpostcode\b|\bzip\s*code\b|\bzip$|\b(?:(?<!\btotal\s)(?<!\bcount\s)(?<!\bunits\s)number|code|id|key)$)");
        private static readonly System.Text.RegularExpressions.Regex CamelId = new System.Text.RegularExpressions.Regex(@"[a-z0-9](ID|Id|Key|SK|Code|No|Num|Number|Year|Month|Quarter|Week|Level)$");
        private static readonly System.Text.RegularExpressions.Regex IdLead = new System.Text.RegularExpressions.Regex(@"^I[dD]s?(?=[A-Z_\s]|$)");
        private static readonly System.Text.RegularExpressions.Regex IdOther = new System.Text.RegularExpressions.Regex(@"(?i)([_\s\-.](id|key|code|no|num)$|^(id|cod|codigo|c\u00f3digo|clave|num)\b|\b(level|tier|grade)$)");
        private static readonly System.Text.RegularExpressions.Regex AmountYear = new System.Text.RegularExpressions.Regex(@"(?i)(last|prior|previous|this|next|current)[\s_]*year$");
        private static readonly System.Text.RegularExpressions.Regex PeriodName = new System.Text.RegularExpressions.Regex(@"(?i)(year|yr|month|mth|quarter|qtr|week|wk|day|period|fiscal|semester|half)(?!s)");
        private static readonly HashSet<string> CountWords = new HashSet<string>(StringComparer.Ordinal) { "count", "counts", "total", "totals", "qty", "quantity", "quantities", "units", "sum" };
        private static readonly HashSet<string> RateWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "rate", "rates", "ratio", "ratios", "pct", "percent", "percentage", "%", "share", "growth", "avg", "average", "mean",
            "score", "scores", "rating", "ratings", "price", "prices", "precio", "preis", "prix", "temperature", "temp",
            "probability", "yield", "efficiency", "utilization", "utilisation", "index", "height", "weight", "bmi", "speed",
            "pressure", "humidity", "concentration", "satisfaction", "involvement", "happy", "happiness", "feeling", "tenure",
            "experience", "years",
        };
        private static readonly HashSet<string> LabelWords = new HashSet<string>(StringComparer.Ordinal) { "coded", "code", "phone", "npi", "offset", "date" };

        // A yes-or-no flag reads "Is Weekend", "Has Discount" or "Weekend Flag"; a line or ordinal number ends in "Line",
        // "Seq", "Position", "Ordinal" or "Step". The endings the replay's rule already caught (Rank, Order, Sort,
        // Sequence, Number, No) stay with it.
        private static readonly HashSet<string> FlagFirst = new HashSet<string>(StringComparer.Ordinal) { "is", "has", "was", "were", "can", "should", "did", "does", "flag" };
        private static readonly HashSet<string> FlagLast = new HashSet<string>(StringComparer.Ordinal) { "flag", "flags", "indicator", "yn", "bool", "boolean" };
        private static readonly HashSet<string> OrdinalLast = new HashSet<string>(StringComparer.Ordinal) { "line", "seq", "position", "pos", "ordinal", "step" };

        /// <summary>A yes-or-no flag, a line number or another ordinal, by its name. Its total means nothing, so it is
        /// never summed (coordinator's ruling, 26 September): not by the principled rule and not by Jev.</summary>
        internal static bool FlagOrOrdinal(string name)
        {
            var w = Words(name);
            if (w.Length == 0) return false;
            return (w.Length > 1 && FlagFirst.Contains(w[0])) || FlagLast.Contains(w[w.Length - 1]) || OrdinalLast.Contains(w[w.Length - 1]);
        }

        internal static string[] Words(string name)
        {
            var n = System.Text.RegularExpressions.Regex.Replace(name ?? "", "([a-z0-9])([A-Z])", "$1 $2");
            n = System.Text.RegularExpressions.Regex.Replace(n, "([A-Z]+)([A-Z][a-z])", "$1 $2");
            return System.Text.RegularExpressions.Regex.Split(n.ToLowerInvariant(), @"[^a-z0-9%\u00c0-\u024f]+").Where(w => w.Length > 0).ToArray();
        }

        private static bool LabelishName(string name)
        {
            var s = (name ?? "").Trim();
            if (AmountYear.IsMatch(s)) return false;
            var w = Words(s);
            if (w.Any(LabelWords.Contains) || (w.Length > 0 && w.Length <= 2 && new[] { "x", "y", "z", "long" }.Contains(w[w.Length - 1]))) return true;
            if (w.Length > 1 && (w[0] == "min" || w[0] == "max")) return true;
            return NonAdditive.IsMatch(s) || CamelId.IsMatch(s) || IdLead.IsMatch(s) || IdOther.IsMatch(IdLead.Replace(s, "\u0000"));
        }

        /// <summary>Never sum keys, ids, years, period numbers or row ids; sum counts and totals; average rates, ratios,
        /// prices and measurements; sum the rest. Returns an option id. The replay's rule line for line, plus one guard
        /// it did not have: a flag, a line number or another ordinal is never summed (<see cref="FlagOrOrdinal"/>).</summary>
        internal static string PrincipledRule(Ask a)
        {
            var name = a.ColumnName ?? "";
            var m = a.Measured;
            if (!string.Equals(a.Role ?? "none", "none", StringComparison.Ordinal) || a.IsKey || a.KeyPart) return "none";
            if (LabelishName(name)) return "none";
            if (m != null && m.LooksLikeYear) return "none";
            if (m != null && m.LooksLikePeriodNumber && PeriodName.IsMatch(name)) return "none";
            if (m != null && m.UniqueOnEveryRow && m.WholeNumbers) return "none";
            if (FlagOrOrdinal(name)) return "none";
            var w = Words(name);
            var joined = string.Join(" ", w);
            if (w.Any(CountWords.Contains) || joined.Contains("number of")) return "sum";
            if (w.Any(RateWords.Contains) || name.Contains("%")
                || new[] { "per unit", "per person", "per capita", "unit cost" }.Any(joined.Contains)) return "average";
            return "sum";
        }

        internal static void Map(Ask ask, DecisionAnswers answers, double bar, List<ChangeItem> lines, List<ModelPlanChangeRefusal> refused = null)
        {
            if (ask == null || answers == null || !answers.Decided) return;
            var answer = answers.For(Question);
            if (answer == null) return;
            Map(ask, answer.Choice, answer.Confidence, bar, lines, refused);
        }

        /// <summary>One column's lines. Pure. The column is set not to add up (unless it already does not), and the base
        /// measure decided (Jev's sure pick, else the principled rule) is created with its format and description, unless
        /// the pick is none, the model already has that measure, or its name is taken.</summary>
        internal static void Map(Ask ask, string pick, double confidence, double bar, List<ChangeItem> lines,
                                 List<ModelPlanChangeRefusal> refused = null)
        {
            if (ask == null) return;
            var chosen = Choose(ask, pick, confidence, bar);
            if (chosen.FlagGuard)
                refused?.Add(new ModelPlanChangeRefusal
                {
                    Target = ask.Ref,
                    Reason = ask.ColumnName + " is a yes-or-no flag or a line number, and adding those up means nothing, so Jev's pick of a total was left out.",
                });
            if (chosen.KeyGuard)
                refused?.Add(new ModelPlanChangeRefusal
                {
                    Target = ask.Ref,
                    Reason = ask.ColumnName + " is part of a key or a link between tables, so Jev's pick of a total or count was left out.",
                });
            var sure = chosen.Sure;
            pick = chosen.Pick;
            if (string.IsNullOrEmpty(pick) || !DecisionRegistry.SummarizeOptions.Contains(pick)) return;   // never offered

            // A measure the pick needs but that cannot be made leaves the column exactly as it is: switching it off with
            // no measure doing its adding would take a number away from people, the opposite of Kane's ruling.
            if (pick != StarterMeasureTemplates.None && !ask.Existing.ContainsKey(pick) && ask.Taken.TryGetValue(pick, out var taken))
            {
                refused?.Add(new ModelPlanChangeRefusal { Target = ask.Ref, Reason = taken });
                return;
            }

            var itemId = JevFamily.ItemId(Name, ask.TableRef);
            if (!ask.KeepsSummarize && !string.Equals(ask.Current, nameof(AggregateFunction.None), StringComparison.Ordinal))
            {
                var off = ask.ColumnName + " stops adding up by itself on a chart. Good practice for Copilot is a measure that does the adding instead.";
                lines.Add(new ChangeItem
                {
                    ObjectRef = ask.Ref, ObjectName = ask.ColumnName, Kind = "set_summarize_by", Source = JevFamily.Source,
                    Category = "Formatting", Severity = "Info", Risk = "safe", Target = "summarizeBy",
                    Title = "Set to Don't summarize", Rationale = off, Before = ask.Current ?? "Default", After = nameof(AggregateFunction.None),
                    Status = "approved", JevItem = itemId, DecisionPoint = DecisionRegistry.ColumnSummarize, RuleId = OffRuleId,
                    Reason = off,
                    CheckText = "Power BI lets a number column be set to not add up (Summarize by: None). The AI readiness checks ask for this.",
                });
            }

            if (pick == StarterMeasureTemplates.None) return;
            if (ask.Existing.TryGetValue(pick, out var existing))
            {
                refused?.Add(new ModelPlanChangeRefusal
                {
                    Target = ask.Ref,
                    Reason = existing + " already gives " + StarterMeasureTemplates.Plain(pick, ask.ColumnName) + ", so no second measure was proposed.",
                });
                return;
            }

            var name = ask.NameOf[pick];
            var measureRef = StarterMeasuresFamily.MeasureRef(ask.TableName, name);
            var dax = StarterMeasureTemplates.Dax(pick, ask.TableName, ask.ColumnName);
            var reason = MeasureReason(pick, ask.ColumnName);
            var format = FormatFor(pick, ask);
            // THE LINES FOLLOW THE MEASURE. Each line has its own Drop, so each says which line it cannot stand without
            // (ChangeItem.Needs): the measure's format, description and words need the measure; hiding the column needs
            // the measure that does its adding; taking it out of Excel needs it hidden. Dropping a line drops what needs
            // it, and keeping one keeps what it needs (LocalEngine.SetPlanItemAsync), so no approval can hide a column
            // with no measure, or take a visible column out of Excel.
            var needsMeasure = JevFamily.NeedsKey("create_measure", measureRef);
            var created = new[]
            {
                (Kind: "create_measure", Target: "expression", Title: "Add as " + StarterMeasureTemplates.Plain(pick, ask.ColumnName), After: dax,
                 Check: name + " is a new name in this model, no measure there computes the same thing, and its formula passes the model's DAX check."),
                (Kind: "set_measure_format", Target: "formatString", Title: "Format as " + format, After: format,
                 Check: pick == StarterMeasureTemplates.Count || pick == StarterMeasureTemplates.DistinctCount
                     ? "A count is a whole number, so it shows no decimal places."
                     : string.Equals(ask.ColumnFormat, format, StringComparison.Ordinal) ? ask.ColumnName + " already shows as " + format + ", and the measure takes the same format."
                     : "A thousands separator, and one decimal place when the values are not whole."),
                (Kind: "set_display_folder", Target: "displayFolder", Title: "Put in the " + ask.Folder + " folder", After: ask.Folder,
                 Check: ask.Folder + " is the folder most of this table's measures already sit in."),
                (Kind: "set_description", Target: "description", Title: "Describe it", After: Description(pick, BaseMeasures.Humanize(ask.ColumnName), ask.TableName),
                 Check: "Semanticus wrote this description from the measure's own formula."),
            }.ToList();
            // A new measure is visible by default, but a hidden table still hides it from Copilot, and set_synonyms refuses it.
            if (string.IsNullOrWhiteSpace(ask.Folder)) created.RemoveAll(c => c.Kind == "set_display_folder");
            if (ask.HasLinguistic && !ask.TableHidden)
                created.Add((Kind: "set_synonyms", Target: "synonyms", Title: "Give it the words people use", After: Synonyms(pick, ask.ColumnName),
                             Check: "Semanticus made these words from the measure's own name, so Copilot knows other ways people ask for it."));
            foreach (var c in created)
                lines.Add(new ChangeItem
                {
                    ObjectRef = measureRef, ObjectName = name, Kind = c.Kind, Source = JevFamily.Source, Category = "Measures",
                    Severity = "Info", Risk = "safe", Target = c.Target, Title = c.Title, Rationale = reason,
                    Before = "(none)", After = c.After, Status = "approved", JevItem = itemId,
                    DecisionPoint = DecisionRegistry.ColumnSummarize,
                    DecisionConfidence = sure ? Math.Round(confidence, 4) : (double?)null,
                    DecisionWord = sure ? JevFamily.ConfidenceWord(confidence, bar) : null,
                    RuleId = sure ? null : PrincipledRuleId,
                    Reason = reason, CheckText = c.Check,
                    Needs = c.Kind == "create_measure" ? null : needsMeasure,
                    // Marks the new measure as added on purpose, so the cleanup never offers it back as unused.
                    Args = c.Kind == "create_measure"
                        ? new Dictionary<string, string>(StringComparer.Ordinal) { ["addedOnPurpose"] = "the measure for " + ask.ColumnName + " in " + ask.TableName }
                        : null,
                });

            // The measure now does the column's adding, so the column is hidden as good practice asks (BPA "Hide fact table
            // columns"). A person can drop this line and keep the column in view.
            if (!ask.IsHidden && !ask.KeepsVisibility)
            {
                var hide = name + " now does the adding up for " + ask.ColumnName + ", so the column is hidden and people pick the measure instead, as good practice asks.";
                lines.Add(new ChangeItem
                {
                    ObjectRef = ask.Ref, ObjectName = ask.ColumnName, Kind = "set_column_hidden", Source = JevFamily.Source,
                    Category = "Visibility", Severity = "Info", Risk = "safe", Target = "isHidden", Title = "Hide the column",
                    Rationale = hide, Before = "Visible", After = "Hidden", Status = "approved", JevItem = itemId,
                    DecisionPoint = DecisionRegistry.ColumnSummarize, RuleId = HideRuleId, Reason = hide,
                    CheckText = "A hidden column still feeds its measure; it only leaves the field list.",
                    Needs = needsMeasure,
                });
                // A hidden column nothing slices by needs no Excel/MDX attribute (BPA "Set IsAvailableInMdx to false").
                if (ask.MdxCanGoOff && !ask.KeepsExcel)
                {
                    var mdx = name + " now does the adding up for " + ask.ColumnName + " and the column is hidden, so Excel no longer needs to slice by it; turning that off saves memory.";
                    lines.Add(new ChangeItem
                    {
                        ObjectRef = ask.Ref, ObjectName = ask.ColumnName, Kind = "set_is_available_in_mdx", Source = JevFamily.Source,
                        Category = "Performance", Severity = "Info", Risk = "safe", Target = "isAvailableInMdx", Title = "Leave it out of Excel pivot tables",
                        Rationale = mdx, Before = "true", After = "false", Status = "approved", JevItem = itemId,
                        DecisionPoint = DecisionRegistry.ColumnSummarize, RuleId = HideRuleId, Reason = mdx,
                        CheckText = "No column sorts by it or is sorted by it, and no hierarchy or variation uses it.",
                        Needs = JevFamily.NeedsKey("set_column_hidden", ask.Ref),
                    });
                }
            }
        }

        /// <summary>The measure's format. A count is a whole number. Otherwise a money or percent format the column already
        /// has is kept, and anything else gets a thousands separator with no decimals for whole values and one for the
        /// rest: the only plain formats BPA's "Whole numbers should be formatted" rule accepts.</summary>
        internal static string FormatFor(string pick, Ask ask)
        {
            if (pick == StarterMeasureTemplates.Count || pick == StarterMeasureTemplates.DistinctCount) return StarterMeasureTemplates.CountFormat;
            if (ask.ColumnFormat != null && (ask.ColumnFormat.Contains('$') || ask.ColumnFormat.Contains('%'))) return ask.ColumnFormat;
            return ask.Whole && pick != StarterMeasureTemplates.Average ? StarterMeasureTemplates.CountFormat : "#,0.0";
        }

        private static string MeasureReason(string pick, string column)
        {
            switch (pick)
            {
                case "sum": return column + " is an amount or a quantity, so its measure adds it up.";
                case "average": return column + " is a rate, a price or a measurement, so its measure takes the average: its total means nothing.";
                case "count": return "People ask how many rows have " + column + ", so its measure counts them.";
                case "distinctcount": return "People ask how many different " + column + " values there are, so its measure counts each one once.";
                case "min": return "The lowest " + column + " is what people ask about, so its measure takes it.";
                default: return "The highest " + column + " is what people ask about, so its measure takes it.";
            }
        }

        /// <summary>Other words for the measure, made by the engine from its aggregation and its column's name.</summary>
        internal static string Synonyms(string pick, string column)
        {
            var w = BaseMeasures.Humanize(column).ToLowerInvariant();
            switch (pick)
            {
                case "sum": return "total " + w + ",sum of " + w;
                case "average": return "avg " + w + ",mean " + w;
                case "min": return "lowest " + w + ",min " + w;
                case "max": return "highest " + w + ",max " + w;
                case "count": return "number of " + w + "," + w + " count";
                default: return "number of different " + w + ",distinct " + w + " count";
            }
        }

        /// <summary>The measure's description, written by the engine from its own formula.</summary>
        internal static string Description(string pick, string column, string table)
        {
            switch (pick)
            {
                case "sum": return "The total of " + column + " in " + table + ".";
                case "average": return "The average " + column + " in " + table + ".";
                case "min": return "The lowest " + column + " in " + table + ".";
                case "max": return "The highest " + column + " in " + table + ".";
                case "count": return "How many rows in " + table + " have a " + column + ".";
                default: return "How many different " + column + " values " + table + " has.";
            }
        }

        /// <summary>The first "why" line of a row.</summary>
        internal static string Missing(int n, string table) =>
            "Number columns in " + table + " add themselves up when you put them on a chart. Copilot works better when "
            + "a measure does the adding instead. So each column is set to stop adding up, and gets a measure where it needs one.";

        /// <summary>A row's title from its lines: the measures it adds, or the columns it stops adding up. Only the lines
        /// still kept count, so dropping one of two measures reads "Add 1 measure"; a row with every line dropped is named
        /// for all of them, since it still needs a name.</summary>
        internal static string Title(IReadOnlyCollection<ChangeItem> lines, string table)
        {
            var kept = lines.Where(l => !string.Equals(l.Status, "rejected", StringComparison.OrdinalIgnoreCase)).ToList();
            IReadOnlyCollection<ChangeItem> counted = kept.Any(l => l.Kind == "create_measure" || l.Kind == "set_summarize_by") ? kept : lines;
            var measures = counted.Where(l => l.Kind == "create_measure").Select(l => l.ObjectRef).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var columns = counted.Where(l => l.Kind == "set_summarize_by").Select(l => l.ObjectRef).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return measures > 0
                ? "Add " + JevFamily.Count(measures, "measure", "measures") + " for the numbers in " + table
                : "Set " + JevFamily.Count(columns, "column", "columns") + " to Don't summarize in " + table;
        }

        /// <summary>Who chose what, in one sentence. The None lines are good practice applied to every column, so they
        /// are neither Jev's nor the rule standing in for Jev.</summary>
        internal static string WhoChose(int byJev, int ruled) =>
            byJev > 0 && ruled == 0 ? "Jev chose each measure from a fixed list using the names, types and facts worked out on this computer. "
            : byJev == 0 && ruled > 0 ? "Semanticus's own rule chose each measure, because Jev was not sure of them. "
            : byJev > 0 ? "Jev chose " + byJev.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " of these measures from a fixed list using the names, types and facts worked out on this computer. Semanticus's own rule chose the other "
                          + ruled.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + ", because Jev was not sure of " + (ruled == 1 ? "it" : "them") + ". "
            : "";
    }

    /// <summary>The model's own base measures: which aggregation of which column a measure already computes, and how
    /// the model names each aggregation, so a new measure is never a second copy and reads like its neighbours.
    ///
    /// BY MEANING, NOT BY TEXT. A formula is read through the DAX lexer the rest of the engine uses (DaxDependencyHelper,
    /// from the vendored TOMWrapper), so comments, spacing, case and quoting never matter, and the spellings that compute
    /// exactly the same total are one thing: a CALCULATE around the aggregation with no filter (a measure is already
    /// evaluated in its own filter context, so it changes nothing), redundant brackets, and the iterator form over the
    /// column's own table (SUMX ( Sales, Sales[Amount] ) is SUM ( Sales[Amount] ); DAX defines SUM that way). Anything
    /// else, a filter, a second column, an expression, is a different measure and never blocks a new one.</summary>
    internal sealed class BaseMeasures
    {
        private readonly Dictionary<string, string> _existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _pattern = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string Key(string function, string table, string column) => function + "\u001f" + table + "\u001f" + column;

        internal static BaseMeasures Read(Model m)
        {
            var r = new BaseMeasures();
            var patterns = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in m.AllMeasures.OrderBy(x => x.Table?.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var meaning = Meaning(x.Expression, x.Table?.Name);
                if (meaning == null) continue;
                var (function, table, column) = meaning.Value;
                var key = Key(function, table, column);
                if (!r._existing.ContainsKey(key)) r._existing[key] = x.Name;
                // A name that carries its table ("Total Sales Quantity", as NameApart makes) teaches the pattern around
                // both, so the next table's measure is not named after this one's table.
                // A name that is only its table and column ("Total Cost" for Total[Cost]) is read by its column, or it would
                // teach a bare "{0}".
                var span = Humanize(table) + " " + Humanize(column);
                var at = x.Name.IndexOf(span, StringComparison.OrdinalIgnoreCase);
                if (at >= 0 && x.Name.Trim().Length == span.Length) at = -1;
                if (at < 0) { span = column; at = x.Name.IndexOf(span, StringComparison.OrdinalIgnoreCase); }
                if (at < 0) continue;
                var pattern = x.Name.Substring(0, at) + "{0}" + x.Name.Substring(at + span.Length);
                if (!patterns.TryGetValue(function, out var counts)) patterns[function] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
                counts[pattern] = counts.TryGetValue(pattern, out var n) ? n + 1 : 1;
            }
            foreach (var (function, counts) in patterns)
                r._pattern[function] = counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
            return r;
        }

        /// <summary>The plain aggregation a formula computes, as (SUM | AVERAGE | MIN | MAX | COUNT | DISTINCTCOUNT, table,
        /// column), or null when it computes anything else. <paramref name="ownTable"/> is the measure's table, which a
        /// bare [Column] belongs to.</summary>
        internal static (string Function, string Table, string Column)? Meaning(string expression, string ownTable)
        {
            IList<TabularEditor.TOMWrapper.Utils.DaxToken> all;
            try { all = TabularEditor.TOMWrapper.Utils.DaxDependencyHelper.Tokenize(expression ?? "", includeHidden: false); }
            catch { return null; }
            var t = all.ToList();
            return Read(t, 0, t.Count, ownTable, null);
        }

        private const int Open = TabularEditor.TOMWrapper.Utils.DaxToken.OPEN_PARENS;
        private const int Close = TabularEditor.TOMWrapper.Utils.DaxToken.CLOSE_PARENS;
        private const int Comma = TabularEditor.TOMWrapper.Utils.DaxToken.COMMA;

        private static readonly Dictionary<int, string> Plain = new Dictionary<int, string>
        {
            [TabularEditor.TOMWrapper.Utils.DaxToken.SUM] = "SUM",
            [TabularEditor.TOMWrapper.Utils.DaxToken.AVERAGE] = "AVERAGE",
            [TabularEditor.TOMWrapper.Utils.DaxToken.MIN] = "MIN",
            [TabularEditor.TOMWrapper.Utils.DaxToken.MAX] = "MAX",
            [TabularEditor.TOMWrapper.Utils.DaxToken.COUNT] = "COUNT",
            [TabularEditor.TOMWrapper.Utils.DaxToken.DISTINCTCOUNT] = "DISTINCTCOUNT",
        };

        // The iterator that computes the same thing when its expression is just the column, over the column's own table.
        private static readonly Dictionary<int, string> Iterator = new Dictionary<int, string>
        {
            [TabularEditor.TOMWrapper.Utils.DaxToken.SUMX] = "SUM",
            [TabularEditor.TOMWrapper.Utils.DaxToken.AVERAGEX] = "AVERAGE",
            [TabularEditor.TOMWrapper.Utils.DaxToken.MINX] = "MIN",
            [TabularEditor.TOMWrapper.Utils.DaxToken.MAXX] = "MAX",
            [TabularEditor.TOMWrapper.Utils.DaxToken.COUNTX] = "COUNT",
        };

        /// <summary>Tokens [from, to) as one aggregation. <paramref name="rowTable"/> is the iterator's table, when inside one.</summary>
        private static (string Function, string Table, string Column)? Read(List<TabularEditor.TOMWrapper.Utils.DaxToken> t, int from, int to, string ownTable, string rowTable)
        {
            // Brackets around the whole thing change nothing.
            while (to - from >= 2 && t[from].Type == Open && Closing(t, from, to) == to - 1) { from++; to--; }
            if (to - from < 3 || t[from + 1].Type != Open || Closing(t, from + 1, to) != to - 1) return null;
            var head = t[from].Type;
            var args = Arguments(t, from + 2, to - 1);
            if (args == null) return null;

            // CALCULATE with no filter argument is the expression itself inside a measure.
            if (head == TabularEditor.TOMWrapper.Utils.DaxToken.CALCULATE)
                return args.Count == 1 ? Read(t, args[0].From, args[0].To, ownTable, rowTable) : null;

            if (Plain.TryGetValue(head, out var function))
            {
                if (args.Count != 1) return null;
                var col = Column(t, args[0].From, args[0].To, rowTable ?? ownTable);
                return col == null ? null : (function, col.Value.Table, col.Value.Column);
            }

            if (Iterator.TryGetValue(head, out var same))
            {
                if (args.Count != 2 || args[0].To - args[0].From != 1) return null;
                var over = TableName(t[args[0].From]);
                if (over == null) return null;
                var col = Column(t, args[1].From, args[1].To, over);
                if (col == null || !string.Equals(col.Value.Table, over, StringComparison.OrdinalIgnoreCase)) return null;
                return (same, col.Value.Table, col.Value.Column);
            }
            return null;
        }

        /// <summary>Where the bracket opened at <paramref name="open"/> closes, or -1.</summary>
        private static int Closing(List<TabularEditor.TOMWrapper.Utils.DaxToken> t, int open, int to)
        {
            var depth = 0;
            for (var i = open; i < to; i++)
            {
                if (t[i].Type == Open) depth++;
                else if (t[i].Type == Close && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>The top-level arguments of tokens [from, to), split at commas outside brackets; null when one is empty.</summary>
        private static List<(int From, int To)> Arguments(List<TabularEditor.TOMWrapper.Utils.DaxToken> t, int from, int to)
        {
            var list = new List<(int From, int To)>();
            var depth = 0;
            var start = from;
            for (var i = from; i < to; i++)
            {
                if (t[i].Type == Open) depth++;
                else if (t[i].Type == Close) depth--;
                else if (t[i].Type == Comma && depth == 0) { list.Add((start, i)); start = i + 1; }
            }
            list.Add((start, to));
            return list.Any(a => a.To <= a.From) ? null : list;
        }

        /// <summary>'Table'[Column], Table[Column] or a bare [Column] (of <paramref name="bareTable"/>).</summary>
        private static (string Table, string Column)? Column(List<TabularEditor.TOMWrapper.Utils.DaxToken> t, int from, int to, string bareTable)
        {
            var n = to - from;
            var last = t[to - 1];
            if (last.Type != TabularEditor.TOMWrapper.Utils.DaxToken.COLUMN_OR_MEASURE || n > 2) return null;
            // The lexer hands a column or table token back already unwrapped: no brackets or quotes, escapes resolved
            // (AntlrGrammars/DAXLexer.g4, TABLE and COLUMN_OR_MEASURE).
            var column = last.Text ?? "";
            if (column.Length == 0) return null;
            var table = n == 2 ? TableName(t[from]) : bareTable;
            return table == null ? null : (table, column);
        }

        private static string TableName(TabularEditor.TOMWrapper.Utils.DaxToken token)
        {
            var text = token.Text ?? "";
            return text.Length > 0 && (token.Type == TabularEditor.TOMWrapper.Utils.DaxToken.TABLE
                                       || token.Type == TabularEditor.TOMWrapper.Utils.DaxToken.TABLE_OR_VARIABLE) ? text : null;
        }

        /// <summary>The measure that already computes this aggregation of this column, whatever its name, or null.</summary>
        internal string Existing(string option, string table, string column)
        {
            var function = StarterMeasureTemplates.Function(option);
            return function != null && _existing.TryGetValue(Key(function, table, column), out var name) ? name : null;
        }

        /// <summary>The new measure's name: the model's own pattern for this aggregation when it has one, else the
        /// engine's (StarterMeasureTemplates.Name), with the column's name in words ("List Price", not "ListPrice"), so
        /// the name reads the way the readiness rule NAME-MEASURE asks. With <paramref name="table"/>, the table's name
        /// comes first ("Returns Quantity"), for a name two tables' columns would otherwise share.</summary>
        internal string NameFor(string option, string column, string table = null)
        {
            var function = StarterMeasureTemplates.Function(option);
            var words = table == null ? Humanize(column) : Humanize(table) + " " + Humanize(column);
            var name = function != null && _pattern.TryGetValue(function, out var pattern)
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, pattern.Replace("{", "{{").Replace("}", "}}").Replace("{{0}}", "{0}"), words)
                : StarterMeasureTemplates.Name(option, words);
            // A column already named "Total Sales" must not become "Total Total Sales" (Kane, 11 Oct).
            return ColumnSummarizeFamily.Plainer(name);
        }

        private static readonly Regex Hump = new Regex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.CultureInvariant);

        /// <summary>A column name in words: camel humps and underscores become spaces ("NetSalesAmount" to "Net Sales
        /// Amount", "unit_cost" to "unit cost"). A name that already has spaces keeps them.</summary>
        internal static string Humanize(string column)
        {
            var s = (column ?? "").Replace('_', ' ').Trim();
            if (s.Contains(' ')) return Regex.Replace(s, @"\s+", " ");
            return Regex.Replace(Hump.Replace(s, " "), @"\s+", " ").Trim();
        }
    }
}
