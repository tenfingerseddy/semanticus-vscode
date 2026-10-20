// pick_check_visuals (1.6): Jev's visual pick for every check on the Validation page, the checks.visual_kind decision point, OFF by default.
//
// The null path is the tested path: no test here reaches a real service. A scripted oracle answers every "visual:<check id>" question
// the way a recorded replay would, and records every request so a test can prove what was (and was not) sent, and that it was ONE call.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class CheckVisualPickTests
    {
        private sealed class ScriptedOracle : IDecisionOracle
        {
            private readonly Func<DecisionQuestion, (string Choice, double P)> _script;
            public readonly List<DecisionRequest> Seen = new List<DecisionRequest>();
            public ScriptedOracle(Func<DecisionQuestion, (string, double)> script) { _script = script; }
            public bool Available => true;

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (Seen) Seen.Add(request);
                var sb = new StringBuilder();
                var first = true;
                foreach (var q in request.Questions)
                {
                    var (choice, p) = _script(q);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                      .Append(",\"confidence\":").Append(p.ToString(CultureInfo.InvariantCulture)).Append('}');
                }
                var json = "{\"entries\":[{\"digest\":" + JsonSerializer.Serialize(request.Digest)
                    + ",\"point\":" + JsonSerializer.Serialize(request.PointName)
                    + ",\"usage\":{\"input_tokens\":300,\"output_tokens\":10},\"answers\":{" + sb + "}}]}";
                return new RecordedDecisionOracle(json, "jev-test-1").AskAsync(request, ct);
            }
        }

        private sealed class Rig : IDisposable
        {
            public DecisionFixture F;
            public ScriptedOracle Jev;
            public LocalEngine Engine => F.Engine;
            public void Dispose() => F.Dispose();
        }

        private static async Task<Rig> BuildAsync(IEntitlement tier = null, bool? barMet = null, Func<DecisionQuestion, (string, double)> script = null)
        {
            var rig = new Rig { F = new DecisionFixture(tier ?? TestEntitlements.Pro) };
            await rig.F.BuildModelAsync();
            await rig.F.ConsentAsync();
            rig.Engine.JevPicksCheckVisualsForTest = barMet;
            rig.Jev = new ScriptedOracle(script ?? (_ => ("bars", 0.95)));
            rig.Engine.UseDecisionsForTest(new DecisionService(rig.Jev, new DecisionBudget()));
            return rig;
        }

        private static CheckVisualItem Item(string id, string title, string measure, string status = "differs", string[] fits = null, string rules = "variance") => new CheckVisualItem
        {
            Id = id, Title = title, Measure = measure, KindWord = "against SQL", Status = status, Rows = 12, HasPrior = true, Runs = 8, HasSplit = true,
            Fits = fits ?? new[] { "variance", "bars", "table" }, RulesPick = rules,
        };

        private static CheckVisualAsk Eight()
        {
            var checks = new List<CheckVisualItem>();
            for (var i = 1; i <= 8; i++) checks.Add(Item("c" + i, "Check number " + i, "Measure " + i));
            return new CheckVisualAsk { Checks = checks.ToArray() };
        }

        [Fact]
        public void The_point_is_registered_pro_only_with_its_bar_and_off_in_this_build()
        {
            var p = DecisionRegistry.Require(DecisionRegistry.CheckVisualKind);
            Assert.True(p.ProOnly);
            Assert.Equal(0.85, p.ConfidenceBar);
            Assert.Equal("pick_check_visuals", p.Caller);
            Assert.Equal(new[] { "check_words", "check_shapes" }, p.SnapshotFields.ToArray());
            var q = Assert.Single(p.Questions);
            Assert.Equal("check_visual", q.Name);
            Assert.Contains("rules", q.FixedOptions);
            Assert.Equal(JevCheckVisuals.Kinds.Concat(new[] { "rules" }), q.FixedOptions);
            Assert.False(JevCheckVisuals.JevPicks);
            Assert.DoesNotContain("—", p.Purpose, StringComparison.Ordinal);
            Assert.Contains("never sends a value or a row", p.Purpose, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_reason_is_one_plain_sentence_and_does_not_open_with_the_visual()
        {
            foreach (var kind in JevCheckVisuals.Kinds)
            {
                var reason = JevCheckVisuals.ReasonFor(kind);
                Assert.True(char.IsUpper(reason[0]) && reason.EndsWith('.'), $"the reason for {kind} is not one plain sentence: {reason}");
                Assert.DoesNotContain("—", reason, StringComparison.Ordinal);
                Assert.False(string.Equals(reason.Split(' ', ':')[0], kind, StringComparison.OrdinalIgnoreCase), $"the reason for {kind} opens with the kind: {reason}");
            }
        }

        [Fact]
        public async Task Off_by_default_it_returns_the_rules_pick_for_every_check_and_asks_nothing()
        {
            using var rig = await BuildAsync();
            var picks = await rig.Engine.PickCheckVisualsAsync(Eight());
            Assert.Equal(8, picks.Picks.Length);
            Assert.All(picks.Picks, p => { Assert.Equal(CheckVisualPick.ByRules, p.By); Assert.Equal("variance", p.Kind); Assert.Equal(JevCheckVisuals.ReasonFor("variance"), p.Reason); });
            Assert.Equal("Jev's visual pick is off until it clears its bar.", picks.Note);
            Assert.Empty(rig.Jev.Seen);
            Assert.Equal(0, rig.Engine.Decisions.RequestsStarted);

            var listed = (await rig.Engine.GetDecisionPointsAsync()).Single(x => x.Name == DecisionRegistry.CheckVisualKind);
            Assert.False(listed.Live);
        }

        [Fact]
        public async Task When_live_all_checks_are_asked_in_one_call_with_named_questions()
        {
            using var rig = await BuildAsync(barMet: true, script: q => (q.Name == "visual:c2" ? "table" : "bars", 0.93));
            var ask = Eight();
            ask.Checks[0].Title = "Total Sales and the GL"; ask.Checks[0].Measure = "Total Sales";
            var picks = await rig.Engine.PickCheckVisualsAsync(ask);

            var request = Assert.Single(rig.Jev.Seen);   // ONE call for eight checks
            Assert.Equal(DecisionRegistry.CheckVisualKind, request.PointName);
            Assert.Equal(8, request.Questions.Count);
            Assert.Equal(Enumerable.Range(1, 8).Select(i => "visual:c" + i), request.Questions.Select(q => q.Name));
            var first = request.Questions[0];
            Assert.Contains("Total Sales and the GL", first.Instructions);
            Assert.Contains("Total Sales", first.Instructions);
            Assert.Equal("rules", first.Options.Last().Id);
            Assert.Equal(new[] { "bars", "table", "variance" }, first.Options.Take(3).Select(o => o.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());

            Assert.Equal(8, picks.Picks.Length);
            var c2 = picks.Picks.Single(p => p.Id == "c2");
            Assert.Equal(("table", CheckVisualPick.ByJev), (c2.Kind, c2.By));
            Assert.Equal(JevCheckVisuals.ReasonFor("table"), c2.Reason);
            Assert.Equal(0.93, c2.Confidence);
            Assert.Null(c2.Note);
            Assert.Equal(("bars", CheckVisualPick.ByJev), (picks.Picks[0].Kind, picks.Picks[0].By));
        }

        [Fact]
        public async Task A_pick_outside_a_checks_fits_is_refused_and_falls_back_to_the_rules()
        {
            using var rig = await BuildAsync(barMet: true, script: _ => ("line", 0.99));
            var picks = await rig.Engine.PickCheckVisualsAsync(Eight());
            Assert.All(picks.Picks, p => { Assert.Equal(CheckVisualPick.ByRules, p.By); Assert.Equal("variance", p.Kind); });
            Assert.Single(rig.Jev.Seen);

            // And the reader alone refuses it too, whatever reached it: an answer to a wider offer than this check's fits.
            var point = DecisionRegistry.Require(DecisionRegistry.CheckVisualKind);
            var wide = new[] { Item("w", "Wide", "Total Sales", fits: new[] { "variance", "bars", "line" }) };
            var snapshot = JevCheckVisuals.Snapshot(wide);
            var request = new DecisionRequest(point.Name, point.SchemaVersion, snapshot, JevCheckVisuals.Questions(snapshot, wide));
            var answers = await new ScriptedOracle(_ => ("line", 0.99)).AskAsync(request, CancellationToken.None);
            Assert.True(answers.Decided);
            var narrow = new[] { Item("w", "Wide", "Total Sales") };
            var read = JevCheckVisuals.Read(answers, point.ConfidenceBar, narrow, new HashSet<string> { "w" });
            Assert.Equal(CheckVisualPick.ByRules, read.Picks[0].By);
            Assert.Equal("variance", read.Picks[0].Kind);
            Assert.Contains("ruled out", read.Picks[0].Note);
        }

        [Fact]
        public async Task A_low_confidence_answer_and_the_neutral_option_keep_the_rules_pick_per_check()
        {
            using var rig = await BuildAsync(barMet: true, script: q => q.Name == "visual:c1" ? ("bars", 0.6) : q.Name == "visual:c2" ? ("rules", 0.97) : ("bars", 0.95));
            var picks = await rig.Engine.PickCheckVisualsAsync(Eight());
            var c1 = picks.Picks.Single(p => p.Id == "c1");
            Assert.Equal(("variance", CheckVisualPick.ByRules), (c1.Kind, c1.By));
            Assert.Contains("not sure enough", c1.Note);
            var c2 = picks.Picks.Single(p => p.Id == "c2");
            Assert.Equal(("variance", CheckVisualPick.ByRules), (c2.Kind, c2.By));
            Assert.Equal("Jev said the rules' pick is fine.", c2.Note);
            Assert.Equal(CheckVisualPick.ByJev, picks.Picks.Single(p => p.Id == "c3").By);
        }

        [Fact]
        public async Task A_check_with_one_fit_or_none_is_never_asked_and_keeps_the_rules_pick()
        {
            using var rig = await BuildAsync(barMet: true);
            var ask = new CheckVisualAsk
            {
                Checks = new[]
                {
                    Item("a", "Alpha", "Total Sales"),
                    Item("off", "Beta", "Net Sales", status: "off", fits: new[] { "table" }, rules: "table"),
                    Item("none", "Gamma", "Returns", status: "off", fits: Array.Empty<string>(), rules: null),
                },
            };
            var picks = await rig.Engine.PickCheckVisualsAsync(ask);
            var request = Assert.Single(rig.Jev.Seen);
            Assert.Equal(new[] { "visual:a" }, request.Questions.Select(q => q.Name).ToArray());
            Assert.Equal(CheckVisualPick.ByJev, picks.Picks.Single(p => p.Id == "a").By);
            Assert.Equal(("table", CheckVisualPick.ByRules), (picks.Picks.Single(p => p.Id == "off").Kind, picks.Picks.Single(p => p.Id == "off").By));
            Assert.Null(picks.Picks.Single(p => p.Id == "none").Kind);

            // Every check on one fit: nothing to ask at all.
            rig.Jev.Seen.Clear();
            var one = await rig.Engine.PickCheckVisualsAsync(new CheckVisualAsk { Checks = new[] { Item("x", "X", "M", fits: new[] { "table" }, rules: "table") } });
            Assert.Empty(rig.Jev.Seen);
            Assert.Equal(JevCheckVisuals.NoChoiceNote, one.Note);
        }

        [Fact]
        public async Task Switched_off_for_the_model_and_no_service_return_the_rules_picks()
        {
            using var rig = await BuildAsync(barMet: true);
            await rig.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.CheckVisualKind, false, "human");
            var off = await rig.Engine.PickCheckVisualsAsync(Eight());
            Assert.All(off.Picks, p => Assert.Equal(CheckVisualPick.ByRules, p.By));
            Assert.Contains("switched off", off.Note);
            Assert.Empty(rig.Jev.Seen);

            await rig.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.CheckVisualKind, true, "human");
            rig.Engine.UseDecisionsForTest(new DecisionService(NullDecisionOracle.Instance, new DecisionBudget()));
            var none = await rig.Engine.PickCheckVisualsAsync(Eight());
            Assert.All(none.Picks, p => Assert.Equal(CheckVisualPick.ByRules, p.By));
            Assert.StartsWith("Jev was not asked", none.Note);
        }

        [Fact]
        public async Task The_request_carries_names_status_words_and_counts_and_never_a_row_or_a_value()
        {
            using var rig = await BuildAsync(barMet: true);
            await rig.Engine.PickCheckVisualsAsync(new CheckVisualAsk { Checks = new[] { Item("gl", "Total Sales and the GL", "Total Sales") } });
            var r = Assert.Single(rig.Jev.Seen);
            Assert.Equal(new[] { "check_shapes", "check_words" }, r.Snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            var json = DecisionDigest.CanonicalSnapshotJson(r.Snapshot);
            Assert.Equal(
                "{\"check_shapes\":[{\"comparisons\":12,\"has_earlier_run\":true,\"id\":\"gl\",\"is_split\":true,\"recorded_runs\":8,\"status\":\"differs\"}],"
                + "\"check_words\":[{\"about\":\"Total Sales\",\"id\":\"gl\",\"kind\":\"against SQL\",\"name\":\"Total Sales and the GL\"}]}",
                json);

            // The wire type itself has nowhere to put a row or a value.
            var props = typeof(CheckVisualItem).GetProperties().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "Fits", "HasPrior", "HasSplit", "Id", "KindWord", "Measure", "Rows", "RulesPick", "Runs", "Status", "Title" }, props);
        }

        [Fact]
        public async Task Plain_messages_for_what_the_page_should_not_send()
        {
            using var rig = await BuildAsync(barMet: true);
            var tooMany = new CheckVisualAsk { Checks = Enumerable.Range(0, 41).Select(i => Item("c" + i, "T" + i, "M")).ToArray() };
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => rig.Engine.PickCheckVisualsAsync(tooMany));
            Assert.Contains("40", ex.Message);
            await Assert.ThrowsAsync<ArgumentException>(() => rig.Engine.PickCheckVisualsAsync(new CheckVisualAsk { Checks = new[] { Item("a", "A", "M"), Item("a", "B", "M") } }));
            await Assert.ThrowsAsync<ArgumentException>(() => rig.Engine.PickCheckVisualsAsync(new CheckVisualAsk { Checks = new[] { Item("a", "A", "M", rules: "pie") } }));
            await Assert.ThrowsAsync<ArgumentException>(() => rig.Engine.PickCheckVisualsAsync(new CheckVisualAsk { Checks = new[] { Item("a", "A", "M", status: "weird") } }));
            Assert.Empty((await rig.Engine.PickCheckVisualsAsync(new CheckVisualAsk { Checks = Array.Empty<CheckVisualItem>() })).Picks);
            Assert.Empty(rig.Jev.Seen);
        }

        [Fact]
        public async Task A_free_session_is_refused_with_the_pro_line_and_nothing_is_sent()
        {
            using var rig = await BuildAsync(tier: TestEntitlements.Free, barMet: true);
            var ex = await Assert.ThrowsAsync<EntitlementException>(() => rig.Engine.PickCheckVisualsAsync(Eight()));
            Assert.Contains("Semanticus Pro feature", ex.Message);
            await Assert.ThrowsAsync<EntitlementException>(() => McpTools.PickCheckVisuals(rig.Engine, Eight().Checks));
            Assert.Empty(rig.Jev.Seen);
            Assert.Equal(0, rig.Engine.Decisions.RequestsStarted);
        }

        [Fact]
        public async Task Both_doors_reach_the_same_method_and_answer_the_same()
        {
            using var rig = await BuildAsync(barMet: true, script: _ => ("bars", 0.93));
            var viaRpc = await new EngineRpcTarget(rig.Engine).pickCheckVisuals(Eight());
            var viaMcp = await McpTools.PickCheckVisuals(rig.Engine, Eight().Checks);
            Assert.Equal(viaRpc.Picks.Select(p => (p.Id, p.Kind, p.By, p.Reason)), viaMcp.Picks.Select(p => (p.Id, p.Kind, p.By, p.Reason)));
            Assert.Equal(2, rig.Jev.Seen.Count);
            Assert.Equal(rig.Jev.Seen[0].Digest, rig.Jev.Seen[1].Digest);

            Assert.True(FeatureMap.TryOperation("pick_check_visuals", out var feature, out _));
            Assert.Equal(ProFeature.AskData, feature);
            Assert.True(FeatureMap.TryRpcMethod("pickCheckVisuals", out feature, out _));
            Assert.Equal(ProFeature.AskData, feature);
        }

        /// <summary>Consent is per point (DecisionConsent.Covers checks the point's own fields), so the version stays 6 and a person who
        /// agreed under it is not shown the card again; the new point just reads as not covered until they agree.</summary>
        [Fact]
        public async Task A_version_6_permission_without_the_new_fields_is_still_current_and_the_card_is_not_shown_again()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync();
            var consent = await f.Engine.GetConsentAsync();
            Assert.Equal(6, consent.SchemaVersionInForce);
            var mine = DecisionRegistry.Require(DecisionRegistry.CheckVisualKind).SnapshotFields.Select(x => DecisionRegistry.CheckVisualKind + ":" + x).ToArray();
            foreach (var field in mine) Assert.Contains(field, consent.FieldsInForce);

            // What a person agreed to before this point existed: version 6, every field except the new point's.
            var before = await f.Engine.SetConsentAsync(consent.FieldsInForce.Except(mine).ToArray(), 6, "human");
            Assert.True(before.Current, "a version 6 permission from before this point must still count: " + before.Reason);
            Assert.True((await f.Engine.GetConsentAsync()).Current, "no new card on upgrade while the point is off");

            // Once the point is live (its bar cleared) the card is asked once for its fields.
            f.Engine.JevPicksCheckVisualsForTest = true;
            var live = await f.Engine.GetConsentAsync();
            Assert.False(live.Current);
            Assert.Contains("check_words", live.Reason, StringComparison.Ordinal);

            var point = DecisionRegistry.Require(DecisionRegistry.CheckVisualKind);
            var record = DecisionConsent.Grant(consent.FieldsInForce.Except(mine), 6, DateTimeOffset.UtcNow);
            Assert.False(record.Covers(point, DecisionRegistry.ConsentSchemaVersion, out var reason));
            Assert.Contains("check_words", reason, StringComparison.Ordinal);
            Assert.True(record.Covers(null, DecisionRegistry.ConsentSchemaVersion, out _));
            var nextStep = DecisionRegistry.Require(DecisionRegistry.NextStep);
            Assert.True(record.Covers(nextStep, DecisionRegistry.ConsentSchemaVersion, out _));
        }
    }
}
