// Verify of lane m11/sol1-plan (M11 Sol 6.1 findings 1.1 to 1.3, 2.1 to 2.3): the nearest sibling of each fix, beside the
// builder's own scenario tests in SolFixesPlan1Tests.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifySolFixesPlan1Tests
    {
        private static string SummarizeFixture(IEnumerable<(string Digest, string Choice, double Confidence)> rows)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (digest, choice, confidence) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":\"column.summarize\"")
                  .Append(",\"usage\":{\"input_tokens\":500,\"output_tokens\":20}")
                  .Append(",\"answers\":{\"summarize_by\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                  .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            }
            return sb.Append("]}").ToString();
        }

        private static async Task<string[]> SummarizeMadeAsync(DecisionFixture f, Func<string, (string, double)?> answer)
        {
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));
            var rows = prepared.Asks.Where(a => a.TableName == "Sales").Select(a => (a.Ref, answer(a.ColumnName)))
                .Where(x => x.Item2 != null).Select(x => (x.Ref, x.Item2.Value.Item1, x.Item2.Value.Item2)).ToArray();
            f.Engine.UseDecisionsForTest(new DecisionService(new ByObjectRecordedOracle(SummarizeFixture(rows), "jev-test-1"), new DecisionBudget()));
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            var plan = await f.Engine.GetModelPlanAsync();
            return plan.Items.Where(i => i.Id == "jev.column-summarize.table:Sales")
                .SelectMany(i => i.ChangeSet.Lines).Where(l => l.Op == "create_measure").Select(l => l.Target).ToArray();
        }

        // 1.1 sibling: a key the spec DECLARED (the annotation, not TOM's IsKey), and the parts of a declared calculated key with
        // no relationship at all.
        [Fact]
        public async Task V_1_1_parts_of_a_declared_key_are_never_totalled_sure_or_unsure()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Line", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Region", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Store", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateCalculatedColumnAsync("table:Sales", "StoreKey", "Sales[Region] & \"-\" & Sales[Store]", "human");
            await f.Sessions.Current.MutateAsync("human", "declare keys", m =>
            {
                SourceKeys.Mark(m.Tables["Sales"].Columns["Line"]);
                SourceKeys.Mark(m.Tables["Sales"].Columns["StoreKey"]);
            });
            await f.SaveAsAsync("V11.bim");
            f.UseNull();
            await f.ConsentAsync();

            var made = await SummarizeMadeAsync(f, n => n switch
            {
                "Line" => ("none", 0.5), "Region" => ("none", 0.5), "Store" => ("sum", 0.95), "Amount" => ("sum", 0.95), _ => null,
            });
            Assert.Contains("measure:Sales/Total Amount", made);
            Assert.DoesNotContain(made, t => t.Contains("Line") || t.Contains("Region") || t.Contains("Store"));
        }

        // 1.2 sibling, the Free starter path: other spellings of a date held as a number.
        [Fact]
        public async Task V_1_2_other_date_spellings_are_never_a_starter_total()
        {
            Assert.True(StarterMeasureTemplates.NeverSummed("DateKeyNum"));
            Assert.True(StarterMeasureTemplates.NeverSummed("YYYYMMDD"));
            Assert.True(StarterMeasureTemplates.NeverSummed("OrderYYYYMMDD"));
            Assert.True(StarterMeasureTemplates.NeverSummed("ship_dt_int"));
            // and no false alarm on words that only contain the letters
            Assert.False(StarterMeasureTemplates.NeverSummed("Mandate Amount"));
            Assert.False(StarterMeasureTemplates.NeverSummed("Updated Units"));
            Assert.False(StarterMeasureTemplates.NeverSummed("Dates Shipped Count"));

            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "DateKeyNum", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "OrderYYYYMMDD", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.SaveAsAsync("V12.bim");
            f.UseNull();
            var starter = (await e.GetModelPlanAsync()).Items.Where(i => i.Id.StartsWith("starter/", StringComparison.Ordinal))
                .SelectMany(i => i.Diff ?? Array.Empty<string>()).ToArray();
            Assert.Contains(starter, d => d.Contains("Total Amount", StringComparison.Ordinal));
            Assert.DoesNotContain(starter, d => d.Contains("DateKeyNum", StringComparison.Ordinal) || d.Contains("YYYYMMDD", StringComparison.Ordinal));
        }

        // 1.2 sibling, the Pro path (column.summarize): the same date held as a number, Jev unsure and Jev sure.
        [Fact]
        public async Task V_1_2_pro_a_date_held_as_a_number_is_never_totalled_by_column_summarize()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "OrderDateInt", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "DateKeyNum", "Int64", null, "human");
            await f.SaveAsAsync("V12p.bim");
            f.UseNull();
            await f.ConsentAsync();
            var made = await SummarizeMadeAsync(f, n => n switch
            {
                "OrderDateInt" => ("none", 0.5), "DateKeyNum" => ("sum", 0.95), "Amount" => ("sum", 0.95), _ => null,
            });
            Assert.Contains("measure:Sales/Total Amount", made);
            Assert.True(!made.Any(t => t.Contains("OrderDateInt") || t.Contains("DateKeyNum")),
                "column.summarize totals a date held as a number: " + string.Join("; ", made));
        }

        // 1.3 sibling: the value survives a real model change (the family works its lines out again), not only a cached re-read.
        [Fact]
        public async Task V_1_3_an_authored_description_survives_a_model_change()
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
            await f.SaveAsAsync("V13.bim");
            f.UseNull();
            await f.ConsentAsync();
            foreach (var other in new[] { DecisionRegistry.MeasureFolderAndFormat, DecisionRegistry.ColumnFormat, DecisionRegistry.ColumnCategory,
                                          DecisionRegistry.RelationshipJoin, DecisionRegistry.DateTable, DecisionRegistry.AiSchema })
                await e.SetDecisionPointEnabledAsync(other, false, "human");
            var made = await SummarizeMadeAsync(f, n => n == "Amount" ? ("sum", 0.95) : null);
            Assert.Contains("measure:Sales/Total Amount", made);
            ModelPlanChangeLine Description(ModelPlanView p) => p.Items.Single(i => i.Id == "jev.column-summarize.table:Sales")
                .ChangeSet.Lines.Single(l => l.Op == "set_description" && l.Target == "measure:Sales/Total Amount");
            var line = Description(await e.GetModelPlanAsync());
            await e.SetPlanItemAsync(line.PlanItemId, "Money in, as finance counts it.", null, "human");

            await e.CreateColumnAsync("table:Customer", "Region", "String", null, "human");   // the model changes under the Plan
            await e.GetModelPlanAsync();
            await e.JevSettledForTest();
            Assert.Equal("Money in, as finance counts it.", Description(await e.GetModelPlanAsync()).After);
        }

        // K19 (8 Oct): the undo-redo-undo and undo-to tests of an undone permission marker are gone. Permission is kept on this machine, not in the model, so an undo never touches it.

        // 2.3 control: a redo is the person giving it again, so it is current after the swap.
        [Fact]
        public async Task V_2_3_a_redone_permission_is_kept_across_the_swap()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("V23r.bim");
            await f.ConsentAsync();
            await f.Engine.UndoAsync("human");
            await f.Engine.RedoAsync("human");
            await f.Engine.CreateModelAsync("Other", 1604, discardUnsaved: true);
            await f.Engine.OpenAsync(Path.Combine(f.Workspace, "V23r.bim"), true);
            Assert.True((await f.Engine.GetConsentAsync()).Current);
        }

        // 2.2 siblings: switch files written before this change (no times).
        private sealed class Machine
        {
            public string Profile, LocalAppData;
            public int Port = 54300;
            public string InstallerRoot => Path.Combine(LocalAppData, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
        }

        private static LiveOrigin StampWindow(Machine m, string title)
        {
            var port = m.Port++;
            var ws = Path.Combine(m.InstallerRoot, "AnalysisServicesWorkspace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ws, "Data"));
            File.WriteAllText(Path.Combine(ws, "Data", "msmdsrv.port.txt"), port.ToString(), Encoding.Unicode);
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
        }

        /// <summary>Strip the times this change added, from every switch file under the fixture's home, as an older engine wrote them.</summary>
        private static int StripTimes(DecisionFixture f, string key)
        {
            var n = 0;
            foreach (var file in Directory.EnumerateFiles(f.Home, "*.json", SearchOption.AllDirectories))
            {
                JsonNode root;
                try { root = JsonNode.Parse(File.ReadAllText(file)); } catch { continue; }
                if (root?["points"] is not JsonObject points) continue;
                var changed = false;
                foreach (var kv in points.ToList())
                    if (kv.Value is JsonObject p && p.Remove(key)) changed = true;
                if (changed) { File.WriteAllText(file, root.ToJsonString()); n++; }
            }
            return n;
        }

        [Fact]
        public async Task V_2_2_an_own_on_an_older_engine_wrote_loses_to_a_newer_off()
        {
            const string point = DecisionRegistry.AiSchema;
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var m = new Machine { Profile = Path.Combine(f.Home, "profile") };
            m.LocalAppData = Path.Combine(m.Profile, "AppData", "Local");
            var a = StampWindow(m, "Sales - Power BI Desktop");
            var b = StampWindow(m, "Sales - Power BI Desktop");
            await AttachAsync(f, a, "A");
            await f.Engine.SetDecisionPointEnabledAsync(point, true, "human");
            Assert.True(StripTimes(f, "at") > 0);   // A's on, as an older engine wrote it
            await AttachAsync(f, b, "B");
            await f.Engine.SetDecisionPointEnabledAsync(point, false, "human");
            await AttachAsync(f, a, "A");
            Assert.False(f.Engine.DecisionPointEnabled(point));
        }

        /// <summary>The disclosed limit: an off an older engine wrote (no times) cannot be told newer, so an own on still wins, as it
        /// did before this change. Pinned so the join sees the reading, not asserted as safe.</summary>
        [Fact]
        public async Task V_2_2_an_off_an_older_engine_wrote_keeps_the_old_reading()
        {
            const string point = DecisionRegistry.AiSchema;
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var m = new Machine { Profile = Path.Combine(f.Home, "profile") };
            m.LocalAppData = Path.Combine(m.Profile, "AppData", "Local");
            var a = StampWindow(m, "Sales - Power BI Desktop");
            var b = StampWindow(m, "Sales - Power BI Desktop");
            await AttachAsync(f, a, "A");
            await f.Engine.SetDecisionPointEnabledAsync(point, true, "human");
            await AttachAsync(f, b, "B");
            await f.Engine.SetDecisionPointEnabledAsync(point, false, "human");
            Assert.True(StripTimes(f, "offAt") > 0);   // B's off, as an older engine wrote it
            await AttachAsync(f, a, "A");
            Assert.True(f.Engine.DecisionPointEnabled(point));   // on until Desktop closes, the pre-change reading
        }
    }
}
