// ask_data on the engine: one question about the model's numbers, answered with one fixed query (Kane, 26 Sep).
//
// Both doors reach this one method: the MCP op ask_data (the person's own assistant) and the Studio's askData RPC.
//
// The order of work, and why:
//   1. The agent door's data gate first. Matching reads the model's values and the answer returns rows, so an agent
//      call is held to the same QueryData rule as run_dax and preview_table before anything is read.
//   0. Pro. Kane, 26 Sep: "Anything involving Jev is always Pro only." The whole feature is Pro, the parts builder
//      too, so a free session is refused before anything is read, matched or sent.
//   2. The parts. Given parts are taken as said ("you"): that is the builder, and how a reading is edited and run
//      again without Jev. Words are read by Jev ("jev") while JevQueryReader.JevReads is true (Kane, 28 Sep) and the
//      model's query.summarize switch is on.
//      There is no word-match fallback: a question Jev did not read is handed back to be settled by hand.
//   3. The checks, before anything runs: every column reaches every measure through active relationships, every
//      filter value is really in the data, the DAX checks out (validate_dax), and the run stays inside a row cap.
//   3b. The reading Jev settled is checked last (JevQueryCheck, the ruling "Jev data questions v2", 29 Sep): does it give
//      the numbers the question asks for? Since K16 (Kane, 8 Oct) it no longer stops the run: a gap it names is said beside
//      the number, as the answer's one assumption line.
//   Ambiguity never stops an answer (K16). Where Jev was not sure, or a part cannot apply (a time the model cannot read, a
//   column the number does not reach), the most likely reading runs and DataAnswer.Assumption says what was assumed, with
//   each part one ask from being changed (Others). What still stops: a question the model surely does not hold (it names the
//   closest numbers), filters that leave nothing, a query that does not check out, and a query that failed.
//   4. The run, and the answer: what it shows in one sentence, a plain reading line, the DAX, the checks, the rows.
// Nothing here writes to the model. Jev writes no text: the reading, the DAX and every line are built here.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Query;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>A test's own answer to "does this build let Jev read words" (JevQueryReader.JevReads). Null on every
        /// real path.</summary>
        internal bool? JevReadsDataForTest;

        private bool JevReadsData => JevReadsDataForTest ?? JevQueryReader.JevReads;

        internal const string NotYetNote = "Reading a question from your words is not switched on in this version yet. Choose the numbers and the columns instead, and Semanticus builds the query.";
        internal const string SwitchedOffNote = "Jev reading your words is switched off for this model. Switch it on under What Pro decides, on the Pro page, or choose the numbers and the columns instead.";
        internal const string NoDataNote = "Connect a copy of this model that holds data. The words are matched against its values, and the query runs on it.";

        /// <summary>The one plain line a failed run reads. The server's own words are NOT in it: they go once into
        /// DataAnswer.Error, which the page puts behind Show details (verify-askdata-r5, r5-062, where the raw text
        /// was printed twice under a green "The query checks out against the model.").</summary>
        // Both doors read this line, and only the page has a Show details button, so it names none (verify-askdata-r6
        // L5). The page draws the button beside it; the assistant door has the server's words in 'error'.
        internal const string QueryDidNotRunLine = "The query did not run on the model it was sent to.";

        private readonly object _queryValuesGate = new object();
        private (LiveConnection Live, string SessionId, DateTime At, Dictionary<string, object[]> Values) _queryValues;

        /// <summary>How long the values read for matching are reused before they are read again.</summary>
        private static readonly TimeSpan QueryValuesLife = TimeSpan.FromMinutes(10);

        /// <summary>Every query the matcher and the run sent, for the tests.</summary>
        internal Action<string> QuerySentForTest;

        /// <summary>The longest question read, in characters: the same bound a described task has.</summary>
        public const int MaxQuestionChars = 100_000;

        public async Task<DataAnswer> AskDataAsync(DataAsk ask, string origin = "human")
        {
            RequireProFeature();
            ask ??= new DataAsk();
            var s = _sessions.Require();
            var answer = await AnswerAsync(ask, origin, s).ConfigureAwait(false);
            // Every outcome carries what was asked. A question refused before its parts were settled (a value that
            // is not in the data, a filter with no model holding data, the agent door held for approval) used to
            // come back with no parts, so its history row said "a question that was not run" and could not be
            // asked again. The parts as given are the question; the settled ones replace them when there are any.
            answer.Parts ??= AsAsked(ask);
            answer.QuestionId = RecordDataQuestion(s, ask, answer, origin);
            PublishAskAnswer(ask, answer, origin);
            return answer;
        }

        /// <summary>The given parts, as they were asked, or null for a question in words only. The run switch and
        /// the row cap go with them, capped the way the answer caps them.</summary>
        private static DataAsk AsAsked(DataAsk ask) => !ask.HasParts ? null : new DataAsk
        {
            MaxRows = Math.Max(1, Math.Min(ask.MaxRows <= 0 ? QueryTemplate.DefaultRowCap : ask.MaxRows, QueryTemplate.MaxRowCap)),
            Run = ask.Run,
            GroupBy = (ask.GroupBy ?? Array.Empty<string>()).ToArray(),
            Measures = (ask.Measures ?? Array.Empty<string>()).ToArray(),
            Filters = (ask.Filters ?? Array.Empty<DataAskFilter>()).Where(f => f != null)
                .Select(f => new DataAskFilter { Column = f.Column, Op = f.Op, Values = (f.Values ?? Array.Empty<string>()).ToArray() }).ToArray(),
            Top = ask.Top,
            TopBy = ask.TopBy,
            Lowest = ask.Lowest,
            SortBy = ask.SortBy,
            Descending = ask.Descending,
        };

        private async Task<DataAnswer> AnswerAsync(DataAsk ask, string origin, Session s)
        {
            var cat = await s.ReadAsync(m => QueryCatalog.Read(m)).ConfigureAwait(false);
            var cap = Math.Max(1, Math.Min(ask.MaxRows <= 0 ? QueryTemplate.DefaultRowCap : ask.MaxRows, QueryTemplate.MaxRowCap));
            var live = _live;
            var hasWords = !string.IsNullOrWhiteSpace(ask.Question);

            if (!ask.HasParts && !hasWords)
                return new DataAnswer { Outcome = DataAnswer.NeedsYou, Unsure = new[] { "Type a question, or choose a number to see." } };
            if (hasWords && ask.Question.Length > MaxQuestionChars)
                return new DataAnswer
                {
                    Outcome = DataAnswer.Refused,
                    Checks = new[] { new DataCheck { Name = "question", Passed = false, Line = $"The question is longer than {MaxQuestionChars.ToString("N0", CultureInfo.InvariantCulture)} characters. Shorten it and ask again." } },
                };

            if (live != null)
            {
                var gate = GuardAgent(AgentCapability.QueryData, live.DataSource, live.Database, origin, isCommit: true,
                    summary: $"answer a data question from {(string.IsNullOrEmpty(live.Database) ? live.DataSource : live.Database + " on " + live.DataSource)}",
                    approvalId: out var approvalId, intentBasis: "querydata", consumeGrant: false);
                if (gate != null) return new DataAnswer { Outcome = DataAnswer.Refused, Error = gate, ApprovalId = approvalId };
            }

            JevQueryReader.Reading reading;
            var answer = new DataAnswer();
            var checks = new List<DataCheck>();

            var notes = new List<string>();
            // What is said about a number is what the model that RUNS the query holds (Sol 3.7): the copy open here may have unpublished
            // edits, and the query names a measure the connected model evaluates by its own formula. Read once the words are allowed to go
            // on (below), so a question stopped at a switch sends nothing.
            ConnectedFormulas connected = null;
            if (ask.HasParts)
            {
                if (live != null) connected = await AlignWithConnectedAsync(cat, live).ConfigureAwait(false);
                answer.ReadBy = "you";
                reading = await PartsAsync(ask, cat, live, checks, notes).ConfigureAwait(false);
                if (reading == null) return Finish(answer, DataAnswer.Refused, checks);
            }
            else
            {
                if (live == null) return new DataAnswer { Outcome = DataAnswer.NeedsYou, Note = NoDataNote, Unsure = new[] { NoDataNote } };
                if (!JevReadsData) return new DataAnswer { Outcome = DataAnswer.NeedsYou, Note = NotYetNote, Unsure = new[] { NotYetNote } };
                // The runtime switch, checked before any value is read, so off means nothing is read, matched or sent.
                if (!DecisionPointEnabled(DecisionRegistry.QuerySummarize))
                    return new DataAnswer { Outcome = DataAnswer.NeedsYou, Note = SwitchedOffNote, Unsure = new[] { SwitchedOffNote } };
                connected = await AlignWithConnectedAsync(cat, live).ConfigureAwait(false);

                // "How many customers bought twice" is a count of customers by their order count: the one query shape cannot say it,
                // and the nearest part (Order Count as one total) answers another question. The engine reads it itself (P21).
                var counted = FrequencyReading.TryRead(ask.Question, cat);
                if (counted != null) return await AnswerFrequencyAsync(counted, ask, cat, cap, live, s).ConfigureAwait(false);
                // "How many customers did we sell in 2023": a count of the thing itself, never the nearest total (UAT 17 Oct, round 3).
                var plain = CountReading.TryRead(ask.Question, cat);
                if (plain != null) return await AnswerCountAsync(plain, ask, cat, cap, live, s).ConfigureAwait(false);

                var values = await QueryValuesAsync(live, s, cat).ConfigureAwait(false);
                var candidates = ValueMatcher.Match(ask.Question, cat, values);
                var (jev, note) = await AskJevQueryAsync(ask.Question, cat, candidates, s).ConfigureAwait(false);
                if (jev == null) return new DataAnswer { Outcome = DataAnswer.NeedsYou, Note = note, Unsure = new[] { note }, NeedsPermission = await PersonCanAllowAsync().ConfigureAwait(false) };
                reading = jev;
                answer.ReadBy = "jev";
                if (reading.DoesNotFit) return DoesNotFit(answer, reading, cat, cap, ask);
                // A bound word before a value read as that one value ("since 2023" read as Year is 2023) is the range it names.
                QueryGuards.FixBounds(reading, ask.Question);
                QueryGuards.FixYearIn(reading, ask.Question);
                // One year mention is one date level: "in 2024" is the calendar year, never Fiscal Year and Year together.
                QueryGuards.OneYearLevel(reading, ask.Question, cat);
                var noYear = QueryGuards.NoDataForYear(reading, values);
                if (noYear != null) { checks.Add(new DataCheck { Name = "filters", Passed = false, Line = noYear }); return Finish(answer, DataAnswer.Refused, checks); }
                foreach (var (candidate, op) in reading.Filters) AddFilter(reading.Spec, candidate.Column, op, candidate.Values);
                // G1, before any time is read: two parts on one column that cancel each other or make each other moot. Said, not
                // a stop (K16): the query runs both, and filters that leave nothing are still refused below.
                foreach (var line in QueryGuards.G1(reading, cat, ask.Question)) JevQueryReader.Assume(reading, "g1:" + line, line, JevQueryReader.RankCaveat);
                if (reading.Spec.Measures.Count == 0 && !reading.Unsure.Any()) reading.Unsure.Add("Not sure which number you want.");
                // A year the question names that no filter holds yet (a label column "CY 2008", a hidden column of plain years) is
                // looked for in the year columns' own values, visible first and then hidden (K21).
                if (reading.Unsure.Count == 0)
                {
                    var named = NamedPeriods.Read(ask.Question);
                    var open = NamedPeriods.BindYears(reading, cat, values, named, false);
                    if (open.Count > 0)
                    {
                        var more = new Dictionary<string, object[]>(values, StringComparer.Ordinal);
                        foreach (var c in NamedPeriods.YearColumns(cat, reading.Spec, true))
                        {
                            var read = await ColumnValuesAsync(live, c).ConfigureAwait(false);
                            if (read.Values != null) more[c.Id] = read.Values;
                        }
                        open = NamedPeriods.BindYears(reading, cat, more, open, true);
                    }
                    foreach (var t in open)
                        if (NamedPeriods.NoDataFor(t, cat, reading.Spec, values) is { } none)
                        {
                            checks.Add(new DataCheck { Name = "filters", Passed = false, Line = none });
                            return Finish(answer, DataAnswer.Refused, checks);
                        }
                }
                // Time words, once every part is sure: "last month" read against the model's latest date, or split by.
                if (reading.Unsure.Count == 0) await ApplyTimeAsync(reading, ask.Question, cat, live, values).ConfigureAwait(false);
                JevQueryReader.ApplySort(reading);
                if (reading.Unsure.Count == 0) JevQueryReader.SayTopOverPairs(reading, cat);
                // A quarter the question names ("Q1 2008") that no column holds is read from the months or the days it covers (A3).
                if (reading.Unsure.Count == 0) NamedPeriods.BindQuarters(reading, cat, values, NamedPeriods.Read(ask.Question), reading.Notes);
                notes.AddRange(reading.Notes);
                // The engine's guards on its own reading (QueryGuards): a part another part cancels, the calendar's and the twin
                // columns' reasons. A value matched only through a slip is said (G2). Nothing here asks what a word means.
                if (reading.Unsure.Count == 0)
                {
                    notes.AddRange(QueryGuards.SlipNotes(reading, cat));
                    foreach (var line in QueryGuards.Check(ask.Question, reading, cat, reading.Years)) JevQueryReader.Assume(reading, "guard:" + line, line, JevQueryReader.RankCaveat);
                    DropUnreached(reading, cat);
                    // The invariant: a year or a month the question names that the query does not filter by is said, never the
                    // all-time total with nothing said (K21).
                    NamedPeriods.SayWhatWasNotApplied(reading, ask.Question);
                }
                if (reading.Unsure.Count == 0) answer.Others = OthersOf(reading, cat, cap, ask.Run);
            }

            var spec = reading.Spec;
            answer.Parts = PartsOf(spec, cap, ask.Run);
            if (connected != null) notes.InsertRange(0, connected.NotesFor(spec));
            if (notes.Count > 0) answer.Note = string.Join(" ", notes);
            if (spec.Measures.Count > 0) answer.Reading = QueryTemplate.Reading(spec, cat);
            if (reading.Unsure.Count > 0)
            {
                answer.Outcome = DataAnswer.NeedsYou;
                answer.Unsure = reading.Unsure.Distinct().ToArray();
                return answer;
            }
            // What this answer shows, in one sentence, from the query that will run: the first line of every answer.
            var stated = StatedReading.From(spec, reading, cat);
            answer.Statement = stated.Person;
            answer.Assumption = JevQueryReader.Compose(reading);

            // ---- the checks, before anything runs ----
            var nothing = Contradiction(spec, cat);
            if (nothing != null)
            {
                checks.Add(new DataCheck { Name = "filters", Passed = false, Line = nothing });
                return Finish(answer, DataAnswer.Refused, checks);
            }
            if (!Reach(spec, cat, checks)) return Finish(answer, DataAnswer.Refused, checks);
            string dax;
            try { dax = QueryTemplate.Build(spec); }
            catch (InvalidOperationException ex)
            {
                checks.Add(new DataCheck { Name = "dax", Passed = false, Line = ex.Message });
                return Finish(answer, DataAnswer.Refused, checks);
            }
            answer.Dax = dax;
            var validation = await s.ReadAsync(m => DaxValidator.Validate(m, dax)).ConfigureAwait(false);
            // The result names ("@Net Sales") are the query's own columns, so the model does not know them.
            var problems = validation.Diagnostics.Where(d => d.Severity == "error" || (d.Message ?? "").IndexOf("@", StringComparison.Ordinal) < 0).ToList();
            if (!validation.Valid || problems.Any(d => d.Severity == "error"))
            {
                checks.Add(new DataCheck { Name = "dax", Passed = false, Line = "The query did not check out: " + (problems.FirstOrDefault()?.Message ?? "unknown problem") });
                return Finish(answer, DataAnswer.Refused, checks);
            }
            checks.Add(new DataCheck { Name = "dax", Passed = true, Line = problems.Count == 0 ? "The query checks out against the model." : "The query checks out, with a note: " + problems[0].Message });

            // Last, on a reading Jev settled and every deterministic check has passed: does it give the numbers the
            // question asks for? Whatever Run says. Since K16 it never stops the run: a gap it names is said beside the number,
            // first in the assumption line. A check that could not be made, or was not sure, says nothing.
            if (answer.ReadBy == "jev")
            {
                // G4 and G5 first: facts of the model and of the reading, so no call is spent on what the engine can already say.
                var guard = QueryGuards.Fiscal(ask.Question, spec, cat, reading) ?? QueryGuards.RatioShare(ask.Question, stated);
                // A question that counts a thing is answered by a count of that thing, never by a sum of its order lines (P21 again).
                if (guard == null && QueryGuards.CountOfThing(ask.Question, spec, cat) is { } notCount)
                {
                    guard = notCount.Line;
                    if (notCount.Counter != null)
                        answer.Others = answer.Others.Concat(new[]
                        {
                            new DataOther
                            {
                                Line = $"Counting the {notCount.Plural} with {notCount.Counter.Name}.",
                                Instead = notCount.Counter.Name,
                                Reading = notCount.Counter.Name,
                                // The same split and narrowing, with the counting measure in place of the one shown.
                                Parts = CountingParts(answer.Parts, notCount.Counter.Name),
                            },
                        }).ToArray();
                }
                var (gap, cleared) = guard != null ? (guard, (string)null)
                    : await CheckReadingAsync(ask.Question, cat, stated, s).ConfigureAwait(false);
                if (gap == null && cleared != null) answer.Note = answer.Note == null ? cleared : answer.Note + " " + cleared;
                if (gap != null && gap != JevQueryCheck.CouldNotCheck && gap != JevQueryCheck.NotSure)
                {
                    JevQueryReader.Assume(reading, "check", gap, JevQueryReader.RankCaveat);
                    answer.Assumption = JevQueryReader.Compose(reading);
                    answer.Flagged = gap;
                }
            }

            if (!ask.Run || live == null)
            {
                if (live == null) answer.Note = answer.Note == null ? NoDataNote : answer.Note + " " + NoDataNote;
                return Finish(answer, DataAnswer.Built, checks);
            }

            QuerySentForTest?.Invoke(dax);
            var result = await ExecuteLiveGuardedAsync(live, dax, cap, 120).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(result.Error))
            {
                // ONE PLAIN NOTICE, AND THE SERVER'S OWN WORDS ONCE (verify-askdata-r5, r5-062). A measure that
                // exists only in the copy open here checks out against THAT model and cannot be evaluated by the
                // one the query runs on. The page used to print the server's raw text twice, under a green line
                // saying the query checked out. So the raw text now travels once, in Error, which the page puts
                // behind Show details, and the "checks out" line is taken off: it contradicts the failure under it.
                answer.Error = result.Error;
                checks.RemoveAll(c => string.Equals(c.Name, "dax", StringComparison.Ordinal) && c.Passed);
                checks.Add(new DataCheck { Name = "rows", Passed = false, Line = QueryDidNotRunLine });
                return Finish(answer, DataAnswer.Failed, checks);
            }
            // The columns the query carried only to set the order (MonthNumber for the months) are dropped: the answer is what was asked.
            var orderOnly = QueryTemplate.OrderColumns(spec).Count;
            if (orderOnly > 0 && result.Columns != null && result.Columns.Length == spec.GroupBy.Count + orderOnly + spec.Measures.Count)
            {
                bool Keep(int i) => i < spec.GroupBy.Count || i >= spec.GroupBy.Count + orderOnly;
                result.Columns = result.Columns.Where((_, i) => Keep(i)).ToArray();
                result.Rows = (result.Rows ?? Array.Empty<object[]>()).Select(r => r.Where((_, i) => Keep(i)).ToArray()).ToArray();
            }
            var names = spec.GroupBy.Select(cat.Shown).Concat(spec.Measures.Select(m => m.Name)).ToList();
            answer.Columns = (result.Columns ?? Array.Empty<ColumnDef>())
                .Select((c, i) => new ColumnDef { Name = result.Columns.Length == names.Count ? names[i] : c.Name, Type = c.Type }).ToArray();
            // A percent measure is shown as one: the page reads the model's own format for each measure column, and for a date column the date format.
            var formats = spec.GroupBy.Select(c => c.Kind == "date" ? c.FormatString : null).Concat(spec.Measures.Select(m => string.IsNullOrWhiteSpace(m.FormatString) ? null : m.FormatString)).ToArray();
            if (answer.Columns.Length == formats.Length && formats.Any(f => f != null)) answer.Formats = formats;
            answer.Rows = result.Rows ?? Array.Empty<object[]>();
            answer.RowCount = result.RowCount;
            answer.Truncated = result.Truncated;
            answer.ElapsedMs = result.ElapsedMs;
            // A question that asks WHY about one number is explained, not answered again (Kane, 8 Oct, hero journey 6).
            if (IsWhyQuestion(ask.Question) && spec.GroupBy.Count == 0 && spec.Measures.Count == 1 && answer.RowCount == 1 && !answer.Truncated)
            {
                try
                {
                    var why = await ExplainValueAsync(spec.Measures[0].Name,
                        new ExplainFilterContext { ExtraPredicates = spec.Filters.Select(QueryTemplate.FilterExpression).ToArray() }, true, null, 5, origin).ConfigureAwait(false);
                    if (why != null && why.Status == "ok" && why.ValueEvaluated) answer.Why = why;
                }
                catch { /* the number is still answered */ }
            }
            checks.Add(new DataCheck
            {
                Name = "rows", Passed = true,
                Line = result.Truncated
                    ? $"Stopped at {cap.ToString("N0", CultureInfo.InvariantCulture)} rows. There are more; ask for a top few to see the ones that matter."
                    : result.RowCount == 1 ? "1 row." : result.RowCount.ToString("N0", CultureInfo.InvariantCulture) + " rows.",
            });
            return Finish(answer, DataAnswer.Answered, checks);
        }

        /// <summary>What the connected model says about its own measures, against what the open copy holds.</summary>
        private sealed class ConnectedFormulas
        {
            /// <summary>Measures whose formula or format the open copy has changed and the connected model has not (yet).</summary>
            public readonly HashSet<string> Changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>The connected model would not say (a reader without the rights, or an endpoint with no such view).</summary>
            public bool Unreadable;

            public IEnumerable<string> NotesFor(QuerySpec spec)
            {
                if (Unreadable)
                {
                    yield return "Semanticus could not read the connected model's formulas, so what this says about each number comes from the copy open here.";
                    yield break;
                }
                var used = spec.Measures.Concat(new[] { spec.TopBy, spec.SortMeasure }.Where(m => m != null)).Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Where(Changed.Contains).ToList();
                if (used.Count > 0)
                    yield return (used.Count == 1 ? used[0] + " has been changed in the copy open here and not in the connected model." : string.Join(", ", used) + " have been changed in the copy open here and not in the connected model.")
                        + " The query runs on the connected model, so this answer uses its version.";
            }
        }

        private const string MeasuresDmv = "SELECT [Name], [Expression], [FormatString] FROM $SYSTEM.TMSCHEMA_MEASURES";
        private const string MeasuresInfoView = "EVALUATE SELECTCOLUMNS ( INFO.VIEW.MEASURES (), \"Name\", [Name], \"Expression\", [Expression], \"FormatString\", [FormatString] )";

        /// <summary>Put the connected model's formula and format on every measure it holds, in this ask's own copy of the catalog, so the
        /// reading (a ratio's parts, a percent) describes the number the connected model will return. Says which measures the open copy
        /// has changed, and when the connected model cannot be read at all.</summary>
        private async Task<ConnectedFormulas> AlignWithConnectedAsync(QueryCatalog cat, LiveConnection live)
        {
            var result = new ConnectedFormulas();
            ResultSet r = null;
            foreach (var q in new[] { MeasuresDmv, MeasuresInfoView })
            {
                try { r = await ExecuteLiveGuardedAsync(live, q, 100_000, 60).ConfigureAwait(false); }
                catch (Exception) { r = null; }
                if (r != null && string.IsNullOrEmpty(r.Error) && r.Columns != null && r.Rows != null) break;
                r = null;
            }
            if (r == null) { result.Unreadable = true; return result; }
            int At(string name) => Array.FindIndex(r.Columns, c => string.Equals((c.Name ?? "").Trim('[', ']'), name, StringComparison.OrdinalIgnoreCase));
            int name = At("Name"), expression = At("Expression"), format = At("FormatString");
            if (name < 0 || expression < 0) { result.Unreadable = true; return result; }
            string Text(object v) => v == null || v is DBNull ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
            string Flat(string v) => (v ?? "").Replace("\r\n", "\n").Trim();
            // DAX NAMES DO NOT CARE ABOUT CASE (Sol 6.1 on the M11 delta, finding B5, the second strike of 3.7): [average price] runs the connected model's
            // [Average Price]. So a measure the open copy only renamed in case is still the connected one, and every name match between the two
            // here (this index, and the note that names the changed ones) is ordinal-ignore-case, the way DAX itself resolves a name.
            var held = new Dictionary<string, (string Expression, string Format)>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in r.Rows)
                if (row != null && row.Length > name && Text(row[name]) is { } n) held[n] = (Text(row[expression]), format >= 0 && row.Length > format ? Text(row[format]) : null);
            foreach (var m in cat.Measures)
            {
                if (!held.TryGetValue(m.Name, out var theirs)) continue;
                // A format the connected model does not carry is read as none, which is what the query's numbers will look like.
                var same = Flat(m.Expression) == Flat(theirs.Expression) && (format < 0 || string.IsNullOrWhiteSpace(m.FormatString) == string.IsNullOrWhiteSpace(theirs.Format) && (m.FormatString ?? "").Trim() == (theirs.Format ?? "").Trim());
                if (same) continue;
                result.Changed.Add(m.Name);
                m.Expression = theirs.Expression;
                if (format >= 0) m.FormatString = theirs.Format;
            }
            return result;
        }

        /// <summary>list_data_questions: the model's question history, newest first. What was asked, how it was read,
        /// when, and what came of it; never the rows. Both doors read the same file.</summary>
        public Task<DataQuestionHistory> ListDataQuestionsAsync(int limit = 50)
        {
            RequireProFeature();
            _sessions.Require();
            var file = DataQuestionFile(_sessions.CurrentContext);
            if (file == null)
                return Task.FromResult(new DataQuestionHistory { Note = "Save this model to keep its questions. Nothing is kept for a model with no place on disk." });
            return Task.FromResult(new DataQuestionHistory { Questions = DataQuestionStore.Read(file, limit) });
        }

        /// <summary>Keep the question on the model it was asked of. A model that changed while it was answered keeps
        /// nothing, because the file would belong to the other model. Never fails the ask.</summary>
        private string RecordDataQuestion(Session s, DataAsk ask, DataAnswer answer, string origin)
        {
            try
            {
                var context = _sessions.CurrentContext;
                if (context.Session == null || context.Session.Id != s.Id) return null;
                var file = DataQuestionFile(context);
                var id = "dq-" + Guid.NewGuid().ToString("N").Substring(0, 12);
                // A folded row opened again is the same row: its answer is brought up to date and the history gains nothing.
                // A pinned row asked again with a part changed is a new row: the pinned question stays as it was (UAT 17 Oct, F37).
                var keptAsIs = !string.IsNullOrWhiteSpace(ask.RecordAs) && file != null && DataQuestionStore.Read(file, 0).FirstOrDefault(r => r.Id == ask.RecordAs) is { Pinned: true } kept
                    && DataQuestionStore.SquashDax(kept.Dax ?? "") != DataQuestionStore.SquashDax(answer.Dax ?? "");
                if (!keptAsIs && !string.IsNullOrWhiteSpace(ask.RecordAs) && DataQuestionStore.Update(file, ask.RecordAs, r =>
                {
                    r.AskedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    r.Reading = answer.Reading; r.ReadBy = answer.ReadBy; r.Outcome = answer.Outcome; r.Parts = answer.Parts;
                    r.Dax = answer.Dax; r.RowCount = answer.RowCount; r.Dropped = answer.Assumption; r.InNotebook = true;
                    // The number of the run before is not this run's: the page notes the new one when it has it. A failed run says so.
                    r.Headline = null; r.ColdMs = null; r.WarmMs = null; r.TookMs = null;
                })) return ask.RecordAs;
                DataQuestionStore.Append(file, new DataQuestionRecord
                {
                    Id = id,
                    Kind = "words",
                    InNotebook = true,
                    Dropped = answer.Assumption,
                    AskedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    Question = string.IsNullOrWhiteSpace(ask.Question) ? null : DataQuestionStore.Clip(ask.Question.Trim()),
                    Reading = answer.Reading,
                    ReadBy = answer.ReadBy,
                    Outcome = answer.Outcome,
                    Parts = answer.Parts,
                    Dax = answer.Dax,
                    RowCount = answer.RowCount,
                    Origin = origin == "agent" ? "agent" : "human",
                });
                return id;
            }
            catch { /* a history that cannot be written must not fail the answer */ return null; }
        }

        /// <summary>Where this model's questions are kept, or null when it has nowhere of its own. A published model, in every form it takes
        /// (open live, or the file Semanticus saved it to: LocalEngine.ModelHome.cs names it), keeps them under the person's own home
        /// (SEMANTICUS_HOME), one file per model made from the endpoint and the database, never in a folder a commit could pick up. So Connect
        /// your assistant, a reload, Ctrl+S or File > Open Folder never makes a kept or pinned row disappear (UAT 18 Oct). Rows an older build
        /// left in the workspace are carried over once. A file of its own keeps them in its sidecar. An unsaved model keeps none: sharing one
        /// file showed one model another model's questions.</summary>
        private string DataQuestionFile(SessionContext context)
        {
            var session = context.Session;
            if (session == null) return null;
            var place = PlaceFor(context);
            if (!string.IsNullOrEmpty(place?.Endpoint))
            {
                var inWorkspace = _workspaceDir == null ? null : DataQuestionStore.FileForLiveModel(Path.Combine(_workspaceDir, LayoutStore.DirName), place.Endpoint, place.Database);
                string home;
                try { home = DataQuestionStore.FileForLiveModel(ConnectionRegistry.Root(), place.Endpoint, place.Database); }
                catch (InvalidOperationException) { return FolderDir == null ? null : inWorkspace; }   // no per-user folder at all: an open folder is the only place left
                DataQuestionStore.CarryOver(inWorkspace, home);
                return home;
            }
            if (!ExperienceStore.IsEphemeralAnchor(session.SourcePath)) return DataQuestionStore.FileIn(SidecarDir(context));
            return null;
        }

        /// <summary>Filters that leave nothing to count: the same value kept and left out, or bounds that do not meet.
        /// Said plainly and refused, rather than run to an empty answer that reads like "there were none".</summary>
        private static string Contradiction(QuerySpec spec, QueryCatalog cat)
        {
            foreach (var group in spec.Filters.GroupBy(f => f.Column))
            {
                var col = group.Key;
                var keep = group.Where(f => f.Op == QueryOps.Is).SelectMany(f => f.Values).ToList();
                var drop = group.Where(f => f.Op == QueryOps.IsNot).SelectMany(f => f.Values).ToList();
                double? low = null, high = null;
                bool lowOpen = false, highOpen = false;
                foreach (var f in group.Where(f => QueryOps.Compares(f.Op)))
                {
                    var v = Number(f.Values[0]);
                    var open = f.Op == QueryOps.After || f.Op == QueryOps.Before;
                    if (f.Op == QueryOps.After || f.Op == QueryOps.From) { if (low == null || v > low || (v == low && open)) { low = v; lowOpen = open; } }
                    else { if (high == null || v < high || (v == high && open)) { high = v; highOpen = open; } }
                }
                bool Inside(object value)
                {
                    if (drop.Any(d => Equals(d, value))) return false;
                    if (low == null && high == null) return true;
                    double x;
                    try { x = Number(value); } catch { return true; }
                    if (low != null && (lowOpen ? x <= low : x < low)) return false;
                    if (high != null && (highOpen ? x >= high : x > high)) return false;
                    return true;
                }
                var boundsMeet = low == null || high == null || (lowOpen || highOpen ? low < high : low <= high);
                if (!boundsMeet || (keep.Count > 0 && !keep.Any(Inside)))
                    return "These filters leave nothing to count: " + string.Join(" and ", group.Select(f => QueryTemplate.FilterWords(f, cat)))
                         + ". Change one of them and ask again.";
            }
            return null;
        }

        /// <summary>A filter value as a number to compare: a day as its serial number.</summary>
        private static double Number(object v) => v is DateTime d ? d.ToOADate() : Convert.ToDouble(v, CultureInfo.InvariantCulture);

        /// <summary>A count of an entity by its order count ("customers who bought twice"), built from the engine's own reading: the
        /// assumption is said ("exactly 2 orders") and the other test is one press away.</summary>
        private async Task<DataAnswer> AnswerFrequencyAsync(FrequencyReading.Read r, DataAsk ask, QueryCatalog cat, int cap, LiveConnection live, Session s)
        {
            var answer = new DataAnswer { ReadBy = "jev" };
            var checks = new List<DataCheck>();
            var dax = FrequencyReading.Dax(r);
            answer.Dax = dax;
            answer.Reading = r.Plural + " with " + r.Phrase;
            answer.Statement = $"This counts the {r.Plural.ToLowerInvariant()} with {r.Phrase}, taking the orders from {r.CounterName}.";
            answer.Assumption = $"Counting {r.Plural.ToLowerInvariant()} with {r.Phrase}.";
            var other = r.Op == FrequencyReading.Equal ? FrequencyReading.AtLeast : r.Op == FrequencyReading.AtLeast ? FrequencyReading.Equal : FrequencyReading.AtLeast;
            var otherSay = FrequencyReading.Say(other, r.N);
            answer.Others = new[]
            {
                new DataOther
                {
                    Line = $"Counting {r.Plural.ToLowerInvariant()} with {r.Phrase}. You may have meant {otherSay}{r.YearWords}.",
                    Instead = otherSay,
                    Reading = r.Plural + " with " + otherSay + r.YearWords,
                    Parts = new DataAsk { Question = FrequencyReading.QuestionFor(r, other) },
                },
            };
            return await RunOneNumberAsync(answer, dax, r.Plural, ask, cap, live, s, checks).ConfigureAwait(false);
        }

        /// <summary>A plain count of a thing ("how many customers did we sell in 2023"), built from the engine's own reading and said in words.</summary>
        private async Task<DataAnswer> AnswerCountAsync(CountReading.Read r, DataAsk ask, QueryCatalog cat, int cap, LiveConnection live, Session s)
        {
            var answer = new DataAnswer { ReadBy = "jev" };
            var dax = CountReading.Dax(r);
            answer.Dax = dax;
            answer.Reading = r.Plural + (r.Active && r.Entity != null ? " with an order" : "") + r.YearWords;
            answer.Statement = CountReading.Say(r);
            return await RunOneNumberAsync(answer, dax, r.Plural, ask, cap, live, s, new List<DataCheck>()).ConfigureAwait(false);
        }

        /// <summary>Check, run and show a query that gives one number, named <paramref name="name"/>.</summary>
        private async Task<DataAnswer> RunOneNumberAsync(DataAnswer answer, string dax, string name, DataAsk ask, int cap, LiveConnection live, Session s, List<DataCheck> checks)
        {
            var validation = await s.ReadAsync(m => DaxValidator.Validate(m, dax)).ConfigureAwait(false);
            var problems = validation.Diagnostics.Where(d => d.Severity == "error" || (d.Message ?? "").IndexOf("@", StringComparison.Ordinal) < 0).ToList();
            if (!validation.Valid || problems.Any(d => d.Severity == "error"))
            {
                checks.Add(new DataCheck { Name = "dax", Passed = false, Line = "The query did not check out: " + (problems.FirstOrDefault()?.Message ?? "unknown problem") });
                return Finish(answer, DataAnswer.Refused, checks);
            }
            checks.Add(new DataCheck { Name = "dax", Passed = true, Line = "The query checks out against the model." });
            if (!ask.Run) return Finish(answer, DataAnswer.Built, checks);
            QuerySentForTest?.Invoke(dax);
            var result = await ExecuteLiveGuardedAsync(live, dax, cap, 120).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(result.Error))
            {
                answer.Error = result.Error;
                checks.RemoveAll(c => string.Equals(c.Name, "dax", StringComparison.Ordinal) && c.Passed);
                checks.Add(new DataCheck { Name = "rows", Passed = false, Line = QueryDidNotRunLine });
                return Finish(answer, DataAnswer.Failed, checks);
            }
            answer.Columns = new[] { new ColumnDef { Name = name, Type = result.Columns?.FirstOrDefault()?.Type } };
            answer.Rows = result.Rows ?? Array.Empty<object[]>();
            answer.RowCount = result.RowCount;
            answer.ElapsedMs = result.ElapsedMs;
            checks.Add(new DataCheck { Name = "rows", Passed = true, Line = "1 row." });
            return Finish(answer, DataAnswer.Answered, checks);
        }

        private static DataAnswer Finish(DataAnswer a, string outcome, List<DataCheck> checks)
        {
            a.Outcome = outcome;
            a.Checks = checks.ToArray();
            return a;
        }

        private static void AddFilter(QuerySpec spec, QueryColumn col, string op, IEnumerable<object> values)
        {
            // Two values of one column that both keep rows are one filter: Region = Coast or North.
            var same = op == QueryOps.Is || op == QueryOps.IsNot ? spec.Filters.FirstOrDefault(f => f.Column == col && f.Op == op) : null;
            if (same != null) { foreach (var v in values) if (!same.Values.Contains(v)) same.Values.Add(v); return; }
            spec.Filters.Add(new QueryFilter { Column = col, Op = op, Values = values.ToList() });
        }

        /// <summary>Every column a question splits or narrows by must reach every measure through active
        /// relationships, or the numbers repeat the grand total on every row, which reads as an answer and is not one.</summary>
        private static bool Reach(QuerySpec spec, QueryCatalog cat, List<DataCheck> checks)
        {
            var cols = spec.GroupBy.Concat(spec.Filters.Select(f => f.Column)).Distinct().ToList();
            foreach (var m in spec.Measures)
                foreach (var c in cols)
                {
                    // Every set of rows the measure counts must be narrowed: a measure over Sales and Budget split by a
                    // column only Sales reaches would show Budget's total on every row.
                    var unreached = cat.Unreached(c.Table, m);
                    if (unreached.Length == 0) continue;
                    var roots = cat.Roots(m);
                    checks.Add(new DataCheck
                    {
                        Name = "reach", Passed = false,
                        Line = roots.Length > 1 && unreached.Length < roots.Length
                            ? $"{cat.Shown(c)} cannot narrow all of {m.Name}: it counts rows of {string.Join(" and ", roots)}, and no active relationship leads from {string.Join(" or ", unreached)} to {c.Table}, so that part would show its total on every row."
                            : $"{cat.Shown(c)} cannot narrow {m.Name}: no active relationship leads from {string.Join(" or ", unreached)} to {c.Table}.",
                    });
                    return false;
                }
            checks.Add(new DataCheck
            {
                Name = "reach", Passed = true,
                Line = cols.Count == 0 ? "Nothing to split or narrow by." : "Every column reaches the numbers through active relationships.",
            });
            return true;
        }

        /// <summary>A question this model surely does not hold (Jev sure it does not fit): the one stop left on the words path
        /// (K16). It names the closest numbers, Jev's own pick first and then the ones whose names share the question's words,
        /// each one ask away.</summary>
        private static DataAnswer DoesNotFit(DataAnswer answer, JevQueryReader.Reading reading, QueryCatalog cat, int cap, DataAsk ask)
        {
            var asked = FuzzyWords.Words(ask.Question ?? "").Select(w => w.Word).ToList();
            int Shared(QueryMeasure m) => QuerySplitShortlist.NameWords(m.Name).Count(w => asked.Any(a => a == w || FuzzyWords.Near(w, a)));
            var closest = reading.Spec.Measures.Concat(cat.VisibleMeasures.Select((m, i) => (m, i)).OrderByDescending(x => Shared(x.m)).ThenBy(x => x.i).Select(x => x.m))
                .Distinct().Take(3).ToList();
            answer.Outcome = DataAnswer.NeedsYou;
            answer.Unsure = new[]
            {
                closest.Count == 0 ? "This model may not hold that, and it shows no number to ask about."
                : closest.Count == 1 ? $"This model may not hold that. The closest number is {closest[0].Name}."
                : $"This model may not hold that. The closest numbers are {string.Join(", ", closest.Take(closest.Count - 1).Select(m => m.Name))} and {closest[closest.Count - 1].Name}.",
            };
            answer.Others = closest.Select(m =>
            {
                var s = new QuerySpec();
                s.Measures.Add(m);
                return new DataOther { Line = $"Show {m.Name}.", Instead = m.Name, Reading = QueryTemplate.Reading(s, cat), Parts = PartsOf(s, cap, ask.Run) };
            }).ToArray();
            return answer;
        }

        /// <summary>A narrowing or a split Jev chose on a column the number cannot reach through active relationships (a metric
        /// picker no relationship reaches, a table on an inactive one) would refuse the whole question. It is left out and said
        /// instead (K16): the number shown is the number asked for, without that part.</summary>
        private static void DropUnreached(JevQueryReader.Reading reading, QueryCatalog cat)
        {
            var spec = reading.Spec;
            QueryMeasure Blocked(QueryColumn c) => spec.Measures.FirstOrDefault(m => cat.Unreached(c.Table, m).Length > 0);
            foreach (var f in spec.Filters.ToList())
            {
                // A period the engine read itself sits on the calendar the number is found on; only Jev's own picks are left out.
                if (!reading.Filters.Any(x => x.Candidate.Column == f.Column) || Blocked(f.Column) is not { } m) continue;
                spec.Filters.Remove(f);
                reading.Filters.RemoveAll(x => x.Candidate.Column == f.Column);
                JevQueryReader.Assume(reading, "reach:" + f.Column.Id, $"{cat.Shown(f.Column)} is not filtered: it is not linked to {m.Name}.", JevQueryReader.RankFilter);
            }
            foreach (var c in spec.GroupBy.ToList())
            {
                if (Blocked(c) is not { } m) continue;
                spec.GroupBy.Remove(c);
                if (spec.SortColumn == c) spec.SortColumn = null;
                JevQueryReader.Assume(reading, "reach:" + c.Id, $"Not split by {cat.Shown(c)}: it is not linked to {m.Name}.", JevQueryReader.RankSplit);
            }
        }

        /// <summary>Each other reading Jev was torn toward, and each part the reading left out or assumed (K16), as the whole
        /// question with that one part changed: its own reading line and its parts, ready to ask as they are. One that would
        /// change nothing, or that the number could not reach, is left out.</summary>
        private static DataOther[] OthersOf(JevQueryReader.Reading reading, QueryCatalog cat, int cap, bool run)
        {
            var list = new List<DataOther>();
            foreach (var o in reading.Others)
            {
                var s = Copy(reading.Spec);
                switch (o.Kind)
                {
                    case "add_measure":
                        if (s.Measures.Contains(o.ToMeasure)) continue;
                        s.Measures.Add(o.ToMeasure);
                        break;
                    case "add_split":
                        if (s.GroupBy.Contains(o.ToColumn) || s.GroupBy.Count >= JevQueryReader.GroupSlots) continue;
                        s.GroupBy.Add(o.ToColumn);
                        break;
                    case "add_filter":
                        if (s.Filters.Any(x => x.Column == o.ToCandidate.Column)) continue;
                        s.Filters.Add(new QueryFilter { Column = o.ToCandidate.Column, Op = o.Op, Values = o.ToCandidate.Values.ToList() });
                        break;
                    case "drop_filter":
                        if (s.Filters.RemoveAll(x => x.Column == o.FromColumn) == 0) continue;
                        break;
                    case "measure":
                        if (s.Measures.Contains(o.ToMeasure) || !s.Measures.Contains(o.FromMeasure)) continue;
                        s.Measures[s.Measures.IndexOf(o.FromMeasure)] = o.ToMeasure;
                        if (s.TopBy == o.FromMeasure) s.TopBy = o.ToMeasure;
                        if (s.SortMeasure == o.FromMeasure) s.SortMeasure = o.ToMeasure;
                        break;
                    case "top_by":
                        if (s.TopBy != o.FromMeasure) continue;
                        s.TopBy = o.ToMeasure;
                        if (!s.Measures.Contains(o.ToMeasure)) s.Measures.Add(o.ToMeasure);
                        break;
                    case "split":
                        if (s.GroupBy.Contains(o.ToColumn) || !s.GroupBy.Contains(o.FromColumn)) continue;
                        s.GroupBy[s.GroupBy.IndexOf(o.FromColumn)] = o.ToColumn;
                        if (s.SortColumn == o.FromColumn) s.SortColumn = o.ToColumn;
                        break;
                    case "filter":
                        var f = s.Filters.FirstOrDefault(x => x.Column == o.FromColumn);
                        if (f == null || s.Filters.Any(x => x.Column == o.ToColumn)) continue;
                        f.Column = o.ToColumn;
                        f.Values = o.ToCandidate.Values.ToList();
                        break;
                    case "compare":
                        // Every compared value moves to the other table's column, and the side-by-side split with them.
                        var cf = s.Filters.FirstOrDefault(x => x.Column == o.FromColumn);
                        if (cf == null || s.Filters.Any(x => x.Column == o.ToColumn) || s.GroupBy.Contains(o.ToColumn)) continue;
                        cf.Column = o.ToColumn;
                        cf.Values = o.ToValues.ToList();
                        if (s.GroupBy.Contains(o.FromColumn)) s.GroupBy[s.GroupBy.IndexOf(o.FromColumn)] = o.ToColumn;
                        if (s.SortColumn == o.FromColumn) s.SortColumn = o.ToColumn;
                        break;
                    default: continue;
                }
                // A way out that would only be refused (a column the number cannot reach) is not offered.
                if (s.GroupBy.Concat(s.Filters.Select(f => f.Column)).Any(c => s.Measures.Any(m => cat.Unreached(c.Table, m).Length > 0))) continue;
                if (list.Any(x => x.Line == o.Line)) continue;
                list.Add(new DataOther { Line = o.Line, Instead = o.Instead, Reading = QueryTemplate.Reading(s, cat), Parts = PartsOf(s, cap, run) });
            }
            return list.ToArray();
        }

        private static QuerySpec Copy(QuerySpec s)
        {
            var c = new QuerySpec { Top = s.Top, TopBy = s.TopBy, Lowest = s.Lowest, SortColumn = s.SortColumn, SortMeasure = s.SortMeasure, Descending = s.Descending };
            c.GroupBy.AddRange(s.GroupBy);
            c.Measures.AddRange(s.Measures);
            c.Filters.AddRange(s.Filters.Select(f => new QueryFilter { Column = f.Column, Op = f.Op, Values = f.Values.ToList() }));
            return c;
        }

        // ---- time words ---------------------------------------------------------------------------------------

        /// <summary>"last month", "this year", "last week", "the summer", "December": QueryPeriods says how each is read. A
        /// period the words name on its own is read against the model's latest date: by the calendar's labels when it has
        /// them, else as a range of days on the model's column of days. Otherwise the numbers are split by the period and
        /// the note says so, or, when no split can name each period, the answer needs you. Time words the engine cannot
        /// apply yet need you too. Never the all-time total in silence (Kane's ruling, 28 Sep).</summary>
        private async Task ApplyTimeAsync(JevQueryReader.Reading reading, string words, QueryCatalog cat, LiveConnection live, Dictionary<string, object[]> values)
        {
            var spec = reading.Spec;
            if (spec.Measures.Count == 0) return;
            var cols = QueryPeriods.Find(cat, spec.Measures);
            // A matched value, a chosen number's name or a split's name is that thing, not a time ("Month over Month
            // Growth" is a measure; "Summer Sale" a value).
            var said = QueryPeriods.Read(words, reading.Filters.Select(f => f.Candidate.Words).Concat(spec.Measures.Select(m => m.Name)).Concat(spec.GroupBy.Select(c => c.Name)));
            if (said == null) { await ApplyTimeWordsAsync(reading, words, cols, cat, live, values, null).ConfigureAwait(false); return; }
            // A time the engine cannot pick out, apply or check (each read stops before it changes the reading) is said, and
            // the reading runs over every date (K16): never the all-time total in silence, and never no answer.
            var before = reading.Unsure.Count;
            await ApplyTimeWordsAsync(reading, words, cols, cat, live, values, said).ConfigureAwait(false);
            if (reading.Unsure.Count > before)
            {
                reading.Unsure.RemoveRange(before, reading.Unsure.Count - before);
                JevQueryReader.Assume(reading, "time", QueryPeriods.NotApplied(said.Words), JevQueryReader.RankCaveat);
            }
            else if (reading.PeriodResolved && reading.Unsure.Count == 0) NoDoubleShift(reading, said, cat);
        }

        /// <summary>The engine read the words' period ("last year" as 2025). A measure whose own meaning already moves time
        /// ([Total net amount PY]) would move it a second time and show 2024's figure as 2025's, so the measure it wraps is shown
        /// instead, said in a note; with no clear base the answer needs you and no figure is shown.</summary>
        private static void NoDoubleShift(JevQueryReader.Reading reading, QueryPeriods.Said said, QueryCatalog cat)
        {
            var spec = reading.Spec;
            var words = said.Words;
            foreach (var m in spec.Measures.ToList())
            {
                if (!MeasureTime.Moves(m, cat)) continue;
                var baseMeasure = MeasureTime.Base(m, cat);
                if (baseMeasure == null) { reading.Unsure.Add(MeasureTime.Cannot(m, words)); return; }
                for (var i = 0; i < spec.Measures.Count; i++) if (ReferenceEquals(spec.Measures[i], m)) spec.Measures[i] = baseMeasure;
                if (ReferenceEquals(spec.TopBy, m)) spec.TopBy = baseMeasure;
                if (ReferenceEquals(spec.SortMeasure, m)) spec.SortMeasure = baseMeasure;
                // Two shifted measures with one base would show the same number twice.
                var seen = new HashSet<QueryMeasure>();
                spec.Measures.RemoveAll(x => !seen.Add(x));
                reading.Notes.Add(MeasureTime.Swapped(m, baseMeasure, words));
                // "Using <m>." was said of the figure before the swap; it would sit above a different number.
                reading.Assumptions.RemoveAll(x => x.Key == "measure" && x.Line == $"Using {m.Name}.");
            }
            // The offers were built from Jev's other picks. One that moves the date would run the period a second time one press
            // away: it is swapped for its base, or dropped when it has none or the base is already shown.
            foreach (var o in reading.Others.ToList())
            {
                var drop = false;
                foreach (var om in new[] { o.FromMeasure, o.ToMeasure })
                {
                    if (om == null || !MeasureTime.Moves(om, cat)) continue;
                    var b = MeasureTime.Base(om, cat);
                    if (b == null) { drop = true; break; }
                    if (ReferenceEquals(om, o.FromMeasure)) o.FromMeasure = b;
                    if (ReferenceEquals(om, o.ToMeasure)) o.ToMeasure = b;
                    o.Line = o.Line?.Replace(om.Name, b.Name);
                    o.Instead = o.Instead?.Replace(om.Name, b.Name);
                }
                // An offer that now shows what is already shown offers nothing.
                if (drop || (o.FromMeasure != null && ReferenceEquals(o.FromMeasure, o.ToMeasure)))
                    reading.Others.Remove(o);
            }
        }

        private async Task ApplyTimeWordsAsync(JevQueryReader.Reading reading, string words, QueryPeriods.Columns cols, QueryCatalog cat, LiveConnection live,
            Dictionary<string, object[]> values, QueryPeriods.Said said)
        {
            if (said?.Unsupported != null) { reading.Unsure.Add(QueryPeriods.CannotApply(said.Unsupported)); return; }
            int? years = cols.Year != null && values != null && values.TryGetValue(cols.Year.Id, out var ys) ? ys.Count(v => v != null && !(v is DBNull)) : (int?)null;
            reading.Years = years;
            if (said != null && said.ToDate)
            {
                // G3: "last year to date" is the same span of last year, read as a range of days; or it is not applied.
                // Never the whole earlier period standing in for it.
                var ranged = live != null && QueryPeriods.RangeReady(reading, said, cols)
                    && await ResolvePeriodAsync(reading, said, cols, cat, live, new List<QueryColumn>()).ConfigureAwait(false);
                if (!ranged && reading.Unsure.Count == 0) reading.Unsure.Add(QueryPeriods.CannotApply(said.Words));
                reading.PeriodResolved = ranged;
            }
            else if (said != null)
            {
                var naming = QueryPeriods.ResolveColumns(reading, said, cols);
                var done = naming != null && live != null && await ResolvePeriodAsync(reading, said, cols, cat, live, naming).ConfigureAwait(false);
                if (!done && live != null && QueryPeriods.RangeReady(reading, said, cols))
                    done = await ResolvePeriodAsync(reading, said, cols, cat, live, new List<QueryColumn>()).ConfigureAwait(false);
                if (!done) QueryPeriods.Split(reading, said, cols, cat, years);
                reading.PeriodResolved = done;
            }
            if (reading.Unsure.Count == 0) QueryPeriods.MonthsOfYears(reading, words, cols, cat, years);
        }

        /// <summary>Read the period against the latest date: by the calendar's labels (<paramref name="naming"/>), or,
        /// with none, as a range on the column of days. A period the words also name ("in 2024") must meet the one read,
        /// or the answer needs you. False when a read failed, so the caller splits instead.</summary>
        private async Task<bool> ResolvePeriodAsync(JevQueryReader.Reading reading, QueryPeriods.Said said, QueryPeriods.Columns cols, QueryCatalog cat,
            LiveConnection live, List<QueryColumn> naming)
        {
            async Task<object[][]> Read(string q, int cap)
            {
                QuerySentForTest?.Invoke(q);
                try
                {
                    var r = await ExecuteLiveGuardedAsync(live, q, cap, 60).ConfigureAwait(false);
                    return string.IsNullOrEmpty(r.Error) ? r.Rows ?? Array.Empty<object[]>() : null;
                }
                catch { return null; }
            }
            var byLabels = naming.Count > 0;
            var on = byLabels ? cols.Day : cols.Days;
            var must = QueryPeriods.MustMeet(reading, said, cols, naming);
            // No calendar to read a named period's labels from: whether the two meet cannot be told.
            if (must.Count > 0 && cols.Day == null) { reading.Unsure.Add(QueryPeriods.CannotCheck(said, must, cat)); return true; }
            var latestRows = await Read(QueryPeriods.LatestQuery(on, reading.Spec.Measures[0]), 1).ConfigureAwait(false);
            if (latestRows == null || latestRows.Length == 0 || latestRows[0].Length == 0 || !TryDay(latestRows[0][0], out var latest)) return false;

            var asked = naming.Concat(must.Select(f => f.Column)).Distinct().ToList();
            var days = byLabels ? QueryPeriods.Days(said, latest) : QueryPeriods.CheckDays(said, latest);
            List<object[]> labels = null;
            if (asked.Count > 0)
            {
                var rows = await Read(QueryPeriods.LabelsQuery(cols.Day, days.Distinct(), asked), 10).ConfigureAwait(false);
                if (rows == null) return false;
                labels = days.Select(d => rows.FirstOrDefault(row => row.Length == asked.Count + 1 && TryDay(row[0], out var x) && x.Date == d.Date))
                    .Select(row => row?.Skip(1).Select((v, i) => QueryPeriods.Typed(asked[i], v)).ToArray()).ToList();
                if (must.Count > 0 && !QueryPeriods.Meets(must, asked, labels, anyDay: said.Unit == "week"))
                {
                    reading.Unsure.Add(QueryPeriods.Misses(said, latest, must, cat));
                    return true;
                }
            }
            if (!byLabels) { QueryPeriods.ResolveRange(reading, said, cols, cat, latest); return true; }
            return QueryPeriods.Resolve(reading, said, cols, cat, latest, naming, labels.Select(l => l?.Take(naming.Count).ToArray()).ToList());
        }

        private static bool TryDay(object v, out DateTime day)
        {
            day = default;
            if (v is DateTime d) { day = d; return true; }
            return v is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
        }

        private static DataAsk CountingParts(DataAsk shown, string measure) => new DataAsk
        {
            MaxRows = shown.MaxRows, Run = shown.Run, GroupBy = shown.GroupBy, Measures = new[] { measure }, Filters = shown.Filters,
            Top = shown.Top, TopBy = shown.Top.HasValue ? measure : null, Lowest = shown.Lowest, SortBy = shown.SortBy, Descending = shown.Descending,
        };

        private static DataAsk PartsOf(QuerySpec s, int maxRows, bool run) => new DataAsk
        {
            MaxRows = maxRows,
            Run = run,
            GroupBy = s.GroupBy.Select(c => c.Dax).ToArray(),
            Measures = s.Measures.Select(m => m.Name).ToArray(),
            Filters = s.Filters.Select(f => new DataAskFilter
            {
                Column = f.Column.Dax, Op = f.Op,
                Values = f.Values.Select(v => v is DateTime d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture)).ToArray(),
            }).ToArray(),
            Top = s.Top,
            TopBy = s.Top.HasValue ? (s.TopBy ?? s.Measures.FirstOrDefault())?.Name : null,
            Lowest = s.Lowest,
            SortBy = s.SortMeasure?.Name ?? s.SortColumn?.Dax,
            Descending = s.Descending,
        };

        /// <summary>Given parts: taken as said, each one found in the model, each filter's words found among the column's
        /// values. Null when a part cannot be taken; the check says which. <paramref name="notes"/> gathers plain lines
        /// about parts that were taken but change nothing (a ranking measure with no top few).</summary>
        private async Task<JevQueryReader.Reading> PartsAsync(DataAsk ask, QueryCatalog cat, LiveConnection live, List<DataCheck> checks, List<string> notes)
        {
            var r = new JevQueryReader.Reading();
            var missing = new List<string>();

            // A column a person named: found, or the plain reason it cannot be used.
            QueryColumn ColumnNamed(string reference, string what)
            {
                if (string.IsNullOrWhiteSpace(reference)) { missing.Add($"A {what} names no column."); return null; }
                var c = cat.Column(reference);
                if (c != null) return c;
                var group = cat.CalculationGroupOf(reference);
                missing.Add(group != null
                    ? $"{group} is a calculation group, and a question cannot use its column yet."
                    : $"No column named {reference.Trim()}.");
                return null;
            }

            QueryMeasure MeasureNamed(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) { missing.Add("A measure name is empty."); return null; }
                var m = cat.Measure(name);
                if (m == null) missing.Add($"No measure named {name.Trim()}.");
                return m;
            }

            foreach (var g in ask.GroupBy ?? Array.Empty<string>())
            {
                var c = ColumnNamed(g, "split");
                if (c != null && !r.Spec.GroupBy.Contains(c)) r.Spec.GroupBy.Add(c);
            }
            foreach (var name in ask.Measures ?? Array.Empty<string>())
            {
                var m = MeasureNamed(name);
                if (m != null && !r.Spec.Measures.Contains(m)) r.Spec.Measures.Add(m);
            }
            if (ask.Top.HasValue && ask.Top.Value > 0)
            {
                r.Spec.Top = ask.Top;
                r.Spec.Lowest = ask.Lowest;
                if (!string.IsNullOrWhiteSpace(ask.TopBy))
                {
                    var by = MeasureNamed(ask.TopBy);
                    if (by != null) { r.Spec.TopBy = by; if (!r.Spec.Measures.Contains(by)) r.Spec.Measures.Add(by); }
                }
            }
            else if (!string.IsNullOrWhiteSpace(ask.TopBy) || ask.Lowest)
                notes.Add("A ranking was given without a top few, so every row is shown. Say how many rows to keep to rank them.");
            if (!string.IsNullOrWhiteSpace(ask.SortBy))
            {
                if (QueryTemplate.TryParseColumn(ask.SortBy, out _, out _))
                {
                    var sc = ColumnNamed(ask.SortBy, "sort");
                    // A column the numbers are not split by is not in the result, so the server cannot sort by it.
                    if (sc != null && !r.Spec.GroupBy.Contains(sc)) missing.Add(QueryTemplate.SortNotSplit(cat.Shown(sc)));
                    else if (sc != null) r.Spec.SortColumn = sc;
                }
                else
                {
                    var sm = cat.Measure(ask.SortBy);
                    if (sm != null && r.Spec.Measures.Contains(sm)) r.Spec.SortMeasure = sm;
                    else missing.Add($"Nothing to sort by named {ask.SortBy.Trim()}. Sort by a split column or a number the question shows.");
                }
                r.Spec.Descending = ask.Descending;
            }
            if (r.Spec.Measures.Count == 0 && missing.Count == 0) missing.Add("Choose at least one number to see.");
            if (missing.Count > 0)
            {
                checks.Add(new DataCheck { Name = "parts", Passed = false, Line = string.Join(" ", missing) });
                return null;
            }

            var valueProblems = new List<string>();
            foreach (var f in ask.Filters ?? Array.Empty<DataAskFilter>())
            {
                var c = ColumnNamed(f?.Column, "filter");
                if (c == null) continue;
                var op = string.IsNullOrWhiteSpace(f.Op) ? QueryOps.Is : f.Op.Trim().ToLowerInvariant();
                if (!QueryOps.All.Contains(op)) { missing.Add($"'{f.Op}' is not a filter. Use is, is_not, after, from, before or up_to."); continue; }
                if (!QueryTemplate.Filterable(c)) { missing.Add($"{cat.Shown(c)} holds {QueryTemplate.KindWords(c.Kind)}, which a question cannot filter yet."); continue; }
                var words = (f.Values ?? Array.Empty<string>()).Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
                if (words.Count == 0) { missing.Add($"Say which values of {cat.Shown(c)} to filter by."); continue; }
                var matched = new List<object>();
                if (c.Kind == "date")
                {
                    // A column of days takes a range (Kane's ruling, 28 Sep: "last week" is read as one), never a list of days.
                    if (!QueryOps.Compares(op)) { missing.Add($"{cat.Shown(c)} holds dates, so it can only be filtered by after, from, before or up to."); continue; }
                    // A day only: the query and the parts keep the day, so a time of day would be dropped on the way.
                    if (words.Count != 1 || !DateTime.TryParseExact(words[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    { missing.Add($"A filter with {JevQueryReader.OpWords(op)} needs one day for {cat.Shown(c)}, written as 2025-12-31."); continue; }
                    matched.Add(day);
                }
                else if (QueryOps.Compares(op))
                {
                    if (c.Kind == "text") { missing.Add($"{cat.Shown(c)} holds text, so it can only be filtered by is or is_not."); continue; }
                    if (words.Count != 1 || !ValueMatcher.TryNumber(c, words[0], out var n)) { missing.Add($"A filter with {JevQueryReader.OpWords(op)} needs one number for {cat.Shown(c)}."); continue; }
                    matched.Add(n);
                }
                else
                {
                    if (live == null) { missing.Add(NoDataNote); continue; }
                    var read = await ColumnValuesAsync(live, c).ConfigureAwait(false);
                    if (read.TooMany)
                    {
                        valueProblems.Add($"{cat.Shown(c)} has more than {ValueMatcher.MaxValuesPerColumn.ToString("N0", CultureInfo.InvariantCulture)} different values, too many to check '{string.Join(", ", words)}' against. "
                                        + "Narrow the question by a column with fewer values instead.");
                        continue;
                    }
                    if (read.Values == null) { valueProblems.Add($"The values of {cat.Shown(c)} could not be read ({read.Error}), so '{string.Join(", ", words)}' could not be checked."); continue; }
                    var values = read.Values;
                    foreach (var w in words)
                    {
                        var found = c.Kind == "text" ? ValueMatcher.Find(c, w, values)
                            : ValueMatcher.TryNumber(c, w, out var n) ? values.Where(v => v != null && !(v is DBNull) && Equals(Convert.ToDouble(v, CultureInfo.InvariantCulture), Convert.ToDouble(n, CultureInfo.InvariantCulture))).ToList()
                            : new List<object>();
                        if (found.Count == 0) valueProblems.Add($"No value in {cat.Shown(c)} matches '{w}'.");
                        else if (found.Count > 1 && !found.Any(v => string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), w.Trim(), StringComparison.OrdinalIgnoreCase)))
                            valueProblems.Add($"'{w}' matches {found.Count.ToString("N0", CultureInfo.InvariantCulture)} values in {cat.Shown(c)}: {string.Join(", ", found.Take(10).Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))}. Say which.");
                        else matched.AddRange(found);
                    }
                }
                AddFilter(r.Spec, c, op, matched);
            }
            if (missing.Count > 0)
            {
                checks.Add(new DataCheck { Name = "parts", Passed = false, Line = string.Join(" ", missing) });
                return null;
            }
            checks.Add(new DataCheck { Name = "parts", Passed = true, Line = "Found every measure and column named." });
            if (valueProblems.Count > 0)
            {
                checks.Add(new DataCheck { Name = "values", Passed = false, Line = string.Join(" ", valueProblems) });
                return null;
            }
            if ((ask.Filters?.Length ?? 0) > 0) checks.Add(new DataCheck { Name = "values", Passed = true, Line = "Every filter value is in the model's data." });
            return r;
        }

        // ---- values, read on this computer and kept here ---------------------------------------------------------

        private static string DistinctQuery(QueryColumn c) =>
            "EVALUATE TOPNSKIP ( " + (ValueMatcher.MaxValuesPerColumn + 1).ToString(CultureInfo.InvariantCulture) + ", 0, DISTINCT ( " + c.Dax + " ) )";

        /// <summary>A column's values as read: the values, or that there were more than the matcher reads (which is not
        /// a failure to read), or the reason the read failed.</summary>
        private readonly struct ColumnRead
        {
            public ColumnRead(object[] values, bool tooMany, string error) { Values = values; TooMany = tooMany; Error = error; }
            public object[] Values { get; }
            public bool TooMany { get; }
            public string Error { get; }
        }

        private async Task<ColumnRead> ColumnValuesAsync(LiveConnection live, QueryColumn c)
        {
            var q = DistinctQuery(c);
            QuerySentForTest?.Invoke(q);
            ResultSet r;
            try { r = await ExecuteLiveGuardedAsync(live, q, ValueMatcher.MaxValuesPerColumn + 1, 60).ConfigureAwait(false); }
            catch (Exception ex) { return new ColumnRead(null, false, ex.Message); }
            if (!string.IsNullOrEmpty(r.Error)) return new ColumnRead(null, false, r.Error);
            var rows = r.Rows ?? Array.Empty<object[]>();
            if (r.Truncated || rows.Length > ValueMatcher.MaxValuesPerColumn) return new ColumnRead(null, true, null);
            return new ColumnRead(rows.Select(row => row.Length > 0 ? row[0] : null).ToArray(), false, null);
        }

        /// <summary>The distinct values of every column a question could narrow by, read from the connected model and
        /// reused for ten minutes on the same connection and model. They never leave this process.</summary>
        private async Task<Dictionary<string, object[]>> QueryValuesAsync(LiveConnection live, Session s, QueryCatalog cat)
        {
            lock (_queryValuesGate)
                if (ReferenceEquals(_queryValues.Live, live) && _queryValues.SessionId == s.Id && DateTime.UtcNow - _queryValues.At < QueryValuesLife)
                    return _queryValues.Values;
            var values = new Dictionary<string, object[]>(StringComparer.Ordinal);
            foreach (var c in ValueMatcher.Readable(cat))
            {
                var read = await ColumnValuesAsync(live, c).ConfigureAwait(false);
                if (read.Values != null) values[c.Id] = read.Values;
            }
            lock (_queryValuesGate) _queryValues = (live, s.Id, DateTime.UtcNow, values);
            return values;
        }

        // ---- Jev ----------------------------------------------------------------------------------------------

        /// <summary>A test's clock on the check: how long the check call took, in milliseconds. Null on every real path.</summary>
        internal Action<long> CheckTimedForTest;

        /// <summary>What the person reads when Jev was asked and gave no answer. A stop nobody can act on is one calm sentence with
        /// no cause and no status; a stop the person can act on (an ended licence, a spent month) is the service's own sentence,
        /// shown once, with no "Jev did not answer:" in front of it (Kane, 8 Oct).</summary>
        private static readonly System.Text.RegularExpressions.Regex WhyWords = new System.Text.RegularExpressions.Regex(
            @"\bwhy\b|^\s*(explain|how come|what('s| is)? (driving|behind|makes|drives))\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>True when the words ask why a number is what it is: "why is Margin % 35.8% in 2025?", "explain net sales for 2025".</summary>
        internal static bool IsWhyQuestion(string question) => !string.IsNullOrWhiteSpace(question) && WhyWords.IsMatch(question);

        /// <summary>Whether this model has no current permission to send its details for advice AND has an address to keep one
        /// under, so an Allow button can give it. False for a model with nothing to keep it under (the note already says to save it).</summary>
        private async Task<bool> PersonCanAllowAsync()
        {
            try
            {
                var info = await GetConsentAsync().ConfigureAwait(false);
                return !info.Current && !string.IsNullOrEmpty(info.AppliesTo);
            }
            catch { return false; }
        }

        internal static string NotAnsweredNote(string reason) =>
            JevDecisionOracle.IsNotAvailable(reason) ? JevDecisionOracle.NotAvailableAskNote : AsReadSentence(reason);

        /// <summary>One typed ask of the query.summarize point: the answers, or null with a plain note when Jev was not asked or
        /// its answer was not used. Never throws for an ordinary failure.</summary>
        private async Task<(DecisionAnswers Answers, string Note)> AskDecisionAsync(DecisionPoint point, JevQueryReader.Request r, Session s)
        {
            var fence = new DecisionSessionFence(s.Id, s.Revision, () =>
            {
                var current = _sessions.Current;
                return (current == null ? null : current.Id, current == null ? -1L : current.Revision);
            });
            DecisionOutcome asked;
            try
            {
                asked = await Splitter.AskAsync(Decisions, point.Name, r.Snapshot, r.Questions,
                    isPro: Entitlement.FeatureGrants.IsProTier(_entitlement),
                    enabled: DecisionPointEnabled(point.Name),
                    fence: fence,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { return (null, "Jev was not asked: " + ex.Message + "."); }
            if (!asked.Asked) return (null, "Jev was not asked: " + asked.SkippedReason + ".");
            if (!asked.Answers.Decided) return (null, NotAnsweredNote(asked.Answers.NotDecidedReason));
            return (asked.Answers, null);
        }

        /// <summary>Ask the check of a settled reading. Stop is null when it may run; else the line it is stopped with: what is
        /// missing, that Jev is not sure, or that the check could not be made (never a run on a reading nobody checked). Note is
        /// what the person reads off the rows when a gap the reading itself covers was cleared.</summary>
        private async Task<(string Stop, string Note)> CheckReadingAsync(string words, QueryCatalog cat, StatedReading stated, Session s)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.QuerySummarize);
            JevQueryReader.Request request;
            try { request = JevQueryCheck.Build(words, cat, stated); }
            catch (Exception) { return (JevQueryCheck.CouldNotCheck, null); }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (answers, _) = await AskDecisionAsync(point, request, s).ConfigureAwait(false);
            var verdict = JevQueryCheck.First(answers, point.ConfidenceBar, stated, words);
            if (verdict.Run || verdict.Stop != null || verdict.Second.Count == 0)
            {
                CheckTimedForTest?.Invoke(clock.ElapsedMilliseconds);
                return (verdict.Run ? null : verdict.Stop ?? JevQueryCheck.NotSure, verdict.Note);
            }
            // The gap is neither nothing nor cleared, and voiding the biggest ones could reach the bar: ask each, side by side.
            var asked = new Dictionary<string, DecisionAnswers>(StringComparer.Ordinal);
            try
            {
                var calls = verdict.Second.Select(async gap =>
                {
                    var second = JevQueryCheck.BuildSecond(words, cat, stated, gap);
                    var (a, _) = await AskDecisionAsync(point, second, s).ConfigureAwait(false);
                    return (gap, a);
                }).ToList();
                foreach (var (gap, a) in await Task.WhenAll(calls).ConfigureAwait(false)) asked[gap] = a;
            }
            catch (Exception) { asked.Clear(); }
            var stop = JevQueryCheck.Second(verdict, asked, point.ConfidenceBar, stated, words, out var run);
            CheckTimedForTest?.Invoke(clock.ElapsedMilliseconds);
            return (run ? null : stop ?? JevQueryCheck.NotSure, run ? verdict.Note : null);
        }

        /// <summary>Ask query.summarize: the engine lists, Jev picks, the engine reads the picks back. Null with a plain
        /// note when Jev was not asked or its answer was not used. Never throws for an ordinary failure.</summary>
        private async Task<(JevQueryReader.Reading Reading, string Note)> AskJevQueryAsync(string words, QueryCatalog cat, List<ValueCandidate> candidates, Session s)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.QuerySummarize);
            JevQueryReader.Request request;
            try { request = JevQueryReader.Build(words, cat, candidates); }
            catch (Exception ex) { return (null, "Jev was not asked: " + ex.Message); }
            Task<(DecisionAnswers Answers, string Note)> Ask(JevQueryReader.Request r) => AskDecisionAsync(point, r, s);

            // 1. The parts: measures, how many splits, filters, top N, order.
            var (answers, note) = await Ask(request).ConfigureAwait(false);
            if (answers == null) return (null, note);
            var reading = JevQueryReader.Read(answers, point.ConfidenceBar, request, cat);
            if (reading.DoesNotFit) return (reading, null);

            // 2. The split, from the engine's shortlist, once everything else is sure. A reading already unsure runs
            //    nothing, so it is handed back without spending another call.
            if (reading.Splits > 0 && reading.Unsure.Count == 0 && reading.Spec.Measures.Count > 0)
            {
                JevQueryReader.Request split;
                try { split = JevQueryReader.BuildSplit(words, cat, reading); }
                catch (Exception ex) { return (null, "Jev was not asked: " + ex.Message); }
                // No question left to ask (the model shows no column to split by): ReadSplit says so, and nothing runs.
                var (splitAnswers, splitNote) = split.Questions.Count == 0 ? (null, null) : await Ask(split).ConfigureAwait(false);
                if (splitAnswers == null && split.Questions.Count > 0) return (null, splitNote);
                var pending = JevQueryReader.ReadSplit(splitAnswers, point.ConfidenceBar, split, reading, cat);

                // 3. Only when the shortlist missed: which column of the table Jev named.
                if (pending.Count > 0 && reading.Unsure.Count == 0)
                {
                    JevQueryReader.Request inTable;
                    try { inTable = JevQueryReader.BuildInTable(words, cat, reading.Spec.Measures, pending); }
                    catch (Exception ex) { return (null, "Jev was not asked: " + ex.Message); }
                    var (tableAnswers, tableNote) = await Ask(inTable).ConfigureAwait(false);
                    if (tableAnswers == null) return (null, tableNote);
                    JevQueryReader.ReadInTable(tableAnswers, point.ConfidenceBar, inTable, reading, pending, cat);
                }
                else if (pending.Count > 0) reading.Unsure.Add("Not sure which column to split by.");
            }
            if (reading.Unsure.Count == 0) JevQueryReader.ApplyCompare(reading, cat);
            return (reading, null);
        }
    }
}
