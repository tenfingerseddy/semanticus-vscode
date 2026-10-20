// pick_chart_kind (D6): Jev's chart pick for a Queries result, the queries.chart_kind decision point, OFF by default.
//
// The null path is the tested path: no test here reaches a real service. A scripted oracle answers chart_kind the way
// a recorded replay would, and records every request so a test can prove what was (and was not) sent.

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
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ChartPickTests
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
                var q = request.Questions.Single();
                var (choice, p) = _script(q);
                var json = "{\"entries\":[{\"digest\":" + JsonSerializer.Serialize(request.Digest)
                    + ",\"point\":" + JsonSerializer.Serialize(request.PointName)
                    + ",\"usage\":{\"input_tokens\":300,\"output_tokens\":10},\"answers\":{"
                    + JsonSerializer.Serialize(q.Name) + ":{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(choice)
                    + ",\"confidence\":" + p.ToString(CultureInfo.InvariantCulture) + "}}}]}";
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
            rig.Engine.JevPicksChartForTest = barMet;
            rig.Jev = new ScriptedOracle(script ?? (_ => ("dumbbell", 0.95)));
            rig.Engine.UseDecisionsForTest(new DecisionService(rig.Jev, new DecisionBudget()));
            return rig;
        }

        /// <summary>"Net Sales and last year's by Region": one split, two numbers in one unit, so the rules leave dumbbell, variance, slope and table.</summary>
        private static ChartPickAsk RegionByMonth(string rulesPick = "dumbbell") => new ChartPickAsk
        {
            Words = "net sales against last year by region",
            Shape = new ChartPickShape
            {
                Dims = new[] { new ChartPickDim { Name = "Region", IsDate = false, Distinct = 12 } },
                Measures = 2,
                MeasureNames = new[] { "Net Sales", "Net Sales PY" },
                Rows = 12,
                SameUnit = true,
                Negatives = false,
            },
            Fits = new[] { "dumbbell", "variance", "slope", "table" },
            RulesPick = rulesPick,
        };

        [Fact]
        public void The_point_is_registered_pro_only_with_its_bar_and_off_in_this_build()
        {
            var p = DecisionRegistry.Require(DecisionRegistry.QueriesChartKind);
            Assert.True(p.ProOnly);
            Assert.Equal(0.85, p.ConfidenceBar);
            Assert.Equal("pick_chart_kind", p.Caller);
            Assert.Equal(new[] { "question_words", "result_shape" }, p.SnapshotFields.ToArray());
            var q = Assert.Single(p.Questions);
            Assert.Equal("chart_kind", q.Name);
            Assert.Equal(DecisionQuestionKind.Choice, q.Kind);
            Assert.Contains("rules", q.FixedOptions);
            Assert.False(JevChartPick.JevPicks);
        }

        [Fact]
        public void A_reason_does_not_name_the_kind_the_label_already_names()
        {
            // Queries writes "Jev picked Table:" and then this sentence, so a sentence that opens with the kind reads
            // "Jev picked Table: Table: ..." (verify round 2). The kind travels in its own field on both doors.
            foreach (var kind in JevChartPick.Kinds)
            {
                var reason = JevChartPick.ReasonFor(kind);
                var first = reason.Split(' ', ':')[0];
                Assert.False(string.Equals(first, kind, StringComparison.OrdinalIgnoreCase), $"the reason for {kind} opens with the kind: {reason}");
                Assert.True(char.IsUpper(reason[0]) && reason.EndsWith('.'), $"the reason for {kind} is not one plain sentence: {reason}");
            }
        }

        [Fact]
        public async Task Off_by_default_it_returns_the_rules_pick_with_the_note_and_asks_nothing()
        {
            using var rig = await BuildAsync();
            var pick = await rig.Engine.PickChartKindAsync(RegionByMonth());
            Assert.Equal("dumbbell", pick.Kind);
            Assert.Equal(ChartPick.ByRules, pick.By);
            Assert.Equal("Jev's visual pick is off until it clears its bar.", pick.Note);
            Assert.Equal(JevChartPick.ReasonFor("dumbbell"), pick.Reason);
            Assert.Empty(rig.Jev.Seen);
            Assert.Equal(0, rig.Engine.Decisions.RequestsStarted);

            var listed = (await rig.Engine.GetDecisionPointsAsync()).Single(x => x.Name == DecisionRegistry.QueriesChartKind);
            Assert.False(listed.Live);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task When_live_a_pick_in_fits_is_jevs_with_the_engines_own_reason()
        {
            using var rig = await BuildAsync(barMet: true, script: _ => ("variance", 0.93));
            var pick = await rig.Engine.PickChartKindAsync(RegionByMonth());
            Assert.Equal("variance", pick.Kind);
            Assert.Equal(ChartPick.ByJev, pick.By);
            Assert.Equal(JevChartPick.ReasonFor("variance"), pick.Reason);
            Assert.Equal(0.93, pick.Confidence);
            Assert.Null(pick.Note);

            var r = Assert.Single(rig.Jev.Seen);
            Assert.Equal(DecisionRegistry.QueriesChartKind, r.PointName);
            var offered = r.Questions.Single().Options.Select(o => o.Id).ToArray();
            Assert.Equal("rules", offered.Last());
            Assert.Equal(new[] { "dumbbell", "slope", "table", "variance" }, offered.Take(4).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public async Task A_pick_outside_fits_is_refused_and_falls_back_to_the_rules()
        {
            using var rig = await BuildAsync(barMet: true, script: _ => ("bars", 0.99));
            var pick = await rig.Engine.PickChartKindAsync(RegionByMonth());
            Assert.Equal("dumbbell", pick.Kind);
            Assert.Equal(ChartPick.ByRules, pick.By);
            Assert.False(string.IsNullOrEmpty(pick.Note));
            Assert.Single(rig.Jev.Seen);

            // And the reader alone refuses it too, whatever reached it: an answer to a wider offer than this result's fits.
            var point = DecisionRegistry.Require(DecisionRegistry.QueriesChartKind);
            var snapshot = JevChartPick.Snapshot("x", RegionByMonth().Shape);
            var wide = new DecisionRequest(point.Name, point.SchemaVersion, snapshot, JevChartPick.Questions(snapshot, new[] { "bars", "dumbbell" }));
            var answers = await new ScriptedOracle(_ => ("bars", 0.99)).AskAsync(wide, CancellationToken.None);
            Assert.True(answers.Decided);
            var read = JevChartPick.Read(answers, point.ConfidenceBar, new[] { "dumbbell", "variance", "table" }, "dumbbell");
            Assert.Equal(ChartPick.ByRules, read.By);
            Assert.Equal("dumbbell", read.Kind);
            Assert.Contains("ruled out", read.Note);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_neutral_option_keeps_the_rules_pick_by_the_rules()
        {
            using var rig = await BuildAsync(barMet: true, script: _ => ("rules", 0.97));
            var pick = await rig.Engine.PickChartKindAsync(RegionByMonth());
            Assert.Equal("dumbbell", pick.Kind);
            Assert.Equal(ChartPick.ByRules, pick.By);
            Assert.Equal("Jev said the rules' pick is fine.", pick.Note);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_low_confidence_answer_keeps_the_rules_pick()
        {
            using var rig = await BuildAsync(barMet: true, script: _ => ("variance", 0.6));
            var pick = await rig.Engine.PickChartKindAsync(RegionByMonth());
            Assert.Equal("dumbbell", pick.Kind);
            Assert.Equal(ChartPick.ByRules, pick.By);
            Assert.Contains("not sure enough", pick.Note);
        }

        [Fact]
        public async Task Switched_off_for_the_model_empty_fits_and_no_service_all_return_the_rules_pick()
        {
            using var rig = await BuildAsync(barMet: true);
            var empty = await rig.Engine.PickChartKindAsync(new ChartPickAsk { Shape = new ChartPickShape(), Fits = Array.Empty<string>(), RulesPick = "table" });
            Assert.Equal(("table", ChartPick.ByRules), (empty.Kind, empty.By));

            await rig.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.QueriesChartKind, false, "human");
            var off = await rig.Engine.PickChartKindAsync(RegionByMonth());
            Assert.Equal(ChartPick.ByRules, off.By);
            Assert.Contains("switched off", off.Note);
            Assert.Empty(rig.Jev.Seen);

            await rig.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.QueriesChartKind, true, "human");
            rig.Engine.UseDecisionsForTest(new DecisionService(NullDecisionOracle.Instance, new DecisionBudget()));
            var none = await rig.Engine.PickChartKindAsync(RegionByMonth());
            Assert.Equal(("dumbbell", ChartPick.ByRules), (none.Kind, none.By));
            Assert.StartsWith("Jev was not asked", none.Note);
        }

        [Fact]
        public async Task The_request_carries_the_words_and_the_shape_and_never_rows_or_values()
        {
            using var rig = await BuildAsync(barMet: true);
            await rig.Engine.PickChartKindAsync(RegionByMonth());
            var r = Assert.Single(rig.Jev.Seen);
            Assert.Equal(new[] { "question_words", "result_shape" }, r.Snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            var json = DecisionDigest.CanonicalSnapshotJson(r.Snapshot);
            Assert.Equal(
                "{\"question_words\":\"net sales against last year by region\",\"result_shape\":{\"dims\":[{\"distinct\":12,\"is_date\":false,\"name\":\"Region\"}],\"has_negatives\":false,\"measure_names\":[\"Net Sales\",\"Net Sales PY\"],\"measures\":2,\"rows\":12,\"same_unit\":true}}",
                json);

            // The wire type itself has nowhere to put a row or a value.
            var props = typeof(ChartPickAsk).GetProperties().Select(p => p.Name)
                .Concat(typeof(ChartPickShape).GetProperties().Select(p => p.Name))
                .Concat(typeof(ChartPickDim).GetProperties().Select(p => p.Name)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "Dims", "Distinct", "Fits", "IsDate", "MeasureNames", "Measures", "Name", "Negatives", "Rows", "RulesPick", "SameUnit", "Shape", "Words" }, props);

            // A result built from parts has no words, and none are sent.
            var noWords = RegionByMonth();
            noWords.Words = null;
            await rig.Engine.PickChartKindAsync(noWords);
            Assert.Equal(new[] { "result_shape" }, rig.Jev.Seen[1].Snapshot.Keys.ToArray());
        }

        [Fact]
        public async Task A_free_session_is_refused_with_the_pro_line_and_nothing_is_sent()
        {
            using var rig = await BuildAsync(tier: TestEntitlements.Free, barMet: true);
            var ex = await Assert.ThrowsAsync<EntitlementException>(() => rig.Engine.PickChartKindAsync(RegionByMonth()));
            Assert.Contains("Ask your data is a Semanticus Pro feature", ex.Message);
            using var shape = JsonDocument.Parse("{\"dims\":[],\"measures\":1,\"rows\":1}");
            await Assert.ThrowsAsync<EntitlementException>(() => McpTools.PickChartKind(rig.Engine, shape.RootElement.Clone(), new[] { "total", "table" }, "total"));
            Assert.Empty(rig.Jev.Seen);
            Assert.Equal(0, rig.Engine.Decisions.RequestsStarted);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Both_doors_reach_the_same_method_and_answer_the_same()
        {
            using var rig = await BuildAsync(barMet: true, script: _ => ("variance", 0.93));
            var viaRpc = await new EngineRpcTarget(rig.Engine).pickChartKind(RegionByMonth());
            using var shape = JsonDocument.Parse("{\"dims\":[{\"name\":\"Region\",\"isDate\":false,\"distinct\":12}],\"measures\":2,\"rows\":12,\"measureNames\":[\"Net Sales\",\"Net Sales PY\"],\"sameUnit\":true,\"negatives\":false}");
            var viaMcp = await McpTools.PickChartKind(rig.Engine, shape.RootElement.Clone(), new[] { "dumbbell", "variance", "slope", "table" }, "dumbbell",
                "net sales against last year by region");
            Assert.Equal((viaRpc.Kind, viaRpc.By, viaRpc.Reason), (viaMcp.Kind, viaMcp.By, viaMcp.Reason));
            Assert.Equal(ChartPick.ByJev, viaMcp.By);
            Assert.Equal(2, rig.Jev.Seen.Count);
            Assert.Equal(rig.Jev.Seen[0].Digest, rig.Jev.Seen[1].Digest);

            Assert.True(FeatureMap.TryOperation("pick_chart_kind", out var feature, out _));
            Assert.Equal(ProFeature.AskData, feature);
            Assert.True(FeatureMap.TryRpcMethod("pickChartKind", out feature, out _));
            Assert.Equal(ProFeature.AskData, feature);
        }

        [Fact]
        public async Task A_rules_pick_that_is_not_in_fits_is_a_caller_error()
        {
            using var rig = await BuildAsync();
            var ask = RegionByMonth("bars");
            await Assert.ThrowsAsync<ArgumentException>(() => rig.Engine.PickChartKindAsync(ask));
        }
    }
}
