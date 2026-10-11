// DecisionBoundary: the frozen case matrix, and the exit gate for M0a.
//
// Five ways a decision can be refused, and one way it can be allowed. On every refused row the claim is the
// same and is measured, not argued:
//   the operation's result is what it is today, with no proposal and a plain reason
//   the model is untouched: same ref, same revision, no folder, no format
//   ZERO requests reached the service, read off the oracle's own counter
// On the allowed row the proposal appears, and both doors carry the same fields.
//
// What "identical to today's" means here is the plan's own wording at docs/redesign/jev-plan.md:11 and :58,
// "identical canonical model and operation outcomes with no provider": the model state and the operation
// results, not a byte comparison of whole suites, which Astra's review ruled out as undefined
// (docs/redesign/jev-plan-astra.md). create_measure's own wire shape is unchanged by this work, so the
// mutation half of that claim is true by construction and is asserted below anyway.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionBoundaryTests
    {
        private static void AssertNoProposal(MeasureDefaultsProposal p, string measureRef)
        {
            Assert.Equal(measureRef, p.ObjectRef);
            Assert.False(p.Asked);
            Assert.False(string.IsNullOrWhiteSpace(p.Reason));
            Assert.Null(p.ProposedDisplayFolder);
            Assert.Null(p.ProposedFormatFamily);
            Assert.Equal(0, p.ProposedDisplayFolderConfidence);
            Assert.Equal(0, p.ProposedFormatFamilyConfidence);
            Assert.Null(p.ServedModelVersion);
            Assert.DoesNotContain("—", p.Reason, StringComparison.Ordinal);
        }

        private static void AssertSameOnBothDoors(MeasureDefaultsProposal mcp, MeasureDefaultsProposal rpc)
        {
            Assert.Equal(mcp.ObjectRef, rpc.ObjectRef);
            Assert.Equal(mcp.Asked, rpc.Asked);
            Assert.Equal(mcp.Reason, rpc.Reason);
            Assert.Equal(mcp.ProposedDisplayFolder, rpc.ProposedDisplayFolder);
            Assert.Equal(mcp.ProposedFormatFamily, rpc.ProposedFormatFamily);
            Assert.Equal(mcp.ProposedDisplayFolderConfidence, rpc.ProposedDisplayFolderConfidence, 6);
            Assert.Equal(mcp.ProposedFormatFamilyConfidence, rpc.ProposedFormatFamilyConfidence, 6);
            Assert.Equal(mcp.ConfidenceBar, rpc.ConfidenceBar, 6);
            Assert.Equal(mcp.ServedModelVersion, rpc.ServedModelVersion);
        }

        /// <summary>The measure is exactly as create_measure left it: nothing proposed was written.</summary>
        private static async Task AssertModelUntouched(DecisionFixture f, string measureRef, long revisionBefore)
        {
            var session = f.Sessions.Require();
            Assert.Equal(revisionBefore, session.Revision);

            var state = await session.ReadAsync(m =>
            {
                var measure = ObjectRefs.Resolve(m, measureRef) as TabularEditor.TOMWrapper.Measure;
                return new[] { measure == null ? null : measure.DisplayFolder, measure == null ? null : measure.FormatString };
            });
            Assert.True(string.IsNullOrEmpty(state[0]), "a proposal was written into the display folder");
            Assert.True(string.IsNullOrEmpty(state[1]), "a proposal was written into the format string");
        }

        // ---- row 1: no service ------------------------------------------------------------------------

        [Fact]
        public async Task With_the_null_oracle_nothing_is_proposed_and_nothing_is_sent()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();
                f.UseNull();
                var revision = f.Sessions.Require().Revision;

                var mcp = await McpTools.ProposeMeasureDefaults(f.Engine, measureRef);
                var rpc = await new EngineRpcTarget(f.Engine).proposeMeasureDefaults(measureRef);

                AssertNoProposal(mcp, measureRef);
                AssertNoProposal(rpc, measureRef);
                AssertSameOnBothDoors(mcp, rpc);
                Assert.Contains("no suggestion service", mcp.Reason, StringComparison.Ordinal);
                Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
                await AssertModelUntouched(f, measureRef, revision);
            }
        }

        // ---- row 2: consent withdrawn -----------------------------------------------------------------

        [Fact]
        public async Task With_consent_withdrawn_nothing_is_proposed_and_nothing_is_sent()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();
                var oracle = f.UseEmptyRecorded();
                await f.Engine.WithdrawConsentAsync("human");
                var revision = f.Sessions.Require().Revision;

                var mcp = await McpTools.ProposeMeasureDefaults(f.Engine, measureRef);
                var rpc = await new EngineRpcTarget(f.Engine).proposeMeasureDefaults(measureRef);

                AssertNoProposal(mcp, measureRef);
                AssertSameOnBothDoors(mcp, rpc);
                Assert.Contains("withdrew permission", mcp.Reason, StringComparison.Ordinal);
                Assert.Empty(oracle.AskedDigests);
                Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
                await AssertModelUntouched(f, measureRef, revision);
            }
        }

        // ---- row 3: a stale session generation ---------------------------------------------------------

        [Fact]
        public async Task A_stale_session_generation_is_refused_before_anything_is_sent()
        {
            var oracle = RecordedDecisionOracle.Empty();
            using (var service = new DecisionService(oracle, readConsent: _ =>
                DecisionConsent.Grant(DecisionRegistry.DeclaredFields, DecisionRegistry.ConsentSchemaVersion, DateTimeOffset.UtcNow))
            { ReadScope = () => Task.FromResult("disk:test") })
            {
                var request = DecisionFixture.SampleRequest();
                var outcome = await service.AskAsync(request.PointName, request.Snapshot, request.Questions,
                    isPro: true, enabled: true,
                    fence: DecisionSessionFence.Stale("s-original", 7),
                    ct: CancellationToken.None);

                Assert.False(outcome.Asked);
                Assert.Contains("the model changed", outcome.SkippedReason, StringComparison.Ordinal);
                Assert.Empty(oracle.AskedDigests);
                Assert.Equal(0, service.RequestsStarted);
            }
        }

        [Fact]
        public async Task A_reply_that_lands_after_the_model_moved_is_dropped()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();

                var slow = new ParkedOracle();
                f.Engine.UseDecisionsForTest(new DecisionService(slow));

                var asking = f.Engine.ProposeMeasureDefaultsAsync(measureRef);
                Assert.True(slow.Started.Wait(TimeSpan.FromSeconds(10)), "the ask never started");

                // The model moves underneath the in-flight ask.
                await f.Engine.CreateMeasureAsync("table:Sales", "Order Value", "SUM ( Sales[Amount] )", "human");

                slow.Reply();
                var proposal = await asking;

                Assert.False(proposal.Asked);
                Assert.Contains("the model changed while", proposal.Reason, StringComparison.Ordinal);
                Assert.Null(proposal.ProposedDisplayFolder);
            }
        }

        /// <summary>The fence has to be cut from the SAME read as the evidence. An edit that lands between the
        /// two used to give an old snapshot a fresh revision stamp, which is a stale piece of evidence wearing
        /// a current fence. Both doors, because a fence that only holds on one of them is not a fence.</summary>
        [Fact]
        public async Task An_edit_between_the_snapshot_and_the_fence_is_refused_on_both_doors()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();
                var oracle = f.UseEmptyRecorded();

                f.Engine.BetweenSnapshotAndFenceForTest = () =>
                    f.Engine.CreateMeasureAsync("table:Sales", "Order Value " + Guid.NewGuid().ToString("N"),
                                                "SUM ( Sales[Amount] )", "human");

                var mcp = await McpTools.ProposeMeasureDefaults(f.Engine, measureRef);
                var rpc = await new EngineRpcTarget(f.Engine).proposeMeasureDefaults(measureRef);

                AssertNoProposal(mcp, measureRef);
                AssertNoProposal(rpc, measureRef);
                AssertSameOnBothDoors(mcp, rpc);
                Assert.Contains("the model changed", mcp.Reason, StringComparison.Ordinal);
                Assert.Empty(oracle.AskedDigests);
                Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
            }
        }

        // ---- F-229: the formula never leaves, only facts worked out from it ---------------------------

        /// <summary>A formula can carry a value: a customer's name in a filter, a threshold, a price. The consent
        /// card promises no values, so the point sends facts worked out from the formula on this computer and
        /// never the text. This plants a string and a number in a measure and reads the whole snapshot, as the
        /// wire would carry it, for either of them and for the formula itself.</summary>
        [Fact]
        public async Task The_measure_snapshot_carries_no_formula_text_and_no_literal()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();
                await f.Engine.CreateColumnAsync("table:Sales", "Customer", "String", null, "human");
                await f.Engine.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
                var formula = "CALCULATE ( SUM ( Sales[Amount] ), Sales[Customer] = \"Contoso Widget Co\", Sales[Quantity] > 48213 )";
                var planted = await f.Engine.CreateMeasureAsync("table:Sales", "Planted", formula, "human");

                var snapshot = await SnapshotOf(f, planted);
                var json = DecisionDigest.CanonicalSnapshotJson(snapshot);

                Assert.False(snapshot.ContainsKey("measure_expression"), "the formula text is still a snapshot field");
                Assert.DoesNotContain("Contoso Widget", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("48213", json, StringComparison.Ordinal);
                Assert.DoesNotContain("CALCULATE (", json, StringComparison.Ordinal);
                Assert.DoesNotContain("Sales[Customer]", json, StringComparison.Ordinal);

                var facts = Facts(snapshot);
                Assert.Equal(new[] { "CALCULATE", "SUM" }, Functions(facts));
            }
        }

        /// <summary>A money measure built from other measures shows no SUM of its own, so the functions alone
        /// cannot say what it is (the live replay lost 17 points on format there). The NAMES of the measures it
        /// uses can, and names are inside the card's promise. They come from the dependency list, sorted, never
        /// from the text, so nothing else in the formula rides along.</summary>
        [Fact]
        public async Task A_measure_built_from_measures_sends_their_names_and_no_text()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();
                var margin = await f.Engine.CreateMeasureAsync("table:Sales", "Margin",
                    "[Total Sales] - [Order Count] * 12345", "human");

                var snapshot = await SnapshotOf(f, margin);
                Assert.True(snapshot.ContainsKey("references_measures"), "the snapshot carries no measure names");
                Assert.Equal(new[] { "Order Count", "Total Sales" },
                    ((IEnumerable<object>)snapshot["references_measures"]).Cast<string>().ToArray());

                var json = DecisionDigest.CanonicalSnapshotJson(snapshot);
                Assert.DoesNotContain("12345", json, StringComparison.Ordinal);
                Assert.DoesNotContain("[Total Sales]", json, StringComparison.Ordinal);
                Assert.DoesNotContain(" - ", json, StringComparison.Ordinal);
            }
        }

        /// <summary>Each fact the point may send, worked out from a formula that plainly has it or plainly does
        /// not. Function names come in the order the formula first uses them, so the outer function leads.</summary>
        [Theory]
        [InlineData("DIVIDE ( SUM ( Sales[Amount] ), SUM ( Sales[Cost] ) )", "DIVIDE,SUM", true, false, false, false, false)]
        [InlineData("SUM ( Sales[Amount] ) / SUM ( Sales[Cost] )", "SUM", true, false, false, false, false)]
        [InlineData("COUNTROWS ( Sales )", "COUNTROWS", false, true, false, false, false)]
        [InlineData("distinctcount ( Sales[Customer] )", "DISTINCTCOUNT", false, true, false, false, false)]
        [InlineData("SUM ( Sales[Net Price] )", "SUM", false, false, true, false, false)]
        [InlineData("SUMX ( Sales, Sales[Amount] * 2 )", "SUMX", false, false, true, false, false)]
        [InlineData("SUM ( Sales[Quantity] )", "SUM", false, false, false, false, false)]
        [InlineData("MAX ( Sales[Order Date] )", "MAX", false, false, false, true, false)]
        [InlineData("CALCULATE ( SUM ( Sales[Amount] ), SAMEPERIODLASTYEAR ( Sales[Order Date] ) )", "CALCULATE,SUM,SAMEPERIODLASTYEAR", false, false, true, true, true)]
        [InlineData("TOTALYTD ( SUM ( Sales[Quantity] ), Sales[Order Date] )", "TOTALYTD,SUM", false, false, false, true, true)]
        [InlineData("DIVIDE ( COUNTROWS ( Sales ), 7 )", "DIVIDE,COUNTROWS", true, false, false, false, false)]
        public async Task Each_formula_fact_is_worked_out_on_this_computer(string formula, string functions,
            bool ratio, bool count, bool currencySum, bool dateColumn, bool timeIntelligence)
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();
                await f.Engine.CreateColumnAsync("table:Sales", "Cost", "Double", null, "human");
                await f.Engine.CreateColumnAsync("table:Sales", "Customer", "String", null, "human");
                await f.Engine.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
                await f.Engine.CreateColumnAsync("table:Sales", "Net Price", "Decimal", null, "human");
                await f.Engine.CreateColumnAsync("table:Sales", "Order Date", "DateTime", null, "human");
                var measure = await f.Engine.CreateMeasureAsync("table:Sales", "Under Test", formula, "human");

                var facts = Facts(await SnapshotOf(f, measure));

                Assert.Equal(functions.Split(','), Functions(facts));
                Assert.Equal(ratio, (bool)facts["returns_ratio"]);
                Assert.Equal(count, (bool)facts["returns_count"]);
                Assert.Equal(currencySum, (bool)facts["returns_currency_like_sum"]);
                Assert.Equal(dateColumn, (bool)facts["references_date_column"]);
                Assert.Equal(timeIntelligence, (bool)facts["time_intelligence"]);
            }
        }

        /// <summary>The length limit existed only because the formula text was sent, and a cut formula would
        /// have made two measures look like one piece of evidence. Nothing of the formula is sent now, so a
        /// long formula is asked about like any other, on both doors, and none of it reaches the service.</summary>
        [Fact]
        public async Task A_long_formula_is_asked_about_on_both_doors_and_none_of_it_is_sent()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();
                await f.ConsentAsync();
                var capture = new CapturingOracle();
                f.Engine.UseDecisionsForTest(new DecisionService(capture));

                var head = "SUM ( Sales[Amount] )";
                var tail = string.Concat(Enumerable.Repeat(" + 91357", 200));
                var big = await f.Engine.CreateMeasureAsync("table:Sales", "Very Long", head + tail, "human");
                Assert.True((head + tail).Length > 1200, "the test formula is not long");

                var mcp = await McpTools.ProposeMeasureDefaults(f.Engine, big);
                var rpc = await new EngineRpcTarget(f.Engine).proposeMeasureDefaults(big);

                AssertSameOnBothDoors(mcp, rpc);
                Assert.True(mcp.Asked, "a long formula was refused: " + mcp.Reason);
                // The second door may be served from the cache, so the count is at least one; every request
                // that did go out is the same request.
                Assert.NotEmpty(capture.Snapshots);
                Assert.All(capture.Snapshots, s => Assert.Equal(capture.Snapshots[0], s));
                foreach (var sent in capture.Snapshots)
                {
                    Assert.DoesNotContain("91357", sent, StringComparison.Ordinal);
                    Assert.DoesNotContain("measure_expression", sent, StringComparison.Ordinal);
                    Assert.Contains("measure_formula_facts", sent, StringComparison.Ordinal);
                }
            }
        }

        private static Task<IReadOnlyDictionary<string, object>> SnapshotOf(DecisionFixture f, string measureRef) =>
            f.Sessions.Require().ReadAsync(m =>
                MeasureDefaultsPoint.Snapshot((TabularEditor.TOMWrapper.Measure)ObjectRefs.Resolve(m, measureRef)));

        private static IReadOnlyDictionary<string, object> Facts(IReadOnlyDictionary<string, object> snapshot)
        {
            Assert.True(snapshot.ContainsKey("measure_formula_facts"), "the snapshot carries no formula facts");
            return (IReadOnlyDictionary<string, object>)snapshot["measure_formula_facts"];
        }

        private static string[] Functions(IReadOnlyDictionary<string, object> facts) =>
            ((IEnumerable<object>)facts["functions"]).Cast<string>().ToArray();

        /// <summary>An oracle that keeps the canonical snapshot of every request and decides nothing.</summary>
        private sealed class CapturingOracle : IDecisionOracle
        {
            public List<string> Snapshots { get; } = new List<string>();

            public bool Available { get { return true; } }

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (Snapshots) Snapshots.Add(DecisionDigest.CanonicalSnapshotJson(request.Snapshot));
                return Task.FromResult(DecisionAnswers.NotDecided(request, "captured for the test"));
            }
        }

        // ---- row 4: a free session ---------------------------------------------------------------------

        [Fact]
        public async Task A_free_session_is_told_it_is_Pro_and_nothing_is_sent()
        {
            using (var f = new DecisionFixture(TestEntitlements.Free))
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();
                var oracle = f.UseEmptyRecorded();
                var revision = f.Sessions.Require().Revision;

                var mcp = await McpTools.ProposeMeasureDefaults(f.Engine, measureRef);
                var rpc = await new EngineRpcTarget(f.Engine).proposeMeasureDefaults(measureRef);

                AssertNoProposal(mcp, measureRef);
                AssertSameOnBothDoors(mcp, rpc);
                Assert.Contains("Semanticus Pro", mcp.Reason, StringComparison.Ordinal);
                Assert.Empty(oracle.AskedDigests);
                Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
                await AssertModelUntouched(f, measureRef, revision);
            }
        }

        // ---- row 5: the point switched off -------------------------------------------------------------

        [Fact]
        public async Task With_the_point_switched_off_nothing_is_proposed_and_nothing_is_sent()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();
                var oracle = f.UseEmptyRecorded();

                var points = await McpTools.SetDecisionPointEnabled(f.Engine, DecisionRegistry.MeasureFolderAndFormat, false);
                Assert.False(points.Single(p => p.Name == DecisionRegistry.MeasureFolderAndFormat).Enabled);
                var revision = f.Sessions.Require().Revision;

                var mcp = await McpTools.ProposeMeasureDefaults(f.Engine, measureRef);
                var rpc = await new EngineRpcTarget(f.Engine).proposeMeasureDefaults(measureRef);

                AssertNoProposal(mcp, measureRef);
                AssertSameOnBothDoors(mcp, rpc);
                Assert.Contains("switched off", mcp.Reason, StringComparison.Ordinal);
                Assert.Empty(oracle.AskedDigests);
                Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
                await AssertModelUntouched(f, measureRef, revision);

                // And the switch survives a read back through the registry on the other door.
                var seenByRpc = await new EngineRpcTarget(f.Engine).getDecisionPoints();
                Assert.False(seenByRpc.Single(p => p.Name == DecisionRegistry.MeasureFolderAndFormat).Enabled);
            }
        }

        // ---- a point whose replay bar is unmet is not live (N1) ----------------------------------------

        /// <summary>An assistant reads get_decision_points' description before the list, so the description must say what
        /// 'live' means: a point with live false is never asked, whatever 'enabled' says. The field was added for exactly that
        /// (N1) and nothing pinned the sentence that explains it (plan-waits targeted verify, L4). The Studio door says the
        /// same thing on the switch itself ("Not on yet in this version."), which the consent drive checks.</summary>
        [Fact]
        public void The_decision_points_tool_tells_an_assistant_that_a_point_not_live_is_never_asked()
        {
            var tool = typeof(McpTools).Assembly.GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed && t.Name.StartsWith("McpTools", StringComparison.Ordinal))
                .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                .Single(m => m.GetCustomAttributes(true).Any(a => a.GetType().Name == "McpServerToolAttribute"
                    && (string)a.GetType().GetProperty("Name")?.GetValue(a) == "get_decision_points"));
            var description = ((System.ComponentModel.DescriptionAttribute)Attribute.GetCustomAttribute(tool, typeof(System.ComponentModel.DescriptionAttribute)))?.Description ?? "";

            Assert.Contains("'live' false", description, StringComparison.Ordinal);
            Assert.Contains("never asks, whatever 'enabled' says", description, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_point_whose_replay_bar_is_unmet_is_listed_as_not_live_on_both_doors()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();
                var mcp = await McpTools.GetDecisionPoints(f.Engine);
                var rpc = await new EngineRpcTarget(f.Engine).getDecisionPoints();
                foreach (var points in new[] { mcp, rpc })
                {
                    // Switched on (absent from the settings file) and still never asked: the list must say so. The
                    // starter bar is unmet. compose.draft cleared Kane's bar on the Workflows door (26 Sep,
                    // JevDraftBuilder.JevDraftsOnWorkflows), so it is live; the Plan door stays on the menu.
                    Assert.True(points.Single(p => p.Name == DecisionRegistry.ComposeDraft).Enabled);
                    Assert.True(points.Single(p => p.Name == DecisionRegistry.ComposeDraft).Live);
                    Assert.False(points.Single(p => p.Name == DecisionRegistry.MeasureStarter).Live);
                    // The date table point did not clear Kane's bar on the models it is asked about (joint verifier, 27 Sep).
                    Assert.True(points.Single(p => p.Name == DecisionRegistry.DateTable).Enabled);
                    Assert.False(points.Single(p => p.Name == DecisionRegistry.DateTable).Live);
                    Assert.True(points.Single(p => p.Name == DecisionRegistry.MeasureFolderAndFormat).Live);
                    Assert.True(points.Single(p => p.Name == DecisionRegistry.ColumnFormat).Live);
                }

                // The same constants the asking paths read decide it, so a bar that clears flips the list too.
                // With both compose.draft doors off (as before 26 Sep) the point is not live, and a starter bar that
                // clears makes that point live.
                f.Engine.JevDraftsForTest = false;
                f.Engine.StarterJevDecidesForTest = true;
                f.Engine.DateTableJevDecidesForTest = true;
                var after = await f.Engine.GetDecisionPointsAsync();
                Assert.False(after.Single(p => p.Name == DecisionRegistry.ComposeDraft).Live);
                Assert.True(after.Single(p => p.Name == DecisionRegistry.MeasureStarter).Live);
                Assert.True(after.Single(p => p.Name == DecisionRegistry.DateTable).Live);
            }
        }

        // ---- the allowed row ---------------------------------------------------------------------------

        [Fact]
        public async Task With_the_recorded_oracle_and_consent_the_proposal_appears_on_both_doors()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();

                var digest = await f.DiscoverDigestAsync(measureRef);
                f.UseRecorded(DecisionFixture.ProofFixture(digest, "Volume", 0.93, "currency", 0.97));
                var revision = f.Sessions.Require().Revision;

                var mcp = await McpTools.ProposeMeasureDefaults(f.Engine, measureRef);
                var rpc = await new EngineRpcTarget(f.Engine).proposeMeasureDefaults(measureRef);

                Assert.True(mcp.Asked);
                Assert.Null(mcp.Reason);
                Assert.Equal("Volume", mcp.ProposedDisplayFolder);
                Assert.Equal("currency", mcp.ProposedFormatFamily);
                Assert.Equal(0.93, mcp.ProposedDisplayFolderConfidence, 3);
                Assert.Equal(0.97, mcp.ProposedFormatFamilyConfidence, 3);
                Assert.Equal(0.85, mcp.ConfidenceBar, 3);
                Assert.Equal("jev-test-1", mcp.ServedModelVersion);
                AssertSameOnBothDoors(mcp, rpc);

                // A proposal is advice. Nothing was written.
                await AssertModelUntouched(f, measureRef, revision);
            }
        }

        [Fact]
        public async Task Below_the_bar_the_proposal_is_left_out_but_the_confidence_is_still_reported()
        {
            using (var f = new DecisionFixture())
            {
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();

                var digest = await f.DiscoverDigestAsync(measureRef);
                f.UseRecorded(DecisionFixture.ProofFixture(digest, "Volume", 0.51, "currency", 0.84));

                var proposal = await McpTools.ProposeMeasureDefaults(f.Engine, measureRef);

                Assert.True(proposal.Asked);
                Assert.Null(proposal.ProposedDisplayFolder);
                Assert.Null(proposal.ProposedFormatFamily);
                Assert.Equal(0.51, proposal.ProposedDisplayFolderConfidence, 3);
                Assert.Equal(0.84, proposal.ProposedFormatFamilyConfidence, 3);
                Assert.Contains("sure enough", proposal.Reason, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task A_measure_that_already_has_a_folder_and_a_format_is_never_asked_about()
        {
            using (var f = new DecisionFixture())
            {
                await f.BuildModelAsync();
                await f.ConsentAsync();
                var oracle = f.UseEmptyRecorded();

                var filed = await f.Engine.CreateMeasureAsync("table:Sales", "Filed Total", "SUM ( Sales[Amount] )", "human", "Volume");
                await f.Engine.SetMeasureFormatAsync(filed, "#,0", "human");

                var proposal = await McpTools.ProposeMeasureDefaults(f.Engine, filed);
                Assert.False(proposal.Asked);
                Assert.Contains("already has a folder and a format", proposal.Reason, StringComparison.Ordinal);
                Assert.Empty(oracle.AskedDigests);
            }
        }

        // ---- create_measure itself is unchanged --------------------------------------------------------

        [Fact]
        public async Task create_measure_still_returns_a_bare_ref_and_writes_no_defaults()
        {
            using (var f = new DecisionFixture())
            {
                await f.Engine.CreateModelAsync("Control", 1604);
                await f.Engine.CreateTableAsync("Sales", "human");
                await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
                await f.SaveAsAsync("Control.bim");
                await f.ConsentAsync();
                f.UseRecorded(DecisionFixture.ProofFixture("never-matched", "Volume", 0.99, "currency", 0.99));

                // The mutation path never asks, so no key, consent or service can change what it returns or
                // how long it takes. Both doors, same bare ref.
                var viaMcp = await McpTools.CreateMeasure(f.Engine, "table:Sales", "Via Agent", "SUM ( Sales[Amount] )");
                var viaRpc = await new EngineRpcTarget(f.Engine).createMeasure("table:Sales", "Via Studio", "SUM ( Sales[Amount] )");

                Assert.Equal("measure:Sales/Via Agent", viaMcp);
                Assert.Equal("measure:Sales/Via Studio", viaRpc);
                Assert.Equal(0, f.Engine.Decisions.RequestsStarted);

                var state = await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures
                    .Select(x => (x.Name, x.DisplayFolder, x.FormatString)).ToArray());
                foreach (var row in state)
                {
                    Assert.True(string.IsNullOrEmpty(row.DisplayFolder), row.Name + " was filed by something");
                    Assert.True(string.IsNullOrEmpty(row.FormatString), row.Name + " was formatted by something");
                }
            }
        }

        /// <summary>An oracle that parks until released, so a test can move the model while an ask is genuinely
        /// in flight.</summary>
        private sealed class ParkedOracle : IDecisionOracle
        {
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public ManualResetEventSlim Started { get; } = new ManualResetEventSlim(false);

            public bool Available { get { return true; } }

            public void Reply() => _release.TrySetResult(true);

            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                Started.Set();
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => cancelled.TrySetResult(true)))
                    await Task.WhenAny(_release.Task, cancelled.Task).ConfigureAwait(false);

                var recorded = new RecordedDecisionOracle(
                    DecisionFixture.ProofFixture(request.Digest, "Volume", 0.99, "currency", 0.99), "jev-test-1");
                return await recorded.AskAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
