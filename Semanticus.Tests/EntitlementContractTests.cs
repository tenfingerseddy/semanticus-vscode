using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The get_entitlement response contract, pinned as exact JSON because both doors and the Studio are
    /// written against it: free carries an empty array, never null, and Pro carries exactly ["workflows"].
    ///
    /// It carried four wire ids until 22 September 2026. Kane set the line "free is the tool, Pro is the tool that
    /// works for you" that day, three of the four features went away with the tabs they gated, and the one paid
    /// thing that is NOT a tab (the ask box) deliberately has no wire id: this array exists for a page to lock a
    /// tab on, and a page that asked for a tab that does not exist would get a meaningless answer. The grants come
    /// from ONE function, so the answer a page renders and the answer the gate enforces are the same answer.</summary>
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
        public async Task Pro_carries_the_one_tab_feature_and_nothing_else()
        {
            var json = await JsonOf(new Fake(true));
            using var doc = JsonDocument.Parse(json);
            var features = doc.RootElement.GetProperty("features").EnumerateArray().Select(x => x.GetString()).ToArray();
            Assert.Equal(new[] { "workflows", "askData" }, features);
            Assert.Equal("pro", doc.RootElement.GetProperty("tier").GetString());
        }

        /// <summary>The ask box is paid and is deliberately NOT in that array, because it is not a tab. A page
        /// that wants to know keys on the tier, and the engine still refuses the operation either way.</summary>
        [Fact]
        public void The_ask_box_is_granted_by_the_tier_and_carries_no_wire_id()
        {
            Assert.Null(ProFeatures.WireId(ProFeature.Ask));
            Assert.DoesNotContain(ProFeature.Ask, ProFeatures.TabFeatures);
            Assert.Contains(ProFeature.Ask, ProFeatures.All);

            Assert.True(FeatureGrants.Grants(new Fake(true), ProFeature.Ask));
            Assert.False(FeatureGrants.Grants(new Fake(false), ProFeature.Ask));
            Assert.DoesNotContain("ask", FeatureGrants.WireIds(new Fake(true)));
        }

        /// <summary>Ask your data is a PAGE since Kane's call on 26 September (the page is built now, under Explore),
        /// so it carries a wire id the page can lock its tab on, and it parses back.</summary>
        [Fact]
        public void Ask_your_data_is_a_tab_feature_with_a_wire_id()
        {
            Assert.Equal("askData", ProFeatures.WireId(ProFeature.AskData));
            Assert.Contains(ProFeature.AskData, ProFeatures.TabFeatures);
            Assert.True(ProFeatures.TryParse("askData", out var parsed));
            Assert.Equal(ProFeature.AskData, parsed);
            Assert.DoesNotContain("askData", FeatureGrants.WireIds(new Fake(false)));
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

        /// <summary>A licence inside the 14-day grace window is still Pro and still carries everything, so a
        /// renewal that lags the expiry instant never drops a paying subscriber onto the gate.</summary>
        [Fact]
        public void A_licence_in_grace_carries_everything()
        {
            var lapsed = LicenseEntitlement.Evaluate(null, null, devPro: true, DateTimeOffset.UtcNow);
            Assert.True(lapsed.IsPro);
            Assert.Equal(ProFeatures.All.Length, FeatureGrants.For(lapsed).Count);
            Assert.Equal(new[] { "workflows", "askData" }, FeatureGrants.WireIds(lapsed));
        }

        /// <summary>The dev escape carries everything, so building Semanticus is never self-gated.</summary>
        [Fact]
        public void Dev_Pro_carries_everything()
        {
            var dev = LicenseEntitlement.DevPro();
            Assert.Equal(ProFeatures.All.Length, FeatureGrants.For(dev).Count);
            Assert.Equal(new[] { "workflows", "askData" }, FeatureGrants.WireIds(dev));
        }

        /// <summary>The token format already has an optional `features` claim and today's tokens omit it. An omitted
        /// claim keeps everything, and an unused optional claim must not quietly become a narrower product tier.</summary>
        [Fact]
        public void A_token_with_no_features_claim_keeps_everything()
        {
            var claims = JsonSerializer.Deserialize<LicenseClaims>("{\"sub\":\"a@b.c\",\"tier\":\"pro\"}");
            Assert.Null(claims.Features);
            Assert.Equal(ProFeatures.All.Length, FeatureGrants.For(new Fake(true)).Count);
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

            // A TAB feature is reported by wire id, and the gate must agree with what was reported.
            foreach (var f in ProFeatures.TabFeatures)
            {
                var granted = reported.Contains(ProFeatures.WireId(f));
                Assert.Equal(pro, granted);
                if (granted) continue;
                var ex = Assert.Throws<EntitlementException>(() => EntitlementGuard.RequirePro(e, f));
                Assert.Contains("is a Semanticus Pro feature.", ex.Message);
            }

            // A feature that is not a tab is reported by nothing, so the gate is the whole answer for it.
            foreach (var f in ProFeatures.All.Except(ProFeatures.TabFeatures))
            {
                Assert.Null(ProFeatures.WireId(f));
                if (pro) { EntitlementGuard.RequirePro(e, f); continue; }
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

        /// <summary>The wire ids are the contract the page is written against. They do not drift. The three that
        /// were retired on 22 September must not parse back either: a page still asking for modelCreate is asking
        /// about a feature that no longer exists, and the honest answer to that is no.</summary>
        [Fact]
        public void The_wire_ids_are_fixed()
        {
            Assert.Equal("workflows", ProFeatures.WireId(ProFeature.Workflows));
            Assert.True(ProFeatures.TryParse("workflows", out var parsed));
            Assert.Equal(ProFeature.Workflows, parsed);
            Assert.False(ProFeatures.TryParse("somethingElse", out _));
            foreach (var retired in new[] { "modelCreate", "tests", "publishedAdvanced" })
                Assert.False(ProFeatures.TryParse(retired, out _), retired + " still parses to a feature.");
            Assert.False(ProFeatures.TryParse(null, out _));
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
                () => EntitlementGuard.RequirePro(new Fake(false), ProFeature.Workflows, "Workflow library"));
            Assert.StartsWith("Workflow library is a Semanticus Pro feature. ", ex.Message);
        }
    }
}
