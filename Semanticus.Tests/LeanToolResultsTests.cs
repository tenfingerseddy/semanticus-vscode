using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class LeanToolResultsTests
    {
        [Fact]
        public async Task Default_scan_with_a_filter_returns_findings_without_an_extra_detail_flag()
        {
            var engine = DispatchProxy.Create<IEngine, LeanEngineFake>();
            var fake = (LeanEngineFake)(object)engine;
            var filtered = Assert.IsType<Scorecard>(await McpTools.AiReadinessScanOp(engine, maxFindings: 3));
            Assert.Equal(3, filtered.Findings.Length);
            Assert.Equal(fake.Ready.Scorecard.Overall, filtered.Overall);
        }

        [Theory]
        [InlineData("get_start_plan")]
        [InlineData("start_model_from_sql")]
        [InlineData("build_start_plan")]
        [InlineData("make_model_ai_ready")]
        [InlineData("apply_safe_fixes")]
        [InlineData("ai_readiness_scan")]
        [InlineData("get_start_words")]
        public async Task Default_fits_the_character_budget(string tool)
        {
            var engine = DispatchProxy.Create<IEngine, LeanEngineFake>();
            var fake = (LeanEngineFake)(object)engine;
            fake.View.Sections[0].Groups[0].Lines = Enumerable.Range(0, 400).Select(i => new StartLine
            {
                Item = new ChangeItem { Id = "line-" + i, Kind = "set_description", ObjectRef = "column:Sales/Column" + i, After = new string('x', 200) },
                Why = "Describe the column.", Kept = true, DecidedBy = "rule",
            }).ToList();
            object result = tool switch
            {
                "start_model_from_sql" => await McpTools.StartModelFromSqlOp(engine, "source", "database", "folder", "model"),
                "build_start_plan" => await McpTools.BuildStartPlanOp(engine, "digest"),
                "make_model_ai_ready" => await McpTools.MakeModelAiReadyOp(engine),
                "apply_safe_fixes" => await McpTools.ApplySafeFixesOp(engine, "air"),
                "ai_readiness_scan" => await McpTools.AiReadinessScanOp(engine),
                "get_start_words" => await McpTools.GetStartWordsOp(engine),
                _ => await McpTools.GetStartPlanOp(engine),
            };
            var size = JsonConvert.SerializeObject(result).Length;
            Assert.True(size <= 8000, "Default " + tool + " returned " + size + " characters.");
            Assert.True(System.Text.Json.JsonSerializer.Serialize(result, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length <= 8000);
            if (tool == "get_start_words")
            {
                var rows = new Newtonsoft.Json.Linq.JArray();
                var offset = 0;
                do
                {
                    var page = await McpTools.GetStartWordsOp(engine, 40, offset);
                    Assert.True(System.Text.Json.JsonSerializer.Serialize(page, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length <= 8000);
                    foreach (var row in page.Items) rows.Add(Newtonsoft.Json.Linq.JToken.FromObject(row));
                    offset += page.Items.Count;
                    if (offset >= page.Total) break;
                    Assert.NotEmpty(page.Items);
                } while (true);
                Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JArray.FromObject(fake.Words), rows));
            }
            else if (tool == "ai_readiness_scan")
            {
                Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JToken.FromObject(fake.Ready.Scorecard),
                    Newtonsoft.Json.Linq.JToken.FromObject(await McpTools.AiReadinessScanOp(engine, detail: "full"))));
            }
            else if (tool == "make_model_ai_ready" || tool == "apply_safe_fixes")
            {
                var rows = new Newtonsoft.Json.Linq.JArray();
                var offset = 0;
                do
                {
                    var response = await McpTools.MakeModelAiReadyOp(engine, item: tool == "apply_safe_fixes" ? "applied" : "queue", offset: offset, max: 17);
                    Assert.True(System.Text.Json.JsonSerializer.Serialize(response, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length <= 8000);
                    var page = response.Summary.Page;
                    foreach (var row in page.Items) rows.Add(Newtonsoft.Json.Linq.JToken.FromObject(row));
                    if (!page.NextOffset.HasValue) break;
                    Assert.True(page.NextOffset.Value > offset);
                    offset = page.NextOffset.Value;
                } while (true);
                object expected = tool == "apply_safe_fixes" ? fake.Ready.Applied : fake.Ready.AiQueue;
                Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JArray.FromObject(expected), rows));
                Assert.Equal(tool == "apply_safe_fixes" ? 0 : 1, fake.ReadyCalls);
            }
            else
            {
                var rows = new Newtonsoft.Json.Linq.JArray();
                var offset = 0;
                do
                {
                    var response = await McpTools.GetStartPlanOp(engine, item: "tables", offset: offset, max: 17);
                    Assert.True(System.Text.Json.JsonSerializer.Serialize(response, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length <= 8000);
                    var page = response.Summary.Page;
                    foreach (var row in page.Items) rows.Add(Newtonsoft.Json.Linq.JToken.FromObject(row));
                    if (!page.NextOffset.HasValue) break;
                    Assert.True(page.NextOffset.Value > offset);
                    offset = page.NextOffset.Value;
                } while (true);
                var expected = fake.View.Sections[0].Groups.SelectMany(g => g.Lines.Select(l => new { g.Table, Line = l }));
                Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JArray.FromObject(expected), rows));
                Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JToken.FromObject(fake.View),
                    Newtonsoft.Json.Linq.JToken.FromObject(await McpTools.GetStartPlanOp(engine, detail: true))));
            }
        }
    }

    public class LeanEngineFake : StartEngineFake
    {
        public int ReadyCalls;
        public StartWordItem[] Words = Enumerable.Range(0, 150).Select(i => new StartWordItem
        { Ref = "column:Sales/Column" + i, Kind = "description", Context = new string('x', 1000) }).ToArray();
        public ReadinessPlan Ready = new ReadinessPlan
        {
            Revision = 7, Applied = Enumerable.Range(0, 300).Select(i => "Fixed column " + i).ToArray(),
            Scorecard = new Scorecard
            {
                Grade = "D", Overall = 60,
                Findings = Enumerable.Range(0, 300).Select(i => new ReadinessFinding
                { ObjectRef = "column:Sales/Column" + i, RuleId = "DESC-COL", Fix = "AiContent", Message = new string('x', 200) }).ToArray(),
            },
            AiQueue = Enumerable.Range(0, 40).Select(i => new ReadinessWorkItem
            {
                Finding = new ReadinessFinding { ObjectRef = "column:Sales/Column" + i, RuleId = "DESC-COL", Fix = "AiContent" },
                Grounding = new GroundingBundle { ObjectRef = "column:Sales/Column" + i, SiblingNames = Enumerable.Range(0, 100).Select(j => "Column" + j).ToArray() },
            }).ToArray(),
        };

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "MakeAiReadyAsync") { ReadyCalls++; return Task.FromResult(Ready); }
            if (method.Name == "ApplySafeFixesAsync") return Task.FromResult(new SafeFixResult { Applied = Ready.Applied, Revision = Ready.Revision, Scorecard = Ready.Scorecard });
            if (method.Name == "AiReadinessScanAsync") return Task.FromResult(Ready.Scorecard);
            if (method.Name == "GetStartWordsAsync") return Task.FromResult(new StartWordsPage
                { Total = Words.Length, Offset = (int)args[1], Items = Words.Skip((int)args[1]).Take((int)args[0]).ToList() });
            if (method.Name == "SessionInfoAsync") return Task.FromResult(new SessionInfo { SessionId = "lean-test", Revision = 7 });
            return base.Invoke(method, args);
        }
    }
    [Collection("restore-root")]
    public sealed class LeanToolResultsModelTests : GateFixtureBase
    {
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;
        private readonly Xunit.Abstractions.ITestOutputHelper _output;
        public LeanToolResultsModelTests(RestoreRootFixture fixture, Xunit.Abstractions.ITestOutputHelper output) : base(fixture)
        {
            _output = output;
            ConnectionRegistry.RootOverride = System.IO.Path.Combine(Root, "connections");
            AgentPolicyStore.SetPreset("open", "human");
        }
        public override void Dispose()
        {
            ConnectionRegistry.RootOverride = _oldConnections;
            base.Dispose();
        }

        private void Size(string shape, string tool, object full, object summary)
        {
            var options = ModelContextProtocol.McpJsonUtilities.DefaultOptions;
            var before = System.Text.Json.JsonSerializer.Serialize(full, options).Length;
            var after = System.Text.Json.JsonSerializer.Serialize(summary, options).Length;
            _output.WriteLine(shape + " | " + tool + " | " + before + " | " + after);
            var log = Environment.GetEnvironmentVariable("SEMANTICUS_LEAN_SIZES");
            if (log != null) System.IO.File.AppendAllText(log, shape + " | " + tool + " | " + before + " | " + after + Environment.NewLine);
            Assert.True(after <= 8000, tool + " returned " + after + " characters.");
        }

        private static async Task CheckPages(IEngine engine, StartPlanView full)
        {
            foreach (var section in full.Sections)
            {
                var expected = Newtonsoft.Json.Linq.JArray.FromObject(section.Groups.SelectMany(g => g.Lines.Select(l => new { g.Table, Line = l })));
                var actual = new Newtonsoft.Json.Linq.JArray();
                var offset = 0;
                do
                {
                    var response = await McpTools.GetStartPlanOp(engine, section.Id, offset, 40);
                    Assert.Equal(full.Digest, response.Digest);
                    var page = response.Summary.Page;
                    Assert.Equal(expected.Count, page.Total);
                    foreach (var row in page.Items) actual.Add(Newtonsoft.Json.Linq.JToken.FromObject(row));
                    if (!page.NextOffset.HasValue) break;
                    Assert.True(page.NextOffset.Value > offset);
                    offset = page.NextOffset.Value;
                } while (true);
                Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(expected, actual), section.Id + " lost detail.");
            }
        }

        [Theory]
        [InlineData("Northstar")]
        [InlineData("Gold-shaped")]
        public async Task Start_build_and_AI_ready_work_on_real_models_with_complete_pages(string shape)
        {
            var schema = shape == "Northstar" ? StartLinkGuessTests.Northstar() :
                JsonConvert.DeserializeObject<FabricSchema>(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "lean-gold-shape.json")))!;
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Free, System.IO.Path.Combine(Root, "workspace"));
            engine.StartPlanSchemaReaderForTest = (_, _) => Task.FromResult(schema);
            var start = await McpTools.StartModelFromSqlOp(engine, schema.Server, schema.Database, System.IO.Path.Combine(Root, "model"), shape);
            Size(shape, "start_model_from_sql", await engine.GetStartPlanAsync(), start);
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            await engine.GetStartPlanAsync();
            await engine.StartPreviewSettledForTest();
            var full = await engine.GetStartPlanAsync();
            var read = await McpTools.GetStartPlanOp(engine);
            Size(shape, "get_start_plan", full, read);
            Size(shape, "get_start_words", await McpTools.GetStartWords(engine, 40, 0), await McpTools.GetStartWordsOp(engine, 40, 0));
            await CheckPages(engine, full);
            Assert.Equal(full.Digest, (await McpTools.GetStartPlanOp(engine, detail: true)).Digest);
            // Other start writers share the same large view. This idempotent edit proves that projection path too.
            var edited = await McpTools.EditStartPlanOp(engine, "include_table", schema.Tables[0].Name, "true");
            Size(shape, "edit_start_plan", await engine.GetStartPlanAsync(), edited);
            await engine.StartPreviewSettledForTest();
            full = await engine.GetStartPlanAsync();
            await engine.StartPreviewSettledForTest();
            full = await engine.GetStartPlanAsync();
            await engine.ApproveStartPlanAsync(full.Digest);
            var build = await McpTools.BuildStartPlanOp(engine, full.Digest);
            Assert.True(build.Built, build.Refused);
            var builtFull = await engine.GetStartPlanAsync();
            Size(shape, "build_start_plan", new StartPlanBuildResult { Built = build.Built, Refused = build.Refused, Revision = build.Revision, View = builtFull }, build);
            Assert.True((await engine.SessionInfoAsync()).Tables > 0);
            await CheckPages(engine, builtFull);
            var ready = await McpTools.MakeModelAiReadyOp(engine, maxQueue: 1000);
            var readyFull = await McpTools.MakeModelAiReadyOp(engine, item: "full");
            Size(shape, "make_model_ai_ready", readyFull, ready);
            var revision = (await engine.SessionInfoAsync()).Revision;
            foreach (var item in new[] { "findings", "queue", "applied" })
            {
                object[] expected = item == "findings" ? readyFull.Scorecard.Findings : item == "queue" ? readyFull.AiQueue : readyFull.Applied;
                var actual = new Newtonsoft.Json.Linq.JArray();
                var offset = 0;
                do
                {
                    var page = (await McpTools.MakeModelAiReadyOp(engine, item: item, offset: offset, max: 40)).Summary.Page;
                    Assert.Equal(expected.Length, page.Total);
                    foreach (var row in page.Items) actual.Add(Newtonsoft.Json.Linq.JToken.FromObject(row));
                    if (!page.NextOffset.HasValue) break;
                    Assert.True(page.NextOffset.Value > offset);
                    offset = page.NextOffset.Value;
                } while (true);
                Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JArray.FromObject(expected), actual), item + " lost detail.");
            }
            Assert.Equal(revision, (await engine.SessionInfoAsync()).Revision);
            var scan = await engine.AiReadinessScanAsync();
            Size(shape, "ai_readiness_scan", scan, await McpTools.AiReadinessScanOp(engine));
            var fixes = await McpTools.ApplySafeFixes(engine, "air");
            Size(shape, "apply_safe_fixes", fixes, await McpTools.ApplySafeFixesOp(engine, "air"));
        }
    }
}
