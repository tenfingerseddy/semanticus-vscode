using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The §6 orientation primer (docs/harness-engineering.md): get_model_summary is the blessed session-start
    /// call, so its payload is a tax on every fresh/compacted session. The budget test is the DESIGN PRESSURE
    /// the doc asks for — if a section grows the payload past ~2k tokens (8,000 chars), trim FIELDS. The other
    /// tests pin the contract: the map is always present, the sections are deterministic, and it never fails
    /// on the log or on a missing session.
    /// </summary>
    public sealed class OrientationTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free", Reason = pro ? null : "No license found." }; }
        }

        [Fact]
        public async Task Orientation_payload_stays_within_the_2k_token_budget()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(false));
            await engine.OpenAsync(TestModels.FindBim());   // AdventureWorks — a rich, real model

            var summary = await engine.GetOrientationAsync();
            var json = JsonSerializer.Serialize(summary);

            // ~2k tokens ≈ 8,000 chars. If this trips, TRIM FIELDS (counts/names only) — do not raise the cap.
            Assert.True(json.Length <= 8000, $"orientation payload is {json.Length} chars (> 8000). Trim fields — see harness-engineering.md §6.");
        }

        [Fact]
        public async Task Orientation_carries_the_map_and_deterministic_sections()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(false));
            await engine.OpenAsync(TestModels.FindBim());

            var s = await engine.GetOrientationAsync();

            Assert.NotNull(s.Overview);
            Assert.NotNull(s.Connection);
            Assert.False(s.Connection.Connected);        // file open, no live query engine
            Assert.NotNull(s.Entitlement);
            Assert.Equal("free", s.Entitlement.Tier);
            Assert.Equal("No license found.", s.Entitlement.Reason);
            Assert.NotNull(s.Readiness);                 // a session is open
            Assert.NotNull(s.Graph);
            Assert.NotNull(s.Primer);
            Assert.Contains("Primer", s.Primer.Markdown);
            Assert.NotNull(s.Note);
            Assert.Contains("get_model_primer", s.Note);
            Assert.Contains("ai_readiness_scan", s.Note);   // the doc-map names the drill-down ops
            Assert.Contains("get_model_graph", s.Note);
        }

        [Fact]
        public async Task Orientation_with_no_model_suggests_opening_one()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(false));

            var s = await engine.GetOrientationAsync();

            Assert.Null(s.Readiness);                    // no session ⇒ no readiness/graph
            Assert.Null(s.Graph);
            Assert.NotNull(s.SuggestedNextActions);
            Assert.Equal("open_model", s.SuggestedNextActions.Single().Op);   // the only actionable step
        }
    }
}
