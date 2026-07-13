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
        public async Task Generate_is_pro_gated_and_needs_an_open_session()
        {
            using var free = new LocalEngine(new SessionManager(), new Fake(pro: false));
            await free.OpenAsync(TestModels.FindBim());
            await Assert.ThrowsAsync<EntitlementException>(() => free.GenerateDataAgentConfigFromModelAsync(200));

            using var proNoModel = new LocalEngine(new SessionManager(), new Fake(pro: true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => proNoModel.GenerateDataAgentConfigFromModelAsync(200));
        }

        // ---- write guards ----

        // The 2026-07-07 gate line (Kane): EVERY data-agent write is Pro — create/update/publish/delete, dry-run
        // included — while reads (list/get) stay free. The gate throws at the very top, so a free call never gets
        // a request summary for a write it can't finish, and nothing is ever sent.
        [Fact]
        public async Task All_data_agent_writes_are_pro_gated_dry_run_included()
        {
            using var free = new LocalEngine(new SessionManager(), new Fake(pro: false));
            await Assert.ThrowsAsync<EntitlementException>(() => free.CreateDataAgentAsync("ws", "Agent", null, commit: false, "azcli", null, "human"));
            await Assert.ThrowsAsync<EntitlementException>(() => free.UpdateDataAgentAsync("ws", "agent-1", "x", null, null, null, commit: false, "azcli", null, "human"));
            await Assert.ThrowsAsync<EntitlementException>(() => free.PublishDataAgentAsync("ws", "agent-1", null, commit: false, "azcli", null, "human"));
            await Assert.ThrowsAsync<EntitlementException>(() => free.DeleteDataAgentAsync("ws", "agent-1", commit: false, "azcli", null, "human"));
            // commit=true is refused by the same top-of-op gate (never reaches token acquisition).
            await Assert.ThrowsAsync<EntitlementException>(() => free.CreateDataAgentAsync("ws", "Agent", null, commit: true, "azcli", null, "human"));
        }

        [Fact]
        public async Task Reads_stay_free_no_gate_throw()
        {
            using var free = new LocalEngine(new SessionManager(), new Fake(pro: false));
            // Reachable on the free tier (no EntitlementException): the missing-id refusal proves the read ran —
            // asserted on empty ids so the test never acquires a token or goes live.
            Assert.Contains("workspaceId", (await free.ListDataAgentsAsync("", "azcli", null)).Error);
            Assert.Contains("agentId", (await free.GetDataAgentAsync("", "", "azcli", null)).Error);
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
