using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The free surface the Overview page renders from. Raising the compatibility level is free and needs
    /// a free human control, but every carrier of the number today (get_spec, list_calendars, get_doc_model) is a
    /// Pro read under the feature line Kane set on 2026-09-15. So the session description carries it, on both
    /// doors, and the free tier really gets it.</summary>
    public sealed class FreeSessionContractTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static async Task<LocalEngine> FreeWithModelAsync()
        {
            var e = new LocalEngine(new SessionManager(), new Fake(false));
            await e.OpenAsync(TestModels.FindBim());
            return e;
        }

        [Fact]
        public async Task The_free_MCP_read_carries_the_compatibility_level()
        {
            using var engine = await FreeWithModelAsync();
            var info = await McpTools.ModelOverview(engine);   // model_overview, free
            // The fixture opens at 1200, so this pins "the model's real level", not a floor.
            Assert.Equal(1200, info.CompatibilityLevel);
        }

        [Fact]
        public async Task The_free_RPC_read_carries_the_compatibility_level()
        {
            using var engine = await FreeWithModelAsync();
            var info = await new EngineRpcTarget(engine).sessionInfo();   // sessionInfo, free
            Assert.Equal(1200, info.CompatibilityLevel);
        }

        /// <summary>Both doors read the same implementation, so they cannot report different levels.</summary>
        [Fact]
        public async Task Both_doors_report_the_same_level()
        {
            using var engine = await FreeWithModelAsync();
            var mcp = await McpTools.ModelOverview(engine);
            var rpc = await new EngineRpcTarget(engine).sessionInfo();
            Assert.Equal(mcp.CompatibilityLevel, rpc.CompatibilityLevel);
        }

        /// <summary>The free control has to be able to act, not only to read. set_compatibility_level stays free.</summary>
        [Fact]
        public async Task The_free_tier_may_raise_the_compatibility_level_and_sees_the_new_one()
        {
            using var engine = await FreeWithModelAsync();
            var before = (await McpTools.ModelOverview(engine)).CompatibilityLevel;
            await engine.SetCompatibilityLevelAsync(Math.Max(before, 1702), "human");
            var after = (await McpTools.ModelOverview(engine)).CompatibilityLevel;
            Assert.True(after >= before, "the level went backwards: " + before + " then " + after);
            Assert.True(after >= 1702, "set_compatibility_level is free and must have raised it; got " + after);
        }

        /// <summary>No model open is a plain zero, not a throw: Overview renders before a model opens.</summary>
        [Fact]
        public async Task With_no_model_open_the_level_is_zero()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(false));
            Assert.Equal(0, (await new EngineRpcTarget(engine).sessionInfo()).CompatibilityLevel);
        }

        /// <summary>The page lane's parity test carries a self-deleting exception for this name, so it is pinned.</summary>
        [Fact]
        public void The_usage_projection_is_reachable_by_its_agreed_RPC_name()
        {
            Assert.Contains(typeof(EngineRpcTarget).GetMethods(), m => m.Name == "listSqlSourceUsage");
        }
    }
}
