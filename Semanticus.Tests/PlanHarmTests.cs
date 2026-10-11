// Anyone who follows the Plan must not be hurt (Kane, 28 Sep 2026; lane m11/plan-harm).
//
// The UAT plan statement check followed the Plan on a new model and found it harmful. Each test here reproduces one
// of its findings on a small model with the same shape as the J8 fixture (a client dimension with a name column, a
// marked date table, an invoice fact with a yyyymmdd date key and an hourly rate, one hand-written measure):
//   the cleanup row offered to delete the date table's key, the name column and hand-written measures, first, with
//   every other row waiting for it;
//   the starter measures Run the plan added were then listed as unused, and a removal brought the starter row back;
//   the starter rule summed a date key and a rate;
//   the Plan called the AI readiness grade "the model quality grade";
//   Jev's plan.rank was refused (422) on every call, and the Plan header printed the status number;
//   a Plan row named the UTC day, not the person's.
// ROUND 5 (Kane, 30 Sep 2026): the cleanup row is a SUGGESTION LIST for a lean model, not a harm gate. Every unused column is listed and
// each starts ticked or unticked with a reason (PlanCleanupReviewTests). The assertions here that said "never offered" now say "never
// ticked": the column is still a line, unticked, and the person decides. What was harm before is now a line the person can read the
// reason on and leave unticked.
// The engine door is tested here; the MCP door reaches the same methods (McpServer get_model_plan, model_plan_action,
// apply_plan_items), and tools/journey.py in the lane report drives it end to end.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>For the tests about what the cleanup row DOES (removing, waiting, commuting). The row STARTS ticked only what the
    /// rule would suggest (nothing built in this session, no measure, key, date or date table's column, on any model; round 5 lists
    /// those unticked instead of hiding them), so those tests first make their fixture what the row is for: an existing model (opened
    /// again, a fresh session) that a report has been read for, with an unused plain column that starts ticked. The report shows one column, <paramref name="table"/>[<paramref name="column"/>] (Sales[Amount]
    /// by default, which the fixtures' measures already use), so the reading matches the model and every unused
    /// object stays a candidate. (A report that matches nothing makes the sweep call everything "caution".)</summary>
    internal static class PlanHarmKit
    {
        public static async Task AsExistingModelWithAReportReadAsync(DecisionFixture f, string file, string table = "Sales", string column = "Amount")
        {
            await f.Engine.OpenAsync(Path.Combine(f.Workspace, file), discardUnsaved: true);
            f.UseNull();
            var projection = "{ \"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Entity\": " + JsonSerializer.Serialize(table) + " } }, "
                + "\"Property\": " + JsonSerializer.Serialize(column) + " } } }";
            var visual = "{ \"visual\": { \"query\": { \"queryState\": { \"Values\": { \"projections\": [ " + projection + " ] } } } } }";
            var reports = Path.Combine(f.Workspace, "Reports-" + Path.GetFileNameWithoutExtension(file));
            var visualDir = Path.Combine(reports, "Main.Report", "definition", "pages", "p1", "visuals", "v1");
            Directory.CreateDirectory(visualDir);
            File.WriteAllText(Path.Combine(reports, "Main.Report", "definition", "report.json"), "{}");
            File.WriteAllText(Path.Combine(visualDir, "visual.json"), visual);
            await f.Engine.SetReportScopeAsync(
                new[] { new Semanticus.Engine.Lineage.ReportScopeChoice { Kind = "local", Name = "Main", Path = reports } }, "human");
            await CheckReportsAgainAsync(f);
        }

        /// <summary>A report reading is stamped with the revision it was read at; a later commit makes it stale.</summary>
        public static Task CheckReportsAgainAsync(DecisionFixture f) =>
            f.Engine.CheckReportsAsync(Array.Empty<string>(), false, null, null, null, "human", default);
    }

    public sealed class PlanHarmTests
    {
        /// <summary>The J8 shape, small. Built in THIS session and saved, like a person's first hour with a new model.</summary>
        private static async Task<DecisionFixture> NewModelAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso Consulting", 1604);
            await e.CreateTableAsync("dim_client", "human");
            await e.CreateColumnAsync("table:dim_client", "CLIENT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:dim_client", "CLNT_NM", "String", null, "human");
            await e.CreateColumnAsync("table:dim_client", "INDUSTRY_CD", "String", null, "human");
            // A date on a table that is not the marked date table: the SQL draft marks its own calendar and leaves the
            // source's dim_date unmarked, so its CAL_DT was offered for removal (J8 SQL-style start, reopened).
            await e.CreateColumnAsync("table:dim_client", "ONBOARDED_ON", "DateTime", null, "human");
            await e.CreateTableAsync("dim_date", "human");
            await e.CreateColumnAsync("table:dim_date", "CAL_DT", "DateTime", null, "human");
            await e.CreateColumnAsync("table:dim_date", "FISCAL_QTR", "String", null, "human");
            await e.CreateTableAsync("fact_invoice", "human");
            await e.CreateColumnAsync("table:fact_invoice", "CLIENT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "INV_DT", "DateTime", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "DUE_DT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "BILL_RATE", "Double", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "INV_AMT", "Double", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "TAX_AMT", "Double", null, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/CLIENT_KEY", "column:dim_client/CLIENT_KEY", null, true, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/INV_DT", "column:dim_date/CAL_DT", null, true, "human");
            await e.MarkDateTableAsync("table:dim_date", "CAL_DT", "human");
            await e.CreateMeasureAsync("table:fact_invoice", "Invoice Count", "COUNTROWS ( fact_invoice )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        /// <summary>The same model opened again: a fresh session, so nothing in it was built "in this session".</summary>
        private static async Task<DecisionFixture> OpenedModelAsync(string file, bool pro = true)
        {
            var f = await NewModelAsync(file, pro);
            await f.Engine.OpenAsync(Path.Combine(f.Workspace, file), discardUnsaved: true);
            f.UseNull();
            return f;
        }

        private static readonly string[] NeverOffered = { "CAL_DT", "FISCAL_QTR", "ONBOARDED_ON", "CLNT_NM", "Invoice Count", "INV_AMT", "DUE_DT_KEY" };

        private static ModelPlanItem Row(ModelPlanActionResult result, string id) => result.Plan.Items.Single(i => i.Id == id);

        private static IEnumerable<ModelPlanItem> Open(ModelPlanView plan) =>
            plan.Items.Where(i => i.Status != ModelPlanStatuses.Skipped && i.Status != ModelPlanStatuses.Done
                               && i.State != ModelPlanItemStates.Applied && i.State != ModelPlanItemStates.Done);

        // ---- 1. the cleanup row never harms a new model ---------------------------------------------------

        [Fact]
        public async Task A_model_built_in_this_session_starts_nothing_ticked_and_nothing_waits_for_a_cleanup()
        {
            using var f = await NewModelAsync("HarmNew.bim");

            var plan = await f.Engine.GetModelPlanAsync();

            // Kane, 30 Sep: "its perfectly fine on a build from scratch". The row is there, optional, and suggests nothing: everything
            // built in this session starts unticked, each line saying why, so a person's first hour with a model never has a delete waiting.
            var cleanup = Assert.Single(plan.Items, i => i.Id == "cleanup/model");
            Assert.True(cleanup.Optional);
            Assert.Empty(cleanup.Diff);
            Assert.StartsWith("Review ", cleanup.Title, StringComparison.Ordinal);
            Assert.DoesNotContain(plan.Items, i => (i.WaitsFor ?? Array.Empty<PlanWaitRef>()).Any(w => w.Id == "cleanup/model"));
            Assert.DoesNotContain(plan.Worth.Lines, l => l.Contains("unused", StringComparison.OrdinalIgnoreCase));

            var set = Row(await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human"), "cleanup/model").ChangeSet;
            Assert.NotEmpty(set.Lines);
            Assert.All(set.Lines, l =>
            {
                Assert.False(l.Kept, l.Target + " starts ticked on a model built in this session");
                Assert.Contains("you built it in this session", l.Reason, StringComparison.Ordinal);
            });
        }

        [Fact]
        public async Task On_an_opened_model_no_report_has_been_read_for_the_cleanup_is_optional_last_and_holds_nothing()
        {
            using var f = await OpenedModelAsync("HarmOpened.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var cleanup = Assert.Single(plan.Items, i => i.Id == "cleanup/model");

            // Only the real candidate: the raw code column nothing uses. Not the date table's quarter, which a report shows. Not the fact table's numbers,
            // which the starter row owns (it offers "Total INV_AMT"; removing INV_AMT beside it would be a contradiction).
            foreach (var name in NeverOffered)
            {
                Assert.DoesNotContain(cleanup.Diff, d => d.Contains("[" + name + "]", StringComparison.Ordinal) || d.EndsWith(" " + name, StringComparison.Ordinal));
                Assert.DoesNotContain(name, cleanup.Sub ?? "", StringComparison.Ordinal);
            }
            Assert.Contains(cleanup.Diff, d => d.Contains("INDUSTRY_CD", StringComparison.Ordinal));

            // Optional, said in words, offered last, and no other row waits for it.
            Assert.True(cleanup.Optional);
            Assert.StartsWith("Optional.", cleanup.Why.First(), StringComparison.Ordinal);
            Assert.Equal("cleanup/model", Open(plan).Last().Id);
            Assert.DoesNotContain(plan.Items, i => (i.WaitsFor ?? Array.Empty<PlanWaitRef>()).Any(w => w.Id == "cleanup/model"));
            Assert.DoesNotContain(plan.Worth.Lines, l => l.Contains("unused", StringComparison.OrdinalIgnoreCase));

            // Work it out proposes exactly what the row listed, never a held-back object.
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            var set = proposed.Plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet;
            Assert.NotNull(set);
            // Round 5: none of them starts TICKED, and each of the columns the sweep lists is still a line with its reason.
            Assert.All(set.Lines.Where(l => l.Kept), l => Assert.DoesNotContain(NeverOffered, n => (l.Target ?? "").EndsWith("/" + n, StringComparison.Ordinal)));
            Assert.Contains(set.Lines.Where(l => l.Kept), l => (l.Target ?? "").EndsWith("/INDUSTRY_CD", StringComparison.Ordinal));
            Assert.All(set.Lines, l => Assert.False(string.IsNullOrWhiteSpace(l.Reason)));
            foreach (var name in new[] { "CLNT_NM", "ONBOARDED_ON" })
            {
                var line = set.Lines.Single(l => (l.Target ?? "").EndsWith("/" + name, StringComparison.Ordinal));
                Assert.False(line.Kept);
                Assert.StartsWith("Not ticked: ", line.Reason, StringComparison.Ordinal);
            }
        }

        // ---- 2. following the Plan reaches an end ---------------------------------------------------------

        [Fact]
        public async Task Starter_measures_Run_the_plan_adds_are_never_listed_unused_and_a_removed_one_is_not_offered_again()
        {
            using var f = await OpenedModelAsync("HarmLoop.bim");

            // Run the plan until it has nothing Ready. A plan that loops never gets there.
            var runs = 0;
            ModelPlanView plan = await f.Engine.GetModelPlanAsync();
            while (plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready))
            {
                Assert.True(++runs <= 6, "Run the plan was still offered after six presses: the Plan loops.");
                var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                Assert.True(ran.Performed, ran.Sentence);
                plan = await f.Engine.GetModelPlanAsync();
            }

            var measures = await f.Sessions.Current.ReadAsync(m => m.AllMeasures.Select(x => x.Name).ToArray());
            Assert.Contains("Total INV_AMT", measures);

            // The starter measure it added is not "unused".
            var cleanup = plan.Items.SingleOrDefault(i => i.Id == "cleanup/model");
            Assert.True(cleanup == null || !cleanup.Diff.Any(d => d.Contains("Total ", StringComparison.Ordinal)),
                "The cleanup row lists a starter measure Run the plan just added.");

            // A person removes it by hand: the starter row does not offer it again, so the Plan still ends.
            await f.Engine.DeleteObjectAsync("measure:fact_invoice/Total INV_AMT", "human");
            plan = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("starter/", StringComparison.Ordinal)
                && Open(plan).Contains(i) && (i.Sub ?? "").Contains("Total INV_AMT", StringComparison.Ordinal));
            Assert.DoesNotContain(plan.Items, i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready
                && i.Id.StartsWith("starter/", StringComparison.Ordinal));
        }

        // ---- 3. starter measures never total an id, a date key or a rate ------------------------------------

        [Fact]
        public async Task The_starter_row_never_totals_a_date_key_or_a_rate()
        {
            using var f = await OpenedModelAsync("HarmStarter.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var starter = plan.Items.Where(i => i.Id.StartsWith("starter/", StringComparison.Ordinal)).ToArray();
            var lines = starter.SelectMany(i => i.Diff).ToArray();

            Assert.Contains(lines, l => l.Contains("Total INV_AMT", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Contains("DUE_DT_KEY", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Contains("BILL_RATE", StringComparison.Ordinal));
        }

        [Fact]
        public void The_SQL_draft_never_totals_a_rate_or_a_key()
        {
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    new FabricTable { Schema = "dbo", Name = "fact_timesheet", Columns = new[]
                    {
                        new FabricColumn { Name = "STAFF_ID", DataType = "Int64", Ordinal = 1 },
                        new FabricColumn { Name = "WORK_DT_KEY", DataType = "Int64", Ordinal = 2 },
                        new FabricColumn { Name = "BILL_RATE", DataType = "Decimal", Ordinal = 3 },
                        new FabricColumn { Name = "HOURS", DataType = "Decimal", Ordinal = 4 },
                    } },
                    new FabricTable { Schema = "dbo", Name = "dim_staff", KeyColumns = new[] { "STAFF_ID" }, Columns = new[]
                    {
                        new FabricColumn { Name = "STAFF_ID", DataType = "Int64", Ordinal = 1 },
                        new FabricColumn { Name = "STAFF_NM", DataType = "String", Ordinal = 2 },
                    } },
                },
                ForeignKeys = new[] { new FabricForeignKey { FromSchema = "dbo", FromTable = "fact_timesheet", FromColumn = "STAFF_ID", ToSchema = "dbo", ToTable = "dim_staff", ToColumn = "STAFF_ID" } },
            };

            var spec = LocalEngine.FabricSchemaToSpec(schema, "srv", "db", "DirectQuery");
            var names = (spec.Measures ?? Array.Empty<SpecMeasure>()).Select(m => m.Name).ToArray();

            Assert.Contains("Total HOURS", names);
            Assert.DoesNotContain("Total BILL_RATE", names);
            Assert.DoesNotContain("Total WORK_DT_KEY", names);
        }

        [Theory]
        [InlineData("DUE_DT_KEY", "none")]
        [InlineData("PAID_DT_KEY", "none")]
        [InlineData("CustomerKey", "none")]
        [InlineData("CLIENT_ID", "none")]
        [InlineData("OrderID", "none")]
        [InlineData("INVOICE_DT", "none")]
        [InlineData("BILL_RATE", "none")]
        [InlineData("Unit Price", "none")]
        [InlineData("DISC_PCT", "none")]
        [InlineData("SalesOrderLineNumber", "none")]
        [InlineData("ORDER_NO", "none")]
        [InlineData("INV_NBR", "none")]
        [InlineData("INV_AMT", "sum")]
        [InlineData("PAID_AMT", "sum")]
        [InlineData("Valid Hours", "sum")]
        [InlineData("No Show Hours", "sum")]
        public void The_rule_reads_a_column_name_before_it_totals_it(string column, string expected)
        {
            Assert.Equal(expected, StarterMeasureTemplates.RuleChoice(column, false, "none", "Default"));
        }

        // ---- 4. the grade the Plan names is AI readiness --------------------------------------------------

        [Fact]
        public async Task The_Plan_names_the_AI_readiness_grade_not_a_model_quality_grade()
        {
            using var f = await OpenedModelAsync("HarmGrade.bim");

            // Start on a safe-fix row (the one-press path), and Approve on a worked-out set (the reviewed path).
            var started = await f.Engine.ModelPlanActionAsync("safefix/Visibility", "start", "human");
            Assert.True(started.Performed, started.Sentence);
            var proposed = await f.Engine.ModelPlanActionAsync("safefix/Formatting", "propose", "human");
            var digest = proposed.Plan.Items.Single(i => i.Id == "safefix/Formatting").ChangeSet?.Digest;
            var approved = await f.Engine.ModelPlanActionAsync("safefix/Formatting", "approve", "human", digest);
            Assert.True(approved.Performed, approved.Sentence);

            foreach (var sentence in new[] { started.Sentence, approved.Sentence })
            {
                Assert.DoesNotContain("model quality grade", sentence ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.Contains("AI readiness grade is now", sentence ?? "", StringComparison.Ordinal);
            }
        }

        // ---- 5. plan.rank: the wire shape the service accepts, and the reply it sends ---------------------

        private static DecisionRequest RankRequest() => new DecisionRequest(DecisionRegistry.PlanRank, 1,
            new Dictionary<string, object> { { "model_shape", new Dictionary<string, object> { { "tables", 3 } } } },
            PlanRankPoint.Questions(new[] { new ModelPlanItem { Id = "cleanup/model", Title = "Remove 3 unused things" } }));

        [Fact]
        public void A_score_question_sends_its_ladder_as_an_ordered_list_on_both_routes()
        {
            var request = RankRequest();
            var snapshot = DecisionDigest.CanonicalSnapshotJson(request.Snapshot);
            foreach (var body in new[]
            {
                JevDecisionOracle.BuildBody(request, snapshot),
                JevDecisionOracle.BuildRelayBody(request, snapshot, "key-1"),
            })
            {
                using var doc = JsonDocument.Parse(body);
                var criteria = doc.RootElement.GetProperty("questions").GetProperty("value:cleanup/model").GetProperty("criteria");
                // The service answers 422 "Input should be a valid list" to anything else (measured 28 Sep).
                Assert.Equal(JsonValueKind.Array, criteria.ValueKind);
                Assert.Equal(DecisionRegistry.PlanValueRungs.Count, criteria.GetArrayLength());
                Assert.StartsWith("doing this would change nothing", criteria[0].GetString(), StringComparison.Ordinal);
            }
        }

        /// <summary>The reply jev-1.13.0 sent on 28 Sep to a five-rung plan.rank ladder, with the criteria put back.</summary>
        private static JsonElement MeasuredReply(DecisionRequest request, string first = "0.09", string second = "0.59")
        {
            var q = request.Questions[0];
            var legend = string.Join(",", q.Options.Select((o, i) => "\"" + i + "\":" + JsonSerializer.Serialize(o.Criterion)));
            return JsonDocument.Parse("{\"value:cleanup/model\":{\"type\":\"score\",\"score\":1.25,\"confidence\":0.64,"
                + "\"legend\":{" + legend + "},\"probabilities\":{\"0\":" + first + ",\"1\":" + second + ",\"2\":0.31,\"3\":0.01,\"4\":0.0}}}").RootElement;
        }

        [Fact]
        public void The_measured_score_reply_is_read_by_position_and_takes_the_most_probable_rung()
        {
            var request = RankRequest();

            var answers = DecisionAnswerReader.Read(request, MeasuredReply(request), "jev-1.13.0");

            Assert.True(answers.Decided, answers.NotDecidedReason);
            Assert.Equal("small", answers.For("value:cleanup/model").Score);
            Assert.Equal(0.64, answers.For("value:cleanup/model").Confidence, 3);
            Assert.Equal(0.59, answers.For("value:cleanup/model").Probabilities["small"], 3);
        }

        [Fact]
        public void A_score_legend_that_is_not_the_ladder_sent_refuses_the_whole_ask()
        {
            var request = RankRequest();
            var reply = JsonDocument.Parse("{\"value:cleanup/model\":{\"type\":\"score\",\"score\":1,\"confidence\":0.9,"
                + "\"legend\":{\"0\":\"something else\"},\"probabilities\":{\"1\":0.9}}}").RootElement;

            var answers = DecisionAnswerReader.Read(request, reply, "jev-1.13.0");

            Assert.False(answers.Decided);
            Assert.Contains("legend", answers.NotDecidedReason, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(HttpStatusCode.UnprocessableEntity)]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.PaymentRequired)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.BadGateway)]
        public void A_refusal_a_person_reads_never_carries_the_status_number(HttpStatusCode status)
        {
            var sentence = JevDecisionOracle.RefusalReason("{\"detail\":[{\"msg\":\"Input should be a valid list\"}]}", status, out var code);

            Assert.DoesNotContain(((int)status).ToString(), sentence, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(sentence));
            Assert.Equal(((int)status).ToString(), code);   // the number still reaches the log line
        }

        [Fact]
        public async Task Giving_or_withdrawing_permission_on_a_model_already_marked_tells_the_other_door()
        {
            // L8 (verify, 28 Sep): permission given on the assistant door while the Studio's card was up left the card
            // asking. The card reads again on any change, but only the FIRST grant made one (it marks the model); a
            // grant or a withdrawal on a model already marked changed a file and told nobody.
            using var f = await OpenedModelAsync("HarmConsentTells.bim");
            await f.ConsentAsync();   // marks the model: this one is a commit
            var told = 0;
            f.Sessions.Bus.Changed += _ => told++;

            await f.Engine.WithdrawConsentAsync("agent");
            Assert.Equal(1, told);
            await f.ConsentAsync();
            Assert.Equal(2, told);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, "")]
        [InlineData(HttpStatusCode.Forbidden, "<html><body>Forbidden</body></html>")]
        [InlineData(HttpStatusCode.Unauthorized, "{\"error\":{\"type\":\"authentication_error\",\"message\":\"Cannot authenticate with the server\"}}")]
        [InlineData(HttpStatusCode.InternalServerError, "")]
        [InlineData(HttpStatusCode.BadGateway, "{\"error\":\"upstream_failed\",\"reason\":\"The advice service could not answer.\"}")]
        public void A_refusal_nobody_can_act_on_reads_as_one_calm_sentence(HttpStatusCode status, string body)
        {
            // Kane, 8 Oct: no blame on "this session's sign-in", no status number, no engine words. One line.
            var sentence = JevDecisionOracle.RefusalReason(body, status, out _);

            Assert.Equal("Jev is not available right now.", sentence);
        }

        [Fact]
        public void A_connection_that_fails_or_a_deadline_that_passes_reads_the_same_calm_sentence()
        {
            Assert.Equal("Jev is not available right now.", JevDecisionOracle.UnreachableReason);
            Assert.Equal("Jev is not available right now.", JevDecisionOracle.TimedOutReason);
            Assert.True(JevDecisionOracle.IsNotAvailable(JevDecisionOracle.UnreachableReason));
            Assert.True(JevDecisionOracle.IsNotAvailable(JevDecisionOracle.QueueDroppedReason));
            // A refusal the person can act on is not in the class: the relay's own sentence stands.
            Assert.False(JevDecisionOracle.IsNotAvailable("This licence has run out. Renew it to use cloud advice again."));
            Assert.False(JevDecisionOracle.IsNotAvailable("Cloud advice is part of Semanticus Pro."));
        }

        [Fact]
        public void A_refusal_the_person_can_act_on_keeps_the_relays_own_sentence()
        {
            const string Ended = "This licence has run out. Renew it to use cloud advice again.";
            var sentence = JevDecisionOracle.RefusalReason("{\"error\":\"expired\",\"reason\":" + System.Text.Json.JsonSerializer.Serialize(Ended) + "}",
                                                          HttpStatusCode.Forbidden, out var code);

            Assert.Equal(Ended, sentence);
            Assert.Equal("expired", code);
        }

        [Fact]
        public void The_Start_plan_says_the_same_calm_sentence_and_keeps_a_sentence_the_person_can_act_on()
        {
            Assert.Equal("Jev is not available right now.", Semanticus.Engine.StartPlan.StartDecideCopy.StoppedReason(JevDecisionOracle.UnreachableReason));
            Assert.Equal("Jev is not available right now.", Semanticus.Engine.StartPlan.StartDecideCopy.StoppedReason(JevDecisionOracle.QueueDroppedReason));
            Assert.Equal("Jev stopped before it had looked at everything: This licence has run out. Renew it to use cloud advice again.",
                         Semanticus.Engine.StartPlan.StartDecideCopy.StoppedReason("This licence has run out. Renew it to use cloud advice again"));
        }

        [Fact]
        public void An_Ask_Jev_did_not_answer_says_one_calm_sentence_with_no_prefix_and_no_cause()
        {
            Assert.Equal("Couldn't reach Jev. Check your internet connection and ask again.",
                         LocalEngine.NotAnsweredNote(JevDecisionOracle.NotAvailableReason));
            Assert.Equal("Couldn't reach Jev. Check your internet connection and ask again.",
                         LocalEngine.NotAnsweredNote(JevDecisionOracle.UnreachableReason));
            // The relay's own sentence, once, with no "Jev did not answer:" in front of it.
            Assert.Equal("This licence has run out. Renew it to use cloud advice again.",
                         LocalEngine.NotAnsweredNote("This licence has run out. Renew it to use cloud advice again."));
            Assert.Equal("This session has used its suggestion budget.",
                         LocalEngine.NotAnsweredNote("this session has used its suggestion budget"));
        }

        [Fact]
        public void A_busy_refusal_promises_no_time_it_will_be_asked_again()
        {
            // A landed plan run is reused for the same revision, and the allowance reading says this sentence too, so
            // "it will be asked again later" was not true of every caller (verify, 28 Sep, L9).
            var sentence = JevDecisionOracle.RefusalReason("", HttpStatusCode.TooManyRequests, out _);

            Assert.Contains("busy", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("later", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("asked again", sentence, StringComparison.Ordinal);
        }

        // ---- 8. the day a Plan row names is the person's ---------------------------------------------------

        [Fact]
        public void A_task_names_the_day_it_was_added_in_the_persons_own_time_zone()
        {
            var brisbane = TimeZoneInfo.CreateCustomTimeZone("AEST-test", TimeSpan.FromHours(10), "AEST", "AEST");
            // 04:56 on 28 September in Brisbane is 18:56 on 27 September in UTC.
            var task = new PlanTask
            {
                Id = "task-1", Words = "Check the invoice totals", Source = PlanSources.Words, Doer = PlanDoers.You,
                CreatedUtc = new DateTimeOffset(2026, 9, 27, 18, 56, 0, TimeSpan.Zero),
            };

            PlanDay.ZoneForTest.Value = brisbane;
            try
            {
                var item = PlanTasks.ItemFor(task, false, null);
                Assert.Contains(item.Why, w => w.Contains("28 September 2026", StringComparison.Ordinal));
                Assert.DoesNotContain(item.Why, w => w.Contains("27 September", StringComparison.Ordinal));
            }
            finally { PlanDay.ZoneForTest.Value = null; }
        }

        // ---- 9. the cleanup is the same, whether or not a report has been read (fix round 2) ----------------
        //
        // Coordinator ruling, 28 Sep (Kane: anyone who follows the Plan must not be hurt): the cleanup row is ALWAYS
        // optional, always last, and no row ever waits for it, whether or not any report has been read, because reading
        // one report proves nothing about the others. With reports read it offers only what none of them uses and says
        // how many were read. It never offers a key, a column a relationship uses (every part of a key made of
        // several), a date or a date key, a column of a date table, a name column, a measure, or a code table.

        [Fact]
        public async Task With_a_report_read_the_cleanup_stays_optional_last_and_says_how_many_reports_were_read()
        {
            using var f = await NewModelAsync("HarmReadOptional.bim");
            await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, "HarmReadOptional.bim", "fact_invoice", "INV_AMT");

            var plan = await f.Engine.GetModelPlanAsync();
            var cleanup = Assert.Single(plan.Items, i => i.Id == "cleanup/model");

            Assert.True(cleanup.Optional);
            Assert.Equal("Optional. 1 report's use of this model has been read, and it does not show these. Other reports may still use them. Nothing else on the plan waits for this.",
                cleanup.Why.First());
            Assert.Equal("cleanup/model", Open(plan).Last().Id);
            Assert.DoesNotContain(plan.Items, i => (i.WaitsFor ?? Array.Empty<PlanWaitRef>()).Any(w => w.Id == "cleanup/model"));
            Assert.DoesNotContain(plan.Worth.Lines, l => l.Contains("unused", StringComparison.OrdinalIgnoreCase));

            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            var set = proposed.Plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet;
            Assert.All(set.Lines.Where(l => l.Kept), l => Assert.DoesNotContain(NeverOffered, n => (l.Target ?? "").EndsWith("/" + n, StringComparison.Ordinal)));
            Assert.Contains(set.Lines.Where(l => l.Kept), l => (l.Target ?? "").EndsWith("/INDUSTRY_CD", StringComparison.Ordinal));
        }

        [Fact]
        public async Task The_cleanup_never_ticks_a_key_or_any_part_of_a_key_made_of_two()
        {
            var f = await NewModelAsync("HarmKeys.bim");
            using (f)
            {
                var e = f.Engine;
                await e.CreateColumnAsync("table:fact_invoice", "INV_NO", "String", null, "human");
                await e.CreateColumnAsync("table:fact_invoice", "INV_LINE_ID", "String", null, "human");
                await e.CreateColumnAsync("table:fact_invoice", "NOTE_TXT", "String", null, "human");
                await e.CreateColumnAsync("table:fact_invoice", "PRJ_KEY", "String", null, "human");
                await e.CreateTableAsync("dim_project", "human");
                await e.CreateColumnAsync("table:dim_project", "PRJ_GRP", "String", null, "human");
                await e.CreateColumnAsync("table:dim_project", "PRJ_SEQ", "Int64", null, "human");
                await e.CreateCalculatedColumnAsync("table:dim_project", "PRJ_ID", "dim_project[PRJ_GRP] & \"-\" & dim_project[PRJ_SEQ]", "human");
                // No relationship to PRJ_ID: a calculated column has no type until it is evaluated, and a relationship
                // needs two of the same. Its name says it is the key, and the guard holds a key's parts either way.
                await f.SaveAsAsync("HarmKeys.bim");
                await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, "HarmKeys.bim", "fact_invoice", "INV_AMT");

                var plan = await e.GetModelPlanAsync();
                var cleanup = Assert.Single(plan.Items, i => i.Id == "cleanup/model");
                var proposed = await e.ModelPlanActionAsync("cleanup/model", "propose", "human");
                var lines = proposed.Plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet.Lines;
                var targets = lines.Where(l => l.Kept).Select(l => l.Target ?? "").ToArray();

                // An invoice number and a line id say which row; PRJ_KEY and PRJ_ID are keys by name; the key PRJ_ID is
                // built from PRJ_GRP and PRJ_SEQ, so removing either breaks it. None of them starts ticked (round 5).
                foreach (var r in new[] { "fact_invoice/INV_NO", "fact_invoice/INV_LINE_ID", "fact_invoice/PRJ_KEY", "dim_project/PRJ_ID", "dim_project/PRJ_GRP", "dim_project/PRJ_SEQ" })
                    Assert.DoesNotContain("column:" + r, targets);
                // The ones the sweep lists at all are lines, unticked, and the key-named ones say so.
                foreach (var r in new[] { "fact_invoice/INV_NO", "fact_invoice/INV_LINE_ID", "fact_invoice/PRJ_KEY" })
                {
                    var line = lines.SingleOrDefault(l => l.Target == "column:" + r);
                    if (line != null) Assert.Contains("key", line.Reason, StringComparison.Ordinal);
                }
                // A real candidate still starts ticked, and the row says what starts ticked and that the person reviews it.
                Assert.Contains("column:fact_invoice/NOTE_TXT", targets);
                Assert.Contains(LocalEngine.CleanupPurposeSentence, cleanup.Why);
            }
        }

        [Fact]
        public async Task A_report_checked_again_at_a_new_revision_brings_the_cleanup_back_on_the_next_read()
        {
            // Found rerunning the check's journeys (fix round 2): on Pro, Run the plan moves the revision and returns a
            // Plan read at once, while the report's reading is still stamped at the old revision, so nothing can be
            // called unused. Checking the report again did not change the Plan's cache key (the same report, the same
            // selection, the same revision), so the cleanup stayed gone for every read until the next commit.
            // (The row that went away is remembered and shown as done, so every look here is at the OPEN rows.)
            using var f = await NewModelAsync("HarmRecheck.bim");
            await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, "HarmRecheck.bim", "fact_invoice", "INV_AMT");
            Assert.Contains(Open(await f.Engine.GetModelPlanAsync()), i => i.Id == "cleanup/model");

            await f.Engine.SetObjectPropertyAsync("column:dim_client/INDUSTRY_CD", "Description", "The client's industry code.", "human");
            var stale = await f.Engine.GetModelPlanAsync();   // the reading is a revision old: nothing is unused yet
            Assert.DoesNotContain(Open(stale), i => i.Id == "cleanup/model");

            await PlanHarmKit.CheckReportsAgainAsync(f);
            Assert.Contains(Open(await f.Engine.GetModelPlanAsync()), i => i.Id == "cleanup/model");
        }

        [Fact]
        public async Task The_optional_row_says_Optional_even_while_it_waits()
        {
            using var f = await OpenedModelAsync("HarmOptionalWaits.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var cleanup = Assert.Single(plan.Items, i => i.Id == "cleanup/model");

            // It waits for the rows it conflicts with, and the waits line replaces its sub line on the collapsed row, so
            // the word must be on that line too (verify, 28 Sep, L1).
            Assert.NotEmpty(cleanup.WaitsFor ?? Array.Empty<PlanWaitRef>());
            Assert.StartsWith("Optional. Waits for: ", cleanup.Sub, StringComparison.Ordinal);
        }
    }

    /// <summary>Round 3 (verify, 29 Sep): the cleanup's never-offer classes are read off the SHAPE of the model, not off
    /// which end of a relationship a table sits on or what a name is spelled like. Each model is small, saved and opened
    /// again (a fresh session); every one keeps a plain unused text column the row must still offer.</summary>
    public sealed class PlanHarmRoundThreeTests
    {
        private static async Task<DecisionFixture> OpenedAsync(string file, Func<LocalEngine, Task> build)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await build(f.Engine);
            await f.SaveAsAsync(file);
            f.UseNull();
            await f.Engine.OpenAsync(Path.Combine(f.Workspace, file), discardUnsaved: true);
            f.UseNull();
            return f;
        }

        private static async Task<string[]> OfferedAsync(DecisionFixture f, string control)
        {
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            // Round 5: "offered" is TICKED. A held column is still a line, unticked, and the person decides.
            var targets = proposed.Plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet.Lines.Where(l => l.Kept).Select(l => l.Target ?? "").ToArray();
            Assert.Contains(control, targets);
            return targets;
        }

        [Fact]
        public async Task A_header_fact_that_no_dimension_relates_to_is_still_a_fact_when_a_line_fact_looks_it_up()
        {
            // The header sits on the one side of the line fact and on the many side of nothing: not a fact by which end it
            // is on. It holds amounts and facts look it up, so its amounts are its data.
            using var f = await OpenedAsync("R3Header.bim", async e =>
            {
                await e.CreateTableAsync("orders", "human");
                await e.CreateColumnAsync("table:orders", "ORDER_NO", "String", null, "human");
                await e.CreateColumnAsync("table:orders", "TOTAL_AMT", "Double", null, "human");
                await e.CreateColumnAsync("table:orders", "NOTE_TXT", "String", null, "human");
                await e.CreateTableAsync("order_lines", "human");
                await e.CreateColumnAsync("table:order_lines", "ORDER_NO", "String", null, "human");
                await e.CreateColumnAsync("table:order_lines", "LINE_AMT", "Double", null, "human");
                await e.CreateRelationshipAsync("column:order_lines/ORDER_NO", "column:orders/ORDER_NO", null, true, "human");
                // Round 4: a table nothing else relates to is never offered from, so the control is a column on a related
                // dimension whose label the row can find (THE CLEANUP OFFER RULE, version 2).
                await e.CreateTableAsync("dim_client", "human");
                await e.CreateColumnAsync("table:dim_client", "CLIENT_KEY", "Int64", null, "human");
                await e.CreateColumnAsync("table:dim_client", "CLIENT", "String", null, "human");
                await e.CreateColumnAsync("table:dim_client", "NOTE_TXT", "String", null, "human");
                await e.CreateColumnAsync("table:order_lines", "CLIENT_KEY", "Int64", null, "human");
                await e.CreateRelationshipAsync("column:order_lines/CLIENT_KEY", "column:dim_client/CLIENT_KEY", null, true, "human");
            });
            var targets = await OfferedAsync(f, "column:dim_client/NOTE_TXT");
            Assert.DoesNotContain("column:orders/TOTAL_AMT", targets);
            Assert.DoesNotContain("column:order_lines/LINE_AMT", targets);
        }

        [Fact]
        public async Task A_prefixed_tables_name_column_named_after_its_entity_is_never_offered()
        {
            using var f = await OpenedAsync("R3Prefixed.bim", async e =>
            {
                await e.CreateTableAsync("dim_products", "human");
                await e.CreateColumnAsync("table:dim_products", "PRODUCT_ID", "Int64", null, "human");
                await e.CreateColumnAsync("table:dim_products", "Product", "String", null, "human");
                await e.CreateColumnAsync("table:dim_products", "NOTE_TXT", "String", null, "human");
                await e.CreateTableAsync("sales", "human");
                await e.CreateColumnAsync("table:sales", "PRODUCT_ID", "Int64", null, "human");
                await e.CreateColumnAsync("table:sales", "AMOUNT", "Double", null, "human");
                await e.CreateRelationshipAsync("column:sales/PRODUCT_ID", "column:dim_products/PRODUCT_ID", null, true, "human");
            });
            var targets = await OfferedAsync(f, "column:dim_products/NOTE_TXT");
            Assert.DoesNotContain("column:dim_products/Product", targets);
        }

        // RETIRED (round 4): A_flat_table_that_carries_a_year_and_a_month_is_data_not_a_calendar. Its premise, that a flat table's
        // plain text column is offered once the calendar guess steps aside, is gone: the offer rule never offers from a table
        // nothing relates to, and it no longer guesses calendars in order to offer. A flat table's columns held is S12's case.

        [Fact]
        public async Task A_report_that_could_not_be_read_makes_the_row_say_so_plainly()
        {
            using var f = await OpenedAsync("R3Unreadable.bim", async e =>
            {
                await e.CreateTableAsync("sales", "human");
                foreach (var (n, t) in new[] { ("REGION_KEY", "Int64"), ("AMOUNT", "Double") })
                    await e.CreateColumnAsync("table:sales", n, t, null, "human");
                // Round 4: a row exists only when something can be offered, and that is only from a related table.
                await e.CreateTableAsync("dim_region", "human");
                foreach (var (n, t) in new[] { ("REGION_KEY", "Int64"), ("REGION", "String"), ("NOTE_TXT", "String") })
                    await e.CreateColumnAsync("table:dim_region", n, t, null, "human");
                await e.CreateRelationshipAsync("column:sales/REGION_KEY", "column:dim_region/REGION_KEY", null, true, "human");
            });
            var pbix = Path.Combine(f.Workspace, "Sales Report.pbix");
            File.WriteAllBytes(pbix, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
            await f.Engine.SetReportScopeAsync(
                new[] { new Semanticus.Engine.Lineage.ReportScopeChoice { Kind = "local", Name = "Sales Report", Path = pbix } }, "human");
            await PlanHarmKit.CheckReportsAgainAsync(f);

            var row = Assert.Single((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "cleanup/model");
            Assert.Equal(LocalEngine.CleanupOptionalUnreadableSentence(1), row.Why[0]);
            Assert.Contains("could not be read", row.Why[0], StringComparison.Ordinal);
            Assert.DoesNotContain(row.Why, w => w.Contains("has been read", StringComparison.Ordinal));
        }
    }
}
