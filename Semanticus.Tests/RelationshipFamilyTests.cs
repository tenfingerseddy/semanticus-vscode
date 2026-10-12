// The relationship family (T268, M4 point 2): for a column no relationship uses yet, which key on another table it
// joins, which way a filter passes, many to one or one to one, and the main date when several dates compete.
//
// WHAT IS PROVED HERE, AND HOW.
//   Free: no relationship item, nothing asked, no run started, even with the replay bar forced met.
//   Below the bar (the shipped constant): nothing is asked for relationship.join, and the Plan keeps today's rule
//   item "Sort out N relationships".
//   Pro, bar met: one item per table, "Add N relationships in T", owned by Jev, in Structure, every line a
//   create_relationship decision with an engine-built reason and check. The main date is active; the other date
//   to the same table arrives inactive and says why.
//   Picks come only from the options: a key that was not listed for the column, or none, is no line.
//   Keys are checked: a key of another type is never listed, a key the facts show repeating is never listed, a
//   key measured in full says so, and a relationship that would make a second filter path arrives inactive (or,
//   both ways, is refused).
//   Approve adds the kept relationships as one undo step.
//   What goes to Jev: the five declared fields, names, types and flags only.
//   The rule the replay scores against matches names the way it says it does.
//
// THE DIGESTS ARE COMPUTED FROM THE FAMILY'S OWN REQUEST (RelationshipFamily.Prepare, the call the run makes), so a
// run that sent a different request would find no answer here.

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
using TabularEditor.TOMWrapper;
using Facts = Semanticus.Engine.Facts;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class RelationshipFamilyTests : IClassFixture<ModelTemplates>
    {
        private const string Point = "relationship.join";

        // Lean suite (8 Oct): each model is built once per class (ModelTemplates) and every test opens its own copy.
        private readonly ModelTemplates _templates;
        public RelationshipFamilyTests(ModelTemplates templates) => _templates = templates;

        /// <summary>A Contoso-style star with no relationships yet. Sales points at Customer, Product and Date (twice:
        /// the order date and the ship date); Returns points at Product. Amount is a number, OrderNumber is text, so
        /// neither has a key to join.</summary>
        private async Task<DecisionFixture> ModelAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await _templates.OpenCopyAsync(f, "star", BuildStarAsync, file);
            f.UseNull();
            return f;
        }

        private static async Task BuildStarAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await e.CreateTableAsync("Product", "human");
            await e.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Product", "Category", "String", null, "human");
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "DateKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Date", "Date", "DateTime", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "OrderDateKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "ShipDateKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "OrderNumber", "String", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateTableAsync("Returns", "human");
            await e.CreateColumnAsync("table:Returns", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
        }

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

        private static async Task<RelationshipFamily.Prepared> PrepareAsync(DecisionFixture f) =>
            await f.Sessions.Current.ReadAsync(m => RelationshipFamily.Prepare(m, null));

        /// <summary>One recorded reply: a join, a direction, and a date role when that question was asked.</summary>
        private sealed record Reply(string Column, string Key, double Confidence, string Direction = "one_way", string Role = null);

        private static async Task<string> FixtureAsync(DecisionFixture f, IEnumerable<Reply> replies)
        {
            var point = DecisionRegistry.Require(Point);
            var prepared = await PrepareAsync(f);
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var r in replies)
            {
                var ask = prepared.Asks.Single(a => a.TableName + "/" + a.ColumnName == r.Column);
                var digest = new DecisionRequest(point.Name, point.SchemaVersion, ask.Snapshot, ask.Questions).Digest;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":\"").Append(Point).Append('"')
                  .Append(",\"usage\":{\"input_tokens\":700,\"output_tokens\":30}")
                  .Append(",\"answers\":{");
                sb.Append("\"join_to\":").Append(Choice(r.Key, r.Confidence));
                sb.Append(",\"filter_direction\":").Append(Choice(r.Direction, 0.95));
                if (ask.Questions.Any(q => q.Name == "cardinality")) sb.Append(",\"cardinality\":").Append(Choice("many_to_one", 0.95));
                if (ask.Questions.Any(q => q.Name == "date_role")) sb.Append(",\"date_role\":").Append(Choice(r.Role ?? "other", 0.9));
                sb.Append("}}");
            }
            return sb.Append("]}").ToString();
        }

        private static string Choice(string id, double confidence) =>
            "{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(id) + ",\"confidence\":"
            + confidence.ToString(CultureInfo.InvariantCulture) + "}";

        private static IEnumerable<Reply> Standard(string customerKey = "key:Customer/CustomerKey") => new[]
        {
            new Reply("Sales/CustomerKey", customerKey, 0.96),
            new Reply("Sales/ProductKey", "key:Product/ProductKey", 0.97),
            new Reply("Sales/OrderDateKey", "key:Date/DateKey", 0.93, Role: "main"),
            new Reply("Sales/ShipDateKey", "key:Date/DateKey", 0.9, Role: "other"),
            new Reply("Returns/ProductKey", "key:Product/ProductKey", 0.4),
        };

        /// <summary>Pro, permission given, the replay bar forced met, and Jev's answers recorded.</summary>
        private static async Task<CapturingOracle> AnswerAsync(DecisionFixture f, IEnumerable<Reply> replies = null, bool barMet = true)
        {
            await f.ConsentAsync();
            f.Engine.RelationshipJevDecidesForTest = barMet;
            var recorded = new RecordedDecisionOracle(await FixtureAsync(f, replies ?? Standard()), "jev-test-1");
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

        private static Task<List<(string From, string To, bool Active, string Filter)>> RelationshipsAsync(DecisionFixture f) =>
            f.Sessions.Current.ReadAsync(m => m.Relationships.OfType<SingleColumnRelationship>()
                .Select(r => (r.FromTable.Name + "/" + r.FromColumn.Name, r.ToTable.Name + "/" + r.ToColumn.Name, r.IsActive,
                              r.CrossFilteringBehavior.ToString()))
                .OrderBy(x => x.Item1, StringComparer.Ordinal).ToList());

        // ---- free -------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_free_session_asks_nothing_and_has_no_relationship_item()
        {
            using var f = await ModelAsync("RelFree.bim", pro: false);
            await f.ConsentAsync();
            f.Engine.RelationshipJevDecidesForTest = true;   // even with the bar met, free never asks
            var oracle = f.UseRecorded("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[]}");

            var plan = await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();

            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("jev.relationship.", StringComparison.Ordinal));
            Assert.Equal(0, oracle.AskCount);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
            Assert.Equal(0, f.Engine.JevRunsStartedForTest);
        }

        // ---- below the bar ----------------------------------------------------------------------------------

        [Fact]
        public async Task Below_the_bar_nothing_is_asked_and_the_rule_item_stays()
        {
            using var f = await ModelAsync("RelBelow.bim");
            // The shipped constant, not a forced value: the point is not live until its replay clears the bar.
            var oracle = await AnswerAsync(f, barMet: false);
            f.Engine.RelationshipJevDecidesForTest = null;

            var plan = await SettledPlanAsync(f);

            Assert.DoesNotContain(oracle.Seen, r => r.PointName == Point);
            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("jev.relationship.", StringComparison.Ordinal));
            var rule = Assert.Single(plan.Items, i => i.Id == "proposal/relationships");
            Assert.StartsWith("Fix ", rule.Title, StringComparison.Ordinal);   // "Sort out N relationships" became "Fix N relationships with model warnings" (6343ed4b, hero15 words)
        }

        // ---- Pro, bar met -----------------------------------------------------------------------------------

        [Fact]
        public async Task Pro_puts_one_Add_N_relationships_row_per_table_on_the_Plan()
        {
            using var f = await ModelAsync("RelPro.bim");
            await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);
            var rows = plan.Items.Where(i => i.Id.StartsWith("jev.relationship.", StringComparison.Ordinal))
                                 .OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();

            Assert.Equal(new[] { "jev.relationship.table:Returns", "jev.relationship.table:Sales" }, rows.Select(i => i.Id).ToArray());
            var sales = rows.Single(i => i.Id == "jev.relationship.table:Sales");
            Assert.Equal("Add 4 relationships in Sales", sales.Title);
            Assert.Equal(ModelPlanSections.Structure, sales.Section);
            Assert.Equal(ModelPlanOwners.Jev, sales.Who);
            Assert.Equal(ModelPlanItemStates.Proposed, sales.State);
            Assert.False(sales.ChangeSet.Stale, sales.ChangeSet.StaleReason);

            foreach (var line in rows.SelectMany(i => i.ChangeSet.Lines))
            {
                Assert.Equal("create_relationship", line.Op);
                // Returns' join is the rule's (Jev was unsure of it), so its provenance names the rule, not the point.
                if (line.Target != "column:Returns/ProductKey")
                {
                    Assert.Equal(ModelPlanChangeKinds.Decision, line.Provenance.Kind);
                    Assert.Equal(Point, line.Provenance.Point);
                }
                Assert.False(string.IsNullOrWhiteSpace(line.Reason), line.Id);
                Assert.True(line.Check.Exists, line.Id);
                Assert.DoesNotContain("\u2014", line.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("\u2014", line.Check.Text, StringComparison.Ordinal);
            }

            string After(string target) => sales.ChangeSet.Lines.Single(l => l.Target == target).After;
            Assert.Equal("Customer[CustomerKey], many to one, filters one way, active", After("column:Sales/CustomerKey"));
            Assert.Equal("Date[DateKey], many to one, filters one way, active", After("column:Sales/OrderDateKey"));
            Assert.Equal("Date[DateKey], many to one, filters one way, inactive", After("column:Sales/ShipDateKey"));
            var ship = sales.ChangeSet.Lines.Single(l => l.Target == "column:Sales/ShipDateKey");
            Assert.Contains("OrderDateKey is the main date for Sales", ship.Reason, StringComparison.Ordinal);
            // No data is connected here, so the check says the one side was not checked for repeats, and why it is the one side.
            Assert.Contains("could not check Customer[CustomerKey] for repeats", sales.ChangeSet.Lines.Single(l => l.Target == "column:Sales/CustomerKey").Check.Text, StringComparison.Ordinal);

            // Jev was unsure of Returns' only join, so the Pro path takes the rule's join, which arrives kept and says it
            // is the rule's (26 Sep: "Jev when sure, else the rule", per question, as the ai.schema family does).
            var returns = rows.Single(i => i.Id == "jev.relationship.table:Returns");
            var ruled = Assert.Single(returns.ChangeSet.Lines);
            Assert.Equal(ModelPlanChangeKinds.Rule, ruled.Provenance.Kind);
            Assert.Equal(RelationshipFamily.RuleId, ruled.Provenance.Rule);
            Assert.Equal("Product[ProductKey], many to one, filters one way, active", ruled.After);
            Assert.True(ruled.Kept);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Approve_adds_the_kept_relationships_as_one_undo_step()
        {
            using var f = await ModelAsync("RelApprove.bim");
            await AnswerAsync(f);
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.relationship.table:Sales");

            var result = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest);

            Assert.True(result.Performed, result.Sentence);
            var rels = await RelationshipsAsync(f);
            Assert.Equal(new[]
            {
                ("Sales/CustomerKey", "Customer/CustomerKey", true, "OneDirection"),
                ("Sales/OrderDateKey", "Date/DateKey", true, "OneDirection"),
                ("Sales/ProductKey", "Product/ProductKey", true, "OneDirection"),
                ("Sales/ShipDateKey", "Date/DateKey", false, "OneDirection"),
            }, rels.ToArray());

            await f.Engine.UndoAsync("human");
            Assert.Empty(await RelationshipsAsync(f));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_second_read_changes_nothing_so_a_digest_stays_approvable()
        {
            using var f = await ModelAsync("RelStable.bim");
            await AnswerAsync(f);

            var first = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.relationship.table:Sales");
            var second = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "jev.relationship.table:Sales");

            Assert.Equal(first.ChangeSet.Digest, second.ChangeSet.Digest);
        }

        // ---- picks come only from the options -----------------------------------------------------------------

        [Fact]
        public async Task A_key_that_was_not_listed_or_none_is_no_line()
        {
            using var f = await ModelAsync("RelBogus.bim");
            await AnswerAsync(f, new[]
            {
                new Reply("Sales/CustomerKey", "key:Customer/Name", 0.99),        // text, never offered to a whole number
                new Reply("Sales/ProductKey", "none", 0.99),
                new Reply("Sales/OrderDateKey", "key:Date/DateKey", 0.93, Role: "main"),
                new Reply("Sales/ShipDateKey", "key:Sales/ShipDateKey", 0.99),     // its own table, never offered
                new Reply("Returns/ProductKey", "key:Product/ProductKey", 0.9),
            });

            var plan = await SettledPlanAsync(f);
            var sales = plan.Items.Single(i => i.Id == "jev.relationship.table:Sales");

            Assert.Equal(new[] { "column:Sales/OrderDateKey" }, sales.ChangeSet.Lines.Select(l => l.Target).ToArray());
            Assert.Contains(plan.Items, i => i.Id == "jev.relationship.table:Returns");
        }

        // ---- keys are checked ---------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_engine_lists_only_keys_of_the_same_type_on_other_tables()
        {
            using var f = await ModelAsync("RelOptions.bim");
            var prepared = await PrepareAsync(f);

            var asked = prepared.Asks.Select(a => a.TableName + "/" + a.ColumnName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "Returns/ProductKey", "Sales/CustomerKey", "Sales/OrderDateKey", "Sales/ProductKey", "Sales/ShipDateKey" }, asked);

            var customer = prepared.Asks.Single(a => a.Ref == "column:Sales/CustomerKey");
            Assert.Equal(new[] { "key:Customer/CustomerKey", "key:Date/DateKey", "key:Product/ProductKey" }, customer.Keys.Select(k => k.Id).ToArray());
            Assert.All(customer.Keys, k => Assert.Equal("Int64", k.DataType));
            Assert.Equal(new[] { "key:Customer/CustomerKey", "key:Date/DateKey", "key:Product/ProductKey", "none" },
                         customer.Questions.Single(q => q.Name == "join_to").Options.Select(o => o.Id).ToArray());
            // Two dates compete for the Date table, so both are asked which is the main one; no key column is unique
            // by measurement, so one to one is never on offer.
            Assert.True(prepared.Asks.Single(a => a.Ref == "column:Sales/OrderDateKey").AsksDateRole);
            Assert.False(customer.AsksDateRole);
            Assert.DoesNotContain(customer.Questions, q => q.Name == "cardinality");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_key_a_column_names_is_listed_even_when_many_keys_come_before_it()
        {
            // Found by the replay: AdventureWorks' SalesTerritoryKey never saw Sales Territory[SalesTerritoryKey],
            // because the list was cut at sixteen keys in table order and that table sorts last.
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "many-keys", async b =>
            {
                var e = b.Engine;
                await e.CreateModelAsync("Contoso", 1604);
                for (var i = 0; i < 20; i++)
                {
                    await e.CreateTableAsync("A" + i.ToString("00", CultureInfo.InvariantCulture), "human");
                    await e.CreateColumnAsync("table:A" + i.ToString("00", CultureInfo.InvariantCulture), "A" + i.ToString("00", CultureInfo.InvariantCulture) + "Key", "Int64", null, "human");
                }
                await e.CreateTableAsync("Zone", "human");
                await e.CreateColumnAsync("table:Zone", "ZoneKey", "Int64", null, "human");
                await e.CreateTableAsync("Orders", "human");
                await e.CreateColumnAsync("table:Orders", "ZoneKey", "Int64", null, "human");
            }, "RelManyKeys.bim");

            var prepared = await PrepareAsync(f);
            var zone = prepared.Asks.Single(a => a.Ref == "column:Orders/ZoneKey");
            Assert.Equal("key:Zone/ZoneKey", zone.Keys.First().Id);
        }

        [Fact]
        public async Task A_key_the_facts_show_repeating_is_never_listed_and_one_measured_in_full_says_so()
        {
            using var f = await ModelAsync("RelFacts.bim");
            Facts.ColumnFacts Measured(string reference, bool unique, bool sampled = false) => new Facts.ColumnFacts
            {
                Ref = reference,
                // Rows were read (a column read with none is an empty table, which shows no repeat).
                Measured = new Facts.MeasuredFacts { Distinct = 40, UniqueOnEveryRow = unique, Sampled = sampled },
            };
            var facts = new Facts.ModelFacts
            {
                Tables = new[]
                {
                    new Facts.TableFacts { Name = "Customer", Columns = new[] { Measured("column:Customer/CustomerKey", unique: false) } },
                    new Facts.TableFacts { Name = "Product", Columns = new[] { Measured("column:Product/ProductKey", unique: true) } },
                    new Facts.TableFacts { Name = "Date", Columns = new[] { Measured("column:Date/DateKey", unique: true, sampled: true) } },
                },
            };

            var prepared = await f.Sessions.Current.ReadAsync(m => RelationshipFamily.Prepare(m, facts));
            var sales = prepared.Asks.Single(a => a.Ref == "column:Sales/CustomerKey");

            Assert.DoesNotContain(sales.Keys, k => k.Id == "key:Customer/CustomerKey");
            Assert.Equal("yes", sales.Keys.Single(k => k.Id == "key:Product/ProductKey").Unique);
            Assert.Equal("not_measured", sales.Keys.Single(k => k.Id == "key:Date/DateKey").Unique);   // a sample proves nothing

            var pick = RelationshipFamily.Map(prepared.Asks.Single(a => a.Ref == "column:Sales/ProductKey"),
                                              "key:Product/ProductKey", 0.95, "one_way", null, null, 0);
            var line = RelationshipFamily.Line(Assert.Single(RelationshipFamily.Resolve(new[] { pick }, null, null, null)));
            Assert.Contains("Product[ProductKey] has no repeats on any row.", line.CheckText, StringComparison.Ordinal);
        }

        [Fact]
        public void A_second_filter_path_arrives_inactive_and_a_both_ways_one_is_refused()
        {
            RelationshipFamily.Ask Col(string table, string column, params (string Table, string Column)[] keys) => new RelationshipFamily.Ask
            {
                Ref = "column:" + table + "/" + column, ColumnName = column, TableName = table, TableRef = "table:" + table, DataType = "Int64",
                Keys = keys.Select(k => new RelationshipFamily.Key
                {
                    Id = "key:" + k.Table + "/" + k.Column, Ref = "column:" + k.Table + "/" + k.Column,
                    Table = k.Table, Column = k.Column, DataType = "Int64", Unique = "not_measured", OwnKey = true,
                }).ToList(),
            };
            RelationshipFamily.Pick P(RelationshipFamily.Ask a, string key, string direction = "one_way") =>
                RelationshipFamily.Map(a, "key:" + key, 0.95, direction, null, null, 0);

            // The shape of AdventureWorks: Sales points at Customer and at Territory, and Customer points at Geography,
            // which points at Territory too. The last one would give Territory two paths into Sales.
            var picks = new[]
            {
                P(Col("Sales", "CustomerKey", ("Customer", "CustomerKey")), "Customer/CustomerKey"),
                P(Col("Sales", "TerritoryKey", ("Territory", "TerritoryKey")), "Territory/TerritoryKey"),
                P(Col("Customer", "GeographyKey", ("Geography", "GeographyKey")), "Geography/GeographyKey"),
                P(Col("Geography", "TerritoryKey", ("Territory", "TerritoryKey")), "Territory/TerritoryKey"),
            };
            var refused = new List<ModelPlanChangeRefusal>();
            var resolved = RelationshipFamily.Resolve(picks, null, null, refused);

            Assert.Empty(refused);
            Assert.Equal(new[] { true, true, true, false }, resolved.OrderBy(p => Array.IndexOf(picks, p)).Select(p => p.Active).ToArray());
            var line = RelationshipFamily.Line(resolved.Single(p => p.Ask.TableName == "Geography"));
            Assert.Contains("inactive", line.After, StringComparison.Ordinal);
            Assert.Contains("A second link would make it unclear which way a filter goes", line.Reason, StringComparison.Ordinal);

            // The same last pick, both ways, is refused rather than quietly made one way.
            var bothWays = picks.Take(3).Concat(new[] { P(Col("Geography", "TerritoryKey", ("Territory", "TerritoryKey")), "Territory/TerritoryKey", "both_ways") }).ToArray();
            var refusedBoth = new List<ModelPlanChangeRefusal>();
            var resolvedBoth = RelationshipFamily.Resolve(bothWays, null, null, refusedBoth);
            Assert.Equal(3, resolvedBoth.Count);
            Assert.Equal("column:Geography/TerritoryKey", Assert.Single(refusedBoth).Target);
        }

        // ---- what goes --------------------------------------------------------------------------------------------

        [Fact]
        public async Task Only_names_types_and_flags_go_to_Jev()
        {
            using var f = await ModelAsync("RelSent.bim");
            var oracle = await AnswerAsync(f);
            await SettledPlanAsync(f);

            var sent = oracle.Seen.Where(r => r.PointName == Point).ToArray();
            Assert.Equal(5, sent.Length);
            var declared = DecisionRegistry.Require(Point).SnapshotFields;
            foreach (var r in sent)
            {
                Assert.Equal(declared.OrderBy(x => x, StringComparer.Ordinal), r.Snapshot.Keys.OrderBy(x => x, StringComparer.Ordinal));
                using var facts = JsonDocument.Parse(JsonSerializer.Serialize(r.Snapshot["column_facts"]));
                Assert.Equal(new[] { "data_type", "is_hidden", "is_key", "no_repeats" },
                             facts.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                using var keys = JsonDocument.Parse(JsonSerializer.Serialize(r.Snapshot["candidate_keys"]));
                foreach (var k in keys.RootElement.EnumerateArray())
                    Assert.Equal(new[] { "column", "data_type", "date_table", "id", "name_match", "no_repeats", "own_key", "table" },
                                 k.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            }
            // Columns with no key to join (a number, free text) are asked about by nobody.
            Assert.DoesNotContain(sent, r => JsonSerializer.Serialize(r.Snapshot["column_name"]) == "\"Amount\"");
        }

        // ---- the rule the replay scores against -------------------------------------------------------------------

        [Fact]
        public void The_rule_joins_by_name_the_way_it_says()
        {
            RelationshipFamily.Key K(string table, string column) => new RelationshipFamily.Key
            {
                Id = "key:" + table + "/" + column, Table = table, Column = column, OwnKey = RelationshipFamily.OwnKeyName(column, table),
            };
            RelationshipFamily.Ask A(string column, params RelationshipFamily.Key[] keys) =>
                new RelationshipFamily.Ask { ColumnName = column, Keys = keys.ToList() };

            var date = K("Date", "DateKey");
            var customer = K("Customer", "CustomerKey");
            var customers = K("Customers", "Id");
            var product = K("Product", "Product");

            Assert.Same(customer, RelationshipFamily.RuleTarget(A("CustomerKey", customer, date)));      // exact own key
            Assert.Same(date, RelationshipFamily.RuleTarget(A("OrderDateKey", customer, date)));         // ends with an own key
            Assert.Same(customers, RelationshipFamily.RuleTarget(A("CustomerId", customers, date)));     // table name then key name
            Assert.Same(product, RelationshipFamily.RuleTarget(A("Product", product, customer)));        // a table named by its column
            Assert.Null(RelationshipFamily.RuleTarget(A("SoldBy", customer, date)));                      // nothing in the name
            Assert.Null(RelationshipFamily.RuleTarget(A("CustomerKey", customer, K("Customers", "CustomerKey"))));   // two equal matches: none
        }

        // ---- the Pro path, kept picks first, and what the check says was read (26 Sep, verifier round 1) ----------

        private static DecisionAnswers Answers(RelationshipFamily.Ask ask, string json)
        {
            var point = DecisionRegistry.Require(Point);
            var request = new DecisionRequest(point.Name, point.SchemaVersion, ask.Snapshot, ask.Questions);
            using var doc = JsonDocument.Parse(json);
            return DecisionAnswerReader.Read(request, doc.RootElement, "jev-test-1");
        }

        /// <summary>The Pro path the replay scored ("Jev when sure, else the rule"), per question: a sure answer is
        /// Jev's, anything less sure is the rule's, and a sure "joins nothing" is no pick at all.</summary>
        [Fact]
        public async Task The_Pro_path_takes_Jev_when_sure_and_the_rule_otherwise_for_each_question()
        {
            using var f = await ModelAsync("RelProPath.bim");
            var prepared = await PrepareAsync(f);
            var bar = DecisionRegistry.Require(Point).ConfidenceBar;
            var order = prepared.Asks.Single(a => a.Ref == "column:Sales/OrderDateKey");
            Assert.True(order.AsksDateRole);

            // All sure: every setting is Jev's.
            var sure = RelationshipFamily.ProPick(order, Answers(order,
                "{\"join_to\":" + Choice("key:Date/DateKey", 0.95) + ",\"filter_direction\":" + Choice("both_ways", 0.9)
                + ",\"date_role\":" + Choice("other", 0.9) + "}"), bar);
            Assert.Equal("key:Date/DateKey", sure.Key.Id);
            Assert.Equal("both_ways", sure.Direction);
            Assert.False(sure.Main);
            Assert.False(sure.FromRule);
            Assert.Equal(0.95, sure.Confidence, 3);

            // Unsure of all three: the rule's key, one way, and the rule's main date (its name says "order").
            var ruled = RelationshipFamily.ProPick(order, Answers(order,
                "{\"join_to\":" + Choice("key:Product/ProductKey", 0.5) + ",\"filter_direction\":" + Choice("both_ways", 0.3)
                + ",\"date_role\":" + Choice("other", 0.4) + "}"), bar);
            Assert.Equal("key:Date/DateKey", ruled.Key.Id);
            Assert.Equal("one_way", ruled.Direction);
            Assert.True(ruled.Main);
            Assert.True(ruled.FromRule);

            // A sure join with an unsure direction: Jev's join, the rule's one way.
            var mixed = RelationshipFamily.ProPick(order, Answers(order,
                "{\"join_to\":" + Choice("key:Date/DateKey", 0.97) + ",\"filter_direction\":" + Choice("both_ways", 0.3)
                + ",\"date_role\":" + Choice("main", 0.9) + "}"), bar);
            Assert.Equal("one_way", mixed.Direction);
            Assert.False(mixed.FromRule);
            Assert.True(mixed.Main);

            // Sure it joins nothing: no pick, whatever the rule says. Unsure of that: the rule's join.
            Assert.Null(RelationshipFamily.ProPick(order, Answers(order,
                "{\"join_to\":" + Choice("none", 0.95) + ",\"filter_direction\":" + Choice("one_way", 0.9)
                + ",\"date_role\":" + Choice("other", 0.9) + "}"), bar));
            Assert.True(RelationshipFamily.ProPick(order, Answers(order,
                "{\"join_to\":" + Choice("none", 0.5) + ",\"filter_direction\":" + Choice("one_way", 0.9)
                + ",\"date_role\":" + Choice("other", 0.9) + "}"), bar).FromRule);

            // Every pick the Pro path makes arrives kept, and so it takes part in choosing what is active.
            Assert.True(sure.Kept && ruled.Kept && mixed.Kept);
        }

        /// <summary>Approve applies kept lines only, so a dropped pick must never win a table pair or a filter path
        /// from a kept one. Resolve takes the kept picks first.</summary>
        [Fact]
        public async Task A_dropped_pick_never_takes_the_active_slot_from_a_kept_one()
        {
            using var f = await ModelAsync("RelKeptFirst.bim");
            var prepared = await PrepareAsync(f);
            RelationshipFamily.Ask A(string r) => prepared.Asks.Single(a => a.Ref == r);
            var kept = RelationshipFamily.Map(A("column:Sales/OrderDateKey"), "key:Date/DateKey", 0.93, "one_way", null, "other", 0.9);
            var dropped = RelationshipFamily.Map(A("column:Sales/ShipDateKey"), "key:Date/DateKey", 0.4, "one_way", null, "main", 0.9);
            var bar = DecisionRegistry.Require(Point).ConfidenceBar;
            Assert.Equal("approved", RelationshipFamily.JevLine(kept, bar).Status);
            Assert.Equal("rejected", RelationshipFamily.JevLine(dropped, bar).Status);

            var resolved = RelationshipFamily.Resolve(new[] { dropped, kept }, null, null, new List<ModelPlanChangeRefusal>());

            Assert.True(resolved.Single(p => ReferenceEquals(p, kept)).Active, "the kept date was left inactive by a dropped one");
            Assert.False(resolved.Single(p => ReferenceEquals(p, dropped)).Active);
        }

        /// <summary>The coordinator's decision (26 Sep, reversible): with no data connected a key is offered on its name
        /// and type only, and every such line and its check say so plainly.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task With_no_data_every_line_and_its_check_say_they_rest_on_names_only()
        {
            using var f = await ModelAsync("RelNamesOnly.bim");
            await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);
            var lines = plan.Items.Where(i => i.Id.StartsWith("jev.relationship.", StringComparison.Ordinal))
                                  .SelectMany(i => i.ChangeSet.Lines).ToArray();

            Assert.NotEmpty(lines);
            foreach (var l in lines)
            {
                Assert.Contains("based on names only", l.Reason, StringComparison.Ordinal);
                Assert.Contains("based on names only", l.Check.Text, StringComparison.Ordinal);
            }
        }

        /// <summary>The check says what Semanticus actually read of the key: every row, a sample, nothing of this
        /// table although other data was read, or no data at all.</summary>
        [Fact]
        public async Task The_check_says_what_was_read_of_the_key()
        {
            using var f = await ModelAsync("RelEvidence.bim");
            Facts.TableFacts Read(string table, string column, bool sampled) => new Facts.TableFacts
            {
                Name = table,
                Columns = new[] { new Facts.ColumnFacts
                {
                    Ref = "column:" + table + "/" + column,
                    Measured = new Facts.MeasuredFacts { Distinct = 500, BlankShare = 0, UniqueOnEveryRow = true, Sampled = sampled },
                } },
            };
            var facts = new Facts.ModelFacts
            {
                Data = Facts.ModelFactsData.Local,
                Tables = new[] { Read("Date", "DateKey", sampled: true), Read("Product", "ProductKey", sampled: false) },
            };
            string Check(Facts.ModelFacts x, string column, string key) => f.Sessions.Current.ReadAsync(m =>
            {
                var ask = RelationshipFamily.Prepare(m, x).Asks.Single(a => a.Ref == column);
                var pick = RelationshipFamily.Map(ask, key, 0.95, "one_way", null, "main", 0.9);
                return RelationshipFamily.Line(Assert.Single(RelationshipFamily.Resolve(new[] { pick }, null, null, null))).CheckText;
            }).GetAwaiter().GetResult();

            Assert.Contains("Product[ProductKey] has no repeats on any row.", Check(facts, "column:Sales/ProductKey", "key:Product/ProductKey"), StringComparison.Ordinal);

            var sampled = Check(facts, "column:Sales/OrderDateKey", "key:Date/DateKey");
            Assert.Contains("sample", sampled, StringComparison.Ordinal);
            Assert.DoesNotContain("names only", sampled, StringComparison.Ordinal);

            var unread = Check(facts, "column:Sales/CustomerKey", "key:Customer/CustomerKey");
            Assert.Contains("did not read", unread, StringComparison.Ordinal);
            Assert.DoesNotContain("names only", unread, StringComparison.Ordinal);
            Assert.DoesNotContain("No data is connected", unread, StringComparison.Ordinal);

            Assert.Contains("based on names only", Check(null, "column:Sales/CustomerKey", "key:Customer/CustomerKey"), StringComparison.Ordinal);
        }

        // ---- verify (m11/m4-relationships, 26 Sep): defects ----------------------------------------------------

        /// <summary>verify: the family's own rule is "a key the facts show repeating is never offered" (RelationshipFamily.cs
        /// lines 14 and 97). A bounded sample never proves "no repeats", but a sample that already holds a repeated value
        /// proves the column repeats: rows were read, none blank, and fewer distinct values than rows. Such a key is
        /// still listed today, because UniqueState reads every sampled measurement as "not measured".</summary>
        [Fact]
        public async Task Verify_a_key_a_sample_already_shows_repeating_is_never_listed()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "orders-shipments", async b =>
            {
                var e = b.Engine;
                await e.CreateModelAsync("Contoso", 1604);
                await e.CreateTableAsync("Orders", "human");
                await e.CreateColumnAsync("table:Orders", "OrderNumber", "String", null, "human");
                await e.CreateColumnAsync("table:Orders", "Amount", "Double", null, "human");
                await e.CreateTableAsync("Shipments", "human");
                await e.CreateColumnAsync("table:Shipments", "OrderNumber", "String", null, "human");
            }, "RelOrdersShipments.bim");
            var facts = new Facts.ModelFacts
            {
                Tables = new[]
                {
                    // Orders was read on a bounded sample: no blanks, and fewer distinct order numbers than rows read,
                    // so an order number repeats (an order has several lines).
                    new Facts.TableFacts { Name = "Orders", Columns = new[] { new Facts.ColumnFacts
                    {
                        Ref = "column:Orders/OrderNumber",
                        Measured = new Facts.MeasuredFacts { Distinct = 250, BlankShare = 0, UniqueOnEveryRow = false, Sampled = true },
                    } } },
                },
            };

            var prepared = await f.Sessions.Current.ReadAsync(m => RelationshipFamily.Prepare(m, facts));
            var shipments = prepared.Asks.SingleOrDefault(a => a.Ref == "column:Shipments/OrderNumber");

            Assert.True(shipments == null || shipments.Keys.All(k => k.Id != "key:Orders/OrderNumber"),
                "Orders[OrderNumber] was offered as the one side although its sample already shows it repeating");
        }

        /// <summary>verify: a line's check must not say "No data is connected" when data IS connected. With the key's table
        /// read on a bounded sample (no repeats in it), the key reads "not measured", and Line() words every
        /// "not measured" key as if no data were connected at all.</summary>
        [Fact]
        public async Task Verify_a_check_never_says_no_data_is_connected_when_the_key_was_read()
        {
            using var f = await ModelAsync("RelSampledCheck.bim");
            var facts = new Facts.ModelFacts
            {
                Tables = new[]
                {
                    new Facts.TableFacts { Name = "Date", Columns = new[] { new Facts.ColumnFacts
                    {
                        Ref = "column:Date/DateKey",
                        Measured = new Facts.MeasuredFacts { Distinct = 1000, BlankShare = 0, UniqueOnEveryRow = true, Sampled = true },
                    } } },
                },
            };
            var prepared = await f.Sessions.Current.ReadAsync(m => RelationshipFamily.Prepare(m, facts));
            var ask = prepared.Asks.Single(a => a.Ref == "column:Sales/OrderDateKey");
            var pick = RelationshipFamily.Map(ask, "key:Date/DateKey", 0.95, "one_way", null, "main", 0.9);
            var line = RelationshipFamily.Line(Assert.Single(RelationshipFamily.Resolve(new[] { pick }, null, null, null)));

            Assert.DoesNotContain("No data is connected", line.CheckText, StringComparison.Ordinal);
        }

        /// <summary>verify: a dropped (unsure) pick must not take the active slot from a kept one. Approve applies only kept
        /// lines, so when an unsure "main date" wins the pair, the kept date arrives inactive and Approve leaves the table
        /// with no active date relationship at all.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_an_unsure_pick_never_leaves_the_kept_date_inactive()
        {
            using var f = await ModelAsync("RelUnsureMain.bim");
            await AnswerAsync(f, new[]
            {
                new Reply("Sales/CustomerKey", "key:Customer/CustomerKey", 0.96),
                new Reply("Sales/ProductKey", "key:Product/ProductKey", 0.97),
                new Reply("Sales/OrderDateKey", "key:Date/DateKey", 0.93, Role: "other"),
                new Reply("Sales/ShipDateKey", "key:Date/DateKey", 0.4, Role: "main"),
                new Reply("Returns/ProductKey", "key:Product/ProductKey", 0.9),
            });
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.relationship.table:Sales");

            var result = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest);
            Assert.True(result.Performed, result.Sentence);
            var toDate = (await RelationshipsAsync(f)).Where(r => r.To == "Date/DateKey").ToArray();

            Assert.NotEmpty(toDate);
            Assert.Contains(toDate, r => r.Active);
        }

        /// <summary>verify: a kept line may not carry a choice Jev was unsure of. Map takes the direction (and the
        /// cardinality and date role) whatever its confidence, so a sure join with a "both ways" Jev gave at 0.3 arrives
        /// KEPT and filtering both ways; the contract drops what Jev is unsure of, and the bar's Pro path would have used
        /// the rule's one way.</summary>
        [Fact]
        public async Task Verify_a_both_ways_answer_Jev_is_unsure_of_never_arrives_kept()
        {
            using var f = await ModelAsync("RelUnsureBoth.bim");
            await f.ConsentAsync();
            f.Engine.RelationshipJevDecidesForTest = true;
            var point = DecisionRegistry.Require(Point);
            var prepared = await PrepareAsync(f);
            var ask = prepared.Asks.Single(a => a.Ref == "column:Sales/CustomerKey");
            Assert.DoesNotContain(ask.Questions, q => q.Name == "cardinality" || q.Name == "date_role");
            var digest = new DecisionRequest(point.Name, point.SchemaVersion, ask.Snapshot, ask.Questions).Digest;
            var json = "{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":" + JsonSerializer.Serialize(digest)
                     + ",\"point\":\"" + Point + "\",\"usage\":{\"input_tokens\":700,\"output_tokens\":30},\"answers\":{"
                     + "\"join_to\":" + Choice("key:Customer/CustomerKey", 0.97)
                     + ",\"filter_direction\":" + Choice("both_ways", 0.3) + "}}]}";
            f.Engine.UseDecisionsForTest(new DecisionService(new RecordedDecisionOracle(json, "jev-test-1"), new DecisionBudget()));

            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.relationship.table:Sales");
            var line = sales.ChangeSet.Lines.Single(l => l.Target == "column:Sales/CustomerKey");

            Assert.False(line.Kept && line.After.Contains("filters both ways", StringComparison.Ordinal), line.After);
        }

        /// <summary>Every registered point says, in its own words, whether this build lets it act, so a new point cannot
        /// inherit "live" by default and show a switch that does nothing. ai.schema, whose bar is also unmet, is listed
        /// as not live too, and a bar that clears flips it.</summary>
        [Fact]
        public async Task Every_point_states_its_own_bar_and_ai_schema_below_its_bar_is_not_live()
        {
            using var f = await ModelAsync("RelEveryPoint.bim");
            foreach (var point in DecisionRegistry.Points)
                Assert.True(f.Engine.DecisionPointBarForTest(point.Name).HasValue, point.Name + " has no case of its own in DecisionPointLiveInThisBuild");
            Assert.Null(f.Engine.DecisionPointBarForTest("not.a.point"));

            // ai.schema's bar cleared on its own lane (its replay passed), so the joined build lists it as the constant says;
            // the point of this check is that the list reads the same constant the asking path reads, so a flip of it flips the list.
            var aiSchemaLive = AiSchemaFamily.JevDecides;
            Assert.Equal(aiSchemaLive, (await f.Engine.GetDecisionPointsAsync()).Single(p => p.Name == DecisionRegistry.AiSchema).Live);
            f.Engine.AiSchemaJevDecidesForTest = !aiSchemaLive;
            Assert.Equal(!aiSchemaLive, (await f.Engine.GetDecisionPointsAsync()).Single(p => p.Name == DecisionRegistry.AiSchema).Live);
        }

        // ---- verify round 2 (m11/m4-relationships at 288f0ef4, 26 Sep): defects -------------------------------

        /// <summary>verify: a point whose replay bar is unmet is listed as NOT live on both doors (DecisionBoundaryTests,
        /// N1; LocalEngine.Decisions.cs DecisionPointLiveInThisBuild: "A point whose bar is unmet is never asked, whatever
        /// its switch says, so the list says it is not on yet rather than showing a switch that is on and does nothing").
        /// relationship.join's bar is unmet (RelationshipFamily.JevDecides is false, so nobody is asked), yet
        /// get_decision_points lists it as live, and Settings, What Pro decides, shows its switch on with no "Not on yet
        /// in this version." beside it.</summary>
        [Fact]
        public async Task Verify_relationship_join_with_its_bar_unmet_is_listed_as_not_live_on_both_doors()
        {
            using var f = await ModelAsync("RelNotLive.bim");
            Assert.False(f.Engine.RelationshipJevDecidesForTest ?? RelationshipFamily.JevDecides);

            var mcp = await McpTools.GetDecisionPoints(f.Engine);
            var rpc = await new EngineRpcTarget(f.Engine).getDecisionPoints();
            foreach (var points in new[] { mcp, rpc })
            {
                var point = points.Single(p => p.Name == Point);
                Assert.True(point.Enabled);
                Assert.False(point.Live, "relationship.join is listed as live although its replay bar is unmet and nothing asks it");
            }

            // The list reads the same constant the asking path reads, so a bar that clears flips it.
            f.Engine.RelationshipJevDecidesForTest = true;
            Assert.True((await f.Engine.GetDecisionPointsAsync()).Single(p => p.Name == Point).Live);
        }

        /// <summary>verify: a key on a table that was read IN FULL and holds no rows shows no repeat, so the family's own
        /// rule ("a key the facts show repeating is never offered", and with no data read a key is offered on its name)
        /// still offers it. UniqueState reads every full measurement that is not unique as "no", and a table with no rows
        /// is never unique on every row (LocalEngine.Facts.cs: rows read and more than none), so connecting data takes away
        /// a key the names alone offer. These facts are exactly what the measurer writes for an empty table.</summary>
        [Fact]
        public async Task Verify_a_key_on_an_empty_table_read_in_full_is_still_listed()
        {
            using var f = await ModelAsync("RelEmptyKey.bim");
            var byNames = await f.Sessions.Current.ReadAsync(m => RelationshipFamily.Prepare(m, null));
            Assert.Contains(byNames.Asks.Single(a => a.Ref == "column:Sales/CustomerKey").Keys, k => k.Id == "key:Customer/CustomerKey");

            var facts = new Facts.ModelFacts
            {
                Data = Facts.ModelFactsData.Local,
                Tables = new[]
                {
                    // Customer was read on every row, and it has none.
                    new Facts.TableFacts { Name = "Customer", Rows = 0, Columns = new[] { new Facts.ColumnFacts
                    {
                        Ref = "column:Customer/CustomerKey",
                        Measured = new Facts.MeasuredFacts { Distinct = 0, BlankShare = 0, UniqueOnEveryRow = false, Sampled = false },
                    } } },
                },
            };
            var prepared = await f.Sessions.Current.ReadAsync(m => RelationshipFamily.Prepare(m, facts));
            var sales = prepared.Asks.SingleOrDefault(a => a.Ref == "column:Sales/CustomerKey");

            Assert.True(sales != null && sales.Keys.Any(k => k.Id == "key:Customer/CustomerKey"),
                "Customer[CustomerKey] is not offered once an empty Customer table has been read, although nothing in it repeats");
        }
    }
}
