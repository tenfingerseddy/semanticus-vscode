// The date table family (T268, M4 point 3): for a model with no marked date table, whether it needs one, which table
// it is or a new one, and whether to mark it. Kane, 26 September: in this release, behind its own replay.
//
// WHAT IS PROVED HERE, AND HOW.
//   Free: no date table item from Jev, nothing asked, no run; the rule's "Mark the date table" row is the one row.
//   The bar not met (the family's real constant): Pro with permission still asks nothing and the rule's row stays.
//   Pro, bar met: one item, "Mark Calendar as the date table", owned by Jev, in Structure; the rule's row stands
//   aside; the line is a decision on date.table with an engine-built reason and check.
//   The engine lists the options: a table on the many side, a table whose dates are measured to repeat, and a table
//   with two date columns and nothing to tell them apart are never options; a pick of one is no line.
//   Approve marks the table on its date column (category and key) as one undo step; generate adds the calculated
//   date table as one undo step; "no" to marking, or "none", is no line; below the bar the line arrives dropped.
//   What goes to Jev: the two declared fields, and never a value out of the data, a first or last day, or a
//   count of rows; the shape counts (how many tables and relationships) do go, and What is sent says so.
//   The contiguity fact itself: whole days, none missing.
//
// THE DIGESTS ARE COMPUTED FROM THE FAMILY'S OWN REQUEST (DateTableFamily.Prepare with the same facts the run
// reads), so a run that sent a different request would find no answer here.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;
using ModelFacts = Semanticus.Engine.Facts.ModelFacts;
using ModelFactsData = Semanticus.Engine.Facts.ModelFactsData;
using TableFacts = Semanticus.Engine.Facts.TableFacts;
using ColumnFacts = Semanticus.Engine.Facts.ColumnFacts;
using MeasuredFacts = Semanticus.Engine.Facts.MeasuredFacts;

namespace Semanticus.Tests
{
    public sealed class DateTableFamilyTests : IClassFixture<ModelTemplates>
    {
        private const string Point = "date.table";

        // Lean suite (8 Oct): each model below is built once per class (ModelTemplates) and every test opens its own copy.
        private readonly ModelTemplates _templates;
        public DateTableFamilyTests(ModelTemplates templates) => _templates = templates;

        /// <summary>Sales is the fact (many side, OrderDate). Calendar is on the one side with one date column, Date.
        /// Promotion stands alone with two date columns and no key. Returns Log stands alone with one date column
        /// whose dates are measured to repeat. A measure uses time intelligence over Calendar[Date].</summary>
        private async Task<DecisionFixture> ModelAsync(string file, bool pro = true, bool bar = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            var e = f.Engine;
            await _templates.OpenCopyAsync(f, "date", BuildDateModelAsync, file);
            f.UseNull();
            e.DateTableJevDecidesForTest = bar;
            e.DateTableFactsForTest = () => Facts(measured: true);
            // Only this point is asked here: the other families' points are switched off so every ask counted is ours.
            await e.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            await e.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            return f;
        }

        private static async Task BuildDateModelAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Calendar", "human");
            await e.CreateColumnAsync("table:Calendar", "Date", "DateTime", null, "human");
            await e.CreateColumnAsync("table:Calendar", "Year", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "OrderDate", "DateTime", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateTableAsync("Promotion", "human");
            await e.CreateColumnAsync("table:Promotion", "StartDate", "DateTime", null, "human");
            await e.CreateColumnAsync("table:Promotion", "EndDate", "DateTime", null, "human");
            await e.CreateTableAsync("Returns Log", "human");
            await e.CreateColumnAsync("table:Returns Log", "Returned On", "DateTime", null, "human");
            await e.CreateRelationshipAsync("column:Sales/OrderDate", "column:Calendar/Date", null, null, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Sales YTD", "TOTALYTD ( [Total Sales], 'Calendar'[Date] )", "human");
        }

        /// <summary>Measured facts: Calendar[Date] one row per day with none missing; Returns Log's dates repeat. With
        /// measured false the model has no data, the way a model opened from files is.</summary>
        private static ModelFacts Facts(bool measured) => new ModelFacts
        {
            Data = measured ? ModelFactsData.Local : ModelFactsData.None,
            Tables = new[]
            {
                new TableFacts
                {
                    Ref = "table:Calendar", Name = "Calendar",
                    Columns = new[]
                    {
                        new ColumnFacts
                        {
                            Ref = "column:Calendar/Date", Name = "Date", DataType = "DateTime",
                            Measured = measured ? new MeasuredFacts { Distinct = 1096, UniqueOnEveryRow = true, ContiguousDays = true } : null,
                        },
                    },
                },
                new TableFacts
                {
                    Ref = "table:Returns Log", Name = "Returns Log",
                    Columns = new[]
                    {
                        new ColumnFacts
                        {
                            Ref = "column:Returns Log/Returned On", Name = "Returned On", DataType = "DateTime",
                            Measured = measured ? new MeasuredFacts { Distinct = 377, UniqueOnEveryRow = false, ContiguousDays = false } : null,
                        },
                    },
                },
            },
        };

        private sealed class CapturingOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public CapturingOracle(IDecisionOracle inner) { _inner = inner; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.ToList(); } }
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                return _inner.AskAsync(request, ct);
            }
        }

        private static async Task<string> DigestAsync(DecisionFixture f, bool measured = true)
        {
            var point = DecisionRegistry.Require(Point);
            var prepared = await f.Sessions.Current.ReadAsync(m => DateTableFamily.Prepare(m, Facts(measured)));
            Assert.NotNull(prepared.Ask);
            return new DecisionRequest(point.Name, point.SchemaVersion, prepared.Ask.Snapshot, prepared.Ask.Questions).Digest;
        }

        private static string Fixture(string digest, string choice, double confidence)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
              .Append(",\"point\":\"").Append(Point).Append('"')
              .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30}")
              .Append(",\"answers\":{\"date_table\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
              .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            return sb.Append("]}").ToString();
        }

        private static async Task<CapturingOracle> AnswerAsync(DecisionFixture f, string choice = "table:Calendar",
            double confidence = 0.95, bool measured = true)
        {
            await f.ConsentAsync();
            f.Engine.DateTableFactsForTest = () => Facts(measured);
            var recorded = new RecordedDecisionOracle(Fixture(await DigestAsync(f, measured), choice, confidence), "jev-test-1");
            var capturing = new CapturingOracle(recorded);
            f.Engine.UseDecisionsForTest(new DecisionService(capturing, new DecisionBudget()));
            return capturing;
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        private static ModelPlanItem[] DateRows(ModelPlanView plan) =>
            plan.Items.Where(i => i.Id.StartsWith("jev.date-table.", StringComparison.Ordinal)).ToArray();

        private static async Task<(string Category, bool DateIsKey)> MarkOf(DecisionFixture f) =>
            await f.Sessions.Current.ReadAsync(m => (m.Tables["Calendar"].DataCategory ?? "", m.Tables["Calendar"].Columns["Date"].IsKey));

        // ---- who decides ------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_free_session_asks_nothing_and_keeps_the_rule_row()
        {
            using var f = await ModelAsync("DateFree.bim", pro: false);
            await f.ConsentAsync();
            var oracle = f.UseRecorded(Fixture(new string('0', 64), "table:Calendar", 0.99));

            var plan = await SettledPlanAsync(f);

            Assert.Empty(DateRows(plan));
            Assert.Contains(plan.Items, i => i.Id == "datetable/model");
            Assert.Equal(0, oracle.AskCount);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
            Assert.Equal(0, f.Engine.JevRunsStartedForTest);
        }

        [Fact]
        public async Task Below_the_replay_bar_Pro_asks_nothing_and_the_rule_row_stays()
        {
            using var f = await ModelAsync("DateNoBar.bim");
            f.Engine.DateTableJevDecidesForTest = null;   // the family's own constant, set from the replay
            var oracle = await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);

            Assert.Equal(DateTableFamily.JevDecides, DateRows(plan).Any());
            if (!DateTableFamily.JevDecides)
            {
                Assert.DoesNotContain(oracle.Seen, r => r.PointName == Point);
                Assert.Contains(plan.Items, i => i.Id == "datetable/model");
            }
        }

        [Fact]
        public async Task Pro_puts_one_Mark_row_on_the_Plan_and_the_rule_row_stands_aside()
        {
            using var f = await ModelAsync("DatePro.bim");
            await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);
            var row = Assert.Single(DateRows(plan));

            Assert.Equal("jev.date-table.table:Calendar", row.Id);
            Assert.Equal("Mark Calendar as the date table", row.Title);
            Assert.Equal(ModelPlanSections.Structure, row.Section);
            Assert.Equal(ModelPlanOwners.Jev, row.Who);
            Assert.Equal(ModelPlanItemStates.Proposed, row.State);
            // Standing aside, and not recorded as done: the rule row was seen on the first read, and moving the work to
            // Jev's row is not the date table being finished.
            Assert.DoesNotContain(plan.Items, i => i.Id == "datetable/model");

            var line = Assert.Single(row.ChangeSet.Lines);
            Assert.Equal("mark_date_table", line.Op);
            Assert.Equal("table:Calendar", line.Target);
            Assert.Equal("Date", line.After);
            Assert.True(line.Kept);
            Assert.Equal(ModelPlanChangeKinds.Decision, line.Provenance.Kind);
            Assert.Equal(Point, line.Provenance.Point);
            Assert.Equal("sure", line.Provenance.ConfidenceWord);
            Assert.Contains("one row per day", line.Reason, StringComparison.Ordinal);
            Assert.Contains("measured on every row", line.Check.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2014", line.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2014", line.Check.Text, StringComparison.Ordinal);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Approve_marks_the_table_on_its_date_column_as_one_undo_step()
        {
            using var f = await ModelAsync("DateApprove.bim");
            await AnswerAsync(f);
            var row = Assert.Single(DateRows(await SettledPlanAsync(f)));

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", row.ChangeSet.Digest);

            Assert.True(result.Performed, result.Sentence);
            Assert.Equal(("Time", true), await MarkOf(f));
            await f.Engine.UndoAsync("human");
            Assert.Equal(("", false), await MarkOf(f));
        }

        [Fact]
        public async Task With_no_data_the_line_says_the_dates_were_not_measured()
        {
            using var f = await ModelAsync("DateNoData.bim");
            await AnswerAsync(f, measured: false);

            var line = Assert.Single(Assert.Single(DateRows(await SettledPlanAsync(f))).ChangeSet.Lines);

            Assert.Contains("could not measure", line.Check.Text, StringComparison.Ordinal);
        }

        // ---- the options the engine lists ---------------------------------------------------------------------

        [Fact]
        public async Task The_engine_lists_only_tables_that_can_be_a_date_table()
        {
            using var f = await ModelAsync("DateOptions.bim");
            var prepared = await f.Sessions.Current.ReadAsync(m => DateTableFamily.Prepare(m, Facts(measured: true)));

            var options = prepared.Ask.Questions.Single(q => q.Name == "date_table").Options.Select(o => o.Id).ToArray();
            Assert.Equal(new[] { "generate", "table:Calendar" }, options);
            Assert.Equal(new[] { "date_table" }, prepared.Ask.Questions.Select(q => q.Name).ToArray());
            // Sales is on the many side; Promotion has two date columns and no key; Returns Log's dates repeat.
            Assert.Contains(prepared.Refused, r => r.Target == "table:Promotion" && r.Reason.Contains("2 date columns", StringComparison.Ordinal));
            Assert.Contains(prepared.Refused, r => r.Target == "table:Returns Log" && r.Reason.Contains("more than one row", StringComparison.Ordinal));
            Assert.DoesNotContain(prepared.Refused, r => r.Target == "table:Sales");
        }

        [Fact]
        public async Task A_pick_that_was_never_offered_is_no_line()
        {
            using var f = await ModelAsync("DateBogus.bim");
            await AnswerAsync(f, choice: "table:Sales");

            Assert.Empty(DateRows(await SettledPlanAsync(f)));
            Assert.Equal(("", false), await MarkOf(f));
        }

        [Fact]
        public async Task A_model_that_is_already_marked_is_not_asked()
        {
            using var f = await ModelAsync("DateMarked.bim");
            await f.Engine.MarkDateTableAsync("table:Calendar", "Date", "human");

            var prepared = await f.Sessions.Current.ReadAsync(m => DateTableFamily.Prepare(m, Facts(measured: true)));

            Assert.Null(prepared.Ask);
        }

        // ---- the other answers -------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Generate_adds_a_calculated_date_table_as_one_undo_step()
        {
            using var f = await ModelAsync("DateGenerate.bim");
            await AnswerAsync(f, choice: "generate");

            var row = Assert.Single(DateRows(await SettledPlanAsync(f)));
            Assert.Equal("Add a date table", row.Title);
            var line = Assert.Single(row.ChangeSet.Lines);
            Assert.Equal("generate_date_table", line.Op);
            Assert.Equal("table:Date", line.Target);

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", row.ChangeSet.Digest);
            Assert.True(result.Performed, result.Sentence);
            var made = await f.Sessions.Current.ReadAsync(m => m.Tables.Contains("Date")
                ? (m.Tables["Date"].DataCategory, (m.Tables["Date"] as TabularEditor.TOMWrapper.CalculatedTable)?.Expression ?? "")
                : (null, null));
            Assert.Equal("Time", made.Item1);
            // The range is DAX over the fact's own date column: no date was read to write it.
            Assert.Contains("MIN('Sales'[OrderDate])", made.Item2, StringComparison.Ordinal);
            Assert.Contains("MAX('Sales'[OrderDate])", made.Item2, StringComparison.Ordinal);

            await f.Engine.UndoAsync("human");
            Assert.False(await f.Sessions.Current.ReadAsync(m => m.Tables.Contains("Date")));
        }

        [Fact]
        public async Task With_no_table_to_offer_or_no_time_intelligence_nothing_is_asked()
        {
            // No table to offer: a new one is the only answer, which is nobody's choice, so the rule's row keeps it.
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "orders", async b =>
            {
                await b.Engine.CreateModelAsync("Contoso", 1604);
                await b.Engine.CreateTableAsync("Orders", "human");
                await b.Engine.CreateColumnAsync("table:Orders", "OrderDate", "DateTime", null, "human");
                await b.Engine.CreateColumnAsync("table:Orders", "Amount", "Double", null, "human");
                await b.Engine.CreateMeasureAsync("table:Orders", "Total", "SUM ( Orders[Amount] )", "human");
                await b.Engine.CreateMeasureAsync("table:Orders", "Total YTD", "TOTALYTD ( [Total], Orders[OrderDate] )", "human");
            }, "DateOrders.bim");
            var repeats = new ModelFacts
            {
                Data = ModelFactsData.Local,
                Tables = new[] { new TableFacts { Ref = "table:Orders", Name = "Orders", Columns = new[] { new ColumnFacts
                {
                    Ref = "column:Orders/OrderDate", Name = "OrderDate", DataType = "DateTime",
                    Measured = new MeasuredFacts { UniqueOnEveryRow = false, ContiguousDays = true },
                } } } },
            };
            Assert.Null((await f.Sessions.Current.ReadAsync(m => DateTableFamily.Prepare(m, repeats))).Ask);

            // No time intelligence: whether a date table is needed is the rule's decision (DATE-MARK), and it says no.
            using var g = await ModelAsync("DateNoTi.bim");
            await g.Engine.DeleteObjectAsync("measure:Sales/Sales YTD", "human");
            Assert.Null((await g.Sessions.Current.ReadAsync(m => DateTableFamily.Prepare(m, Facts(measured: true)))).Ask);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Jev_when_sure_else_the_rule()
        {
            // Likely (0.7) and unsure (0.45) are both below the point's bar: Jev proposes nothing and the rule's row stays.
            foreach (var confidence in new[] { 0.7, 0.45 })
            {
                using var f = await ModelAsync("DateUnsure" + (int)(confidence * 100) + ".bim");
                await AnswerAsync(f, confidence: confidence);

                var plan = await SettledPlanAsync(f);
                Assert.Empty(DateRows(plan));
                Assert.Contains(plan.Items, i => i.Id == "datetable/model");
            }
        }

        // ---- what goes ---------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Only_names_types_and_yes_or_no_facts_go_to_Jev()
        {
            using var f = await ModelAsync("DateSent.bim");
            var oracle = await AnswerAsync(f);
            await SettledPlanAsync(f);

            var sent = Assert.Single(oracle.Seen.Where(r => r.PointName == Point));
            Assert.Equal(DecisionRegistry.Require(Point).SnapshotFields.OrderBy(x => x, StringComparer.Ordinal),
                         sent.Snapshot.Keys.OrderBy(x => x, StringComparer.Ordinal));
            var json = JsonSerializer.Serialize(sent.Snapshot);
            // The measured counts behind the facts never leave: only their yes or no does.
            Assert.DoesNotContain("1096", json, StringComparison.Ordinal);
            Assert.DoesNotContain("377", json, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(json);
            var calendar = doc.RootElement.GetProperty("model_tables").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Calendar");
            var date = Assert.Single(calendar.GetProperty("date_columns").EnumerateArray());
            Assert.Equal(new[] { "built_by_calendar_formula", "dates_repeat", "days_missing", "is_key", "name", "read_by_formulas" },
                         date.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal("no", date.GetProperty("dates_repeat").GetString());
            Assert.Equal("no", date.GetProperty("days_missing").GetString());
            Assert.True(doc.RootElement.GetProperty("model_facts").GetProperty("uses_time_intelligence").GetBoolean());
            Assert.Equal(new[] { "Sales" }, calendar.GetProperty("related_from").EnumerateArray().Select(x => x.GetString()).ToArray());
        }

        /// <summary>DT5 (round 3): What is sent for the date table has to name everything the request carries. It left
        /// out whether the model's formulas read a date column, whether a table is built by a formula, and the table and
        /// relationship counts, and then ended "or any other value", which those two counts make untrue. The asserts run
        /// off the REAL request, so dropping one of these facts fails here too and the words cannot start over-claiming
        /// in the other direction either.</summary>
        [Fact]
        public async Task What_is_sent_for_the_date_table_names_every_fact_the_request_carries()
        {
            using var f = await ModelAsync("DateSentCopy.bim");
            var oracle = await AnswerAsync(f);
            await SettledPlanAsync(f);

            var sent = Assert.Single(oracle.Seen.Where(r => r.PointName == Point));
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(sent.Snapshot));
            var calendar = doc.RootElement.GetProperty("model_tables").EnumerateArray()
                .Single(t => t.GetProperty("name").GetString() == "Calendar");
            var date = Assert.Single(calendar.GetProperty("date_columns").EnumerateArray());
            var facts = doc.RootElement.GetProperty("model_facts");

            // The request really does carry each of these, so the words have to say so.
            Assert.True(date.TryGetProperty("read_by_formulas", out _));
            Assert.True(calendar.TryGetProperty("is_calculated", out _));
            Assert.True(facts.TryGetProperty("tables", out _));
            Assert.True(facts.TryGetProperty("relationships", out _));

            var words = DecisionRegistry.Require(Point).Purpose;
            Assert.Contains("formulas read", words, StringComparison.Ordinal);
            Assert.Contains("built by a formula", words, StringComparison.Ordinal);
            Assert.Contains("how many tables and relationships", words, StringComparison.Ordinal);
            // Two counts do leave, so a blanket "any other value" would be a false claim.
            Assert.DoesNotContain("any other value", words, StringComparison.Ordinal);
        }

        // ---- what the date row waits for (lane m11/plan-waits, UAT 26 Sep) ---------------------------------------

        private static string[] Tasks(ModelPlanItem row) =>
            row.WaitsFor.Select(w => w.Id).Where(PlanTasks.IsTaskId).ToArray();

        private static async Task<string> TaskAsync(DecisionFixture f, string words, string objRef) =>
            (await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = words, Objects = new[] { objRef } })).ItemId;

        /// <summary>On Northstar the Jev row "Mark Date as the date table" waited for two tasks about Margin %, a ratio
        /// with no time intelligence. Marking a date table can change time intelligence and what filters on that table,
        /// nothing else, so only a task on such a measure holds the row.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_date_row_waits_only_for_tasks_on_what_the_mark_can_change()
        {
            using var f = await ModelAsync("DateWaits.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin %", "DIVIDE ( [Total Sales], COUNTROWS ( Sales ) )", "human");
            await AnswerAsync(f);
            var margin = await TaskAsync(f, "Fix Margin % to a ratio of totals", "measure:Sales/Margin %");
            var ytd = await TaskAsync(f, "Check Sales YTD against the finance pack", "measure:Sales/Sales YTD");

            var plan = await SettledPlanAsync(f);
            var row = Assert.Single(DateRows(plan));

            Assert.DoesNotContain("measure:Sales/Margin %", row.Reads);
            Assert.DoesNotContain(row.WaitsFor, w => w.Id == margin);
            Assert.Equal(new[] { ytd }, Tasks(row));
            Assert.Equal(PlanStateWords.Waiting, row.StateWord);
        }

        /// <summary>The same wait on the rule's row, before Run the plan works its line out: on Free (or before consent)
        /// "Mark Calendar as the date table" still waited for a task about a measure with no dates in it (UAT 26 Sep,
        /// lane m11/plan-waits, second pass).</summary>
        [Fact]
        public async Task The_rule_row_waits_only_for_tasks_on_what_its_mark_can_change()
        {
            using var f = await ModelAsync("DateRuleWaits.bim", pro: false);
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin %", "DIVIDE ( [Total Sales], COUNTROWS ( Sales ) )", "human");
            var margin = await TaskAsync(f, "Fix Margin % to a ratio of totals", "measure:Sales/Margin %");
            var ytd = await TaskAsync(f, "Check Sales YTD against the finance pack", "measure:Sales/Sales YTD");

            var plan = await SettledPlanAsync(f);
            var row = Assert.Single(plan.Items, i => i.Id == "datetable/model");

            Assert.Equal("Mark Calendar as the date table", row.Title);
            Assert.Null(row.ChangeSet);
            Assert.DoesNotContain("measure:Sales/Margin %", row.Reads);
            Assert.DoesNotContain(row.WaitsFor, w => w.Id == margin);
            Assert.Equal(new[] { ytd }, Tasks(row));
            Assert.Equal(PlanStateWords.Waiting, row.StateWord);
        }

        /// <summary>Contract 5.2: a paused row still counts as open, so the date row that waits for it stays Waiting,
        /// and its sub line names the paused row even when the plan's order puts another blocker first. Resumed, the
        /// plan's order is back.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Pausing_a_task_the_date_row_waits_for_keeps_it_waiting_and_names_it()
        {
            using var f = await ModelAsync("DatePaused.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "Sales LY", "CALCULATE ( [Total Sales], SAMEPERIODLASTYEAR ( 'Calendar'[Date] ) )", "human");
            await AnswerAsync(f);
            var ytd = await TaskAsync(f, "Check Sales YTD", "measure:Sales/Sales YTD");
            var ly = await TaskAsync(f, "Check Sales LY", "measure:Sales/Sales LY");
            var row = Assert.Single(DateRows(await SettledPlanAsync(f)));
            Assert.Equal(new[] { ytd, ly }, Tasks(row));

            await f.Engine.HoldPlanItemAsync(ly, true);
            var plan = await f.Engine.GetModelPlanAsync();
            row = Assert.Single(DateRows(plan));
            Assert.Equal(PlanStateWords.Paused, plan.Items.Single(i => i.Id == ly).StateWord);
            Assert.Equal(PlanStateWords.Waiting, row.StateWord);
            // The paused row is named FIRST, ahead of the rows the plan's order puts before it (a safe fix, the other task).
            Assert.Equal(ly, row.WaitsFor[0].Id);
            Assert.Equal(new[] { ly, ytd }, Tasks(row));
            Assert.StartsWith(PlanFootprint.WaitsForWords(row.WaitsFor[0].Doer) + "Check Sales LY and ", row.Sub, StringComparison.Ordinal);

            await f.Engine.HoldPlanItemAsync(ly, false);
            row = Assert.Single(DateRows(await f.Engine.GetModelPlanAsync()));
            Assert.NotEqual(ly, row.WaitsFor[0].Id);
            Assert.Equal(new[] { ytd, ly }, Tasks(row));
        }

        // ---- the rule's own date mark reads calculation items ---------------------------------------------------

        [Fact]
        public async Task The_rule_names_the_date_table_a_calculation_group_reads()
        {
            // Northstar's shape: time intelligence lives only in a calculation group. PlanDateMark read measures, calculated
            // columns and calculated tables only, so the rule could not name the table and the row stayed the person's.
            using var f = new DecisionFixture(TestEntitlements.Free);
            await _templates.OpenCopyAsync(f, "calcgroup", async b =>
            {
                var e = b.Engine;
                await e.CreateModelAsync("Contoso", 1604);
                await e.CreateTableAsync("Calendar", "human");
                await e.CreateColumnAsync("table:Calendar", "Date", "DateTime", null, "human");
                await e.CreateTableAsync("Sales", "human");
                await e.CreateColumnAsync("table:Sales", "OrderDate", "DateTime", null, "human");
                await e.CreateRelationshipAsync("column:Sales/OrderDate", "column:Calendar/Date", null, null, "human");
                var cg = await e.CreateCalculationGroupAsync("Time Intelligence", "human");
                await e.CreateCalculationItemAsync(cg, "YTD", "CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Calendar'[Date] ) )", "human");
            }, "DateCalcGroup.bim");

            var mark = await f.Sessions.Current.ReadAsync(m => PlanDateMark.Candidate(m));

            Assert.NotNull(mark);
            Assert.Equal("Calendar", mark.Table);
            Assert.Equal("Date", mark.Column);
        }

        // ---- the fact itself ---------------------------------------------------------------------------------

        [Fact]
        public void Contiguous_days_means_whole_days_with_none_missing()
        {
            var d = new DateTime(2024, 2, 27);
            Assert.True(Semanticus.Engine.Facts.ValueFacts.Compute(new object[] { d, d.AddDays(1), d.AddDays(2), d.AddDays(3) }).ContiguousDays);
            Assert.False(Semanticus.Engine.Facts.ValueFacts.Compute(new object[] { d, d.AddDays(2) }).ContiguousDays);
            Assert.False(Semanticus.Engine.Facts.ValueFacts.Compute(new object[] { d, d.AddDays(1).AddHours(6) }).ContiguousDays);
            var dup = Semanticus.Engine.Facts.ValueFacts.Compute(new object[] { d, d, d.AddDays(1) });
            Assert.True(dup.ContiguousDays);   // repeats are the uniqueness fact's job, not this one's
            Assert.True(dup.AllDates);
            Assert.False(Semanticus.Engine.Facts.ValueFacts.Compute(new object[] { 1L, 2L }).AllDates);
        }

        // ---- verify (m11/plan-waits + m11/m4-datetable, joint verifier 27 Sep) ------------------------------------

        /// <summary>The Jev line for a new table reads "Add Date and mark it as the date table", and the family says the
        /// engine always marks. Marking is the table's category AND its date column as the key (MarkDateTableCore, the
        /// same mark the Approve test above checks; BPA MODEL_SHOULD_HAVE_A_DATE_TABLE asks for both). The line the
        /// family writes runs generate_date_table, whose core sets the category only.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_a_new_date_table_the_line_says_it_marks_is_marked_on_its_date_column()
        {
            using var f = await ModelAsync("DateGenerateKey.bim");
            await AnswerAsync(f, choice: "generate");
            var row = Assert.Single(DateRows(await SettledPlanAsync(f)));
            var line = Assert.Single(row.ChangeSet.Lines);
            Assert.Equal("generate_date_table", line.Op);

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", row.ChangeSet.Digest);
            Assert.True(result.Performed, result.Sentence);
            var made = await f.Sessions.Current.ReadAsync(m => (
                Category: m.Tables["Date"].DataCategory,
                Columns: string.Join(", ", m.Tables["Date"].Columns.Select(c => c.Name + (c.IsKey ? " (key)" : ""))),
                Keyed: m.Tables["Date"].Columns.Any(c => c.IsKey && c.DataType == TabularEditor.TOMWrapper.DataType.DateTime)));
            Assert.Equal("Time", made.Category);
            Assert.True(made.Keyed, "the line says '" + line.Line + "' but no date column of the new table is its key; columns: [" + made.Columns + "]");
        }

        /// <summary>Integration note 2 (coordinator, 26 Sep): when a later date table ask gets no answer, the Jev line
        /// already on the plan must stay. On this lane the family files the unanswered objects by their bare refs, and the
        /// sync reads them the same way, so it holds here. m11/integration (568435d3) keys the unanswered set by point
        /// (JevRun.UnansweredKey); a merge that keeps this lane's bare refs drops the waiting line instead, and this test
        /// then fails. It passes on the lane on purpose: it is the test the note asks for.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("table:Calendar")]
        [InlineData("generate")]
        public async Task Verify_a_date_table_ask_that_gets_no_answer_keeps_the_line_already_on_the_plan(string choice)
        {
            using var f = await ModelAsync("DateAskFails-" + choice.Replace(':', '-') + ".bim");
            await AnswerAsync(f, choice: choice);
            var row = Assert.Single(DateRows(await SettledPlanAsync(f)));

            // A change the date table request reads (a new column on Calendar): the next run asks again with new evidence,
            // and the recorded service has no answer for it, so the ask comes back undecided.
            await f.Engine.CreateColumnAsync("table:Calendar", "Month", "Int64", null, "human");
            var after = await SettledPlanAsync(f);

            Assert.Contains(after.Items, i => i.Id == row.Id);
        }

        /// <summary>The rule's row standing aside for Jev's row is not the date table being done. a7c4766e kept it out of the
        /// Done register (newlyDone), but the History's redraw sentence (PlanMemory.RefreshNote) still counts every row
        /// that left the plan as done, on the rule that "a row that leaves means the gap went", which the stand-aside
        /// breaks. Seen live on Northstar (UAT lane g, 27 Sep): "1 item done, 4 new." at the moment Jev's row replaced
        /// the rule's, with nothing done, while that entry's own Completed list was empty.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_the_rule_row_standing_aside_for_Jev_is_not_told_as_done_in_History()
        {
            using var f = await ModelAsync("DateAsideHistory.bim");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "datetable/model");
            await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);
            Assert.Single(DateRows(plan));
            Assert.DoesNotContain(plan.Items, i => i.Id == "datetable/model");

            var entry = plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
            Assert.Contains("Mark Calendar as the date table", entry.Gone);
            Assert.Empty(entry.Completed);
            Assert.DoesNotContain("done", entry.Sentence, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>With no data the line's own check says Semanticus could not measure the dates, and its reason still
        /// says the table "has one row per day": a claim nothing checked, on the same line that says it was not checked.
        /// Seen live on Northstar, which is opened from files (UAT lane g, 27 Sep): "Date has one row per day in Date"
        /// beside "Semanticus could not measure Date, because this model has no data here."</summary>
        [Fact]
        public async Task Verify_with_no_data_the_line_does_not_say_the_table_has_one_row_per_day()
        {
            using var f = await ModelAsync("DateNoDataReason.bim");
            await AnswerAsync(f, measured: false);

            var line = Assert.Single(Assert.Single(DateRows(await SettledPlanAsync(f))).ChangeSet.Lines);

            Assert.Contains("could not measure", line.Check.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("one row per day", line.Reason, StringComparison.Ordinal);
        }

        /// <summary>Round 3's stand-aside test (LocalEngine.Plan.cs, StoodAside) asks only whether a sure Jev date line is
        /// waiting, not whether the rule's row would still be on the plan. So when the person marks the date table
        /// THEMSELVES while Jev's line is still waiting (it waits until the run for the new revision finds nothing to
        /// ask), the rule's row leaves because its gap is gone, and History tells that as "replaced by a suggestion" and
        /// the Done register never records it. Latent while the point is off; the wiring stays in the build.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_a_date_table_the_person_marks_while_Jevs_line_waits_is_told_as_done()
        {
            using var f = await ModelAsync("DateMarkedMeanwhile.bim");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "datetable/model");
            await AnswerAsync(f);
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "datetable/model");
            await f.Engine.JevSettledForTest();

            await f.Engine.MarkDateTableAsync("table:Calendar", "Date", "human");
            var plan = await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            var settled = await f.Engine.GetModelPlanAsync();

            Assert.Equal(("Time", true), await MarkOf(f));
            var entry = plan.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
            var told = entry.Sentence + " | gone: " + string.Join(", ", entry.Gone ?? Array.Empty<string>())
                     + " | completed: " + string.Join(", ", entry.Completed ?? Array.Empty<string>());
            Assert.Contains(entry.Gone ?? Array.Empty<string>(), g => g.Contains("date table", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("replaced by a suggestion", entry.Sentence, StringComparison.Ordinal);
            Assert.True((entry.Completed ?? Array.Empty<string>()).Any(c => c.Contains("date table", StringComparison.OrdinalIgnoreCase)), told);
            Assert.Contains(settled.Items, i => i.Id == "datetable/model" && i.Status == ModelPlanStatuses.Done);
        }

        [Fact]
        public void A_calendar_formula_is_read_off_the_expression()
        {
            Assert.True(DateTableFamily.BuiltByCalendar("ADDCOLUMNS ( CALENDAR ( DATE ( 2023, 1, 1 ), DATE ( 2025, 12, 31 ) ), \"Year\", YEAR ( [Date] ) )"));
            Assert.True(DateTableFamily.BuiltByCalendar("CALENDARAUTO ( )"));
            Assert.False(DateTableFamily.BuiltByCalendar("DATATABLE ( \"Date\", DATETIME, { { \"2024-01-01\" } } )"));
            Assert.False(DateTableFamily.BuiltByCalendar("// CALENDAR ( ) in a comment\n{ 1 }"));
        }
    }
}
