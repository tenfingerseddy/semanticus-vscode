using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Fabric Data Agent engine slice (docs/data-agent-tab-plan.md) — OFFLINE only (no live calls). Pins: the part
    /// codec round-trips (build → decode → typed parse), the publish assembly is exact (published/* mirror +
    /// publish_info), the model-scope generator emits the semantic_model element tree with descriptions + hidden→
    /// unselected + false-safe placeholder ids, the 15k instruction cap refuses before anything is sent, the
    /// dry-run write paths return WITHOUT touching HttpClient/token, and the [verify-at-build] type filter matches.
    /// </summary>
    public sealed class DataAgentTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // ---- part codec ----

        [Fact]
        public void Part_codec_round_trips_through_build_decode_parse()
        {
            var parts = new (string, string)[]
            {
                (DataAgentRest.DataAgentJsonPath, "{\"$schema\":\"2.1.0\"}"),
                (DataAgentRest.DraftStageConfigPath, "{\"$schema\":\"1.0.0\",\"aiInstructions\":\"Be helpful\"}"),
                ("Files/Config/draft/semantic_model-Sales/datasource.json", "{\"type\":\"semantic_model\",\"displayName\":\"Sales\"}"),
                ("Files/Config/draft/semantic_model-Sales/fewshots.json", "{\"fewShots\":[{\"id\":\"1\",\"question\":\"q\",\"query\":\"EVALUATE\"}]}"),
            };
            var def = DataAgentRest.BuildDefinitionJson(parts);
            var decoded = FabricRest.DecodeDefinitionParts(def);   // base64 → text, exactly as a live getDefinition would arrive
            var parsed = DataAgentRest.ParseDataAgentParts(decoded);

            Assert.Equal("{\"$schema\":\"2.1.0\"}", parsed.DataAgentJson);
            Assert.Contains("aiInstructions", parsed.DraftStageConfig);
            var ds = Assert.Single(parsed.DraftDatasources);
            Assert.Equal("semantic_model-Sales", ds.Folder);
            Assert.Contains("semantic_model", ds.DatasourceJson);
            Assert.Contains("fewShots", ds.FewshotsJson);
        }

        [Fact]
        public void Decoded_data_source_retains_the_complete_json_for_lossless_schema_edits()
        {
            const string raw = "{\"type\":\"semantic_model\",\"displayName\":\"Sales\",\"serviceMetadata\":{\"futureFlag\":true},\"elements\":[{\"display_name\":\"Sales\",\"is_selected\":true}]}";
            var source = LocalEngine.BuildDataSource(new DataAgentRest.DataAgentDatasourceFolder
            {
                Folder = "semantic_model-Sales",
                DatasourceJson = raw,
            });

            Assert.Equal(raw, source.DatasourceJson);
            Assert.Contains("futureFlag", source.DatasourceJson);
            Assert.Contains("display_name", source.ElementsJson);
            Assert.Equal("semantic_model", source.Type);
            Assert.Equal("Sales", source.DisplayName);
        }

        [Fact]
        public void Publish_assembly_mirrors_draft_and_writes_publish_info()
        {
            var draft = new[]
            {
                new FabricRest.ReportPart { Path = DataAgentRest.DataAgentJsonPath, Content = "{\"$schema\":\"2.1.0\"}" },
                new FabricRest.ReportPart { Path = DataAgentRest.DraftStageConfigPath, Content = "{\"aiInstructions\":\"x\"}" },
                new FabricRest.ReportPart { Path = "Files/Config/draft/semantic_model-Sales/datasource.json", Content = "{\"type\":\"semantic_model\"}" },
            };
            var built = DataAgentRest.BuildPublishParts(draft, "v1 release");
            var map = built.ToDictionary(x => x.path, x => x.jsonText, StringComparer.OrdinalIgnoreCase);

            // every draft/* part is copied to published/*
            Assert.True(map.ContainsKey("Files/Config/published/stage_config.json"));
            Assert.True(map.ContainsKey("Files/Config/published/semantic_model-Sales/datasource.json"));
            // publish_info written with the description
            Assert.True(map.ContainsKey(DataAgentRest.PublishInfoPath));
            Assert.Contains("v1 release", map[DataAgentRest.PublishInfoPath]);
            // draft + non-draft parts preserved (never dropped)
            Assert.True(map.ContainsKey(DataAgentRest.DraftStageConfigPath));
            Assert.True(map.ContainsKey(DataAgentRest.DataAgentJsonPath));
            // data_agent.json is NOT under draft/, so it must not have been mirrored under published/
            Assert.False(map.ContainsKey("Files/Config/published/data_agent.json"));
        }

        [Theory]
        [InlineData("DataAgent", true)]
        [InlineData("dataAgent", true)]
        [InlineData("DATAAGENT", true)]
        [InlineData("SemanticModel", false)]
        [InlineData(null, false)]
        public void IsDataAgentType_matches_case_insensitively(string type, bool expected)
            => Assert.Equal(expected, DataAgentRest.IsDataAgentType(type));

        // ---- generate from the open model ----

        [Fact]
        public async Task Generate_from_model_builds_the_semantic_model_tree_with_descriptions_and_exclusions()
        {
            using var e = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var open = await e.OpenAsync(TestModels.FindBim());

            // Pick a table that actually has a plain column (skip any calc-group / column-less table).
            var tables = await e.ListTreeAsync(null);
            TreeNode table = null, col = null;
            foreach (var t in tables.Where(t => t.Kind == "table"))
            {
                var c = (await e.ListTreeAsync(t.Ref)).FirstOrDefault(x => x.Kind == "column");
                if (c != null) { table = t; col = c; break; }
            }
            Assert.NotNull(col);
            await e.SetDescriptionAsync(table.Ref, "AI scope test description", "human");
            await e.SetColumnMetadataAsync(col.Ref, true, null, null, null, "human");   // hide it → is_selected must be false

            var cfg = await e.GenerateDataAgentConfigFromModelAsync(200);

            Assert.Equal(open.ModelName, cfg.DisplayName);
            Assert.Contains("semantic_model.table", cfg.DatasourceJson);
            Assert.Contains("semantic_model.column", cfg.DatasourceJson);
            Assert.Contains("semantic_model.measure", cfg.DatasourceJson);   // AdventureWorks has measures
            Assert.Contains("AI scope test description", cfg.DatasourceJson);  // model descriptions carried
            Assert.Contains("placeholder", cfg.Note.ToLowerInvariant());

            using var doc = JsonDocument.Parse(cfg.DatasourceJson);
            Assert.Equal("<workspace-id>", doc.RootElement.GetProperty("workspaceId").GetString());  // false-safe placeholder id, never a guessed GUID
            Assert.Equal("<dataset-id>", doc.RootElement.GetProperty("artifactId").GetString());

            // the hidden column is present but unselected — scoped to the exact table we modified
            var tableEl = doc.RootElement.GetProperty("elements").EnumerateArray()
                .Single(t => t.GetProperty("display_name").GetString() == table.Name);
            var colEl = tableEl.GetProperty("children").EnumerateArray()
                .Single(k => k.GetProperty("type").GetString() == "semantic_model.column" && k.GetProperty("display_name").GetString() == col.Name);
            Assert.False(colEl.GetProperty("is_selected").GetBoolean());
        }

        [Fact]
        public async Task Generate_is_free_now_and_still_needs_an_open_session()
        {
            // The advanced half of Published, the Data agent included, went free on 22 September 2026.
            using var free = new LocalEngine(new SessionManager(), new Fake(pro: false));
            await free.OpenAsync(TestModels.FindBim());
            Assert.NotNull(await free.GenerateDataAgentConfigFromModelAsync(200));

            using var noModel = new LocalEngine(new SessionManager(), new Fake(pro: false));
            await Assert.ThrowsAsync<InvalidOperationException>(() => noModel.GenerateDataAgentConfigFromModelAsync(200));
        }

        // ---- write guards ----

        // The 2026-09-15 line made the WHOLE Data agent tab Pro. Kane replaced that line on 2026-09-22 ("free
        // is the tool, Pro is the tool that works for you") and the advanced half of Published came with it, so
        // every one of these is free. The DRY RUN still changes nothing and still sends nothing, which is what
        // these tests are really about.
        [Fact]
        public async Task All_data_agent_writes_are_free_and_a_dry_run_still_sends_nothing()
        {
            using var free = new LocalEngine(new SessionManager(), new Fake(pro: false));
            // Empty ids keep this off the network: each call is refused by its own argument checking, not by a
            // tier gate and not by a token acquisition, so nothing is ever sent.
            foreach (var report in new[]
            {
                await free.CreateDataAgentAsync("", "Agent", null, commit: false, "azcli", null, "human"),
                await free.UpdateDataAgentAsync("", "agent-1", "x", null, null, null, commit: false, "azcli", null, "human"),
                await free.PublishDataAgentAsync("", "agent-1", null, commit: false, "azcli", null, "human"),
                await free.DeleteDataAgentAsync("", "agent-1", commit: false, "azcli", null, "human"),
            })
                Assert.NotEqual("ok", report.Status);   // nothing was sent, and nothing was committed
        }

        [Fact]
        public async Task Reads_are_free_and_really_run()
        {
            // The read really runs on a free session: the missing-id refusal proves it, and empty ids keep the
            // test off the network (no token acquired, nothing live).
            using var free = new LocalEngine(new SessionManager(), new Fake(pro: false));
            Assert.Contains("workspaceId", (await free.ListDataAgentsAsync("", "azcli", null)).Error);
            Assert.Contains("agentId", (await free.GetDataAgentAsync("", "", "azcli", null)).Error);

            using var pro = new LocalEngine(new SessionManager(), new Fake(pro: true));
            Assert.Contains("workspaceId", (await pro.ListDataAgentsAsync("", "azcli", null)).Error);
            Assert.Contains("agentId", (await pro.GetDataAgentAsync("", "", "azcli", null)).Error);
        }

        [Fact]
        public async Task Instruction_cap_is_enforced_before_anything_is_sent()
        {
            using var e = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var big = new string('x', 15001);
            var create = await e.CreateDataAgentAsync("ws", "Agent", big, commit: true, "azcli", null, "human");
            Assert.Equal("error", create.Status);
            Assert.Contains("15000", create.Message);

            var update = await e.UpdateDataAgentAsync("ws", "agent-1", big, null, null, null, commit: true, "azcli", null, "human");
            Assert.Equal("error", update.Status);
            Assert.Contains("15000", update.Message);
        }

        [Fact]
        public async Task Dry_run_writes_are_pure_and_offline_safe()
        {
            using var e = new LocalEngine(new SessionManager(), new Fake(pro: true));

            var create = await e.CreateDataAgentAsync("ws", "Agent", "hello", commit: false, "azcli", null, "human");
            Assert.Equal("dry-run", create.Status);
            Assert.Contains("create_data_agent", create.RequestSummary);

            var update = await e.UpdateDataAgentAsync("ws", "agent-1", "hello", "semantic_model-Sales", "{\"type\":\"semantic_model\"}", null, commit: false, "azcli", null, "human");
            Assert.Equal("dry-run", update.Status);
            Assert.Contains("datasource.json", update.RequestSummary);

            var publish = await e.PublishDataAgentAsync("ws", "agent-1", "v1", commit: false, "azcli", null, "human");
            Assert.Equal("dry-run", publish.Status);
            Assert.Contains("publish_data_agent", publish.RequestSummary);

            var delete = await e.DeleteDataAgentAsync("ws", "agent-1", commit: false, "azcli", null, "human");
            Assert.Equal("dry-run", delete.Status);
            Assert.Contains("delete_data_agent", delete.RequestSummary);
        }

        [Fact]
        public async Task Schema_updates_reject_malformed_missing_target_and_no_op_before_network_access()
        {
            using var e = new LocalEngine(new SessionManager(), new Fake(pro: true));

            var malformed = await e.UpdateDataAgentAsync("ws", "agent-1", null, "semantic_model-Sales", "{bad", null, commit: false, "azcli", null, "human");
            Assert.Equal("error", malformed.Status);
            Assert.Contains("not valid JSON", malformed.Message);

            var wrongRoot = await e.UpdateDataAgentAsync("ws", "agent-1", null, "semantic_model-Sales", "[]", null, commit: false, "azcli", null, "human");
            Assert.Equal("error", wrongRoot.Status);
            Assert.Contains("JSON object", wrongRoot.Message);

            var missingTarget = await e.UpdateDataAgentAsync("ws", "agent-1", null, null, "{}", null, commit: false, "azcli", null, "human");
            Assert.Equal("error", missingTarget.Status);
            Assert.Contains("Choose a data source", missingTarget.Message);

            var noOp = await e.UpdateDataAgentAsync("ws", "agent-1", null, null, null, null, commit: false, "azcli", null, "human");
            Assert.Equal("error", noOp.Status);
            Assert.Contains("Nothing changed", noOp.Message);
        }

        // ---- Sol6 round 3 (D2): a Teach save names the text it started from, and the write compares it with the part as Fabric holds it now ----

        private static System.Collections.Generic.Dictionary<string, string> Parts(string instructions, string fewshotsJson = null)
        {
            var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (instructions != null) map[DataAgentRest.DraftStageConfigPath] = "{\"$schema\":\"1.0.0\",\"aiInstructions\":" + JsonSerializer.Serialize(instructions) + "}";
            if (fewshotsJson != null) map[DataAgentRest.DraftPrefix + "semantic_model-Sales/fewshots.json"] = fewshotsJson;
            return map;
        }

        [Fact]
        public void A_Teach_save_over_instructions_that_moved_is_refused_with_the_words_the_page_looks_for()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalEngine.GuardDataAgentExpected("instructions", "Use Sales Amount.", Parts("Use Net Sales."), null, "Use Sales Amount. Fiscal year starts in July.", null));
            Assert.Contains("changed in the model after you opened it", ex.Message);
            Assert.Contains("Nothing was written", ex.Message);
        }

        [Fact]
        public void A_Teach_save_passes_when_the_instructions_are_where_it_started_or_already_say_what_it_writes_or_it_names_nothing()
        {
            LocalEngine.GuardDataAgentExpected("instructions", "Use Sales Amount.", Parts("Use Sales Amount."), null, "Mine", null);
            LocalEngine.GuardDataAgentExpected("instructions", "Old", Parts("Mine"), null, "Mine", null);
            LocalEngine.GuardDataAgentExpected(null, null, Parts("Use Net Sales."), null, "Mine", null);   // the MCP door names nothing: unfenced
            LocalEngine.GuardDataAgentExpected("instructions", "", Parts(null), null, "Mine", null);       // an agent with no stage config reads as empty
            Assert.Throws<InvalidOperationException>(() => LocalEngine.GuardDataAgentExpected("instructions", "Old", Parts(null), null, "Mine", null));
        }

        [Fact]
        public void A_Teach_save_of_examples_compares_the_rows_not_the_file_text()
        {
            const string started = "{\"fewShots\":[{\"id\":\"1\",\"question\":\"q\",\"query\":\"EVALUATE 1\"}]}";
            const string sameRowsOtherLayout = "{ \"fewShots\" : [ { \"query\":\"EVALUATE 1\", \"id\":\"1\", \"question\":\"q\", \"extra\": true } ] }";
            const string moved = "{\"fewShots\":[{\"id\":\"1\",\"question\":\"q\",\"query\":\"EVALUATE 2\"}]}";
            LocalEngine.GuardDataAgentExpected("fewshots", started, Parts(null, sameRowsOtherLayout), "semantic_model-Sales", null, "{\"fewShots\":[]}");
            var ex = Assert.Throws<InvalidOperationException>(() => LocalEngine.GuardDataAgentExpected("fewshots", started, Parts(null, moved), "semantic_model-Sales", null, "{\"fewShots\":[]}"));
            Assert.Contains("changed in the model after you opened it", ex.Message);
        }

        // Join 7 (K4, the sol6-ui round 3 note e): the schema's field ticks were the last Teach-side save that sent the loaded document whole over whatever Fabric holds.
        private const string SchemaStarted = "{\"elements\":[{\"display_name\":\"Sales\",\"is_selected\":true,\"children\":[{\"display_name\":\"Amount\",\"is_selected\":true}]}]}";
        private static System.Collections.Generic.Dictionary<string, string> SchemaParts(string datasourceJson)
        {
            var map = Parts(null);
            if (datasourceJson != null) map[DataAgentRest.DraftPrefix + "semantic_model-Sales/datasource.json"] = datasourceJson;
            return map;
        }

        [Fact]
        public void A_schema_save_over_a_schema_that_moved_is_refused_with_the_words_the_page_looks_for()
        {
            const string moved = "{\"elements\":[{\"display_name\":\"Sales\",\"is_selected\":true,\"children\":[{\"display_name\":\"Amount\",\"is_selected\":false}]}]}";
            const string mine = "{\"elements\":[{\"display_name\":\"Sales\",\"is_selected\":false,\"children\":[{\"display_name\":\"Amount\",\"is_selected\":false}]}]}";
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LocalEngine.GuardDataAgentExpected("schema", SchemaStarted, SchemaParts(moved), "semantic_model-Sales", null, null, mine));
            Assert.Contains("changed in the model after you opened it", ex.Message);
            Assert.Contains("Nothing was written", ex.Message);
        }

        [Fact]
        public void A_schema_save_compares_the_document_not_its_layout_and_passes_where_it_started_or_already_says_what_it_writes()
        {
            const string otherLayout = "{ \"elements\" : [ { \"children\" : [ { \"is_selected\":true, \"display_name\":\"Amount\" } ], \"is_selected\":true, \"display_name\":\"Sales\" } ] }";
            const string mine = "{\"elements\":[{\"display_name\":\"Sales\",\"is_selected\":false}]}";
            LocalEngine.GuardDataAgentExpected("schema", SchemaStarted, SchemaParts(otherLayout), "semantic_model-Sales", null, null, mine);   // the same document, laid out another way
            LocalEngine.GuardDataAgentExpected("schema", "{}", SchemaParts(mine), "semantic_model-Sales", null, null, mine);                       // it already says what is being written
            LocalEngine.GuardDataAgentExpected(null, null, SchemaParts("{\"elements\":[]}"), "semantic_model-Sales", null, null, mine);           // the MCP door names nothing: unfenced
            Assert.Throws<InvalidOperationException>(() => LocalEngine.GuardDataAgentExpected("schema", SchemaStarted, SchemaParts(null), "semantic_model-Sales", null, null, mine));   // the source is gone
        }

        [Fact]
        public async Task A_schema_starting_point_needs_the_schema_to_be_what_is_written()
        {
            using var e = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var none = await e.UpdateDataAgentAsync("ws", "agent-1", "hello", null, null, null, commit: true, "azcli", null, "human", "schema", SchemaStarted);
            Assert.Equal("error", none.Status);
            Assert.Contains("Nothing was sent", none.Message);
        }

        [Fact]
        public async Task A_starting_point_that_matches_no_part_being_written_is_refused_before_anything_is_sent()
        {
            using var e = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var wrongPart = await e.UpdateDataAgentAsync("ws", "agent-1", "hello", null, null, null, commit: true, "azcli", null, "human", "fewshots", "{}");
            Assert.Equal("error", wrongPart.Status);
            Assert.Contains("Nothing was sent", wrongPart.Message);
            var noPart = await e.UpdateDataAgentAsync("ws", "agent-1", null, "semantic_model-Sales", "{}", null, commit: true, "azcli", null, "human", "instructions", "old");
            Assert.Equal("error", noPart.Status);
        }

        [Fact]
        public async Task Writes_validate_required_ids()
        {
            using var e = new LocalEngine(new SessionManager(), new Fake(pro: true));
            Assert.Equal("error", (await e.CreateDataAgentAsync("", "Agent", null, false, "azcli", null, "human")).Status);
            Assert.Equal("error", (await e.CreateDataAgentAsync("ws", "", null, false, "azcli", null, "human")).Status);
            Assert.Equal("error", (await e.UpdateDataAgentAsync("ws", "", null, null, null, null, false, "azcli", null, "human")).Status);
            Assert.Equal("error", (await e.DeleteDataAgentAsync("", "a", false, "azcli", null, "human")).Status);
        }
    }
}
