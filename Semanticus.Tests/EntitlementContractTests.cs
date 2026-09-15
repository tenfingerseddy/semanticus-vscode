using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The get_entitlement response contract. Adding `features` changes what every door and every fixture
    /// asserts, so the exact JSON is pinned here: free carries an empty array, never null, and Pro carries all four
    /// wire ids. The grants come from ONE function, so the answer a page renders and the answer the gate enforces
    /// are the same answer.</summary>
    public sealed class EntitlementContractTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static readonly JsonSerializerOptions Wire = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private static async Task<string> JsonOf(IEntitlement e)
        {
            using var engine = new LocalEngine(new SessionManager(), e);
            return JsonSerializer.Serialize(await engine.GetEntitlementAsync(), Wire);
        }

        [Fact]
        public async Task Free_carries_an_empty_features_array()
        {
            var json = await JsonOf(new Fake(false));
            using var doc = JsonDocument.Parse(json);
            var features = doc.RootElement.GetProperty("features");
            Assert.Equal(JsonValueKind.Array, features.ValueKind);
            Assert.Empty(features.EnumerateArray());
            Assert.Equal("free", doc.RootElement.GetProperty("tier").GetString());
        }

        [Fact]
        public async Task Pro_carries_all_four_wire_ids()
        {
            var json = await JsonOf(new Fake(true));
            using var doc = JsonDocument.Parse(json);
            var features = doc.RootElement.GetProperty("features").EnumerateArray().Select(x => x.GetString()).ToArray();
            Assert.Equal(new[] { "modelCreate", "tests", "publishedAdvanced", "workflows" }, features);
            Assert.Equal("pro", doc.RootElement.GetProperty("tier").GetString());
        }

        /// <summary>Every field the response had before is still there, so nothing that read it breaks.</summary>
        [Fact]
        public async Task The_response_keeps_every_field_it_had()
        {
            var json = await JsonOf(new Fake(false));
            using var doc = JsonDocument.Parse(json);
            foreach (var field in new[] { "tier", "licensedTo", "expiry", "reason", "manageUrl", "features" })
                Assert.True(doc.RootElement.TryGetProperty(field, out _), "get_entitlement lost the field " + field);
        }

        /// <summary>A licence inside the 14-day grace window is still Pro and still carries all four, so a renewal
        /// that lags the expiry instant never drops a paying subscriber onto the gate.</summary>
        [Fact]
        public void A_licence_in_grace_carries_all_four()
        {
            var lapsed = LicenseEntitlement.Evaluate(null, null, devPro: true, DateTimeOffset.UtcNow);
            Assert.True(lapsed.IsPro);
            Assert.Equal(4, FeatureGrants.WireIds(lapsed).Length);
        }

        /// <summary>The dev escape carries all four, so building Semanticus is never self-gated.</summary>
        [Fact]
        public void Dev_Pro_carries_all_four()
        {
            Assert.Equal(new[] { "modelCreate", "tests", "publishedAdvanced", "workflows" },
                FeatureGrants.WireIds(LicenseEntitlement.DevPro()));
        }

        /// <summary>The token format already has an optional `features` claim and today's tokens omit it. An omitted
        /// claim keeps all four, and an unused optional claim must not quietly become a narrower product tier.</summary>
        [Fact]
        public void A_token_with_no_features_claim_keeps_all_four()
        {
            var claims = JsonSerializer.Deserialize<LicenseClaims>("{\"sub\":\"a@b.c\",\"tier\":\"pro\"}");
            Assert.Null(claims.Features);
            Assert.Equal(4, FeatureGrants.For(new Fake(true)).Count);
        }

        /// <summary>The gate and the response read the same function, so they cannot disagree about any feature.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_response_and_the_gate_agree_on_every_feature(bool pro)
        {
            var e = new Fake(pro);
            using var engine = new LocalEngine(new SessionManager(), e);
            var reported = (await engine.GetEntitlementAsync()).Features;
            foreach (var f in ProFeatures.All)
            {
                var granted = reported.Contains(ProFeatures.WireId(f));
                if (granted) continue;
                var ex = Assert.Throws<EntitlementException>(() => EntitlementGuard.RequirePro(e, f));
                Assert.Contains("is a Semanticus Pro feature.", ex.Message);
            }
            if (!pro) Assert.Empty(reported);
        }

        /// <summary>An engine with no entitlement at all, the state a reconnect passes through, resolves to free.</summary>
        [Fact]
        public void An_absent_entitlement_grants_nothing()
        {
            Assert.Empty(FeatureGrants.For((IEntitlement)null));
            Assert.Empty(FeatureGrants.WireIds(null));
        }

        /// <summary>The wire ids are the contract the page is written against. They do not drift.</summary>
        [Fact]
        public void The_wire_ids_are_fixed()
        {
            Assert.Equal("modelCreate", ProFeatures.WireId(ProFeature.ModelCreate));
            Assert.Equal("tests", ProFeatures.WireId(ProFeature.Tests));
            Assert.Equal("publishedAdvanced", ProFeatures.WireId(ProFeature.PublishedAdvanced));
            Assert.Equal("workflows", ProFeatures.WireId(ProFeature.Workflows));
            Assert.True(ProFeatures.TryParse("workflows", out var parsed));
            Assert.Equal(ProFeature.Workflows, parsed);
            Assert.False(ProFeatures.TryParse("somethingElse", out _));
        }

        /// <summary>One template, one phrase. pro.tsx:50 and two tests match on "Semanticus Pro feature" exactly, so
        /// the refusal reads the same for every feature and only the tab and the alternative change.</summary>
        [Fact]
        public void Every_feature_refuses_with_the_one_template()
        {
            foreach (var f in ProFeatures.All)
            {
                var ex = Assert.Throws<EntitlementException>(() => EntitlementGuard.RequirePro(new Fake(false), f));
                Assert.StartsWith(ProFeatures.DefaultTab(f) + " is a Semanticus Pro feature. ", ex.Message);
                Assert.Contains(ProFeatures.FreeAlternative(f), ex.Message);
                Assert.EndsWith("Unlock Pro from Pro Plans and Support.", ex.Message);
                Assert.DoesNotContain("—", ex.Message);
            }
        }

        /// <summary>The tab a person would open, passed through from the map.</summary>
        [Fact]
        public void A_named_tab_replaces_the_feature_name_in_the_sentence()
        {
            var ex = Assert.Throws<EntitlementException>(
                () => EntitlementGuard.RequirePro(new Fake(false), ProFeature.ModelCreate, "Power Query"));
            Assert.StartsWith("Power Query is a Semanticus Pro feature. ", ex.Message);
        }
    }
}
