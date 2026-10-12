using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Round 7 of m11/sql-start (the fix for verifier round 6, held-out FAIL), the date table half. A date table is
    /// known by its STRUCTURE, not by its name: a table with a date column and nothing else but calendar parts, that a fact
    /// reaches by an active direct link of its own, is the source's date table whether it is called dim_date, dim_day or
    /// Days. A source with none gets none added and none marked, because a calendar nothing links to filters nothing.
    /// Sol's 15 held-out schemas are in SqlDraftVerifyR6Tests and are not repeated here; these are the shapes around
    /// them that the round 6 fix has to get right the other way too (a dimension that only holds a timestamp is NOT a
    /// date table).</summary>
    public sealed class SqlDraftFixR7Tests
    {
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricTable T(string name, string[] keys, params FabricColumn[] cols) =>
            new FabricTable { Schema = "dbo", Name = name, KeyColumns = keys, Columns = cols };

        private static FabricForeignKey Fk(string from, string fromCol, string to, string toCol) => new FabricForeignKey
        { FromSchema = "dbo", FromTable = from, FromColumn = fromCol, ToSchema = "dbo", ToTable = to, ToColumn = toCol };

        private static ModelSpec Draft(FabricSchema s) => LocalEngine.FabricSchemaToSpec(s, "x", "DW", "import");

        /// <summary>One fact with a key to a day table and to a dimension. The day table's name, key and column names vary.</summary>
        private static FabricSchema OneFactOneDay(FabricTable day, string fkColumn, string dayKey) => new FabricSchema
        {
            Tables = new[]
            {
                day,
                T("dim_store", new[] { "STORE_ID" }, C("STORE_ID", "String"), C("STORE_NAME", "String")),
                T("fact_sale", new[] { "SALE_ID" }, C("SALE_ID", "String"), C(fkColumn, "Int64"), C("STORE_ID", "String"), C("AMOUNT", "Decimal")),
            },
            ForeignKeys = new[]
            {
                Fk("fact_sale", fkColumn, day.Name, dayKey),
                Fk("fact_sale", "STORE_ID", "dim_store", "STORE_ID"),
            },
        };

        public static IEnumerable<object[]> DayTablesWithNoDateInTheName()
        {
            // The grain in the name, the way Sol's sources write it.
            yield return new object[] { "dim_day", "DAY_ID", "DAY_DATE", "YEAR_NO" };
            yield return new object[] { "dim_billing_day", "DAY_KEY", "BILL_DATE", "MONTH_NO" };
            // No date word and no day word at all: the structure is the only evidence (a key, one date column, calendar parts).
            yield return new object[] { "dim_period", "PERIOD_KEY", "PERIOD_START", "FISCAL_YEAR" };
            yield return new object[] { "ref_z7", "K", "D", "YEAR_NO" };
        }

        /// <summary>A day table with no date or calendar word in its name is still the date table, and the draft does not add a
        /// CALENDAR beside it. Fails at 608fa69d: only a name with date, calendar, dimdate or datedim was ever a candidate.</summary>
        [Theory]
        [MemberData(nameof(DayTablesWithNoDateInTheName))]
        public void A_day_table_is_the_date_table_whatever_it_is_called(string name, string key, string dateColumn, string partColumn)
        {
            var day = T(name, new[] { key }, C(key, "Int64"), C(dateColumn, "DateTime"), C(partColumn, "Int64"));
            var spec = Draft(OneFactOneDay(day, "DAY_REF", key));

            Assert.Equal(name, spec.DateTable.Name);
            Assert.Equal(dateColumn, spec.DateTable.DateColumn);
            Assert.True(spec.DateTable.MarkAsDate);
            Assert.DoesNotContain(spec.Tables, t => t.Name == "Date");
            Assert.Equal("date", spec.Tables.Single(t => t.Name == name).Role);
            Assert.Equal(new[] { "fact_sale" }, spec.Measures.Where(m => (spec.TimeIntelligenceBaseMeasures ?? Array.Empty<string>()).Contains(m.Name)).Select(m => m.Table).Distinct().ToArray());
        }

        /// <summary>A date column that IS the key (one row per day by definition) is the same evidence, with any name.</summary>
        [Fact]
        public void A_table_whose_key_is_a_date_is_the_date_table()
        {
            var days = T("Days", new[] { "Day" }, C("Day", "DateTime"), C("YearNo", "Int64"));
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    days,
                    T("fact_visit", new[] { "VISIT_ID" }, C("VISIT_ID", "String"), C("VISIT_DAY", "DateTime"), C("MINUTES", "Int64")),
                },
                ForeignKeys = new[] { Fk("fact_visit", "VISIT_DAY", "Days", "Day") },
            };
            var spec = Draft(schema);

            Assert.Equal("Days", spec.DateTable.Name);
            Assert.Equal("Day", spec.DateTable.DateColumn);
            Assert.DoesNotContain(spec.Tables, t => t.Name == "Date");
        }

        public static IEnumerable<object[]> DimensionsThatOnlyHoldATimestamp()
        {
            // A customer with a sign-up time, a trip with a departure time, an employee with a hire date and a salary: each
            // is reached by a fact's own link and each has a date column, and none is one row per day. Marking one as the
            // date table would fail the model's own uniqueness check, and every time measure over it would be wrong.
            yield return new object[] { T("dim_customer", new[] { "CUSTOMER_ID" }, C("CUSTOMER_ID", "String"), C("CUSTOMER_NAME", "String"), C("SIGNED_UP_AT", "DateTime")) };
            yield return new object[] { T("dim_trip", new[] { "TRIP_ID" }, C("TRIP_ID", "String"), C("ROUTE_NAME", "String"), C("DEPART_AT", "DateTime")) };
            yield return new object[] { T("dim_employee", new[] { "EMPLOYEE_ID" }, C("EMPLOYEE_ID", "String"), C("HIRED_ON", "DateTime"), C("SALARY", "Decimal")) };
            yield return new object[] { T("dim_contract", new[] { "CONTRACT_ID" }, C("CONTRACT_ID", "String"), C("START_DATE", "DateTime"), C("END_DATE", "DateTime")) };
        }

        /// <summary>The guard for the structural rule: a dimension with a timestamp is not a date table, and the draft says
        /// there is none rather than marking it. Passes at 608fa69d (its name never said date) and must keep passing.</summary>
        [Theory]
        [MemberData(nameof(DimensionsThatOnlyHoldATimestamp))]
        public void A_dimension_that_only_holds_a_timestamp_is_not_a_date_table(FabricTable dim)
        {
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    dim,
                    T("fact_sale", new[] { "SALE_ID" }, C("SALE_ID", "String"), C("DIM_ID", "String"), C("AMOUNT", "Decimal")),
                },
                ForeignKeys = new[] { Fk("fact_sale", "DIM_ID", dim.Name, dim.KeyColumns[0]) },
            };
            var spec = Draft(schema);

            Assert.True(string.IsNullOrEmpty(spec.DateTable.Name), $"marked {spec.DateTable.Name}");
            Assert.False(spec.DateTable.MarkAsDate);
            Assert.DoesNotContain(spec.Tables, t => t.Role == "date");
            Assert.Empty(spec.TimeIntelligenceBaseMeasures ?? Array.Empty<string>());
        }

        private static FabricSchema SalesWithItsOwnDateAndNoDateTable() => new FabricSchema
        {
            Tables = new[]
            {
                T("Sales", new[] { "Id" }, C("Id", "Int64"), C("CustomerId", "Int64"), C("OrderDate", "DateTime"), C("Amount", "Decimal")),
                T("Customer", new[] { "CustomerId" }, C("CustomerId", "Int64"), C("Name", "String")),
            },
            ForeignKeys = new[] { Fk("Sales", "CustomerId", "Customer", "CustomerId") },
        };

        /// <summary>Ruling 1, the other half: a source with no date table gets no CALENDAR and marks nothing. A calendar that no
        /// fact links to is marked in the draft but filters nothing, and every later time calculation, by a person, the
        /// assistant or Copilot, is written against it and returns the plain total. The note says what can be done, and names
        /// the fact that has a date column of its own to link to one. Fails at 608fa69d, which added and marked "Date".</summary>
        [Fact]
        public void A_source_with_no_date_table_gets_no_calendar_and_no_mark_and_the_note_names_the_fact_that_can_be_linked()
        {
            var spec = Draft(SalesWithItsOwnDateAndNoDateTable());

            Assert.True(string.IsNullOrEmpty(spec.DateTable.Name), $"drafted a date table called {spec.DateTable.Name}");
            Assert.False(spec.DateTable.MarkAsDate);
            Assert.DoesNotContain(spec.Tables, t => t.Name == "Date" || t.Role == "date");
            Assert.Empty(spec.TimeIntelligence ?? Array.Empty<string>());
            Assert.Empty(spec.TimeIntelligenceBaseMeasures ?? Array.Empty<string>());
            Assert.Contains("no date table", spec.DateTable.Note, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Sales", spec.DateTable.Note);
            Assert.Contains("OrderDate", spec.DateTable.Note);
            Assert.DoesNotContain("Plan", spec.DateTable.Note);
            Assert.DoesNotContain("—", spec.DateTable.Note);
        }

        /// <summary>The same draft built on the engine: no error, no table marked as a date table, and the note reaches the report.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_source_with_no_date_table_builds_with_no_marked_table_and_the_report_carries_the_note()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("NoDateTable", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(Draft(SalesWithItsOwnDateAndNoDateTable()), Camel), "human");

            var report = await engine.BuildModelFromSpecAsync("human");

            Assert.Empty(report.Errors);
            var doc = await engine.GetDocModelAsync(0);
            Assert.DoesNotContain(doc.Tables, t => t.IsDateTable);
            Assert.DoesNotContain(doc.Tables, t => t.Name == "Date");
            Assert.Contains(report.Notes, n => n.Contains("no date table", StringComparison.OrdinalIgnoreCase) && n.Contains("OrderDate"));
        }

        /// <summary>A source with no date column anywhere has nothing to link, so there is no date note to write: no calendar,
        /// no mark, and no note that talks about dates at all.</summary>
        [Fact]
        public void A_source_with_no_date_column_at_all_says_nothing_about_dates()
        {
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    T("Sales", new[] { "Id" }, C("Id", "Int64"), C("CustomerId", "Int64"), C("Amount", "Decimal")),
                    T("Customer", new[] { "CustomerId" }, C("CustomerId", "Int64"), C("Name", "String")),
                },
                ForeignKeys = new[] { Fk("Sales", "CustomerId", "Customer", "CustomerId") },
            };
            var spec = Draft(schema);

            Assert.True(string.IsNullOrEmpty(spec.DateTable.Name));
            Assert.False(spec.DateTable.MarkAsDate);
            Assert.DoesNotContain(spec.Tables, t => t.Name == "Date");
        }
    }

    /// <summary>Round 7, the ask half: whether an ask is a request to MAKE a model from a source is read by its intent
    /// (AskMakeIntent), the engine's door is the same on route_ask and the RPC routeAsk (both call RouteAskAsync), and the one
    /// case the words cannot settle asks the decision service exactly once. The list is test/model-from-sql-asks.json, which the
    /// bar's test reads too.</summary>
    public sealed class AskMakeIntentFixR7Tests
    {
        private static string ListPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return Path.Combine(dir!.FullName, "Semanticus.VSCode", "test", "model-from-sql-asks.json");
        }

        private static (string Ask, bool Place)[] Asks()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ListPath()));
            return doc.RootElement.GetProperty("asks").EnumerateArray()
                .Select(a => (a.GetProperty("ask").GetString()!, a.GetProperty("place").GetBoolean())).ToArray();
        }

        /// <summary>With no decision service (the tested path, and every free session) the engine's door sends an ask to the Model
        /// Spec exactly when the list says it is a request to make a model, and never sends one about a model that exists.</summary>
        [Fact]
        public async Task The_engines_door_opens_the_model_spec_for_a_request_to_make_a_model_and_for_nothing_else()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskMakeIntentR7.bim");
            f.UseNull();
            var wrong = new List<string>();
            foreach (var (ask, place) in Asks())
            {
                var result = await f.Engine.RouteAskAsync(ask);
                if ((result.Tool == "spec") != place || result.Asked) wrong.Add($"\"{ask}\" (want {(place ? "the Model Spec" : "not the Model Spec")}, got {result.Tool ?? "no place"})");
            }
            Assert.True(wrong.Count == 0, string.Join("; ", wrong));
        }

        private static (string Id, string Ask, bool Opens)[] RoundSevenAsks()
        {
            var path = Path.Combine(Path.GetDirectoryName(ListPath())!, "sql-start-r7-asks.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("asks").EnumerateArray()
                .Select(a => (a.GetProperty("id").GetString()!, a.GetProperty("ask").GetString()!, a.GetProperty("opens").GetBoolean())).ToArray();
        }

        /// <summary>The m11 join, part 2, item 7 (30 Sep): the verifier's round 7 attack list. At 0ad1c1be 15 of its 40 asks about a
        /// model that exists opened the Model Spec by the words alone ("generate model documentation from the sql database", "turn
        /// this sql model into a direct lake model"). A word that only names what is made ("model measures") is not the thing made, a
        /// model is never the source a convert verb turns into a new one, and "the model ... again" is one that exists. The guards
        /// (G rows) are requests to make one that the narrowed rule must still open.</summary>
        [Fact]
        public async Task The_round_seven_asks_about_a_model_that_exists_do_not_open_the_model_spec_by_the_words()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskMakeIntentR7Attack.bim");
            f.UseNull();
            var wrong = new List<string>();
            var rows = RoundSevenAsks();
            Assert.Equal(56, rows.Length);
            foreach (var (id, ask, opens) in rows)
            {
                var intent = AskMakeIntent.Classify(ask);
                var result = await f.Engine.RouteAskAsync(ask);
                if ((result.Tool == "spec") != opens || (intent == MakeIntent.Yes) != opens || result.Asked)
                    wrong.Add($"{id} \"{ask}\" (want {(opens ? "the Model Spec" : "not the Model Spec")}, got {result.Tool ?? "no place"}, words read {intent})");
            }
            Assert.True(wrong.Count == 0, wrong.Count + " wrong: " + string.Join("; ", wrong));
        }

        /// <summary>Answers the box's questions from a script: the place question is always "none", the intent question is the choice
        /// and confidence the test names. Counts every request it gets.</summary>
        private sealed class IntentOracle : IDecisionOracle
        {
            private readonly string _intent;
            private readonly double _confidence;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public IntentOracle(string intent, double confidence) { _intent = intent; _confidence = confidence; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.ToList(); } }
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                var sb = new StringBuilder("{\"entries\":[{\"digest\":").Append(JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":300,\"output_tokens\":20},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    var choice = q.Name == AskRoutePoint.IntentQuestion ? _intent : AskRouteCatalog.NoTool;
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                      .Append(",\"confidence\":").Append(_confidence.ToString(CultureInfo.InvariantCulture)).Append('}');
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }

        private const string Unsure = "get my warehouse into a semantic model";

        private static async Task<(DecisionFixture F, IntentOracle O)> WithService(string intent, double confidence)
        {
            var f = new DecisionFixture();
            await f.BuildModelAsync("AskMakeIntentR7Jev.bim");
            var oracle = new IntentOracle(intent, confidence);
            f.Engine.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
            await f.ConsentAsync();
            return (f, oracle);
        }

        /// <summary>The one case the words cannot settle asks the service once, typed: a new model, this model, or neither.</summary>
        [Fact]
        public async Task An_ask_the_words_cannot_settle_is_asked_once_and_a_new_model_opens_the_model_spec()
        {
            var (f, oracle) = await WithService(AskRoutePoint.IntentNewModel, 0.95);
            using (f)
            {
                Assert.Equal(MakeIntent.Unsure, AskMakeIntent.Classify(Unsure));

                var result = await f.Engine.RouteAskAsync(Unsure);

                Assert.Equal("spec", result.Tool);
                Assert.True(result.Asked);
                Assert.Equal(1, oracle.Seen.Count);
                var intent = oracle.Seen[0].Questions.Single(q => q.Name == AskRoutePoint.IntentQuestion);
                Assert.Equal(DecisionQuestionKind.Choice, intent.Kind);
                Assert.Equal(new[] { "new_model", "this_model", "neither" }, intent.Options.Select(o => o.Id).ToArray());
                Assert.Equal(new[] { "ask_text" }, oracle.Seen[0].Snapshot.Keys.ToArray());
            }
        }

        [Theory]
        [InlineData("this_model", 0.95)]
        [InlineData("neither", 0.95)]
        [InlineData("new_model", 0.5)]
        public async Task A_service_answer_that_is_not_a_confident_new_model_does_not_open_the_model_spec(string intent, double confidence)
        {
            var (f, oracle) = await WithService(intent, confidence);
            using (f)
            {
                var result = await f.Engine.RouteAskAsync(Unsure);

                Assert.NotEqual("spec", result.Tool);
                Assert.Equal(1, oracle.Seen.Count);
            }
        }

        /// <summary>The service is not asked about an ask the words settle: not a request to make a model (yes), not an ask about a
        /// model that exists (no), and not one a page's words already place. Only the unsure ones reach it, and each only once.</summary>
        [Theory]
        [InlineData("make a new model from the data warehouse")]
        [InlineData("connect to SQL and create a model")]
        [InlineData("refresh the model we made from the warehouse")]
        [InlineData("create a new report from this warehouse model")]
        [InlineData("publish the model we built from SQL")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task An_ask_the_words_settle_never_reaches_the_intent_question(string ask)
        {
            var (f, oracle) = await WithService(AskRoutePoint.IntentNewModel, 0.99);
            using (f)
            {
                Assert.NotEqual(MakeIntent.Unsure, AskMakeIntent.Classify(ask));

                var result = await f.Engine.RouteAskAsync(ask);

                Assert.DoesNotContain(oracle.Seen, r => r.Questions.Any(q => q.Name == AskRoutePoint.IntentQuestion));
                if (AskMakeIntent.Classify(ask) == MakeIntent.Yes) Assert.Equal("spec", result.Tool);
                else Assert.NotEqual("spec", result.Tool);
            }
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("make a new model from the data warehouse")]
        public Task An_ask_the_words_settle_never_reaches_the_intent_question_sample(string ask) => An_ask_the_words_settle_never_reaches_the_intent_question(ask);

        /// <summary>With no service the unsure ask is routed like any other, and nothing is sent.</summary>
        [Fact]
        public async Task With_no_service_an_unsure_ask_is_not_the_model_spec_and_nothing_is_sent()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskMakeIntentR7Null.bim");
            var oracle = f.UseEmptyRecorded();
            // No consent given: the service is not asked.
            var result = await f.Engine.RouteAskAsync(Unsure);

            Assert.NotEqual("spec", result.Tool);
            Assert.Empty(oracle.AskedDigests);
        }

        /// <summary>The registry declares the intent question beside the place question, and no new field is sent for it: the request
        /// still carries only the words typed, so the consent the person gave still covers it.</summary>
        [Fact]
        public void The_intent_question_is_declared_on_the_ask_point_and_sends_no_new_field()
        {
            var point = DecisionRegistry.Require(DecisionRegistry.AskRoute);
            Assert.Contains(point.Questions, q => q.Name == "model_intent" && q.Kind == DecisionQuestionKind.Choice);
            Assert.Equal(new[] { "ask_text" }, point.SnapshotFields.ToArray());
        }
    }
}
