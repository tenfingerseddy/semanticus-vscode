using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// UAT C9.1: documentation snapshot and spec save. D-109 (perspectives ride get_doc_model for both
    /// doors) and D-111 (a typed name that already ends in .json does not gain a second .json).
    /// </summary>
    public sealed class DocumentationExportTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // Pro: Docs (get_doc_model) and perspectives are both Pro features since 2026-09-15, reads included.
        private static async Task<LocalEngine> OpenAwAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(true));
            await engine.OpenAsync(TestModels.FindBim());
            return engine;
        }

        [Fact]
        public void CollapseDuplicateJsonExtension_strips_one_extra_json()
        {
            Assert.Equal("LaneEDoc-spec.json", LocalEngine.CollapseDuplicateJsonExtension("LaneEDoc-spec.json.json"));
            Assert.Equal("LaneEDoc-spec.json", LocalEngine.CollapseDuplicateJsonExtension("LaneEDoc-spec.json"));
            Assert.Equal("model.spec.json", LocalEngine.CollapseDuplicateJsonExtension("model.spec.json"));
        }

        [Fact]
        public async Task SaveSpec_does_not_write_a_double_json_extension()
        {
            var root = Path.Combine(Path.GetTempPath(), "semanticus-d111-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var doubled = Path.Combine(root, "LaneEDoc-spec.json.json");
            var wanted = Path.Combine(root, "LaneEDoc-spec.json");
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            try
            {
                await engine.SetSpecAsync("{\"name\":\"LaneEDoc\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}", "human");
                await engine.SaveSpecAsync(doubled);
                Assert.True(File.Exists(wanted), "the spec must land at the typed .json name");
                Assert.False(File.Exists(doubled), "a second .json must not be appended");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
            }
        }

        [Fact]
        public async Task Mcp_save_spec_does_not_write_a_double_json_extension()
        {
            var root = Path.Combine(Path.GetTempPath(), "semanticus-d111-mcp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var doubled = Path.Combine(root, "LaneEDoc-spec.json.json");
            var wanted = Path.Combine(root, "LaneEDoc-spec.json");
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            try
            {
                await engine.SetSpecAsync("{\"name\":\"LaneEDoc\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}", "human");
                await McpTools.SaveSpec(engine, doubled);
                Assert.True(File.Exists(wanted), "the MCP door must land at the typed .json name");
                Assert.False(File.Exists(doubled), "the MCP door must not append a second .json");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
            }
        }

        [Fact]
        public async Task GetDocModel_includes_a_created_perspective()
        {
            using var engine = await OpenAwAsync();
            await engine.CreatePerspectiveAsync("Sales handover", "human");
            var dto = await engine.GetDocModelAsync(50);
            Assert.NotNull(dto.Perspectives);
            Assert.Contains(dto.Perspectives, p => p.Name == "Sales handover");
        }

        [Fact]
        public async Task Mcp_get_doc_model_includes_a_created_perspective()
        {
            using var engine = await OpenAwAsync();
            await engine.CreatePerspectiveAsync("Sales handover", "agent");
            var dto = await McpTools.GetDocModel(engine, 50);
            Assert.NotNull(dto.Perspectives);
            Assert.Contains(dto.Perspectives, p => p.Name == "Sales handover");
        }
    }
}
