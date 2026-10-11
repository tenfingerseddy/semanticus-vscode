// M11 Sol 6.1 review fixes, Plan and Sending areas (lane m11/sol1-plan). Every fact here failed first, against the
// build at d141ce01, on Sol's own scenario (findings-1.md 1.1 to 1.3, findings-2.md 2.1 to 2.3) and the join-2
// completeness items B to D.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class SolFixesPlan1Tests
    {
        private const string SummarizePoint = "column.summarize";

        private static string SummarizeFixture(IEnumerable<(string Digest, string Choice, double Confidence)> rows)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (digest, choice, confidence) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":\"").Append(SummarizePoint).Append('"')
                  .Append(",\"usage\":{\"input_tokens\":500,\"output_tokens\":20}")
                  .Append(",\"answers\":{\"summarize_by\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                  .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            }
            return sb.Append("]}").ToString();
        }

        // ---- 1.1: Pro can total a part of a calculated key --------------------------------------------------------

        [Fact]
        public async Task Sol_1_1_a_part_of_a_calculated_key_is_never_totalled_when_Jev_is_unsure()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Dim", "human");
            await e.CreateColumnAsync("table:Dim", "Link", "String", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Department", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Ticket", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateCalculatedColumnAsync("table:Sales", "Link", "Sales[Department] & \"-\" & Sales[Ticket]", "human");
            await e.SetObjectPropertyAsync("column:Sales/Link", "DataType", "String", "human");
            await e.CreateRelationshipAsync("column:Sales/Link", "column:Dim/Link", null, true, "human");
            await f.SaveAsAsync("Sol11.bim");
            f.UseNull();
            await f.ConsentAsync();

            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));
            string Digest(string name) => prepared.Asks.Single(a => a.TableName == "Sales" && a.ColumnName == name).Ref;
            var recorded = new ByObjectRecordedOracle(SummarizeFixture(new[]
            {
                (Digest("Department"), "none", 0.5),   // Sol: none at 0.5 falls to the rule, which chose sum
                (Digest("Ticket"), "sum", 0.95),       // and a SURE sum of the other part is refused the same way
                (Digest("Amount"), "sum", 0.95),
            }), "jev-test-1");
            f.Engine.UseDecisionsForTest(new DecisionService(recorded, new DecisionBudget()));

            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            var plan = await f.Engine.GetModelPlanAsync();
            var sales = plan.Items.Single(i => i.Id == "jev.column-summarize.table:Sales");
            var made = sales.ChangeSet.Lines.Where(l => l.Op == "create_measure").Select(l => l.Target).ToArray();

            Assert.Contains("measure:Sales/Total Amount", made);
            Assert.DoesNotContain(made, t => t.Contains("Department", StringComparison.Ordinal));
            Assert.DoesNotContain(made, t => t.Contains("Ticket", StringComparison.Ordinal));
        }

        // ---- 1.2: a numeric date column becomes a starter total -----------------------------------------------------

        [Fact]
        public async Task Sol_1_2_a_date_held_as_a_number_is_never_a_starter_total_whatever_its_last_word()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "OrderDateInt", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.SaveAsAsync("Sol12.bim");
            f.UseNull();

            var plan = await e.GetModelPlanAsync();
            var starter = plan.Items.Where(i => i.Id.StartsWith("starter/", StringComparison.Ordinal)).SelectMany(i => i.Diff ?? Array.Empty<string>()).ToArray();
            Assert.Contains(starter, d => d.Contains("Total Amount", StringComparison.Ordinal));   // the starter row is there
            Assert.True(!starter.Any(d => d.Contains("OrderDateInt", StringComparison.Ordinal)),
                "the starter row totals a date held as a number: " + string.Join("; ", starter));
            Assert.True(StarterMeasureTemplates.NeverSummed("OrderDateInt"));
            Assert.False(StarterMeasureTemplates.NeverSummed("Update Count"));
        }

        // ---- 1.3: a refresh drops an authored starter value ---------------------------------------------------------

        [Theory]
        [InlineData("agent")]
        [InlineData("human")]
        public async Task Sol_1_3_a_description_somebody_wrote_over_a_starter_measure_survives_the_next_read(string origin)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.SaveAsAsync("Sol13-" + origin + ".bim");
            f.UseNull();
            await f.ConsentAsync();
            foreach (var other in new[] { DecisionRegistry.MeasureFolderAndFormat, DecisionRegistry.ColumnFormat, DecisionRegistry.ColumnCategory,
                                          DecisionRegistry.RelationshipJoin, DecisionRegistry.DateTable, DecisionRegistry.AiSchema })
                await e.SetDecisionPointEnabledAsync(other, false, "human");
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));
            var recorded = new ByObjectRecordedOracle(SummarizeFixture(new[]
            {
                (prepared.Asks.Single(a => a.TableName == "Sales" && a.ColumnName == "Amount").Ref, "sum", 0.95),
            }), "jev-test-1");
            e.UseDecisionsForTest(new DecisionService(recorded, new DecisionBudget()));

            await e.GetModelPlanAsync();
            await e.JevSettledForTest();
            var plan = await e.GetModelPlanAsync();
            ModelPlanChangeLine Description(ModelPlanView p) => p.Items.Single(i => i.Id == "jev.column-summarize.table:Sales")
                .ChangeSet.Lines.Single(l => l.Op == "set_description" && l.Target == "measure:Sales/Total Amount");
            var line = Description(plan);
            Assert.NotEqual("Total takings, as our finance team says it.", line.After);

            await e.SetPlanItemAsync(line.PlanItemId, "Total takings, as our finance team says it.", null, origin);
            Assert.Equal("Total takings, as our finance team says it.", Description(await e.GetModelPlanAsync()).After);

            // Read again, model unchanged, twice: the cached run puts its own lines back, and the person's words stay.
            await e.JevSettledForTest();
            Assert.Equal("Total takings, as our finance team says it.", Description(await e.GetModelPlanAsync()).After);
            await e.JevSettledForTest();
            var again = await e.GetModelPlanAsync();
            Assert.Equal("Total takings, as our finance team says it.", Description(again).After);
            Assert.Single(again.Items.Single(i => i.Id == "jev.column-summarize.table:Sales").ChangeSet.Lines,
                l => l.Op == "set_description" && l.Target == "measure:Sales/Total Amount");
        }

        // ---- 2.1: queued Jev requests keep sending after their point is switched off ---------------------------------

        private sealed class GatedOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private int _calls;
            public readonly TaskCompletionSource<bool> Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public GatedOracle(IDecisionOracle inner) { _inner = inner; }
            public bool Available => true;
            public int Calls => Volatile.Read(ref _calls);
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                Interlocked.Increment(ref _calls);
                await Gate.Task.ConfigureAwait(false);
                return await _inner.AskAsync(request, ct).ConfigureAwait(false);
            }
        }

        [Fact]
        public async Task Sol_2_1_a_request_waiting_behind_six_others_sends_nothing_once_its_point_is_switched_off()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Sales", "human");
            for (var i = 1; i <= 9; i++) await e.CreateColumnAsync("table:Sales", "Measure" + i, "Double", null, "human");
            await f.SaveAsAsync("Sol21.bim");
            f.UseNull();
            e.JevBatchedForTest = _ => false;   // one request per column: nine asks, six in flight, three waiting
            await f.ConsentAsync();
            foreach (var other in new[] { DecisionRegistry.MeasureFolderAndFormat, DecisionRegistry.ColumnFormat, DecisionRegistry.ColumnCategory,
                                          DecisionRegistry.RelationshipJoin, DecisionRegistry.DateTable, DecisionRegistry.MeasureStarter, DecisionRegistry.AiSchema })
                await e.SetDecisionPointEnabledAsync(other, false, "human");
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));
            var gated = new GatedOracle(new ByObjectRecordedOracle(SummarizeFixture(prepared.Asks.Select(a => (a.Ref, "sum", 0.95))), "jev-test-1"));
            e.UseDecisionsForTest(new DecisionService(gated, new DecisionBudget()));

            await e.GetModelPlanAsync();   // starts the background run
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (gated.Calls < 6 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(6, gated.Calls);

            await e.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");
            gated.Gate.SetResult(true);
            await e.JevSettledForTest();
            await Task.Delay(500);

            Assert.Equal(6, gated.Calls);   // the three waiting requests never started
        }

        // ---- 2.2: an older Desktop connection's on beats a newer off for the same title -----------------------------

        private sealed class Machine
        {
            public string Profile, LocalAppData;
            public int Port = 54100;
            public string InstallerRoot => Path.Combine(LocalAppData, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
        }

        private static LiveOrigin StampWindow(Machine m, string title)
        {
            var port = m.Port++;
            var ws = Path.Combine(m.InstallerRoot, "AnalysisServicesWorkspace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ws, "Data"));
            File.WriteAllText(Path.Combine(ws, "Data", "msmdsrv.port.txt"), port.ToString(), System.Text.Encoding.Unicode);
            var exe = "\"C:\\Program Files\\Microsoft Power BI Desktop\\bin\\PBIDesktop.exe\"";
            var id = LocalDesktop.Resolve(ws, new List<LocalDesktop.ProcInfo>
            {
                new LocalDesktop.ProcInfo { Pid = 30, ParentPid = 1, Name = "PBIDesktop.exe", Title = title, CommandLine = exe },
                new LocalDesktop.ProcInfo { Pid = 31, ParentPid = 30, Name = "msmdsrv.exe", CommandLine = "\"msmdsrv.exe\" -c -s \"" + ws + "\\Data\"" },
            });
            return LocalEngine.LocalOriginFor("localhost:" + port, Guid.NewGuid().ToString(), id, null,
                LocalDesktop.WorkspaceRoots(m.LocalAppData, m.Profile), () => new List<LocalDesktop.ProcInfo>());
        }

        private static async Task AttachAsync(DecisionFixture f, LiveOrigin origin, string modelName)
        {
            await f.Engine.CreateModelAsync(modelName, 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = origin;
            Assert.True(origin.LocalDesktop);
        }

        [Fact]
        public async Task Sol_2_2_a_newer_off_for_the_same_title_beats_an_older_on_of_a_reattached_connection_and_a_later_on_still_works()
        {
            const string point = DecisionRegistry.AiSchema;
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var m = new Machine { Profile = Path.Combine(f.Home, "profile") };
            m.LocalAppData = Path.Combine(m.Profile, "AppData", "Local");
            var windowA = StampWindow(m, "Sales - Power BI Desktop");
            var windowB = StampWindow(m, "Sales - Power BI Desktop");

            await AttachAsync(f, windowA, "A");
            await f.Engine.SetDecisionPointEnabledAsync(point, true, "human");   // explicit on, in window A
            Assert.True(f.Engine.DecisionPointEnabled(point));

            await AttachAsync(f, windowB, "B");
            await f.Engine.SetDecisionPointEnabledAsync(point, false, "human");   // the newer off, in window B, same title
            Assert.False(f.Engine.DecisionPointEnabled(point));

            await AttachAsync(f, windowA, "A");   // reattach A without restarting Desktop
            Assert.False(f.Engine.DecisionPointEnabled(point), "window A's older on beat the newer off for the title they share");

            await f.Engine.SetDecisionPointEnabledAsync(point, true, "human");   // a later explicit on still works
            Assert.True(f.Engine.DecisionPointEnabled(point));
            await AttachAsync(f, windowB, "B");
            Assert.False(f.Engine.DecisionPointEnabled(point));   // window B's own off is still its own
        }

        // ---- 2.3: undo and permission, then a quick model swap ------------------------------------------------------
        // K19 (8 Oct): permission is kept on this machine, not as a model edit, so an undo no longer takes it back (the old test pinned the
        // marker). What the person switched off with Withdraw stays off across a swap and a reopen.

        [Fact]
        public async Task Sol_2_3_a_withdrawn_permission_stays_withdrawn_when_another_model_is_opened_and_the_same_files_come_back()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("Sol23.bim");
            await f.ConsentAsync();
            Assert.True((await f.Engine.GetConsentAsync()).Current);

            await f.Engine.WithdrawConsentAsync("human");   // the person takes the permission back
            await f.Engine.CreateModelAsync("Other", 1604, discardUnsaved: true);   // and opens another model at once

            await f.Engine.OpenAsync(Path.Combine(f.Workspace, "Sol23.bim"), true);   // the same files, unchanged
            Assert.False((await f.Engine.GetConsentAsync()).Current, "the permission the person withdrew came back with the same files");
        }

        // ---- join-2 items B, C, D -----------------------------------------------------------------------------------

        [Fact]
        public void Join2_B_words_that_take_something_away_are_not_told_they_mention_adding()
        {
            var r = AddingRule.Read(new[] { "make the old measures go away" })[0];
            Assert.Equal(AddingRule.Tier.Unticked, r.Tier);   // still offered unticked, as v5 and v6 ruled
            Assert.True(r.TakesAway);
            Assert.NotEqual(AddingRule.NotAskedReason, r.Reason);
            Assert.Contains("take something away", r.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void Join2_C_a_read_after_an_add_is_a_step_on_Workflows_so_Kanes_own_words_keep_their_add()
        {
            var d = DescribedSteps.From("create a meaure and test its performance");
            Assert.Equal(new[] { "create_measure", "benchmark_dax" }, d.Steps.Select(x => x.Op));
            Assert.True(d.Steps[0].AsksToAdd);
            // The Plan door still counts a read as unread (a draft there runs no read), so the same words keep no add there.
            Assert.False(AddingRule.Read(new[] { "create a meaure", "test its performance" }, new[] { 0, 0 })[0].AsksToAdd);
        }

        [Fact]
        public void Join2_D_the_workflows_reason_never_tells_the_person_to_use_words_they_already_used()
        {
            foreach (var reason in new[] { AddingRule.UnreadReason, AddingRule.SignReason, AddingRule.IntoASetReason, AddingRule.NotNewReason })
            {
                var text = DescribedSteps.WorkflowReasonFor(new AddingRule.Reading(AddingRule.Tier.Unticked, "create_measure", reason, false));
                Assert.DoesNotContain("start with add or create", text, StringComparison.Ordinal);
                Assert.Contains("Author", text, StringComparison.Ordinal);
            }
            var d = DescribedSteps.From("add a Sales measure, Delete the old role");
            Assert.DoesNotContain(d.Steps.Select(x => x.Reason ?? "").Concat(d.Steps.Select(x => x.NotAnAdd ?? "")), t => t.Contains("start with add or create", StringComparison.Ordinal));
            // Words that do not ask to add still get the advice that is true for them.
            var notAsked = DescribedSteps.WorkflowReasonFor(new AddingRule.Reading(AddingRule.Tier.Unticked, "create_measure", AddingRule.NotAskedReason, false));
            Assert.Contains("start with add or create", notAsked, StringComparison.Ordinal);
        }
    }
}
